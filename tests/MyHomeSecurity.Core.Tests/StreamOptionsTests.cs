using MyHomeSecurity.Core.Cameras;
using MyHomeSecurity.Core.Models;

namespace MyHomeSecurity.Core.Tests;

public sealed class StreamOptionsTests
{
    [Fact]
    public void GridUsesTheSubstreamSmallAndSlow()
    {
        var o = StreamOptions.ForGrid(new AppSettings { GridFps = 99, GridKeyframesOnly = true, HardwareDecoding = false });
        Assert.Equal(new StreamOptions(false, 640, 360, 30, true, false), o);
    }

    [Theory]
    [InlineData(480, 854, 480)]
    [InlineData(720, 1280, 720)]
    [InlineData(1080, 1920, 1080)]
    [InlineData(2160, 3840, 2160)]
    [InlineData(100, 426, 240)]
    public void LiveUsesTheMainStreamAtSixteenByNine(int setting, int width, int height)
    {
        var o = StreamOptions.ForLive(new AppSettings { LiveHeight = setting });
        Assert.True(o.MainStream);
        Assert.Equal((width, height), (o.Width, o.Height));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(15, 15)]
    [InlineData(100, 60)]
    public void LiveFpsZeroMeansTheCamerasOwnRate(int setting, int fps) =>
        Assert.Equal(fps, StreamOptions.ForLive(new AppSettings { LiveFps = setting }).Fps);
}
