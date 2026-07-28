using System;

namespace uAudioEditorPro.Models
{
    public sealed class RecoveryFileModel
    {
        public int FormatVersion { get; set; } = 1;
        public string AppName { get; set; } = "uAudio Editor Pro";
        public DateTime SavedUtc { get; set; } = DateTime.UtcNow;
        public string SourceProjectPath { get; set; } = string.Empty;
        public string SourceProjectName { get; set; } = string.Empty;
        public ProjectFileModel Project { get; set; } = new ProjectFileModel();
    }
}
