using System;

namespace uAudio.Services
{
    public static class TimeFormatService
    {
        public static string FormatTimelineTime(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0d)
            {
                seconds = 0d;
            }

            TimeSpan value = TimeSpan.FromSeconds(seconds);

            return value.TotalHours >= 1d
                ? $"{(int)value.TotalHours:D2}:{value.Minutes:D2}:{value.Seconds:D2}.{value.Milliseconds:D3}"
                : $"{value.Minutes:D2}:{value.Seconds:D2}.{value.Milliseconds:D3}";
        }

        public static string FormatRulerLabel(double seconds)
        {
            if (seconds < 0d || double.IsNaN(seconds) || double.IsInfinity(seconds))
            {
                seconds = 0d;
            }

            if (seconds >= 60d)
            {
                int minutes = (int)(seconds / 60d);
                double remainder = seconds - (minutes * 60d);
                return $"{minutes}:{remainder:00.#}";
            }

            return $"{seconds:0.#}s";
        }
    }
}
