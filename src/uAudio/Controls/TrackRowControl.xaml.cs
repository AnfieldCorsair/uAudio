using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace uAudio.Controls
{
    public partial class TrackRowControl : UserControl
    {
        // События
        public event EventHandler<float> VolumeChanged;
        public event EventHandler<double> OffsetChanged; // Возвращает секунды
        public event EventHandler Deleted;

        public Guid TrackId { get; set; }
        public string TrackFilePath { get; private set; }

        // --- Настройки масштаба ---
        private const double PixelsPerSecond = 50.0; // 1 секунда = 50 пикселей

        // --- Переменные для перетаскивания ---
        private bool isDragging = false;
        private Point clickPosition;
        private double startLeft;

        public TrackRowControl()
        {
            InitializeComponent();

            // Громкость
            VolumeSlider.ValueChanged += (s, e) => {
                if (MuteCheckBox.IsChecked == false) VolumeChanged?.Invoke(this, (float)VolumeSlider.Value);
            };
            MuteCheckBox.Checked += (s, e) => VolumeChanged?.Invoke(this, 0);
            MuteCheckBox.Unchecked += (s, e) => VolumeChanged?.Invoke(this, (float)VolumeSlider.Value);

            // Удаление
            BtnDelete.Click += (s, e) => Deleted?.Invoke(this, EventArgs.Empty);
        }

        public void SetTrackInfo(string path, TimeSpan duration)
        {
            TrackFilePath = path;
            FileNameText.Text = Path.GetFileName(path);
            ClipNameText.Text = Path.GetFileName(path);

            // Устанавливаем ширину клипа в зависимости от длительности
            double width = duration.TotalSeconds * PixelsPerSecond;
            AudioClipContainer.Width = width;
        }

        // Рисуем волну (теперь через Polygon для заливки)
        public void RenderWaveform(float[] peaks)
        {
            var points = new PointCollection();

            // Низ (центр)
            points.Add(new Point(0, 40));

            // Верхние точки
            for (int i = 0; i < peaks.Length; i++)
            {
                // Растягиваем точки по ширине контейнера
                // X - это процент от ширины (0..1) * РеальнаяШирина
                // Но у нас peaks просто массив.
                // Упростим: просто равномерно распределим точки по X
                /* 
                 * ВНИМАНИЕ: Для сложного зума нужно пересчитывать, 
                 * но для простоты просто нарисуем "рыбу" на весь контейнер
                 */
            }

            // Временный простой вариант отрисовки "на глаз" для UI
            double width = AudioClipContainer.Width;
            double step = width / peaks.Length;

            // Верхняя грань
            for (int i = 0; i < peaks.Length; i++)
            {
                points.Add(new Point(i * step, 40 - (peaks[i] * 40)));
            }
            // Нижняя грань (зеркально, чтобы была волна)
            for (int i = peaks.Length - 1; i >= 0; i--)
            {
                points.Add(new Point(i * step, 40 + (peaks[i] * 40)));
            }

            WaveformPolyline.Points = points;
        }

        // --- DRAG AND DROP LOGIC ---

        private void AudioClip_MouseDown(object sender, MouseButtonEventArgs e)
        {
            isDragging = true;
            clickPosition = e.GetPosition(TimelineCanvas); // Где нажали на канвасе
            startLeft = Canvas.GetLeft(AudioClipContainer); // Где сейчас блок
            AudioClipContainer.CaptureMouse();
            AudioClipContainer.BorderBrush = Brushes.White; // Подсветка
        }

        private void AudioClip_MouseMove(object sender, MouseEventArgs e)
        {
            if (isDragging)
            {
                var currentPos = e.GetPosition(TimelineCanvas);
                var offset = currentPos.X - clickPosition.X;

                var newLeft = startLeft + offset;

                // Не даем утащить в минус (раньше начала)
                if (newLeft < 0) newLeft = 0;

                Canvas.SetLeft(AudioClipContainer, newLeft);
            }
        }

        private void AudioClip_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (isDragging)
            {
                isDragging = false;
                AudioClipContainer.ReleaseMouseCapture();
                AudioClipContainer.BorderBrush = new SolidColorBrush(Color.FromRgb(0, 122, 204));

                // Пересчитываем позицию в секунды
                double finalLeft = Canvas.GetLeft(AudioClipContainer);
                double offsetSeconds = finalLeft / PixelsPerSecond;

                // Сообщаем MainWindow
                OffsetChanged?.Invoke(this, offsetSeconds);
            }
        }
    }
}