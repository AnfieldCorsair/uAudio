using uAudio.Services;
using Xunit;

namespace uAudio.Tests;

public sealed class TimeFormatServiceTests
{
    [Theory]
    [InlineData(0, "00:00.000")]
    [InlineData(0.063, "00:00.063")]
    [InlineData(65.432, "01:05.432")]
    [InlineData(-1, "00:00.000")]
    public void FormatTimelineTime_UsesStopwatchStyleFormat(double seconds, string expected)
    {
        Assert.Equal(expected, TimeFormatService.FormatTimelineTime(seconds));
    }

    [Fact]
    public void FormatTimelineTime_ShowsHoursWhenNeeded()
    {
        Assert.Equal("01:00:00.000", TimeFormatService.FormatTimelineTime(3600));
    }
}
