using System;
using uAudio;

namespace uAudio.Models
{
    public sealed class RecoveryFileModel
    {
        public int FormatVersion { get; set; } = AppConstants.ProjectFormatVersion;
        public string AppName { get; set; } = AppConstants.AppName;
        public DateTime SavedUtc { get; set; } = DateTime.UtcNow;
        public string SourceProjectPath { get; set; } = string.Empty;
        public string SourceProjectName { get; set; } = string.Empty;
        public ProjectFileModel Project { get; set; } = new ProjectFileModel();
    }
}
