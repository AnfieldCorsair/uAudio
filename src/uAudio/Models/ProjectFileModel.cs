using System;
using System.Collections.Generic;

namespace uAudioEditorPro.Models
{
    public sealed class ProjectFileModel
    {
        public int FormatVersion { get; set; } = 1;
        public string AppName { get; set; } = "uAudio Editor Pro";
        public double PixelsPerSecond { get; set; } = 100d;
        public bool EditAllTracks { get; set; }
        public double CurrentTime { get; set; }
        public List<ProjectTrackFileModel> Tracks { get; set; } = new List<ProjectTrackFileModel>();
        public DateTime SavedUtc { get; set; } = DateTime.UtcNow;
    }

    public sealed class ProjectTrackFileModel
    {
        public string TrackName { get; set; } = string.Empty;
        public float Volume { get; set; } = 1.0f;
        public bool IsMuted { get; set; }
        public List<ProjectClipFileModel> Clips { get; set; } = new List<ProjectClipFileModel>();
    }

    public sealed class ProjectClipFileModel
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string FilePath { get; set; } = string.Empty;
        public bool IsRelativePath { get; set; }
        public double StartTime { get; set; }
        public double OffsetInFile { get; set; }
        public double Duration { get; set; }
    }
}
