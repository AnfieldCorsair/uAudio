using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;
using uAudioEditorPro.Models;
using uAudioEditorPro.Services;
using IoPath = System.IO.Path;

namespace uAudioEditorPro.Controls
{
    public partial class TrackRowControl : UserControl
    {
        private const double ClipHeight = 100d;
        private const double SelectionMinWidthPixels = 3d;
        private const double DragThresholdPixels = 2d;
        private const double DurationEpsilon = 1e-7;

        public List<AudioClipModel> Clips { get; private set; } = new List<AudioClipModel>();

        public TrackSampleProvider? SampleProvider { get; set; }

        public string TrackName { get; private set; } = "Track";

        private double _pixelsPerSecond = 100d;
        private bool _uiInitialized;

        public double PixelsPerSecond
        {
            get => _pixelsPerSecond;
            set
            {
                _pixelsPerSecond = Math.Max(5d, value);

                if (_uiInitialized)
                {
                    RedrawClips();
                }
            }
        }

        private bool _isDragging;
        private bool _dragMoved;
        private bool _dragUndoCaptured;
        private AudioClipModel? _draggedClip;
        private Border? _draggedBorder;
        private double _dragStartMouseX;
        private double _dragOriginalStartTime;

        private bool _isSelecting;
        private double _selectionAnchorTime;
        private double _selectionStartTime;
        private double _selectionEndTime;
        private Rectangle? _selectionRect;
        private bool _hasSelectionVisual;

        public event Action? TrackChanged;
        public event Action<TrackRowControl>? TrackDeleteRequested;
        public event Action? UndoSnapshotRequested;
        public event Action<TrackRowControl, double, double>? CutRequested;
        public event Action<TrackRowControl, double, double>? TrimRequested;
        public event Action<TrackRowControl, double, double, bool>? SelectionPreviewChanged;

        public TrackRowControl()
        {
            InitializeComponent();

            _uiInitialized = true;

            if (VolumeValueText != null)
            {
                VolumeValueText.Text = $"{(int)Math.Round(VolumeSlider.Value * 100d)}%";
            }
        }

        public void SetTrackName(string name)
        {
            TrackName = name ?? "Track";

            if (TrackNameText != null)
            {
                TrackNameText.Text = TrackName;
            }
        }

        public void SyncToProvider()
        {
            if (SampleProvider == null)
            {
                return;
            }

            SampleProvider.UpdateClips(Clips);
            SampleProvider.Volume = VolumeSlider != null ? (float)VolumeSlider.Value : 1.0f;
            SampleProvider.IsMuted = MuteCheckBox != null && MuteCheckBox.IsChecked == true;
        }

        public TrackSnapshot CreateSnapshot()
        {
            return new TrackSnapshot
            {
                TrackName = TrackName,
                Volume = VolumeSlider != null ? (float)VolumeSlider.Value : 1.0f,
                IsMuted = MuteCheckBox != null && MuteCheckBox.IsChecked == true,
                Clips = Clips.Select(c => c.Clone()).ToList()
            };
        }

        public void RestoreFromSnapshot(TrackSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return;
            }

            SetTrackName(snapshot.TrackName);
            Clips = snapshot.Clips.Select(c => c.Clone()).OrderBy(c => c.StartTime).ThenBy(c => c.Id).ToList();

            if (VolumeSlider != null)
            {
                VolumeSlider.Value = snapshot.Volume;
            }

            if (MuteCheckBox != null)
            {
                MuteCheckBox.IsChecked = snapshot.IsMuted;
            }

            SyncToProvider();
            RedrawClips();
        }

        public float GetVolume()
        {
            return VolumeSlider != null ? (float)VolumeSlider.Value : 1.0f;
        }

        public bool GetIsMuted()
        {
            return MuteCheckBox != null && MuteCheckBox.IsChecked == true;
        }

        public void SetVolume(float volume)
        {
            if (VolumeSlider != null)
            {
                VolumeSlider.Value = volume;
            }
        }

        public void SetIsMuted(bool isMuted)
        {
            if (MuteCheckBox != null)
            {
                MuteCheckBox.IsChecked = isMuted;
            }
        }

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (VolumeValueText != null)
            {
                VolumeValueText.Text = $"{(int)Math.Round(e.NewValue * 100d)}%";
            }

            if (SampleProvider != null)
            {
                SampleProvider.Volume = (float)e.NewValue;
            }

            if (_uiInitialized)
            {
                TrackChanged?.Invoke();
            }
        }

        private void MuteCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (SampleProvider != null && MuteCheckBox != null)
            {
                SampleProvider.IsMuted = MuteCheckBox.IsChecked == true;
            }

            if (_uiInitialized)
            {
                TrackChanged?.Invoke();
            }
        }

        private void AddFileButton_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog dialog = new OpenFileDialog
            {
                Title = "Select audio file",
                Filter = "Audio files|*.wav;*.mp3;*.aiff;*.wma;*.aac;*.m4a;*.flac|All files|*.*"
            };

            if (dialog.ShowDialog() != true)
            {
                return;
            }

            double duration = AudioAnalyzer.GetFileDuration(dialog.FileName);
            if (duration <= 0d)
            {
                MessageBox.Show("Не удалось прочитать аудиофайл.", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            UndoSnapshotRequested?.Invoke();

            double startTime = Clips.Count == 0 ? 0d : Clips.Max(c => c.EndTime) + 0.5d;

            AudioClipModel clip = new AudioClipModel
            {
                FilePath = dialog.FileName,
                StartTime = startTime,
                OffsetInFile = 0d,
                Duration = duration
            };

            Clips.Add(clip);
            SortClips();
            SyncToProvider();
            RedrawClips();
            TrackChanged?.Invoke();
        }

        private void DeleteTrackButton_Click(object sender, RoutedEventArgs e)
        {
            UndoSnapshotRequested?.Invoke();
            TrackDeleteRequested?.Invoke(this);
        }

        public void RedrawClips()
        {
            if (!_uiInitialized || ClipCanvas == null)
            {
                return;
            }

            ClipCanvas.Children.Clear();

            foreach (AudioClipModel clip in Clips.OrderBy(c => c.StartTime).ThenBy(c => c.Id))
            {
                double x = clip.StartTime * _pixelsPerSecond;
                double width = Math.Max(1d, clip.Duration * _pixelsPerSecond);
                bool fileExists = !string.IsNullOrWhiteSpace(clip.FilePath) && File.Exists(clip.FilePath);
                Color fillColor = fileExists ? Color.FromRgb(40, 80, 120) : Color.FromRgb(120, 50, 50);
                Color borderColor = fileExists ? Color.FromRgb(80, 140, 200) : Color.FromRgb(220, 100, 100);
                string fileLabel = IoPath.GetFileName(clip.FilePath);

                if (!fileExists)
                {
                    fileLabel = $"[Missing] {fileLabel}";
                }

                Border clipBorder = new Border
                {
                    Width = width,
                    Height = ClipHeight,
                    Background = new SolidColorBrush(fillColor),
                    BorderBrush = new SolidColorBrush(borderColor),
                    BorderThickness = new Thickness(1d),
                    CornerRadius = new CornerRadius(3d),
                    Tag = clip,
                    Cursor = Cursors.Hand,
                    ClipToBounds = true,
                    ToolTip = $"{fileLabel}\nStart: {clip.StartTime:F3}s\nOffset: {clip.OffsetInFile:F3}s\nDuration: {clip.Duration:F3}s"
                };

                Grid contentGrid = new Grid();

                int peakCount = Math.Min(Math.Max((int)(width / 2d), 32), 4000);
                float[] peaks = AudioAnalyzer.GetPeaks(clip.FilePath, peakCount, clip.OffsetInFile, clip.Duration);
                Polygon waveform = CreateWaveformPolygon(peaks, width, ClipHeight);
                contentGrid.Children.Add(waveform);

                TextBlock fileNameText = new TextBlock
                {
                    Text = fileLabel,
                    Foreground = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)),
                    FontSize = 10d,
                    Margin = new Thickness(4d, 2d, 4d, 0d),
                    IsHitTestVisible = false,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };

                contentGrid.Children.Add(fileNameText);
                clipBorder.Child = contentGrid;

                clipBorder.MouseLeftButtonDown += Clip_MouseLeftButtonDown;
                clipBorder.MouseLeftButtonUp += Clip_MouseLeftButtonUp;
                clipBorder.MouseMove += Clip_MouseMove;

                Canvas.SetLeft(clipBorder, x);
                Canvas.SetTop(clipBorder, 5d);
                ClipCanvas.Children.Add(clipBorder);
            }

            RestoreSelectionVisual();
        }

        private Polygon CreateWaveformPolygon(float[] peaks, double width, double height)
        {
            float[] safePeaks = peaks == null || peaks.Length == 0 ? new float[] { 0f, 0f } : peaks;
            double centerY = height / 2d;
            double stepX = safePeaks.Length > 1 ? width / (safePeaks.Length - 1) : width;

            PointCollection points = new PointCollection();

            for (int i = 0; i < safePeaks.Length; i++)
            {
                double x = i * stepX;
                double y = centerY - (Math.Abs(safePeaks[i]) * centerY * 0.9d);
                points.Add(new Point(x, y));
            }

            for (int i = safePeaks.Length - 1; i >= 0; i--)
            {
                double x = i * stepX;
                double y = centerY + (Math.Abs(safePeaks[i]) * centerY * 0.9d);
                points.Add(new Point(x, y));
            }

            return new Polygon
            {
                Fill = new SolidColorBrush(Color.FromArgb(130, 100, 180, 255)),
                Stroke = new SolidColorBrush(Color.FromArgb(210, 120, 200, 255)),
                StrokeThickness = 0.5d,
                IsHitTestVisible = false,
                Points = points
            };
        }

        private void Clip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Border border || border.Tag is not AudioClipModel clip)
            {
                return;
            }

            _isDragging = true;
            _dragMoved = false;
            _dragUndoCaptured = false;
            _draggedClip = clip;
            _draggedBorder = border;
            _dragStartMouseX = e.GetPosition(ClipCanvas).X;
            _dragOriginalStartTime = clip.StartTime;

            border.CaptureMouse();
            e.Handled = true;
        }

        private void Clip_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDragging || _draggedClip == null || _draggedBorder == null || ClipCanvas == null)
            {
                return;
            }

            double currentX = e.GetPosition(ClipCanvas).X;
            double deltaX = currentX - _dragStartMouseX;

            if (!_dragUndoCaptured && Math.Abs(deltaX) >= DragThresholdPixels)
            {
                UndoSnapshotRequested?.Invoke();
                _dragUndoCaptured = true;
            }

            double deltaTime = deltaX / _pixelsPerSecond;
            double newStartTime = Math.Max(0d, _dragOriginalStartTime + deltaTime);

            _draggedClip.StartTime = newStartTime;
            Canvas.SetLeft(_draggedBorder, newStartTime * _pixelsPerSecond);
            _dragMoved = true;
        }

        private void Clip_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isDragging)
            {
                return;
            }

            if (_draggedBorder != null)
            {
                _draggedBorder.ReleaseMouseCapture();
            }

            bool changed = _dragMoved;

            _isDragging = false;
            _dragMoved = false;
            _dragUndoCaptured = false;
            _draggedBorder = null;
            _draggedClip = null;

            if (changed)
            {
                SortClips();
                SyncToProvider();
                RedrawClips();
                TrackChanged?.Invoke();
            }
        }

        private void ClipCanvas_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (ClipCanvas == null)
            {
                return;
            }

            _isSelecting = true;
            _selectionAnchorTime = Math.Max(0d, e.GetPosition(ClipCanvas).X / _pixelsPerSecond);

            SetSelectionVisual(_selectionAnchorTime, _selectionAnchorTime);
            SelectionPreviewChanged?.Invoke(this, _selectionStartTime, _selectionEndTime, true);

            ClipCanvas.CaptureMouse();
            e.Handled = true;
        }

        private void ClipCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isSelecting || ClipCanvas == null)
            {
                return;
            }

            double currentTime = Math.Max(0d, e.GetPosition(ClipCanvas).X / _pixelsPerSecond);
            SetSelectionVisual(_selectionAnchorTime, currentTime);
            SelectionPreviewChanged?.Invoke(this, _selectionStartTime, _selectionEndTime, true);
        }

        private void ClipCanvas_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isSelecting || ClipCanvas == null)
            {
                return;
            }

            _isSelecting = false;
            ClipCanvas.ReleaseMouseCapture();

            if (_selectionRect == null || _selectionRect.Width < SelectionMinWidthPixels)
            {
                FinishSelectionInteraction();
                e.Handled = true;
                return;
            }

            bool actionExecuted = false;

            ContextMenu menu = new ContextMenu();

            MenuItem cutItem = new MenuItem
            {
                Header = "Cut Inside"
            };
            cutItem.Click += (s, args) =>
            {
                actionExecuted = true;
                ExecuteCutFromSelection();
            };

            MenuItem trimItem = new MenuItem
            {
                Header = "Trim Outside"
            };
            trimItem.Click += (s, args) =>
            {
                actionExecuted = true;
                ExecuteTrimFromSelection();
            };

            menu.Items.Add(cutItem);
            menu.Items.Add(trimItem);

            menu.Closed += (s, args) =>
            {
                if (!actionExecuted)
                {
                    FinishSelectionInteraction();
                }
            };

            menu.IsOpen = true;
            e.Handled = true;
        }

        private void ExecuteCutFromSelection()
        {
            double start = _selectionStartTime;
            double end = _selectionEndTime;

            FinishSelectionInteraction();
            UndoSnapshotRequested?.Invoke();

            if (CutRequested != null)
            {
                CutRequested.Invoke(this, start, end);
            }
            else
            {
                ExecuteCut(start, end);
            }
        }

        private void ExecuteTrimFromSelection()
        {
            double start = _selectionStartTime;
            double end = _selectionEndTime;

            FinishSelectionInteraction();
            UndoSnapshotRequested?.Invoke();

            if (TrimRequested != null)
            {
                TrimRequested.Invoke(this, start, end);
            }
            else
            {
                ExecuteTrim(start, end);
            }
        }

        public void ShowSelectionRange(double startTime, double endTime)
        {
            SetSelectionVisual(startTime, endTime);
        }

        public void ClearSelectionVisual()
        {
            _hasSelectionVisual = false;

            if (_selectionRect != null && ClipCanvas != null)
            {
                ClipCanvas.Children.Remove(_selectionRect);
                _selectionRect = null;
            }
        }

        private void FinishSelectionInteraction()
        {
            _isSelecting = false;
            ClearSelectionVisual();
            SelectionPreviewChanged?.Invoke(this, 0d, 0d, false);
        }

        private void SetSelectionVisual(double startTime, double endTime)
        {
            _selectionStartTime = Math.Max(0d, Math.Min(startTime, endTime));
            _selectionEndTime = Math.Max(0d, Math.Max(startTime, endTime));
            _hasSelectionVisual = true;

            EnsureSelectionRectangle();
            UpdateSelectionRectangle();
        }

        private void EnsureSelectionRectangle()
        {
            if (ClipCanvas == null)
            {
                return;
            }

            if (_selectionRect != null)
            {
                if (!ClipCanvas.Children.Contains(_selectionRect))
                {
                    ClipCanvas.Children.Add(_selectionRect);
                }

                return;
            }

            _selectionRect = new Rectangle
            {
                Fill = new SolidColorBrush(Color.FromArgb(60, 50, 120, 255)),
                Stroke = new SolidColorBrush(Color.FromArgb(180, 80, 150, 255)),
                StrokeThickness = 1d,
                IsHitTestVisible = false
            };

            ClipCanvas.Children.Add(_selectionRect);
        }

        private void UpdateSelectionRectangle()
        {
            if (!_hasSelectionVisual || _selectionRect == null || ClipCanvas == null)
            {
                return;
            }

            double left = _selectionStartTime * _pixelsPerSecond;
            double right = _selectionEndTime * _pixelsPerSecond;

            Canvas.SetLeft(_selectionRect, left);
            Canvas.SetTop(_selectionRect, 0d);

            _selectionRect.Width = Math.Max(0d, right - left);
            _selectionRect.Height = Math.Max(ClipCanvas.ActualHeight, ActualHeight);
        }

        private void RestoreSelectionVisual()
        {
            if (!_hasSelectionVisual)
            {
                return;
            }

            EnsureSelectionRectangle();
            UpdateSelectionRectangle();
        }

        public void ExecuteCut(double cutStart, double cutEnd)
        {
            double start = Math.Max(0d, Math.Min(cutStart, cutEnd));
            double end = Math.Max(0d, Math.Max(cutStart, cutEnd));

            if (end - start <= DurationEpsilon)
            {
                return;
            }

            List<AudioClipModel> result = new List<AudioClipModel>();

            foreach (AudioClipModel clip in Clips.OrderBy(c => c.StartTime).ThenBy(c => c.Id))
            {
                double clipStart = clip.StartTime;
                double clipEnd = clip.EndTime;

                if (clipEnd <= start + DurationEpsilon || clipStart >= end - DurationEpsilon)
                {
                    result.Add(clip.Clone());
                    continue;
                }

                double leftStart = clipStart;
                double leftEnd = Math.Min(clipEnd, start);

                if (leftEnd - leftStart > DurationEpsilon)
                {
                    AudioClipModel leftPart = clip.Clone();
                    leftPart.Id = Guid.NewGuid();
                    leftPart.StartTime = leftStart;
                    leftPart.OffsetInFile = clip.OffsetInFile;
                    leftPart.Duration = leftEnd - leftStart;
                    result.Add(leftPart);
                }

                double rightStart = Math.Max(clipStart, end);
                double rightEnd = clipEnd;

                if (rightEnd - rightStart > DurationEpsilon)
                {
                    AudioClipModel rightPart = clip.Clone();
                    rightPart.Id = Guid.NewGuid();
                    rightPart.StartTime = rightStart;
                    rightPart.OffsetInFile = clip.OffsetInFile + (rightStart - clipStart);
                    rightPart.Duration = rightEnd - rightStart;
                    result.Add(rightPart);
                }
            }

            Clips = result
                .Where(c => c.Duration > DurationEpsilon)
                .OrderBy(c => c.StartTime)
                .ThenBy(c => c.Id)
                .ToList();

            SyncToProvider();
            RedrawClips();
            TrackChanged?.Invoke();
        }

        public void ExecuteTrim(double trimStart, double trimEnd)
        {
            double start = Math.Max(0d, Math.Min(trimStart, trimEnd));
            double end = Math.Max(0d, Math.Max(trimStart, trimEnd));

            if (end - start <= DurationEpsilon)
            {
                return;
            }

            List<AudioClipModel> result = new List<AudioClipModel>();

            foreach (AudioClipModel clip in Clips.OrderBy(c => c.StartTime).ThenBy(c => c.Id))
            {
                double intersectionStart = Math.Max(clip.StartTime, start);
                double intersectionEnd = Math.Min(clip.EndTime, end);

                if (intersectionEnd - intersectionStart <= DurationEpsilon)
                {
                    continue;
                }

                AudioClipModel trimmed = clip.Clone();
                trimmed.Id = Guid.NewGuid();
                trimmed.StartTime = intersectionStart;
                trimmed.OffsetInFile = clip.OffsetInFile + (intersectionStart - clip.StartTime);
                trimmed.Duration = intersectionEnd - intersectionStart;

                if (trimmed.Duration > DurationEpsilon)
                {
                    result.Add(trimmed);
                }
            }

            Clips = result
                .OrderBy(c => c.StartTime)
                .ThenBy(c => c.Id)
                .ToList();

            SyncToProvider();
            RedrawClips();
            TrackChanged?.Invoke();
        }

        private void SortClips()
        {
            Clips = Clips
                .OrderBy(c => c.StartTime)
                .ThenBy(c => c.Id)
                .ToList();
        }
    }
}
