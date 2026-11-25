using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NAudio.Wave;

namespace uAudio.Services
{
    public sealed class AudioFormatValidationResult
    {
        public bool IsValid { get; init; }
        public string FilePath { get; init; } = string.Empty;
        public string Message { get; init; } = string.Empty;
        public double DurationSeconds { get; init; }
        public WaveFormat? WaveFormat { get; init; }

        public static AudioFormatValidationResult Valid(string filePath, double durationSeconds, WaveFormat waveFormat)
        {
            return new AudioFormatValidationResult
            {
                IsValid = true,
                FilePath = filePath,
                DurationSeconds = durationSeconds,
                WaveFormat = waveFormat,
                Message = "OK"
            };
        }

        public static AudioFormatValidationResult Invalid(string filePath, string message)
        {
            return new AudioFormatValidationResult
            {
                IsValid = false,
                FilePath = filePath ?? string.Empty,
                Message = message
            };
        }
    }

    public static class AudioFormatValidator
    {
        private static readonly HashSet<string> SupportedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".wav",
            ".mp3",
            ".ogg",
            ".oga",
            ".flac",
            ".aiff",
            ".aif",
            ".wma",
            ".aac",
            ".m4a",
            ".mp4",
            ".m4b",
            ".asf",
            ".opus",
            ".webm",
            ".3gp"
        };

        private static readonly HashSet<string> ProjectExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".uaudioproj",
            ".json"
        };

        public static string AudioFilePattern =>
            "*.wav;*.mp3;*.ogg;*.oga;*.flac;*.aiff;*.aif;*.wma;*.aac;*.m4a;*.mp4;*.m4b;*.asf;*.opus;*.webm;*.3gp";

        public static string OpenFileDialogFilter =>
            $"Audio files ({AudioFilePattern})|{AudioFilePattern}|All files (*.*)|*.*";

        public static string OpenProjectOrAudioDialogFilter =>
            $"Supported files (*.uaudioproj;*.json;{AudioFilePattern})|*.uaudioproj;*.json;{AudioFilePattern}|uAudio Project (*.uaudioproj;*.json)|*.uaudioproj;*.json|Audio files ({AudioFilePattern})|{AudioFilePattern}|All files (*.*)|*.*";

        public static bool IsSupportedExtension(string filePath)
        {
            string extension = Path.GetExtension((filePath ?? string.Empty).Trim().Trim('"'));
            return !string.IsNullOrWhiteSpace(extension) && SupportedExtensions.Contains(extension);
        }

        public static bool IsProjectExtension(string filePath)
        {
            string extension = Path.GetExtension((filePath ?? string.Empty).Trim().Trim('"'));
            return !string.IsNullOrWhiteSpace(extension) && ProjectExtensions.Contains(extension);
        }

        /// <summary>
        /// Resolves dropped or selected paths (including directories) into distinct existing file paths in stable order.
        /// </summary>
        public static List<string> ExpandDroppedPaths(IEnumerable<string>? rawPaths, out bool projectFileDetected, out string? firstProjectPath)
        {
            projectFileDetected = false;
            firstProjectPath = null;
            List<string> results = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (rawPaths == null)
            {
                return results;
            }

            foreach (string raw in rawPaths)
            {
                if (string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }

                string cleaned = raw.Trim().Trim('"');
                string fullPath;
                try
                {
                    fullPath = Path.GetFullPath(cleaned);
                }
                catch
                {
                    continue;
                }

                if (File.Exists(fullPath))
                {
                    if (IsProjectExtension(fullPath))
                    {
                        projectFileDetected = true;
                        firstProjectPath ??= fullPath;
                        continue;
                    }

                    if (seen.Add(fullPath))
                    {
                        results.Add(fullPath);
                    }
                }
                else if (Directory.Exists(fullPath))
                {
                    try
                    {
                        IEnumerable<string> dirFiles = Directory
                            .EnumerateFiles(fullPath, "*.*", SearchOption.AllDirectories)
                            .Where(IsSupportedExtension)
                            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase);

                        foreach (string file in dirFiles)
                        {
                            string normalized = Path.GetFullPath(file);
                            if (seen.Add(normalized))
                            {
                                results.Add(normalized);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warning($"Could not enumerate dropped folder: {fullPath}", ex);
                    }
                }
            }

            return results;
        }

        public static AudioFormatValidationResult ValidateReadable(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return AudioFormatValidationResult.Invalid(filePath, "Audio file path is empty.");
            }

            string cleanedPath = filePath.Trim().Trim('"');
            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(cleanedPath);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                AppLogger.Warning($"Invalid audio path: {filePath}", ex);
                return AudioFormatValidationResult.Invalid(filePath, "Audio file path is invalid.");
            }

            if (!File.Exists(fullPath))
            {
                return AudioFormatValidationResult.Invalid(fullPath, "Audio file was not found.");
            }

            if (!IsSupportedExtension(fullPath))
            {
                return AudioFormatValidationResult.Invalid(
                    fullPath,
                    "Unsupported audio extension. Supported formats: WAV, MP3, OGG, FLAC, AIFF, WMA, AAC, M4A, MP4, OPUS.");
            }

            try
            {
                using AudioSourceReader reader = AudioSourceReader.Open(fullPath);
                double durationSeconds = reader.TotalSeconds;

                if (durationSeconds <= 0d || double.IsNaN(durationSeconds) || double.IsInfinity(durationSeconds))
                {
                    return AudioFormatValidationResult.Invalid(fullPath, "Audio file has no readable duration.");
                }

                if (reader.WaveFormat.SampleRate <= 0 || reader.WaveFormat.Channels <= 0)
                {
                    return AudioFormatValidationResult.Invalid(fullPath, "Audio format has invalid sample rate or channel count.");
                }

                return AudioFormatValidationResult.Valid(fullPath, durationSeconds, reader.WaveFormat);
            }
            catch (UnauthorizedAccessException ex)
            {
                AppLogger.Warning($"No permission to read audio file: {fullPath}", ex);
                return AudioFormatValidationResult.Invalid(fullPath, "No permission to read this audio file.");
            }
            catch (IOException ex)
            {
                AppLogger.Warning($"I/O error while reading audio file: {fullPath}", ex);
                return AudioFormatValidationResult.Invalid(fullPath, $"Audio file could not be read from disk: {ex.Message}");
            }
            catch (InvalidOperationException ex)
            {
                AppLogger.Warning($"Invalid audio format: {fullPath}", ex);
                return AudioFormatValidationResult.Invalid(fullPath, $"Audio format is invalid or damaged: {ex.Message}");
            }
            catch (FormatException ex)
            {
                AppLogger.Warning($"Unsupported audio format: {fullPath}", ex);
                return AudioFormatValidationResult.Invalid(fullPath, $"Audio format is not supported: {ex.Message}");
            }
            catch (NotSupportedException ex)
            {
                AppLogger.Warning($"Unsupported audio file: {fullPath}", ex);
                return AudioFormatValidationResult.Invalid(fullPath, $"Audio format is not supported: {ex.Message}");
            }
            catch (Exception ex)
            {
                AppLogger.Error($"Unexpected error while validating audio file: {fullPath}", ex);
                return AudioFormatValidationResult.Invalid(fullPath, $"Audio file could not be opened: {ex.Message}");
            }
        }
    }
}
