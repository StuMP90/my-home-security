using MyHomeSecurity.Core.Cameras;

namespace MyHomeSecurity.Core.Tests;

public sealed class MjpegFrameSplitterTests
{
    private static readonly byte[] FrameA = [0xFF, 0xD8, 1, 2, 3, 0xFF, 0xD9];
    private static readonly byte[] FrameB = [0xFF, 0xD8, 4, 0xFF, 5, 0xFF, 0xD9];

    [Fact]
    public void FindsFramesAmongMultipartHeaders()
    {
        var data = "--boundary\r\nContent-Type: image/jpeg\r\n\r\n"u8.ToArray()
            .Concat(FrameA).Concat("\r\n--boundary\r\n\r\n"u8.ToArray()).Concat(FrameB).ToArray();
        var frames = new List<byte[]>();

        new MjpegFrameSplitter().Push(data, f => frames.Add(f.ToArray()));

        Assert.Equal([FrameA, FrameB], frames);
    }

    [Fact]
    public void HandlesFramesSplitAcrossPushesOneByteAtATime()
    {
        var data = new byte[] { 0, 0xFF }.Concat(FrameA).Concat(new byte[] { 7, 7 }).Concat(FrameB).ToArray();
        var frames = new List<byte[]>();
        var splitter = new MjpegFrameSplitter();

        foreach (var b in data)
            splitter.Push([b], f => frames.Add(f.ToArray()));

        Assert.Equal([FrameA, FrameB], frames);
    }
}
