using NAudio.Wave;
using uAudio.Services;
using Xunit;

namespace uAudio.Tests;

public sealed class AudioFormatValidatorTests
{
    [Theory]
    [InlineData("song.wav")]
    [InlineData("song.mp3")]
    [InlineData("song.ogg")]
    [InlineData("song.oga")]
    [InlineData("song.aiff")]
    [InlineData("song.aif")]
    [InlineData("song.wma")]
    [InlineData("song.aac")]
    [InlineData("song.m4a")]
    [InlineData("song.flac")]
    [InlineData("song.mp4")]
    [InlineData("song.opus")]
    public void IsSupportedExtension_AcceptsKnownAudioTypes(string fileName)
    {
        Assert.True(AudioFormatValidator.IsSupportedExtension(fileName));
    }

    [Theory]
    [InlineData("song.txt")]
    [InlineData("song.exe")]
    [InlineData("song")]
    public void IsSupportedExtension_RejectsUnknownTypes(string fileName)
    {
        Assert.False(AudioFormatValidator.IsSupportedExtension(fileName));
    }

    [Fact]
    public void ValidateReadable_ReturnsFriendlyMessageForMissingFile()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".wav");

        AudioFormatValidationResult result = AudioFormatValidator.ValidateReadable(path);

        Assert.False(result.IsValid);
        Assert.Contains("not found", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateReadable_RejectsUnsupportedExistingFileBeforeNaudioOpen()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".txt");
        File.WriteAllText(path, "not audio");

        try
        {
            AudioFormatValidationResult result = AudioFormatValidator.ValidateReadable(path);

            Assert.False(result.IsValid);
            Assert.Contains("Unsupported", result.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ValidateReadable_OpensWaveFormatExtensible24BitAnd32BitFloatWav()
    {
        string dir = Path.Combine(Path.GetTempPath(), "uaudio-wav-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string wav24Path = Path.Combine(dir, "extensible24 #test.wav");
        string wavFloatPath = Path.Combine(dir, "extensible32f %test.wav");

        try
        {
            var ext24 = new WaveFormatExtensible(48000, 24, 2);
            using (var writer = new WaveFileWriter(wav24Path, ext24))
            {
                byte[] frameBytes = new byte[ext24.AverageBytesPerSecond / 2]; // 0.5 sec
                writer.Write(frameBytes, 0, frameBytes.Length);
            }

            var extFloat = new WaveFormatExtensible(44100, 32, 2);
            using (var writer = new WaveFileWriter(wavFloatPath, extFloat))
            {
                byte[] frameBytes = new byte[extFloat.AverageBytesPerSecond / 2]; // 0.5 sec
                writer.Write(frameBytes, 0, frameBytes.Length);
            }

            AudioFormatValidationResult res24 = AudioFormatValidator.ValidateReadable(wav24Path);
            AudioFormatValidationResult resFloat = AudioFormatValidator.ValidateReadable(wavFloatPath);

            Assert.True(res24.IsValid, res24.Message);
            Assert.InRange(res24.DurationSeconds, 0.45, 0.55);

            Assert.True(resFloat.IsValid, resFloat.Message);
            Assert.InRange(resFloat.DurationSeconds, 0.45, 0.55);

            float[] peaks = AudioAnalyzer.GetPeaks(wav24Path, 64, 0, res24.DurationSeconds);
            Assert.Equal(64, peaks.Length);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ExpandDroppedPaths_ExpandsFoldersAndDetectsProjectFiles()
    {
        string dir = Path.Combine(Path.GetTempPath(), "uaudio-drop-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string subDir = Path.Combine(dir, "Pack");
        Directory.CreateDirectory(subDir);

        string wav1 = Path.Combine(dir, "kick.wav");
        string mp3 = Path.Combine(subDir, "snare.mp3");
        string txt = Path.Combine(subDir, "readme.txt");
        string proj = Path.Combine(dir, "song.uaudioproj");

        File.WriteAllBytes(wav1, new byte[] { 1, 2, 3 });
        File.WriteAllBytes(mp3, new byte[] { 1, 2, 3 });
        File.WriteAllText(txt, "ignore");
        File.WriteAllText(proj, "{}");

        try
        {
            List<string> expanded = AudioFormatValidator.ExpandDroppedPaths(
                new[] { wav1, subDir, proj },
                out bool projectDetected,
                out string? firstProjectPath);

            Assert.True(projectDetected);
            Assert.Equal(Path.GetFullPath(proj), firstProjectPath);
            Assert.Equal(2, expanded.Count);
            Assert.Contains(Path.GetFullPath(wav1), expanded);
            Assert.Contains(Path.GetFullPath(mp3), expanded);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
