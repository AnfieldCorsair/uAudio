using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Linq; // Важно
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using uAudio.Controls;
using uAudio.Services;

namespace uAudio
{
    public partial class MainWindow : Window
    {
        private AudioPlaybackEngine audioEngine;
        private DispatcherTimer uiTimer;
        private const double PixelsPerSecond = 50.0;
        private const double LeftPanelWidth = 150.0; // Ширина панели управления треком

        // Текущее время воспроизведения (в секундах)
        private double currentPlayTime = 0;

        public MainWindow()
        {
            InitializeComponent();
            audioEngine = new AudioPlaybackEngine();
            audioEngine.PlaybackStopped += (s, e) => StopPlayback();

            uiTimer = new DispatcherTimer();
            uiTimer.Interval = TimeSpan.FromMilliseconds(30); // ~30 FPS
            uiTimer.Tick += UiTimer_Tick;
        }

        private void UiTimer_Tick(object sender, EventArgs e)
        {
            
            currentPlayTime += 0.030;
            UpdateCursor();

            // ХАК ДЛЯ ТИШИНЫ
            foreach (TrackRowControl tc in TracksContainer.Children)
            {
                // Получаем текущий сдвиг в секундах
                double offset = Canvas.GetLeft(tc.AudioClipContainer) / PixelsPerSecond;

                // Если курсор еще не дошел до трека
                if (currentPlayTime < offset)
                {
                    // Глушим движок (не слайдер UI, а именно звук)
                    audioEngine.SetVolume(tc.TrackId, 0);
                }
                else
                {
                    // Возвращаем громкость как на слайдере
                    // (учти Mute чекбокс)
                    float vol = (float)tc.VolumeSlider.Value;
                    if (tc.MuteCheckBox.IsChecked == true) vol = 0;
                    audioEngine.SetVolume(tc.TrackId, vol);
                }
            }
        }

       

        private void UpdateCursor()
        {
            // Форматируем время
            var ts = TimeSpan.FromSeconds(currentPlayTime);
            TxtGlobalTimer.Text = ts.ToString(@"mm\:ss\.fff");

            // Двигаем красную палку
            // Позиция X = (Время * Пиксели) + ШиринаЛевойПанели
            double xPos = (currentPlayTime * PixelsPerSecond) + LeftPanelWidth;

            PlayheadLine.X1 = xPos;
            PlayheadLine.X2 = xPos;

            // Если курсор уехал за экран, можно подвинуть скролл (автоскролл)
            if (xPos > MainScrollViewer.HorizontalOffset + MainScrollViewer.ViewportWidth)
            {
                MainScrollViewer.ScrollToHorizontalOffset(xPos - 100);
            }
        }

        private async void BtnAddTrack_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Filter = "Audio|*.mp3;*.wav;*.wma" };
            if (dlg.ShowDialog() == true)
            {
                try
                {
                    var trackId = audioEngine.AddTrack(dlg.FileName);

                    // Получаем длительность файла (грубо, открыв Reader еще раз, или прокинув из движка)
                    // Давай просто откроем Reader временно для инфо
                    TimeSpan duration;
                    using (var reader = new NAudio.Wave.AudioFileReader(dlg.FileName)) duration = reader.TotalTime;

                    var tc = new TrackRowControl();
                    tc.SetTrackInfo(dlg.FileName, duration);
                    tc.TrackId = trackId;

                    // Привязки
                    tc.VolumeChanged += (s, vol) => audioEngine.SetVolume(trackId, vol);
                    tc.Deleted += (s, ev) => {
                        audioEngine.RemoveTrack(trackId);
                        TracksContainer.Children.Remove(tc);
                    };

                    // ГЛАВНОЕ: Когда юзер подвинул блок
                    tc.OffsetChanged += (s, offsetSeconds) => {
                        audioEngine.SetOffset(trackId, offsetSeconds);
                    };

                    TracksContainer.Children.Add(tc);

                    // Анализ волны
                    var waveform = await Task.Run(() => AudioAnalyzer.GetWaveform(dlg.FileName, 500)); // 500 точек для скорости
                    tc.RenderWaveform(waveform);
                }
                catch (Exception ex) { MessageBox.Show(ex.Message); }
            }
        }

        private void BtnPlay_Click(object sender, RoutedEventArgs e)
        {
            // Если нажали плей, начинаем с того места, где остановились (или с 0)
            // Важно: нужно сказать движку "Seek" на текущее время currentPlayTime
            audioEngine.Seek(TimeSpan.FromSeconds(currentPlayTime));
            audioEngine.Play();
            uiTimer.Start();
        }

        private void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            StopPlayback();
            // Сброс в начало при стопе (как в плеерах) или пауза?
            // Давай сделаем сброс в 0 для стопа.
            currentPlayTime = 0;
            UpdateCursor();
        }

        private void StopPlayback()
        {
            audioEngine.Stop();
            uiTimer.Stop();
        }

        private void BtnExport_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog { Filter = "WAV|*.wav", FileName = "Mix.wav" };
            if (dlg.ShowDialog() == true)
            {
                var list = new List<TrackRenderInfo>();
                foreach (TrackRowControl tc in TracksContainer.Children)
                {
                    // Вычисляем офсет из позиции на экране
                    double left = Canvas.GetLeft(tc.AudioClipContainer); // Относительно канваса трека
                    double offset = left / PixelsPerSecond;

                    float vol = (float)tc.VolumeSlider.Value;
                    if (tc.MuteCheckBox.IsChecked == true) vol = 0;

                    list.Add(new TrackRenderInfo
                    {
                        FilePath = tc.TrackFilePath,
                        Volume = vol,
                        OffsetSeconds = offset
                    });
                }

                audioEngine.RenderMix(dlg.FileName, list);
                MessageBox.Show("Saved!");
            }
        }
    }
}