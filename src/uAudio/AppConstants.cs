namespace uAudio
{
    /// <summary>
    /// Shared product and audio defaults. Keeping these values named avoids drift and magic numbers.
    /// </summary>
    public static class AppConstants
    {
        public const string AppName = "uAudio";
        public const int ProjectFormatVersion = 1;

        public const int DefaultSampleRate = 44100;
        public const int DefaultChannelCount = 2;

        public const double DefaultPixelsPerSecond = 100d;
        public const double MinPixelsPerSecond = 10d;
        public const double MaxPixelsPerSecond = 500d;
        public const double TimelinePaddingSeconds = 30d;
        public const double LeftPanelWidth = 160d;

        public const double AutosaveIntervalSeconds = 30d;
        public const int PlayheadRefreshMilliseconds = 30;
        public const int ZoomRedrawDebounceMilliseconds = 80;

        public const float DefaultVolume = 1.0f;
        public const float MinVolume = 0.0f;
        public const float MaxVolume = 1.0f;

        public const int MaxUndoLevels = 50;
        public const int MaxWaveformPeakCount = 1200;
        public const int MinWaveformPeakCount = 32;

        /// <summary>
        /// One 44.1 kHz sample. Used only for comparing timeline values, not for trimming UI precision.
        /// </summary>
        public const double TimeComparisonEpsilonSeconds = 1d / DefaultSampleRate;
    }
}
