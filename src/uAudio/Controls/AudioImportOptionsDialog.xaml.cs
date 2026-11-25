using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using uAudio.Services;

namespace uAudio.Controls
{
    public sealed class ImportTrackTargetOption
    {
        public int ExistingTrackIndex { get; init; } = -1;
        public bool CreateNewTrack { get; init; }
        public string DisplayName { get; init; } = string.Empty;

        public override string ToString() => DisplayName;
    }

    public sealed class ValidatedAudioImportItem
    {
        public string FilePath { get; init; } = string.Empty;
        public double DurationSeconds { get; init; }
    }

    public sealed class AudioImportDecision
    {
        public string FilePath { get; init; } = string.Empty;
        public double DurationSeconds { get; init; }
        public int ExistingTrackIndex { get; init; } = -1;
        public bool CreateDedicatedNewTrack { get; init; }
        public bool ShareSingleNewTrack { get; init; }
    }

    public sealed class AudioImportPlan
    {
        public List<AudioImportDecision> Decisions { get; init; } = new List<AudioImportDecision>();
        public double? PreferredStartTime { get; init; }
    }

    public sealed class FileImportAssignmentRow : INotifyPropertyChanged
    {
        private ImportTrackTargetOption? _selectedTarget;
        private bool _isCustomEnabled;

        public string FilePath { get; init; } = string.Empty;
        public string FileName => Path.GetFileName(FilePath);
        public double DurationSeconds { get; init; }
        public string DurationLabel => $"Длительность: {TimeFormatService.FormatTimelineTime(DurationSeconds)}";
        public IReadOnlyList<ImportTrackTargetOption> AvailableTargets { get; init; } = Array.Empty<ImportTrackTargetOption>();

        public ImportTrackTargetOption? SelectedTarget
        {
            get => _selectedTarget;
            set
            {
                if (!ReferenceEquals(_selectedTarget, value))
                {
                    _selectedTarget = value;
                    OnPropertyChanged(nameof(SelectedTarget));
                }
            }
        }

        public bool IsCustomEnabled
        {
            get => _isCustomEnabled;
            set
            {
                if (_isCustomEnabled != value)
                {
                    _isCustomEnabled = value;
                    OnPropertyChanged(nameof(IsCustomEnabled));
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public partial class AudioImportOptionsDialog : Window
    {
        private readonly List<ImportTrackTargetOption> _targetOptions;
        private readonly ImportTrackTargetOption _newTrackOption;
        private readonly List<FileImportAssignmentRow> _rows;
        private readonly double? _dropTimelineTime;
        private readonly bool _isUiReady;

        public AudioImportPlan? ResultPlan { get; private set; }

        public AudioImportOptionsDialog(
            IReadOnlyList<ValidatedAudioImportItem> items,
            IReadOnlyList<string> existingTrackNames,
            int preferredTrackIndex = -1,
            double? dropTimelineTime = null)
        {
            if (items == null || items.Count == 0)
            {
                throw new ArgumentException("At least one audio item is required.", nameof(items));
            }

            InitializeComponent();

            _dropTimelineTime = dropTimelineTime;
            _targetOptions = new List<ImportTrackTargetOption>();

            if (existingTrackNames != null)
            {
                for (int i = 0; i < existingTrackNames.Count; i++)
                {
                    string label = string.IsNullOrWhiteSpace(existingTrackNames[i])
                        ? $"Track {i + 1}"
                        : existingTrackNames[i];

                    _targetOptions.Add(new ImportTrackTargetOption
                    {
                        ExistingTrackIndex = i,
                        CreateNewTrack = false,
                        DisplayName = $"{i + 1}. {label}"
                    });
                }
            }

            _newTrackOption = new ImportTrackTargetOption
            {
                ExistingTrackIndex = -1,
                CreateNewTrack = true,
                DisplayName = "+ Создать новую дорожку"
            };
            _targetOptions.Add(_newTrackOption);

            ImportTrackTargetOption defaultTarget = (preferredTrackIndex >= 0 && preferredTrackIndex < _targetOptions.Count - 1)
                ? _targetOptions[preferredTrackIndex]
                : (_targetOptions.Count > 1 ? _targetOptions[0] : _newTrackOption);

            SingleTargetComboBox.ItemsSource = _targetOptions;
            SingleTargetComboBox.DisplayMemberPath = nameof(ImportTrackTargetOption.DisplayName);
            SingleTargetComboBox.SelectedItem = defaultTarget;

            _rows = items.Select(item => new FileImportAssignmentRow
            {
                FilePath = item.FilePath,
                DurationSeconds = item.DurationSeconds,
                AvailableTargets = _targetOptions,
                SelectedTarget = defaultTarget,
                IsCustomEnabled = false
            }).ToList();

            FileAssignmentsItemsControl.ItemsSource = _rows;

            if (items.Count == 1)
            {
                HeaderText.Text = $"Куда добавить файл «{Path.GetFileName(items[0].FilePath)}»?";
                SameTargetRadio.Content = "Добавить в выбранную дорожку";
                SeparateNewTracksRadio.Content = "Создать новую дорожку для этого звука";
                PerFileCustomRadio.Visibility = Visibility.Collapsed;
                PerFileListBorder.Visibility = Visibility.Collapsed;
            }
            else
            {
                HeaderText.Text = $"Выбрано звуков: {items.Count}. Выберите способ добавления:";
                SameTargetRadio.Content = "Добавить все звуки в одну дорожку (последовательно)";
                SeparateNewTracksRadio.Content = "Создать новую дорожку для каждого выбранного звука";
            }

            if (dropTimelineTime.HasValue)
            {
                PlaceAtDropPositionCheckBox.Visibility = Visibility.Visible;
                PlaceAtDropPositionCheckBox.IsChecked = true;
                PlaceAtDropPositionCheckBox.Content =
                    $"Разместить начиная с позиции {TimeFormatService.FormatTimelineTime(dropTimelineTime.Value)} на таймлайне";
            }

            _isUiReady = true;
            SyncUiForSelectedMode();
        }

        private void ModeRadio_Checked(object sender, RoutedEventArgs e)
        {
            if (!_isUiReady)
            {
                return;
            }

            SyncUiForSelectedMode();
        }

        private void SingleTargetComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (!_isUiReady)
            {
                return;
            }

            SyncUiForSelectedMode();
        }

        private void SyncUiForSelectedMode()
        {
            bool isSingleTarget = SameTargetRadio.IsChecked == true;
            bool isSeparateTracks = SeparateNewTracksRadio.IsChecked == true;
            bool isCustom = PerFileCustomRadio.IsChecked == true;

            SingleTargetSelectorPanel.IsEnabled = isSingleTarget;
            SingleTargetComboBox.IsEnabled = isSingleTarget;

            foreach (FileImportAssignmentRow row in _rows)
            {
                row.IsCustomEnabled = isCustom;
                if (isSeparateTracks)
                {
                    row.SelectedTarget = _newTrackOption;
                }
                else if (isSingleTarget && SingleTargetComboBox.SelectedItem is ImportTrackTargetOption singleOpt)
                {
                    row.SelectedTarget = singleOpt;
                }
            }
        }

        private void ConfirmButton_Click(object sender, RoutedEventArgs e)
        {
            bool isSingleTarget = SameTargetRadio.IsChecked == true;
            bool isSeparateTracks = SeparateNewTracksRadio.IsChecked == true;

            List<AudioImportDecision> decisions = new List<AudioImportDecision>(_rows.Count);
            ImportTrackTargetOption singleTarget = (SingleTargetComboBox.SelectedItem as ImportTrackTargetOption) ?? _newTrackOption;

            foreach (FileImportAssignmentRow row in _rows)
            {
                if (isSeparateTracks)
                {
                    decisions.Add(new AudioImportDecision
                    {
                        FilePath = row.FilePath,
                        DurationSeconds = row.DurationSeconds,
                        ExistingTrackIndex = -1,
                        CreateDedicatedNewTrack = true,
                        ShareSingleNewTrack = false
                    });
                }
                else if (isSingleTarget)
                {
                    decisions.Add(new AudioImportDecision
                    {
                        FilePath = row.FilePath,
                        DurationSeconds = row.DurationSeconds,
                        ExistingTrackIndex = singleTarget.CreateNewTrack ? -1 : singleTarget.ExistingTrackIndex,
                        CreateDedicatedNewTrack = false,
                        ShareSingleNewTrack = singleTarget.CreateNewTrack
                    });
                }
                else
                {
                    ImportTrackTargetOption chosen = row.SelectedTarget ?? _newTrackOption;
                    decisions.Add(new AudioImportDecision
                    {
                        FilePath = row.FilePath,
                        DurationSeconds = row.DurationSeconds,
                        ExistingTrackIndex = chosen.CreateNewTrack ? -1 : chosen.ExistingTrackIndex,
                        CreateDedicatedNewTrack = chosen.CreateNewTrack,
                        ShareSingleNewTrack = false
                    });
                }
            }

            double? startTime = (_dropTimelineTime.HasValue && PlaceAtDropPositionCheckBox.IsChecked == true)
                ? Math.Max(0d, _dropTimelineTime.Value)
                : null;

            ResultPlan = new AudioImportPlan
            {
                Decisions = decisions,
                PreferredStartTime = startTime
            };

            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
