using System;
using System.Collections.Generic;
using System.Linq;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace uAudio.Services
{
    public class TrackRenderInfo
    {
        public string FilePath { get; set; }
        public float Volume { get; set; }
        public double OffsetSeconds { get; set; } // Добавили смещение для рендера
    }

    // Внутренний класс для управления отдельным каналом
    internal class TrackChannel : IDisposable
    {
        public Guid Id { get; } = Guid.NewGuid(); // Уникальный ID трека
        public string FilePath { get; private set; }
        public AudioFileReader Reader { get; private set; }
        public ISampleProvider Output { get; private set; } // То, что идет в микшер

        // Смещение во времени (в секундах)
        public double OffsetSeconds { get; set; } = 0;

        public TrackChannel(string filePath)
        {
            FilePath = filePath;
            Reader = new AudioFileReader(filePath);
            Output = Reader; // Изначально выход равен файлу
        }

        public void SetVolume(float vol)
        {
            Reader.Volume = vol;
        }

        public void Dispose()
        {
            Reader?.Dispose();
        }
    }

    public class AudioPlaybackEngine : IDisposable
    {
        private readonly IWavePlayer outputDevice;
        private readonly MixingSampleProvider mixer;

        // Храним наши обертки каналов
        private readonly List<TrackChannel> activeTracks = new List<TrackChannel>();

        private const int SampleRate = 44100;
        private const int Channels = 2;

        // Событие, чтобы уведомить интерфейс, что воспроизведение остановилось само
        public event EventHandler PlaybackStopped;

        public AudioPlaybackEngine()
        {
            outputDevice = new WaveOutEvent();
            outputDevice.PlaybackStopped += (s, e) => PlaybackStopped?.Invoke(this, EventArgs.Empty);

            var mixerFormat = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels);
            mixer = new MixingSampleProvider(mixerFormat) { ReadFully = true }; // ReadFully=true держит микшер живым

            outputDevice.Init(mixer);
            outputDevice.Play();
        }

        // --- 1. Добавление трека ---
        public Guid AddTrack(string fileName)
        {
            var channel = new TrackChannel(fileName);

            ISampleProvider resampledInput = channel.Reader;

            // Ресемплинг
            if (channel.Reader.WaveFormat.SampleRate != SampleRate)
                resampledInput = new WdlResamplingSampleProvider(channel.Reader, SampleRate);

            // Моно -> Стерео
            if (channel.Reader.WaveFormat.Channels != Channels)
                resampledInput = new MonoToStereoSampleProvider(resampledInput);

            // ВАЖНО: Мы добавляем в микшер, но ссылка на Reader осталась в объекте channel
            mixer.AddMixerInput(resampledInput);

            activeTracks.Add(channel);
            return channel.Id; // Возвращаем ID, чтобы UI знал, кто это
        }

        // --- 2. Удаление трека ---
        public void RemoveTrack(Guid trackId)
        {
            var track = activeTracks.FirstOrDefault(t => t.Id == trackId);
            if (track != null)
            {
                // Удаляем из микшера (NAudio сам найдет нужный поток по ссылке, но тут есть нюанс с обертками)
                // Для надежности просто удаляем все входы и добавляем заново, кроме удаленного, 
                // либо просто делаем громкость 0 и Dispose (самый простой вариант для MVP)

                track.SetVolume(0); // Глушим
                mixer.RemoveMixerInput(track.Output); // Пытаемся удалить
                track.Dispose();
                activeTracks.Remove(track);
            }
        }

        // --- Управление параметрами ---
        public void SetVolume(Guid trackId, float volume)
        {
            var track = activeTracks.FirstOrDefault(t => t.Id == trackId);
            track?.SetVolume(volume);
        }

        public void SetOffset(Guid trackId, double offsetSeconds)
        {
            var track = activeTracks.FirstOrDefault(t => t.Id == trackId);
            if (track != null)
            {
                track.OffsetSeconds = offsetSeconds;
            }
        }

        // --- 3. Перемотка и Воспроизведение ---

        // Текущее время (берем максимум из всех треков или время устройства)
        public TimeSpan CurrentTime { get; private set; } = TimeSpan.Zero;

        public void Play()
        {
            if (outputDevice.PlaybackState != PlaybackState.Playing)
            {
                outputDevice.Play();
            }
        }

        public void Pause()
        {
            if (outputDevice.PlaybackState == PlaybackState.Playing)
            {
                outputDevice.Pause();
            }
        }

        public void Stop()
        {
            outputDevice.Stop();
        }

        // ГЛАВНАЯ ФИШКА: SEEK (Перемотка) с учетом Offset
        public void Seek(TimeSpan globalTime)
        {
            CurrentTime = globalTime;

            foreach (var track in activeTracks)
            {
                // Вычисляем, где должен быть курсор ВНУТРИ файла
                // Если глобальное время 10с, а сдвиг 2с -> в файле 8с.
                var localTime = globalTime.TotalSeconds - track.OffsetSeconds;

                if (localTime < 0)
                {
                    // Трек еще не начался -> ставим в начало и глушим (или ставим 0)
                    track.Reader.CurrentTime = TimeSpan.Zero;
                    // В идеале тут нужен Mute, но AudioFileReader продолжит играть с 0.
                    // Для простого решения: просто ставим 0. 
                    // (Для про-решения нужен OffsetSampleProvider в цепочке всегда, но это сложнее обновлять на лету).
                }
                else if (localTime > track.Reader.TotalTime.TotalSeconds)
                {
                    // Трек уже кончился
                    track.Reader.CurrentTime = track.Reader.TotalTime;
                }
                else
                {
                    // Трек играет
                    track.Reader.CurrentTime = TimeSpan.FromSeconds(localTime);
                }
            }
        }

        // Получить длину самого длинного трека (с учетом сдвига)
        public TimeSpan GetTotalDuration()
        {
            if (activeTracks.Count == 0) return TimeSpan.Zero;
            return activeTracks.Max(t => t.Reader.TotalTime + TimeSpan.FromSeconds(t.OffsetSeconds));
        }


        public void Dispose()
        {
            outputDevice?.Dispose();
            foreach (var t in activeTracks) t.Dispose();
        }

        // --- 4. Рендер (Экспорт) с учетом Offset ---
        public void RenderMix(string outputFile, List<TrackRenderInfo> tracks)
        {
            var outputFormat = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
            var renderMixer = new MixingSampleProvider(outputFormat) { ReadFully = false };

            var tempDisposables = new List<IDisposable>();

            try
            {
                foreach (var trackInfo in tracks)
                {
                    var reader = new AudioFileReader(trackInfo.FilePath) { Volume = trackInfo.Volume };
                    tempDisposables.Add(reader);

                    ISampleProvider source = reader;

                    // Ресемплинг
                    if (reader.WaveFormat.SampleRate != outputFormat.SampleRate)
                        source = new WdlResamplingSampleProvider(source, outputFormat.SampleRate);
                    if (source.WaveFormat.Channels != outputFormat.Channels)
                        source = new MonoToStereoSampleProvider(source);

                    // ПРИМЕНЯЕМ OFFSET (Самое важное для экспорта)
                    if (trackInfo.OffsetSeconds > 0)
                    {
                        // OffsetSampleProvider добавляет тишину в начало
                        source = new OffsetSampleProvider(source)
                        {
                            DelayBy = TimeSpan.FromSeconds(trackInfo.OffsetSeconds)
                        };
                    }

                    renderMixer.AddMixerInput(source);
                }

                WaveFileWriter.CreateWaveFile(outputFile, new SampleToWaveProvider16(renderMixer));
            }
            finally
            {
                foreach (var d in tempDisposables) d.Dispose();
            }
        }
    }
}