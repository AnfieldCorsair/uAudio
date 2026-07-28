using System;

namespace uAudioEditorPro.Models
{
    public sealed class AudioClipModel
    {
        public Guid Id { get; set; }
        public string FilePath { get; set; }
        public double StartTime { get; set; }
        public double OffsetInFile { get; set; }
        public double Duration { get; set; }

        public AudioClipModel()
        {
            Id = Guid.NewGuid();
            FilePath = string.Empty;
            StartTime = 0d;
            OffsetInFile = 0d;
            Duration = 0d;
        }

        public AudioClipModel(string filePath, double startTime, double offsetInFile, double duration)
        {
            Id = Guid.NewGuid();
            FilePath = filePath ?? string.Empty;
            StartTime = startTime;
            OffsetInFile = offsetInFile;
            Duration = duration;
        }

        public double EndTime => StartTime + Duration;

        public AudioClipModel Clone()
        {
            return new AudioClipModel
            {
                Id = Id,
                FilePath = FilePath,
                StartTime = StartTime,
                OffsetInFile = OffsetInFile,
                Duration = Duration
            };
        }

        public override string ToString()
        {
            return $"Clip[{Id.ToString().Substring(0, 8)}] {FilePath} Start={StartTime:F3}s Offset={OffsetInFile:F3}s Duration={Duration:F3}s";
        }
    }
}