using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using uAudio;
using uAudio.Models;

namespace uAudio.Services
{
    public sealed class AudioExportResult
    {
        public double DurationSeconds { get; init; }
        public IReadOnlyList<string> MissingFiles { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> InvalidFiles { get; init; } = Array.Empty<string>();
    }

    public static class AudioExportService
    {
        public static AudioExportResult ExportWave(
            ProjectFileModel project,
            string outputFilePath,
            int sampleRate = AppConstants.DefaultSampleRate,
            int channels = AppConstants.DefaultChannelCount)
        {
            if (project == null)
            {
                throw new ArgumentNullException(nameof(project));
            }

            if (string.IsNullOrWhiteSpace(outputFilePath))
            {
                throw new ArgumentException("Output path is required.", nameof(outputFilePath));
            }

            sampleRate = Math.Max(1, sampleRate);
            channels = Math.Max(1, channels);

            List<ProjectTrackFileModel> tracks = project.Tracks ?? new List<ProjectTrackFileModel>();
            double durationSeconds = GetProjectDuration(project);
            if (durationSeconds <= 0d)
            {
                throw new InvalidOperationException("The project has no audio clips to export.");
            }

            string fullOutputPath = Path.GetFullPath(outputFilePath);
            string? outputDirectory = Path.GetDirectoryName(fullOutputPath);
            if (string.IsNullOrWhiteSpace(outputDirectory))
            {
                throw new InvalidOperationException("Output directory could not be resolved.");
            }

            Directory.CreateDirectory(outputDirectory);

            List<string> missingFiles = tracks
                .SelectMany(track => track.Clips ?? new List<ProjectClipFileModel>())
                .Select(clip => clip.FilePath ?? string.Empty)
                .Where(path => !string.IsNullOrWhiteSpace(path) && !File.Exists(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();

            List<string> invalidFiles = tracks
                .SelectMany(track => track.Clips ?? new List<ProjectClipFileModel>())
                .Select(clip => clip.FilePath ?? string.Empty)
                .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
                .Select(path => AudioFormatValidator.ValidateReadable(path))
                .Where(result => !result.IsValid)
                .Select(result => $"{result.FilePath}: {result.Message}")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (invalidFiles.Count > 0)
            {
                AppLogger.Warning("Export contains invalid source files: " + string.Join("; ", invalidFiles));
            }

            List<TrackSampleProvider> providers = new List<TrackSampleProvider>();

            try
            {
                WaveFormat mixFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
                MixingSampleProvider mixer = new MixingSampleProvider(mixFormat)
                {
                    ReadFully = true
                };

                foreach (ProjectTrackFileModel track in tracks)
                {
                    TrackSampleProvider provider = new TrackSampleProvider(sampleRate, channels)
                    {
                        Volume = track.Volume,
                        IsMuted = track.IsMuted
                    };

                    provider.UpdateClips((track.Clips ?? new List<ProjectClipFileModel>()).Select(clip => new AudioClipModel
                    {
                        Id = clip.Id == Guid.Empty ? Guid.NewGuid() : clip.Id,
                        FilePath = clip.FilePath ?? string.Empty,
                        StartTime = clip.StartTime,
                        OffsetInFile = clip.OffsetInFile,
                        Duration = clip.Duration
                    }).ToList());

                    provider.Seek(0d);
                    providers.Add(provider);
                    mixer.AddMixerInput(provider);
                }

                WaveFormat outputFormat = new WaveFormat(sampleRate, 16, channels);
                IWaveProvider waveProvider = new SampleToWaveProvider16(mixer);

                using WaveFileWriter writer = new WaveFileWriter(fullOutputPath, outputFormat);

                long totalFrames = Math.Max(1L, (long)Math.Ceiling(durationSeconds * sampleRate));
                long totalBytesToWrite = totalFrames * outputFormat.BlockAlign;
                byte[] buffer = new byte[Math.Max(outputFormat.AverageBytesPerSecond, outputFormat.BlockAlign * 4096)];
                long totalBytesWritten = 0L;

                while (totalBytesWritten < totalBytesToWrite)
                {
                    int bytesToRead = (int)Math.Min(buffer.Length, totalBytesToWrite - totalBytesWritten);
                    bytesToRead -= bytesToRead % outputFormat.BlockAlign;

                    if (bytesToRead <= 0)
                    {
                        break;
                    }

                    int bytesRead = waveProvider.Read(buffer, 0, bytesToRead);
                    if (bytesRead <= 0)
                    {
                        AppLogger.Warning($"Export stopped early: source returned 0 bytes after {totalBytesWritten} of {totalBytesToWrite} bytes.");
                        break;
                    }

                    writer.Write(buffer, 0, bytesRead);
                    totalBytesWritten += bytesRead;
                }

                writer.Flush();

                return new AudioExportResult
                {
                    DurationSeconds = durationSeconds,
                    MissingFiles = missingFiles,
                    InvalidFiles = invalidFiles
                };
            }
            catch (IOException ex)
            {
                AppLogger.Error($"I/O error during export to {fullOutputPath}", ex);
                throw;
            }
            catch (UnauthorizedAccessException ex)
            {
                AppLogger.Error($"No permission to export to {fullOutputPath}", ex);
                throw;
            }
            catch (InvalidOperationException ex)
            {
                AppLogger.Error($"Invalid export operation for {fullOutputPath}", ex);
                throw;
            }
            catch (Exception ex)
            {
                AppLogger.Error($"Unexpected export error for {fullOutputPath}", ex);
                throw;
            }
            finally
            {
                foreach (TrackSampleProvider provider in providers)
                {
                    provider.Dispose();
                }
            }
        }

        private static double GetProjectDuration(ProjectFileModel project)
        {
            double maxEndTime = 0d;

            foreach (ProjectTrackFileModel track in project.Tracks ?? new List<ProjectTrackFileModel>())
            {
                foreach (ProjectClipFileModel clip in track.Clips ?? new List<ProjectClipFileModel>())
                {
                    double endTime = clip.StartTime + clip.Duration;
                    if (endTime > maxEndTime)
                    {
                        maxEndTime = endTime;
                    }
                }
            }

            return maxEndTime;
        }
    }
}
