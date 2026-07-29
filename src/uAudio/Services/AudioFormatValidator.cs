using System;
using System.Collections.Generic;
using System.IO;
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
            ".aiff",
            ".aif",
            ".wma",
            ".aac",
            ".m4a",
            ".flac"
        };

        public static string OpenFileDialogFilter =>
            "Audio files (*.wav;*.mp3;*.aiff;*.aif;*.wma;*.aac;*.m4a;*.flac)|*.wav;*.mp3;*.aiff;*.aif;*.wma;*.aac;*.m4a;*.flac|All files (*.*)|*.*";

        public static bool IsSupportedExtension(string filePath)
        {
            string extension = Path.GetExtension(filePath ?? string.Empty);
            return !string.IsNullOrWhiteSpace(extension) && SupportedExtensions.Contains(extension);
        }

        public static AudioFormatValidationResult ValidateReadable(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return AudioFormatValidationResult.Invalid(filePath, "Audio file path is empty.");
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(filePath);
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
                return AudioFormatValidationResult.Invalid(fullPath, "Unsupported audio extension. Use WAV, MP3, AIFF, WMA, AAC, M4A or FLAC.");
            }

            try
            {
                using AudioFileReader reader = new AudioFileReader(fullPath);
                double durationSeconds = reader.TotalTime.TotalSeconds;

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
                return AudioFormatValidationResult.Invalid(fullPath, "Audio file could not be read from disk.");
            }
            catch (InvalidOperationException ex)
            {
                AppLogger.Warning($"Invalid audio format: {fullPath}", ex);
                return AudioFormatValidationResult.Invalid(fullPath, "Audio format is invalid or damaged.");
            }
            catch (FormatException ex)
            {
                AppLogger.Warning($"Unsupported audio format: {fullPath}", ex);
                return AudioFormatValidationResult.Invalid(fullPath, "Audio format is not supported.");
            }
            catch (NotSupportedException ex)
            {
                AppLogger.Warning($"Unsupported audio file: {fullPath}", ex);
                return AudioFormatValidationResult.Invalid(fullPath, "Audio format is not supported.");
            }
            catch (Exception ex)
            {
                AppLogger.Error($"Unexpected error while validating audio file: {fullPath}", ex);
                return AudioFormatValidationResult.Invalid(fullPath, "Audio file could not be opened.");
            }
        }
    }
}
