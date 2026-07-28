using System;
using System.IO;
using NAudio.Wave;

namespace uAudioEditorPro.Services
{
    public static class AudioAnalyzer
    {
        public static float[] GetPeaks(string filePath, int peakCount, double offsetInFile, double duration)
        {
            int safePeakCount = Math.Max(peakCount, 1);
            float[] peaks = new float[safePeakCount];

            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath) || duration <= 0d)
            {
                return peaks;
            }

            try
            {
                using AudioFileReader reader = new AudioFileReader(filePath);

                double totalSeconds = reader.TotalTime.TotalSeconds;
                if (totalSeconds <= 0d)
                {
                    return peaks;
                }

                double clampedOffset = Math.Max(0d, Math.Min(offsetInFile, totalSeconds));
                double availableDuration = Math.Max(0d, totalSeconds - clampedOffset);
                double clampedDuration = Math.Max(0d, Math.Min(duration, availableDuration));

                if (clampedDuration <= 0d)
                {
                    return peaks;
                }

                reader.CurrentTime = TimeSpan.FromSeconds(clampedOffset);

                int channels = Math.Max(reader.WaveFormat.Channels, 1);
                int sampleRate = Math.Max(reader.WaveFormat.SampleRate, 1);

                long totalFramesToRead = Math.Max(1L, (long)Math.Ceiling(clampedDuration * sampleRate));
                long framesPerPeak = Math.Max(1L, totalFramesToRead / safePeakCount);

                int maxBufferSamples = Math.Max(channels * 4096, 4096);
                float[] buffer = new float[maxBufferSamples];

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

                return peaks;
            }
            catch
            {
                return peaks;
            }
        }

        public static double GetFileDuration(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                return 0d;
            }

            try
            {
                using AudioFileReader reader = new AudioFileReader(filePath);
                return reader.TotalTime.TotalSeconds;
            }
            catch
            {
                return 0d;
            }
        }
    }
}