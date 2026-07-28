using NAudio.Wave;
using System;
using System.Linq;

namespace uAudio.Services
{
    public static class AudioAnalyzer
    {
        // Метод возвращает массив float (громкость от 0 до 1)
        public static float[] GetWaveform(string filePath, int pointCount = 2000)
        {
            try
            {
                using (var reader = new AudioFileReader(filePath))
                {
                    // Длина одного кусочка в семплах
                    long totalSamples = reader.Length / (reader.WaveFormat.BitsPerSample / 8);
                    int samplesPerPoint = (int)(totalSamples / pointCount);

                    // Если файл очень короткий
                    if (samplesPerPoint < 1) samplesPerPoint = 1;

                    float[] waveform = new float[pointCount];
                    float[] buffer = new float[samplesPerPoint];

                    // Читаем файл кусками
                    for (int i = 0; i < pointCount; i++)
                    {
                        // Читаем кусок аудио
                        int samplesRead = reader.Read(buffer, 0, samplesPerPoint);
                        if (samplesRead == 0) break;

                        // Находим максимальную громкость в этом куске (пик)
                        // Max() может быть медленным, можно оптимизировать, но для MVP пойдет
                        float max = 0;
                        for (int n = 0; n < samplesRead; n++)
                        {
                            float val = Math.Abs(buffer[n]);
                            if (val > max) max = val;
                        }

                        waveform[i] = max;
                    }

                    return waveform;
                }
            }
            catch
            {
                // Если ошибка - возвращаем пустой массив
                return new float[pointCount];
            }
        }
    }
}
