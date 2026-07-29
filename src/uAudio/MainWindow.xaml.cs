using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using uAudio.Controls;
using uAudio.Models;
using uAudio.Services;
using IoPath = System.IO.Path;

namespace uAudio
{
    public partial class MainWindow : Window
    {
        private const string DefaultProjectName = "Untitled";
        private const string ProjectFileFilter = "uAudio Project|*.uaudioproj|JSON files|*.json|All files|*.*";
        private const string ExportFileFilter = "Wave PCM (*.wav)|*.wav";

        public static readonly RoutedUICommand ExportMixCommand = new RoutedUICommand(
            "Export Mix",
            nameof(ExportMixCommand),
            typeof(MainWindow));

        public static readonly RoutedUICommand AddTrackCommand = new RoutedUICommand("Add Track", nameof(AddTrackCommand), typeof(MainWindow));
        public static readonly RoutedUICommand PlayPauseCommand = new RoutedUICommand("Play/Pause", nameof(PlayPauseCommand), typeof(MainWindow));
        public static readonly RoutedUICommand StopPlaybackCommand = new RoutedUICommand("Stop", nameof(StopPlaybackCommand), typeof(MainWindow));
        public static readonly RoutedUICommand ZoomInCommand = new RoutedUICommand("Zoom In", nameof(ZoomInCommand), typeof(MainWindow));
        public static readonly RoutedUICommand ZoomOutCommand = new RoutedUICommand("Zoom Out", nameof(ZoomOutCommand), typeof(MainWindow));
        public static readonly RoutedUICommand ResetZoomCommand = new RoutedUICommand("Reset Zoom", nameof(ResetZoomCommand), typeof(MainWindow));
        public static readonly RoutedUICommand CutSelectionCommand = new RoutedUICommand("Cut Selection", nameof(CutSelectionCommand), typeof(MainWindow));
        public static readonly RoutedUICommand TrimSelectionCommand = new RoutedUICommand("Trim Selection", nameof(TrimSelectionCommand), typeof(MainWindow));
        public static readonly RoutedUICommand ShowShortcutsCommand = new RoutedUICommand("Shortcuts", nameof(ShowShortcutsCommand), typeof(MainWindow));

        private readonly AudioPlaybackEngine _engine = new AudioPlaybackEngine();
        private readonly List<TrackRowControl> _trackControls = new List<TrackRowControl>();
        private readonly DispatcherTimer _playheadTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(AppConstants.PlayheadRefreshMilliseconds)
        };
        private readonly DispatcherTimer _autosaveTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(AppConstants.AutosaveIntervalSeconds)
        };
        private readonly DispatcherTimer _zoomRedrawTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(AppConstants.ZoomRedrawDebounceMilliseconds)
        };

        private string? _currentProjectPath;
        private string _savedStateSignature = string.Empty;
        private int _trackCounter;
        private DateTime? _lastAutosaveUtc;
        private double _pixelsPerSecond = AppConstants.DefaultPixelsPerSecond;
        private bool _isAutosaveInProgress;
        private bool _isProjectDirty;
        private bool _suppressDirtyTracking;
        private bool _uiInitialized;
        private TrackRowControl? _activeSelectionTrack;

        public MainWindow()
        {
            _playheadTimer.Tick += PlayheadTimer_Tick;
            _autosaveTimer.Tick += AutosaveTimer_Tick;
            _zoomRedrawTimer.Tick += ZoomRedrawTimer_Tick;

            InitializeComponent();

            _uiInitialized = true;

            UpdateZoomValueText();
            UpdateAutosaveStatusText();
            UpdateTimelineWidth();
            DrawRuler();

            bool recovered = TryRestoreRecoveryOnStartup();
            if (!recovered)
            {
                CaptureSavedStateBaseline();
            }

            _playheadTimer.Start();
            _autosaveTimer.Start();
        }

        private void AddTrackButton_Click(object sender, RoutedEventArgs e)
        {
            SaveUndoSnapshot();
            AddTrack();
        }

        private TrackRowControl AddTrack()
        {
            _trackCounter++;

            TrackRowControl trackControl = CreateTrackControl();
            trackControl.SetTrackName($"Track {_trackCounter}");

            _trackControls.Add(trackControl);

            if (TracksPanel != null)
            {
                TracksPanel.Children.Add(trackControl);
            }

            trackControl.SyncToProvider();
            UpdateTimelineWidth();
            RefreshDirtyState();
            CommandManager.InvalidateRequerySuggested();

            return trackControl;
        }

        private TrackRowControl CreateTrackControl()
        {
            TrackRowControl trackControl = new TrackRowControl
            {
                PixelsPerSecond = _pixelsPerSecond
            };

            trackControl.SampleProvider = _engine.AddTrack();
            WireTrackControl(trackControl);

            return trackControl;
        }

        private void WireTrackControl(TrackRowControl trackControl)
        {
            trackControl.TrackChanged += TrackControl_TrackChanged;
            trackControl.TrackDeleteRequested += TrackControl_TrackDeleteRequested;
            trackControl.UndoSnapshotRequested += SaveUndoSnapshot;
            trackControl.CutRequested += TrackControl_CutRequested;
            trackControl.TrimRequested += TrackControl_TrimRequested;
            trackControl.SelectionPreviewChanged += TrackControl_SelectionPreviewChanged;
        }

        private void UnwireTrackControl(TrackRowControl trackControl)
        {
            trackControl.TrackChanged -= TrackControl_TrackChanged;
            trackControl.TrackDeleteRequested -= TrackControl_TrackDeleteRequested;
            trackControl.UndoSnapshotRequested -= SaveUndoSnapshot;
            trackControl.CutRequested -= TrackControl_CutRequested;
            trackControl.TrimRequested -= TrackControl_TrimRequested;
            trackControl.SelectionPreviewChanged -= TrackControl_SelectionPreviewChanged;
        }

        private void TrackControl_TrackChanged()
        {
            UpdateTimelineWidth();
            DrawRuler();
            RefreshDirtyState();
            CommandManager.InvalidateRequerySuggested();
        }

        private void TrackControl_TrackDeleteRequested(TrackRowControl trackControl)
        {
            DeleteTrack(trackControl);
        }

        private void DeleteTrack(TrackRowControl trackControl)
        {
            ClearSelectionVisualsExcept(null);
            if (ReferenceEquals(_activeSelectionTrack, trackControl))
            {
                _activeSelectionTrack = null;
            }

            UnwireTrackControl(trackControl);

            if (trackControl.SampleProvider != null)
            {
                _engine.RemoveTrack(trackControl.SampleProvider);
            }

            _trackControls.Remove(trackControl);

            if (TracksPanel != null)
            {
                TracksPanel.Children.Remove(trackControl);
            }

            RecalculateTrackCounter();
            UpdateTimelineWidth();
            DrawRuler();
            RefreshDirtyState();
            CommandManager.InvalidateRequerySuggested();
        }

        private void TrackControl_CutRequested(TrackRowControl sourceTrack, double start, double end)
        {
            ClearSelectionVisualsExcept(null);

            if (EditAllTracksCheckBox != null && EditAllTracksCheckBox.IsChecked == true)
            {
                foreach (TrackRowControl track in _trackControls)
                {
                    track.ExecuteCut(start, end);
                }
            }
            else
            {
                sourceTrack.ExecuteCut(start, end);
            }
        }

        private void TrackControl_TrimRequested(TrackRowControl sourceTrack, double start, double end)
        {
            ClearSelectionVisualsExcept(null);

            if (EditAllTracksCheckBox != null && EditAllTracksCheckBox.IsChecked == true)
            {
                foreach (TrackRowControl track in _trackControls)
                {
                    track.ExecuteTrim(start, end);
                }
            }
            else
            {
                sourceTrack.ExecuteTrim(start, end);
            }
        }

        private void TrackControl_SelectionPreviewChanged(TrackRowControl sourceTrack, double start, double end, bool isActive)
        {
            if (isActive)
            {
                _activeSelectionTrack = sourceTrack;
            }
            else if (ReferenceEquals(_activeSelectionTrack, sourceTrack))
            {
                _activeSelectionTrack = null;
            }

            CommandManager.InvalidateRequerySuggested();

            if (EditAllTracksCheckBox == null || EditAllTracksCheckBox.IsChecked != true || !isActive)
            {
                ClearSelectionVisualsExcept(sourceTrack);
                return;
            }

            foreach (TrackRowControl track in _trackControls)
            {
                if (ReferenceEquals(track, sourceTrack))
                {
                    continue;
                }

                track.ShowSelectionRange(start, end);
            }
        }

        private void ClearSelectionVisualsExcept(TrackRowControl? exceptTrack)
        {
            foreach (TrackRowControl track in _trackControls)
            {
                if (ReferenceEquals(track, exceptTrack))
                {
                    continue;
                }

                track.ClearSelectionVisual();
            }
        }

        private void PlayButton_Click(object sender, RoutedEventArgs e)
        {
            _engine.Play();
        }

        private void PauseButton_Click(object sender, RoutedEventArgs e)
        {
            _engine.Pause();
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            _engine.Stop();
        }

        private void TogglePlayPause()
        {
            if (_engine.IsPlaying)
            {
                _engine.Pause();
            }
            else
            {
                _engine.Play();
            }
        }

        private void ZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            _pixelsPerSecond = ZoomSlider != null ? ZoomSlider.Value : e.NewValue;
            UpdateZoomValueText();

            if (!_uiInitialized)
            {
                return;
            }

            UpdateTimelineWidth();
            ScheduleZoomRedraw();
        }

        private void ScheduleZoomRedraw()
        {
            _zoomRedrawTimer.Stop();
            _zoomRedrawTimer.Start();
        }

        private void ZoomRedrawTimer_Tick(object? sender, EventArgs e)
        {
            _zoomRedrawTimer.Stop();
            ApplyZoomToTracks(redraw: true);
            DrawRuler();
            RefreshDirtyState();
        }

        private void ApplyZoomToTracks(bool redraw)
        {
            foreach (TrackRowControl track in _trackControls)
            {
                track.SetPixelsPerSecond(_pixelsPerSecond, redraw);
            }
        }

        private void ChangeZoom(double delta)
        {
            if (ZoomSlider == null)
            {
                return;
            }

            ZoomSlider.Value = Math.Clamp(ZoomSlider.Value + delta, ZoomSlider.Minimum, ZoomSlider.Maximum);
        }

        private void ResetZoom()
        {
            if (ZoomSlider == null)
            {
                return;
            }

            ZoomSlider.Value = AppConstants.DefaultPixelsPerSecond;
        }

        private void EditAllTracksCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (!_uiInitialized)
            {
                return;
            }

            RefreshDirtyState();
        }

        private void AutosaveTimer_Tick(object? sender, EventArgs e)
        {
            if (!_uiInitialized || _suppressDirtyTracking || _isAutosaveInProgress || !_isProjectDirty)
            {
                return;
            }

            try
            {
                _isAutosaveInProgress = true;
                UpdateAutosaveStatusText();

                RecoveryFileService.Save(new RecoveryFileModel
                {
                    SourceProjectPath = _currentProjectPath ?? string.Empty,
                    SourceProjectName = GetDisplayProjectName(),
                    Project = CaptureProjectModel()
                });

                _lastAutosaveUtc = DateTime.Now;
                UpdateAutosaveStatusText();
            }
            catch (Exception ex)
            {
                AppLogger.Warning("Autosave failed.", ex);
                if (AutosaveStatusText != null)
                {
                    AutosaveStatusText.Text = "Autosave failed";
                    AutosaveStatusText.Foreground = new SolidColorBrush(Color.FromRgb(220, 120, 120));
                }
            }
            finally
            {
                _isAutosaveInProgress = false;
                UpdateAutosaveStatusText();
            }
        }

        private void PlayheadTimer_Tick(object? sender, EventArgs e)
        {
            if (!_uiInitialized ||
                PlayheadLine == null ||
                PlayheadCanvas == null ||
                TracksPanel == null ||
                TimelineGrid == null ||
                TimelineScrollViewer == null ||
                RulerScrollViewer == null ||
                TimeDisplay == null)
            {
                return;
            }

            double time = _engine.GlobalTime;
            double x = AppConstants.LeftPanelWidth + (time * _pixelsPerSecond);

            PlayheadLine.X1 = x;
            PlayheadLine.X2 = x;
            PlayheadLine.Y1 = 0d;
            PlayheadLine.Y2 = Math.Max(TracksPanel.ActualHeight, 400d);

            PlayheadCanvas.Width = Math.Max(TimelineGrid.ActualWidth, TimelineGrid.MinWidth);
            PlayheadCanvas.Height = PlayheadLine.Y2;

            TimeDisplay.Text = TimeFormatService.FormatTimelineTime(time);

            if (_engine.IsPlaying)
            {
                double viewportLeft = TimelineScrollViewer.HorizontalOffset;
                double viewportWidth = TimelineScrollViewer.ViewportWidth;

                if (viewportWidth > 0d && x > viewportLeft + (viewportWidth * 0.85d))
                {
                    TimelineScrollViewer.ScrollToHorizontalOffset(Math.Max(0d, x - (viewportWidth * 0.3d)));
                }
                else if (x < viewportLeft)
                {
                    TimelineScrollViewer.ScrollToHorizontalOffset(Math.Max(0d, x - 50d));
                }
            }

            RulerScrollViewer.ScrollToHorizontalOffset(TimelineScrollViewer.HorizontalOffset);
        }

        private void DrawRuler()
        {
            if (!_uiInitialized || RulerCanvas == null)
            {
                return;
            }

            RulerCanvas.Children.Clear();

            double totalDuration = GetMaxDuration() + AppConstants.TimelinePaddingSeconds;
            double totalWidth = AppConstants.LeftPanelWidth + (totalDuration * _pixelsPerSecond);

            RulerCanvas.Width = totalWidth;

            double stepSeconds;
            if (_pixelsPerSecond >= 300d)
            {
                stepSeconds = 0.25d;
            }
            else if (_pixelsPerSecond >= 180d)
            {
                stepSeconds = 0.5d;
            }
            else if (_pixelsPerSecond >= 80d)
            {
                stepSeconds = 1d;
            }
            else if (_pixelsPerSecond >= 30d)
            {
                stepSeconds = 5d;
            }
            else
            {
                stepSeconds = 10d;
            }

            int index = 0;
            for (double t = 0d; t <= totalDuration + 0.0001d; t += stepSeconds, index++)
            {
                double x = AppConstants.LeftPanelWidth + (t * _pixelsPerSecond);
                bool major = stepSeconds >= 5d || index % 5 == 0;

                Line tick = new Line
                {
                    X1 = x,
                    X2 = x,
                    Y1 = major ? 0d : 15d,
                    Y2 = 30d,
                    Stroke = major ? Brushes.White : new SolidColorBrush(Color.FromRgb(100, 100, 100)),
                    StrokeThickness = major ? 1d : 0.5d
                };
                RulerCanvas.Children.Add(tick);

                if (major || stepSeconds <= 1d)
                {
                    string label = TimeFormatService.FormatRulerLabel(t);

                    TextBlock text = new TextBlock
                    {
                        Text = label,
                        Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)),
                        FontSize = 9d,
                        FontFamily = new FontFamily("Consolas")
                    };

                    Canvas.SetLeft(text, x + 2d);
                    Canvas.SetTop(text, 1d);
                    RulerCanvas.Children.Add(text);
                }
            }
        }

        private void RulerCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (RulerCanvas == null)
            {
                return;
            }

            double x = e.GetPosition(RulerCanvas).X;
            double time = (x - AppConstants.LeftPanelWidth) / _pixelsPerSecond;
            _engine.Seek(Math.Max(0d, time));
        }

        private void TimelineScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (RulerScrollViewer != null && TimelineScrollViewer != null)
            {
                RulerScrollViewer.ScrollToHorizontalOffset(TimelineScrollViewer.HorizontalOffset);
            }
        }

        private void SaveUndoSnapshot()
        {
            ProjectSnapshot snapshot = new ProjectSnapshot
            {
                Tracks = _trackControls.Select(t => t.CreateSnapshot()).ToList()
            };

            UndoManager.SaveSnapshot(snapshot);
            CommandManager.InvalidateRequerySuggested();
        }

        private void UndoCommand_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            ProjectSnapshot? snapshot = UndoManager.PopSnapshot();
            if (snapshot == null)
            {
                MessageBox.Show("Nothing to undo.", "Undo", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            bool wasPlaying = _engine.IsPlaying;
            double currentTime = _engine.GlobalTime;

            _suppressDirtyTracking = true;

            try
            {
                _engine.Pause();
                ClearSelectionVisualsExcept(null);
                RemoveAllTracks();

                foreach (TrackSnapshot trackSnapshot in snapshot.Tracks)
                {
                    TrackRowControl trackControl = CreateTrackControl();
                    trackControl.RestoreFromSnapshot(trackSnapshot);

                    _trackControls.Add(trackControl);

                    if (TracksPanel != null)
                    {
                        TracksPanel.Children.Add(trackControl);
                    }
                }

                RecalculateTrackCounter();
                _engine.Seek(currentTime);

                if (wasPlaying)
                {
                    _engine.Play();
                }

                UpdateTimelineWidth();
                DrawRuler();
            }
            finally
            {
                _suppressDirtyTracking = false;
            }

            RefreshDirtyState();
            CommandManager.InvalidateRequerySuggested();
        }

        private void CommandBinding_CanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            if (e.Command == ApplicationCommands.Undo)
            {
                e.CanExecute = UndoManager.CanUndo;
            }
            else if (e.Command == ExportMixCommand)
            {
                e.CanExecute = HasAudioContent();
            }
            else if (e.Command == CutSelectionCommand || e.Command == TrimSelectionCommand)
            {
                e.CanExecute = _activeSelectionTrack != null && _activeSelectionTrack.HasUsableSelection;
            }
            else
            {
                e.CanExecute = true;
            }

            e.Handled = true;
        }

        private void NewCommand_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            CreateNewProject();
        }

        private void OpenCommand_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            OpenProject();
        }

        private void SaveCommand_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            SaveProject(false);
        }

        private void SaveAsCommand_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            SaveProject(true);
        }

        private void ExportMixCommand_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            ExportMix();
        }

        private void AddTrackCommand_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            SaveUndoSnapshot();
            AddTrack();
        }

        private void PlayPauseCommand_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            TogglePlayPause();
        }

        private void StopPlaybackCommand_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            _engine.Stop();
        }

        private void ZoomInCommand_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            ChangeZoom(10d);
        }

        private void ZoomOutCommand_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            ChangeZoom(-10d);
        }

        private void ResetZoomCommand_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            ResetZoom();
        }

        private void CutSelectionCommand_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            ExecuteActiveSelection(trimOutside: false);
        }

        private void TrimSelectionCommand_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            ExecuteActiveSelection(trimOutside: true);
        }

        private void ShowShortcutsCommand_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            MessageBox.Show(GetShortcutsMemo(), "uAudio shortcuts", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void ExecuteActiveSelection(bool trimOutside)
        {
            TrackRowControl? selectedTrack = _activeSelectionTrack;
            if (selectedTrack == null || !selectedTrack.HasUsableSelection)
            {
                return;
            }

            if (trimOutside)
            {
                selectedTrack.TryExecuteTrimFromSelection();
            }
            else
            {
                selectedTrack.TryExecuteCutFromSelection();
            }
        }

        private static string GetShortcutsMemo()
        {
            return string.Join(Environment.NewLine, new[]
            {
                "RU",
                "Ctrl+N — новый проект",
                "Ctrl+O — открыть",
                "Ctrl+S — сохранить",
                "Ctrl+Shift+S — сохранить как",
                "Ctrl+E — экспорт WAV",
                "Ctrl+Z — отменить",
                "Ctrl+T — добавить дорожку",
                "Space — play/pause",
                "Esc — стоп",
                "Ctrl+X — вырезать выделение",
                "Ctrl+Shift+X — обрезать вне выделения",
                "Ctrl++ / Ctrl+- / Ctrl+0 — зум",
                "F1 — горячие клавиши",
                string.Empty,
                "EN",
                "Ctrl+N — new project",
                "Ctrl+O — open",
                "Ctrl+S — save",
                "Ctrl+Shift+S — save as",
                "Ctrl+E — export WAV",
                "Ctrl+Z — undo",
                "Ctrl+T — add track",
                "Space — play/pause",
                "Esc — stop",
                "Ctrl+X — cut selection",
                "Ctrl+Shift+X — trim outside selection",
                "Ctrl++ / Ctrl+- / Ctrl+0 — zoom",
                "F1 — shortcuts"
            });
        }

        private void CreateNewProject()
        {
            if (!PromptSaveChangesIfNeeded())
            {
                return;
            }

            ClearRecoveryState();
            _currentProjectPath = null;
            ApplyProject(new ProjectFileModel());
            CaptureSavedStateBaseline();
        }

        private bool TryRestoreRecoveryOnStartup()
        {
            RecoveryLoadResult? loadResult = RecoveryFileService.TryLoad();
            if (loadResult == null)
            {
                return false;
            }

            RecoveryFileModel recovery = loadResult.Recovery;
            string recoveredProjectName = string.IsNullOrWhiteSpace(recovery.SourceProjectName)
                ? DefaultProjectName
                : recovery.SourceProjectName;

            MessageBoxResult result = MessageBox.Show(
                $"An autosaved recovery was found for \"{recoveredProjectName}\".{Environment.NewLine}{Environment.NewLine}Saved: {recovery.SavedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}{Environment.NewLine}{Environment.NewLine}Do you want to restore it?",
                "Restore Autosave",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
            {
                ClearRecoveryState();
                return false;
            }

            ApplyProject(recovery.Project ?? new ProjectFileModel());
            _currentProjectPath = string.IsNullOrWhiteSpace(recovery.SourceProjectPath)
                ? null
                : NormalizeClipPath(recovery.SourceProjectPath);

            string currentSignature = ProjectFileService.CreateStateSignature(CaptureProjectModel());
            string baselineSignature = string.Empty;

            if (!string.IsNullOrWhiteSpace(_currentProjectPath) && File.Exists(_currentProjectPath))
            {
                try
                {
                    baselineSignature = ProjectFileService.CreateStateSignature(ProjectFileService.Load(_currentProjectPath).Project);
                }
                catch (Exception ex)
                {
                    AppLogger.Warning("Could not load baseline project during recovery restore.", ex);
                    baselineSignature = string.Empty;
                }
            }

            _savedStateSignature = baselineSignature;
            SetProjectDirty(string.IsNullOrWhiteSpace(baselineSignature) || !string.Equals(currentSignature, baselineSignature, StringComparison.Ordinal));
            _lastAutosaveUtc = recovery.SavedUtc.ToLocalTime();
            UpdateAutosaveStatusText();
            ShowMissingFilesWarning(loadResult.MissingFiles);

            return true;
        }

        private void OpenProject()
        {
            if (!PromptSaveChangesIfNeeded())
            {
                return;
            }

            OpenFileDialog dialog = new OpenFileDialog
            {
                Title = "Open project",
                Filter = ProjectFileFilter,
                DefaultExt = ".uaudioproj",
                CheckFileExists = true
            };

            if (dialog.ShowDialog() != true)
            {
                return;
            }

            try
            {
                ProjectLoadResult loadResult = ProjectFileService.Load(dialog.FileName);
                ClearRecoveryState();
                ApplyProject(loadResult.Project);
                _currentProjectPath = IoPath.GetFullPath(dialog.FileName);
                CaptureSavedStateBaseline();
                ShowMissingFilesWarning(loadResult.MissingFiles);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to open project.\n\n{ex.Message}", "Open Project", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private bool SaveProject(bool saveAs)
        {
            string? projectPath = _currentProjectPath;

            if (saveAs || string.IsNullOrWhiteSpace(projectPath))
            {
                SaveFileDialog dialog = new SaveFileDialog
                {
                    Title = saveAs ? "Save project as" : "Save project",
                    Filter = ProjectFileFilter,
                    DefaultExt = ".uaudioproj",
                    AddExtension = true,
                    OverwritePrompt = true,
                    FileName = GetSuggestedProjectFileName()
                };

                if (dialog.ShowDialog() != true)
                {
                    return false;
                }

                projectPath = dialog.FileName;
            }

            try
            {
                ProjectFileService.Save(projectPath, CaptureProjectModel());
                _currentProjectPath = IoPath.GetFullPath(projectPath);
                CaptureSavedStateBaseline();
                ClearRecoveryState();
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to save project.\n\n{ex.Message}", "Save Project", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        private void ExportMix()
        {
            if (!HasAudioContent())
            {
                MessageBox.Show("There are no audio clips to export.", "Export Mix", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            SaveFileDialog dialog = new SaveFileDialog
            {
                Title = "Export final mix",
                Filter = ExportFileFilter,
                DefaultExt = ".wav",
                AddExtension = true,
                OverwritePrompt = true,
                FileName = GetSuggestedExportFileName()
            };

            if (dialog.ShowDialog() != true)
            {
                return;
            }

            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                AudioExportResult result = AudioExportService.ExportWave(CaptureProjectModel(), dialog.FileName);
                Mouse.OverrideCursor = null;

                string message = $"Final mix exported successfully.{Environment.NewLine}{Environment.NewLine}File:{Environment.NewLine}{dialog.FileName}{Environment.NewLine}{Environment.NewLine}Duration: {result.DurationSeconds:F2}s";

                if (result.MissingFiles.Count > 0)
                {
                    string preview = string.Join(Environment.NewLine, result.MissingFiles.Take(5));
                    if (result.MissingFiles.Count > 5)
                    {
                        preview += $"{Environment.NewLine}...";
                    }

                    message += $"{Environment.NewLine}{Environment.NewLine}Some clips were missing and were rendered as silence:{Environment.NewLine}{preview}";
                }

                if (result.InvalidFiles.Count > 0)
                {
                    string preview = string.Join(Environment.NewLine, result.InvalidFiles.Take(5));
                    if (result.InvalidFiles.Count > 5)
                    {
                        preview += $"{Environment.NewLine}...";
                    }

                    message += $"{Environment.NewLine}{Environment.NewLine}Some clips had unsupported/damaged formats and were rendered as silence:{Environment.NewLine}{preview}";
                }

                MessageBox.Show(message, "Export Mix", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Mouse.OverrideCursor = null;
                MessageBox.Show($"Failed to export mix.{Environment.NewLine}{Environment.NewLine}{ex.Message}", "Export Mix", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private bool PromptSaveChangesIfNeeded()
        {
            if (!_isProjectDirty)
            {
                return true;
            }

            string projectName = GetDisplayProjectName();
            MessageBoxResult result = MessageBox.Show(
                $"Save changes to \"{projectName}\" before continuing?",
                "Unsaved Changes",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Cancel)
            {
                return false;
            }

            if (result == MessageBoxResult.Yes)
            {
                return SaveProject(false);
            }

            return true;
        }

        private void ApplyProject(ProjectFileModel project)
        {
            _suppressDirtyTracking = true;

            try
            {
                _engine.Pause();
                ClearSelectionVisualsExcept(null);
                RemoveAllTracks();

                double zoomValue = project.PixelsPerSecond;
                if (ZoomSlider != null)
                {
                    zoomValue = Math.Max(ZoomSlider.Minimum, Math.Min(ZoomSlider.Maximum, zoomValue));
                    ZoomSlider.Value = zoomValue;
                }

                _pixelsPerSecond = zoomValue;
                UpdateZoomValueText();

                if (EditAllTracksCheckBox != null)
                {
                    EditAllTracksCheckBox.IsChecked = project.EditAllTracks;
                }

                foreach (ProjectTrackFileModel trackModel in project.Tracks)
                {
                    TrackRowControl trackControl = CreateTrackControl();
                    trackControl.RestoreFromSnapshot(CreateTrackSnapshot(trackModel));

                    _trackControls.Add(trackControl);

                    if (TracksPanel != null)
                    {
                        TracksPanel.Children.Add(trackControl);
                    }
                }

                RecalculateTrackCounter();
                _engine.Seek(Math.Max(0d, project.CurrentTime));
                UndoManager.Clear();
                CommandManager.InvalidateRequerySuggested();
                UpdateTimelineWidth();
                DrawRuler();
            }
            finally
            {
                _suppressDirtyTracking = false;
            }
        }

        private TrackSnapshot CreateTrackSnapshot(ProjectTrackFileModel trackModel)
        {
            return new TrackSnapshot
            {
                TrackName = string.IsNullOrWhiteSpace(trackModel.TrackName) ? "Track" : trackModel.TrackName,
                Volume = trackModel.Volume,
                IsMuted = trackModel.IsMuted,
                Clips = trackModel.Clips.Select(clip => new AudioClipModel
                {
                    Id = clip.Id == Guid.Empty ? Guid.NewGuid() : clip.Id,
                    FilePath = clip.FilePath ?? string.Empty,
                    StartTime = clip.StartTime,
                    OffsetInFile = clip.OffsetInFile,
                    Duration = clip.Duration
                }).ToList()
            };
        }

        private void RemoveAllTracks()
        {
            foreach (TrackRowControl track in _trackControls.ToList())
            {
                UnwireTrackControl(track);

                if (track.SampleProvider != null)
                {
                    _engine.RemoveTrack(track.SampleProvider);
                }

                if (TracksPanel != null)
                {
                    TracksPanel.Children.Remove(track);
                }
            }

            _trackControls.Clear();
            _activeSelectionTrack = null;
            _trackCounter = 0;
        }

        private ProjectFileModel CaptureProjectModel()
        {
            return new ProjectFileModel
            {
                FormatVersion = AppConstants.ProjectFormatVersion,
                AppName = AppConstants.AppName,
                PixelsPerSecond = _pixelsPerSecond,
                EditAllTracks = EditAllTracksCheckBox != null && EditAllTracksCheckBox.IsChecked == true,
                CurrentTime = _engine.GlobalTime,
                Tracks = _trackControls.Select(track => new ProjectTrackFileModel
                {
                    TrackName = track.TrackName,
                    Volume = track.GetVolume(),
                    IsMuted = track.GetIsMuted(),
                    Clips = track.Clips.Select(clip => new ProjectClipFileModel
                    {
                        Id = clip.Id,
                        FilePath = NormalizeClipPath(clip.FilePath),
                        StartTime = clip.StartTime,
                        OffsetInFile = clip.OffsetInFile,
                        Duration = clip.Duration
                    }).ToList()
                }).ToList()
            };
        }

        private static string NormalizeClipPath(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return string.Empty;
            }

            try
            {
                return IoPath.GetFullPath(filePath);
            }
            catch (Exception ex)
            {
                AppLogger.Warning($"Could not normalize path: {filePath}", ex);
                return filePath;
            }
        }

        private void CaptureSavedStateBaseline()
        {
            _savedStateSignature = ProjectFileService.CreateStateSignature(CaptureProjectModel());
            SetProjectDirty(false);
        }

        private void RefreshDirtyState()
        {
            if (!_uiInitialized || _suppressDirtyTracking)
            {
                return;
            }

            string currentSignature = ProjectFileService.CreateStateSignature(CaptureProjectModel());
            SetProjectDirty(!string.Equals(currentSignature, _savedStateSignature, StringComparison.Ordinal));
        }

        private void SetProjectDirty(bool isDirty)
        {
            _isProjectDirty = isDirty;
            UpdateWindowTitle();
            UpdateProjectPathText();
            UpdateAutosaveStatusText();
        }

        private void UpdateWindowTitle()
        {
            string projectName = GetDisplayProjectName();
            string dirtySuffix = _isProjectDirty ? "*" : string.Empty;
            Title = $"uAudio - {projectName}{dirtySuffix}";
        }

        private void UpdateProjectPathText()
        {
            if (ProjectPathText == null)
            {
                return;
            }

            string projectName = GetDisplayProjectName();
            ProjectPathText.Text = _isProjectDirty ? $"{projectName} (modified)" : projectName;
            ProjectPathText.ToolTip = string.IsNullOrWhiteSpace(_currentProjectPath) ? "Project is not saved yet." : _currentProjectPath;
        }

        private void UpdateAutosaveStatusText()
        {
            if (AutosaveStatusText == null)
            {
                return;
            }

            if (_isAutosaveInProgress)
            {
                AutosaveStatusText.Text = "Autosaving...";
                AutosaveStatusText.Foreground = new SolidColorBrush(Color.FromRgb(220, 190, 110));
                return;
            }

            if (_lastAutosaveUtc.HasValue)
            {
                AutosaveStatusText.Text = $"Autosaved {_lastAutosaveUtc.Value:HH:mm:ss}";
                AutosaveStatusText.Foreground = new SolidColorBrush(Color.FromRgb(127, 191, 127));
                return;
            }

            AutosaveStatusText.Text = _isProjectDirty ? "Autosave pending" : "Autosave idle";
            AutosaveStatusText.Foreground = new SolidColorBrush(Color.FromRgb(127, 191, 127));
        }

        private string GetDisplayProjectName()
        {
            if (string.IsNullOrWhiteSpace(_currentProjectPath))
            {
                return DefaultProjectName;
            }

            return IoPath.GetFileName(_currentProjectPath);
        }

        private string GetSuggestedProjectFileName()
        {
            if (!string.IsNullOrWhiteSpace(_currentProjectPath))
            {
                return IoPath.GetFileName(_currentProjectPath);
            }

            return $"{DefaultProjectName}.uaudioproj";
        }

        private string GetSuggestedExportFileName()
        {
            string baseName = string.IsNullOrWhiteSpace(_currentProjectPath)
                ? DefaultProjectName
                : IoPath.GetFileNameWithoutExtension(_currentProjectPath);

            return $"{baseName}_mix.wav";
        }

        private bool HasAudioContent()
        {
            return _trackControls.Any(track => track.Clips.Any(clip => clip.Duration > 0d));
        }

        private void ClearRecoveryState()
        {
            RecoveryFileService.Clear();
            _lastAutosaveUtc = null;
            UpdateAutosaveStatusText();
        }

        private void ShowMissingFilesWarning(IReadOnlyList<string> missingFiles)
        {
            if (missingFiles == null || missingFiles.Count == 0)
            {
                return;
            }

            string preview = string.Join(Environment.NewLine, missingFiles.Take(5));
            if (missingFiles.Count > 5)
            {
                preview += $"{Environment.NewLine}...";
            }

            MessageBox.Show(
                $"The project was opened, but some audio files are missing:{Environment.NewLine}{Environment.NewLine}{preview}",
                "Missing Audio Files",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        private void UpdateZoomValueText()
        {
            if (ZoomValueText != null)
            {
                ZoomValueText.Text = $"{(int)Math.Round(_pixelsPerSecond)} px/s";
            }
        }

        private double GetMaxDuration()
        {
            double max = 10d;

            foreach (TrackRowControl track in _trackControls)
            {
                foreach (AudioClipModel clip in track.Clips)
                {
                    if (clip.EndTime > max)
                    {
                        max = clip.EndTime;
                    }
                }
            }

            return max;
        }

        private void UpdateTimelineWidth()
        {
            if (!_uiInitialized ||
                TimelineGrid == null ||
                PlayheadCanvas == null ||
                TracksPanel == null ||
                RulerCanvas == null)
            {
                return;
            }

            double totalDuration = GetMaxDuration() + AppConstants.TimelinePaddingSeconds;
            double totalWidth = AppConstants.LeftPanelWidth + (totalDuration * _pixelsPerSecond);

            TimelineGrid.MinWidth = totalWidth;
            PlayheadCanvas.Width = totalWidth;
            PlayheadCanvas.Height = Math.Max(TracksPanel.ActualHeight, 400d);

            foreach (TrackRowControl track in _trackControls)
            {
                track.MinWidth = totalWidth;
            }

            RulerCanvas.Width = totalWidth;
        }

        private void RecalculateTrackCounter()
        {
            int maxTrackNumber = 0;

            foreach (TrackRowControl track in _trackControls)
            {
                string name = track.TrackName;
                if (name.StartsWith("Track ", StringComparison.OrdinalIgnoreCase))
                {
                    string numberPart = name.Substring("Track ".Length);
                    if (int.TryParse(numberPart, out int number) && number > maxTrackNumber)
                    {
                        maxTrackNumber = number;
                    }
                }
            }

            _trackCounter = Math.Max(maxTrackNumber, _trackControls.Count);
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            if (!PromptSaveChangesIfNeeded())
            {
                e.Cancel = true;
                return;
            }

            ClearRecoveryState();
            _playheadTimer.Stop();
            _autosaveTimer.Stop();
            _zoomRedrawTimer.Stop();
            UndoManager.Clear();
            _engine.Dispose();
        }
    }
}
