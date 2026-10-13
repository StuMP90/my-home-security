using MyHomeSecurity.Core.Cameras;
using MyHomeSecurity.Core.Models;

namespace MyHomeSecurity.Core.Tests;

public sealed class CapturesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mhs-captures-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, true);
    }

    [Theory]
    [InlineData("Front door", "Front door")]
    [InlineData("Garage/Side: 2?", "Garage_Side_ 2_")]
    [InlineData(" ..  ", "camera")]
    [InlineData("..hidden.", "hidden")]
    public void MakesNamesSafeForFiles(string name, string expected) => Assert.Equal(expected, Captures.SafeName(name));

    [Fact]
    public void NeverReusesAnExistingFile()
    {
        var camera = new CameraProfile { Name = "Porch", Host = "192.168.1.20" };
        var now = new DateTime(2026, 3, 4, 5, 6, 7);

        var first = Captures.NewPath(_dir, camera, ".jpg", now);
        File.WriteAllText(first, "");
        var second = Captures.NewPath(_dir, camera, ".jpg", now);
        File.WriteAllText(second, "");
        var third = Captures.NewPath(_dir, camera, ".jpg", now);

        Assert.Equal("Porch-20260304-050607.jpg", Path.GetFileName(first));
        Assert.Equal("Porch-20260304-050607-2.jpg", Path.GetFileName(second));
        Assert.Equal("Porch-20260304-050607-3.jpg", Path.GetFileName(third));
    }

    [Fact]
    public void UnnamedCamerasUseTheirAddress()
    {
        var camera = new CameraProfile { Host = "192.168.1.20" };
        Assert.StartsWith("192.168.1.20-", Path.GetFileName(Captures.NewPath(_dir, camera, ".mp4")));
    }
}
