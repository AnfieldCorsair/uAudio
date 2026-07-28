using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using uAudioEditorPro.Models;

namespace uAudioEditorPro.Services
{
    public sealed class ProjectLoadResult
    {
        public ProjectFileModel Project { get; init; } = new ProjectFileModel();
        public IReadOnlyList<string> MissingFiles { get; init; } = Array.Empty<string>();
    }

    public static class ProjectFileService
    {
        private static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };

        public static void Save(string projectFilePath, ProjectFileModel project)
        {
            if (string.IsNullOrWhiteSpace(projectFilePath))
            {
                throw new ArgumentException("Project path is required.", nameof(projectFilePath));
            }

            if (project == null)
            {
                throw new ArgumentNullException(nameof(project));
            }

            string fullProjectPath = Path.GetFullPath(projectFilePath);
            string? projectDirectory = Path.GetDirectoryName(fullProjectPath);

            if (string.IsNullOrWhiteSpace(projectDirectory))
            {
                throw new InvalidOperationException("Project directory could not be resolved.");
            }

            Directory.CreateDirectory(projectDirectory);

            ProjectFileModel persistedProject = CreatePersistedProject(project, projectDirectory);
            persistedProject.SavedUtc = DateTime.UtcNow;

            string json = JsonSerializer.Serialize(persistedProject, SerializerOptions);
            File.WriteAllText(fullProjectPath, json);
        }

        public static ProjectLoadResult Load(string projectFilePath)
        {
            if (string.IsNullOrWhiteSpace(projectFilePath))
            {
                throw new ArgumentException("Project path is required.", nameof(projectFilePath));
            }

            string fullProjectPath = Path.GetFullPath(projectFilePath);
            if (!File.Exists(fullProjectPath))
            {
                throw new FileNotFoundException("Project file not found.", fullProjectPath);
            }

            string json = File.ReadAllText(fullProjectPath);
            ProjectFileModel? persistedProject = JsonSerializer.Deserialize<ProjectFileModel>(json, SerializerOptions);

            if (persistedProject == null)
            {
                throw new InvalidOperationException("Project file is empty or invalid.");
            }

            if (persistedProject.FormatVersion <= 0)
            {
                throw new InvalidOperationException("Unsupported project format.");
            }

            string projectDirectory = Path.GetDirectoryName(fullProjectPath) ?? Directory.GetCurrentDirectory();
            List<string> missingFiles = new List<string>();

            ProjectFileModel resolvedProject = new ProjectFileModel
            {
                FormatVersion = persistedProject.FormatVersion,
                AppName = persistedProject.AppName,
                PixelsPerSecond = persistedProject.PixelsPerSecond,
                EditAllTracks = persistedProject.EditAllTracks,
                CurrentTime = persistedProject.CurrentTime,
                SavedUtc = persistedProject.SavedUtc,
                Tracks = (persistedProject.Tracks ?? new List<ProjectTrackFileModel>()).Select(track => new ProjectTrackFileModel
                {
                    TrackName = track.TrackName,
                    Volume = track.Volume,
                    IsMuted = track.IsMuted,
                    Clips = (track.Clips ?? new List<ProjectClipFileModel>()).Select(clip =>
                    {
                        string resolvedPath = ResolveClipPath(projectDirectory, clip.FilePath, clip.IsRelativePath);
                        if (!string.IsNullOrWhiteSpace(resolvedPath) && !File.Exists(resolvedPath))
                        {
                            missingFiles.Add(resolvedPath);
                        }

                        return new ProjectClipFileModel
                        {
                            Id = clip.Id,
                            FilePath = resolvedPath,
                            IsRelativePath = false,
                            StartTime = clip.StartTime,
                            OffsetInFile = clip.OffsetInFile,
                            Duration = clip.Duration
                        };
                    }).ToList()
                }).ToList()
            };

            return new ProjectLoadResult
            {
                Project = resolvedProject,
                MissingFiles = missingFiles
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToList()
            };
        }

        public static string CreateStateSignature(ProjectFileModel project)
        {
            if (project == null)
            {
                throw new ArgumentNullException(nameof(project));
            }

            ProjectFileModel normalized = new ProjectFileModel
            {
                FormatVersion = project.FormatVersion,
                AppName = project.AppName,
                PixelsPerSecond = Math.Round(project.PixelsPerSecond, 3),
                EditAllTracks = project.EditAllTracks,
                CurrentTime = 0d,
                SavedUtc = DateTime.MinValue,
                Tracks = (project.Tracks ?? new List<ProjectTrackFileModel>()).Select(track => new ProjectTrackFileModel
                {
                    TrackName = track.TrackName ?? string.Empty,
                    Volume = track.Volume,
                    IsMuted = track.IsMuted,
                    Clips = (track.Clips ?? new List<ProjectClipFileModel>()).Select(clip => new ProjectClipFileModel
                    {
                        Id = clip.Id,
                        FilePath = NormalizePathForComparison(clip.FilePath),
                        IsRelativePath = false,
                        StartTime = Math.Round(clip.StartTime, 6),
                        OffsetInFile = Math.Round(clip.OffsetInFile, 6),
                        Duration = Math.Round(clip.Duration, 6)
                    }).ToList()
                }).ToList()
            };

            return JsonSerializer.Serialize(normalized, SerializerOptions);
        }

        private static ProjectFileModel CreatePersistedProject(ProjectFileModel project, string projectDirectory)
        {
            return new ProjectFileModel
            {
                FormatVersion = Math.Max(project.FormatVersion, 1),
                AppName = string.IsNullOrWhiteSpace(project.AppName) ? "uAudio Editor Pro" : project.AppName,
                PixelsPerSecond = project.PixelsPerSecond,
                EditAllTracks = project.EditAllTracks,
                CurrentTime = Math.Max(0d, project.CurrentTime),
                SavedUtc = project.SavedUtc,
                Tracks = (project.Tracks ?? new List<ProjectTrackFileModel>()).Select(track => new ProjectTrackFileModel
                {
                    TrackName = track.TrackName ?? string.Empty,
                    Volume = track.Volume,
                    IsMuted = track.IsMuted,
                    Clips = (track.Clips ?? new List<ProjectClipFileModel>()).Select(clip =>
                    {
                        bool isRelativePath;
                        string storedPath = CreateStoredClipPath(projectDirectory, clip.FilePath, out isRelativePath);

                        return new ProjectClipFileModel
                        {
                            Id = clip.Id,
                            FilePath = storedPath,
                            IsRelativePath = isRelativePath,
                            StartTime = clip.StartTime,
                            OffsetInFile = clip.OffsetInFile,
                            Duration = clip.Duration
                        };
                    }).ToList()
                }).ToList()
            };
        }

        private static string CreateStoredClipPath(string projectDirectory, string clipPath, out bool isRelativePath)
        {
            isRelativePath = false;

            if (string.IsNullOrWhiteSpace(clipPath))
            {
                return string.Empty;
            }

            string fullClipPath = NormalizePathForComparison(clipPath);
            if (!Path.IsPathRooted(fullClipPath))
            {
                return fullClipPath;
            }

            string projectRoot = Path.GetPathRoot(projectDirectory) ?? string.Empty;
            string clipRoot = Path.GetPathRoot(fullClipPath) ?? string.Empty;

            if (!string.Equals(projectRoot, clipRoot, StringComparison.OrdinalIgnoreCase))
            {
                return fullClipPath;
            }

            string relativePath = Path.GetRelativePath(projectDirectory, fullClipPath);
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                return fullClipPath;
            }

            isRelativePath = true;
            return relativePath;
        }

        private static string ResolveClipPath(string projectDirectory, string clipPath, bool isRelativePath)
        {
            if (string.IsNullOrWhiteSpace(clipPath))
            {
                return string.Empty;
            }

            if (isRelativePath)
            {
                return NormalizePathForComparison(Path.Combine(projectDirectory, clipPath));
            }

            return NormalizePathForComparison(clipPath);
        }

        private static string NormalizePathForComparison(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            try
            {
                return Path.GetFullPath(path);
            }
            catch
            {
                return path.Trim();
            }
        }
    }
}
