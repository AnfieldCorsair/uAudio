using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using uAudio;
using uAudio.Models;

namespace uAudio.Services
{
    public sealed class RecoveryLoadResult
    {
        public RecoveryFileModel Recovery { get; init; } = new RecoveryFileModel();
        public IReadOnlyList<string> MissingFiles { get; init; } = Array.Empty<string>();
    }

    public static class RecoveryFileService
    {
        private static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };

        private static string RecoveryDirectoryPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppConstants.AppName, "Recovery");

        private static string RecoveryFilePath =>
            Path.Combine(RecoveryDirectoryPath, "last-recovery.uaudiorecovery");

        public static void Save(RecoveryFileModel recovery)
        {
            if (recovery == null)
            {
                throw new ArgumentNullException(nameof(recovery));
            }

            Directory.CreateDirectory(RecoveryDirectoryPath);

            RecoveryFileModel persistedRecovery = new RecoveryFileModel
            {
                FormatVersion = Math.Max(recovery.FormatVersion, AppConstants.ProjectFormatVersion),
                AppName = string.IsNullOrWhiteSpace(recovery.AppName) ? AppConstants.AppName : recovery.AppName,
                SavedUtc = DateTime.UtcNow,
                SourceProjectPath = recovery.SourceProjectPath ?? string.Empty,
                SourceProjectName = recovery.SourceProjectName ?? string.Empty,
                Project = recovery.Project ?? new ProjectFileModel()
            };

            string tempPath = RecoveryFilePath + ".tmp";
            string json = JsonSerializer.Serialize(persistedRecovery, SerializerOptions);

            File.WriteAllText(tempPath, json);
            File.Move(tempPath, RecoveryFilePath, true);
        }

        public static RecoveryLoadResult? TryLoad()
        {
            try
            {
                if (!File.Exists(RecoveryFilePath))
                {
                    return null;
                }

                return Load();
            }
            catch (Exception ex)
            {
                AppLogger.Warning("Recovery load failed. The recovery file will be cleared.", ex);
                Clear();
                return null;
            }
        }

        public static RecoveryLoadResult Load()
        {
            if (!File.Exists(RecoveryFilePath))
            {
                throw new FileNotFoundException("Recovery file not found.", RecoveryFilePath);
            }

            string json = File.ReadAllText(RecoveryFilePath);
            RecoveryFileModel? recovery = JsonSerializer.Deserialize<RecoveryFileModel>(json, SerializerOptions);

            if (recovery == null)
            {
                throw new InvalidOperationException("Recovery file is empty or invalid.");
            }

            List<string> missingFiles = (recovery.Project?.Tracks ?? new List<ProjectTrackFileModel>())
                .SelectMany(track => track.Clips ?? new List<ProjectClipFileModel>())
                .Select(clip => NormalizePath(clip.FilePath))
                .Where(path => !string.IsNullOrWhiteSpace(path) && !File.Exists(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new RecoveryLoadResult
            {
                Recovery = recovery,
                MissingFiles = missingFiles
            };
        }

        public static void Clear()
        {
            try
            {
                if (File.Exists(RecoveryFilePath))
                {
                    File.Delete(RecoveryFilePath);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warning("Failed to clear recovery file.", ex);
            }
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            try
            {
                return Path.GetFullPath(path);
            }
            catch (Exception ex)
            {
                AppLogger.Warning($"Could not normalize recovery path: {path}", ex);
                return path.Trim();
            }
        }
    }
}
