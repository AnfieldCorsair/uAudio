using System;
using System.Collections.Concurrent;
using System.IO;
using NAudio.Wave;
using uAudio;

namespace uAudio.Services
{
    public static class AudioAnalyzer
    {
        private sealed class PeakCacheEntry
        {
            public float[] Peaks { get; init; } = Array.Empty<float>();
            public DateTime LastWriteUtc { get; init; }
            public long LengthBytes { get; init; }
        }

        private static readonly ConcurrentDictionary<string, PeakCacheEntry> PeakCache = new ConcurrentDictionary<string, PeakCacheEntry>(StringComparer.OrdinalIgnoreCase);

        public static float[] GetPeaks(string filePath, int peakCount, double offsetInFile, double duration)
        {
            int safePeakCount = Math.Clamp(peakCount, 1, AppConstants.MaxWaveformPeakCount);
            float[] emptyPeaks = new float[safePeakCount];

            if (string.IsNullOrWhiteSpace(filePath) || duration <= 0d)
            {
                return emptyPeaks;
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(filePath);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                AppLogger.Warning($"Waveform skipped for invalid path: {filePath}", ex);
                return emptyPeaks;
            }

            if (!File.Exists(fullPath) || !AudioFormatValidator.IsSupportedExtension(fullPath))
            {
                return emptyPeaks;
            }

            try
            {
                FileInfo fileInfo = new FileInfo(fullPath);
                string cacheKey = string.Join("|",
                    fullPath,
                    fileInfo.LastWriteTimeUtc.Ticks,
                    fileInfo.Length,
                    safePeakCount,
                    Math.Round(offsetInFile, 4),
                    Math.Round(duration, 4));

                if (PeakCache.TryGetValue(cacheKey, out PeakCacheEntry? cached) &&
                    cached.LastWriteUtc == fileInfo.LastWriteTimeUtc &&
                    cached.LengthBytes == fileInfo.Length &&
                    cached.Peaks.Length == safePeakCount)
                {
                    return (float[])cached.Peaks.Clone();
                }

                using AudioFileReader reader = new AudioFileReader(fullPath);

                double totalSeconds = reader.TotalTime.TotalSeconds;
                if (totalSeconds <= 0d)
                {
                    return emptyPeaks;
                }

                double clampedOffset = Math.Max(0d, Math.Min(offsetInFile, totalSeconds));
                double availableDuration = Math.Max(0d, totalSeconds - clampedOffset);
                double clampedDuration = Math.Max(0d, Math.Min(duration, availableDuration));

                if (clampedDuration <= 0d)
                {
                    return emptyPeaks;
                }

                reader.CurrentTime = TimeSpan.FromSeconds(clampedOffset);

                int channels = Math.Max(reader.WaveFormat.Channels, 1);
                int sampleRate = Math.Max(reader.WaveFormat.SampleRate, 1);

                long totalFramesToRead = Math.Max(1L, (long)Math.Ceiling(clampedDuration * sampleRate));
                long framesPerPeak = Math.Max(1L, (long)Math.Ceiling((double)totalFramesToRead / safePeakCount));

                int maxBufferSamples = Math.Max(channels * 4096, 4096);
                float[] buffer = new float[maxBufferSamples];
                float[] peaks = new float[safePeakCount];

                int peakIndex = 0;
                long framesProcessed = 0;

                while (peakIndex < safePeakCount && framesProcessed < totalFramesToRead)
                {
                    long targetFramesForPeak = Math.Min(framesPerPeak, totalFramesToRead - framesProcessed);
                    long peakFramesRead = 0;
                    float peakMax = 0f;

                    while (peakFramesRead < targetFramesForPeak)
                    {
                        long framesLeftForPeak = targetFramesForPeak - peakFramesRead;
                        int samplesToRead = (int)Math.Min(framesLeftForPeak * channels, buffer.Length);

                        int samplesRead = reader.Read(buffer, 0, samplesToRead);
                        if (samplesRead <= 0)
                        {
                            break;
                        }

                        for (int i = 0; i < samplesRead; i++)
                        {
                            float value = Math.Abs(buffer[i]);
                            if (value > peakMax)
                            {
                                peakMax = value;
                            }
                        }

                        long framesRead = samplesRead / channels;
                        peakFramesRead += framesRead;

                        if (framesRead == 0)
                        {
                            break;
                        }
                    }

                    peaks[peakIndex] = peakMax;
                    peakIndex++;
                    framesProcessed += peakFramesRead;

                    if (peakFramesRead == 0)
                    {
                        break;
                    }
                }

                PeakCache[cacheKey] = new PeakCacheEntry
                {
                    Peaks = (float[])peaks.Clone(),
                    LastWriteUtc = fileInfo.LastWriteTimeUtc,
                    LengthBytes = fileInfo.Length
                };

                TrimPeakCacheIfNeeded();

                return peaks;
            }
            catch (UnauthorizedAccessException ex)
            {
                AppLogger.Warning($"No permission while reading peaks: {fullPath}", ex);
                return emptyPeaks;
            }
            catch (IOException ex)
            {
                AppLogger.Warning($"I/O error while reading peaks: {fullPath}", ex);
                return emptyPeaks;
            }
            catch (InvalidOperationException ex)
            {
                AppLogger.Warning($"Invalid audio data while reading peaks: {fullPath}", ex);
                return emptyPeaks;
            }
            catch (NotSupportedException ex)
            {
                AppLogger.Warning($"Unsupported audio data while reading peaks: {fullPath}", ex);
                return emptyPeaks;
            }
            catch (Exception ex)
            {
                AppLogger.Error($"Unexpected waveform error: {fullPath}", ex);
                return emptyPeaks;
            }
        }

        public static double GetFileDuration(string filePath)
        {
            AudioFormatValidationResult validation = AudioFormatValidator.ValidateReadable(filePath);
            return validation.IsValid ? validation.DurationSeconds : 0d;
        }

        public static AudioFormatValidationResult ValidateFile(string filePath)
        {
            return AudioFormatValidator.ValidateReadable(filePath);
        }

        public static void ClearPeakCache()
        {
            PeakCache.Clear();
        }

        private static void TrimPeakCacheIfNeeded()
        {
            const int maxEntries = 512;
            if (PeakCache.Count <= maxEntries)
            {
                return;
            }

            PeakCache.Clear();
        }
    }
}
