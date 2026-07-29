using uAudio.Services;
using Xunit;

namespace uAudio.Tests;

public sealed class AudioFormatValidatorTests
{
    [Theory]
    [InlineData("song.wav")]
    [InlineData("song.mp3")]
    [InlineData("song.aiff")]
    [InlineData("song.aif")]
    [InlineData("song.wma")]
    [InlineData("song.aac")]
    [InlineData("song.m4a")]
    [InlineData("song.flac")]
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
}
