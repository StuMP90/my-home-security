using MyHomeSecurity.Core.Cameras;
using MyHomeSecurity.Core.Media;
using MyHomeSecurity.Core.Models;

namespace MyHomeSecurity.Core.Tests;

public sealed class CameraStreamTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "myhomesecurity-stream-" + Guid.NewGuid().ToString("N"));

    public CameraStreamTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void RefusedLoginIsNotRetried()
    {
        // A stand-in ffmpeg (a shell script, so Linux and macOS only) that fails the way ffmpeg
        // does when the camera answers 401, and counts how often it was started.
        if (OperatingSystem.IsWindows())
            return;
        var runs = Path.Combine(_dir, "runs");
        var script = Path.Combine(_dir, "ffmpeg");
        File.WriteAllText(script, $"""
            #!/bin/sh
            case "$*" in *-version*) echo "ffmpeg version 7.1"; exit 0;; esac
            echo run >> '{runs}'
            echo "method DESCRIBE failed: 401 Unauthorized" >&2
            exit 1
            """);
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        using var proxy = new RtspAuthProxy();
        var camera = new CameraProfile { Name = "Front Door", Host = "192.0.2.1", Username = "admin", Password = "secret" };
        using var stream = new CameraStream(camera, StreamOptions.ForGrid(new AppSettings()), new FfmpegLocator(script), proxy);
        stream.Start();

        // Long enough for two retries if it were retrying (2s, then 4s).
        Thread.Sleep(TimeSpan.FromSeconds(7));
        Assert.Equal(CameraStreamState.Error, stream.State);
        Assert.StartsWith(CameraStream.LoginRefused, stream.StatusMessage);
        Assert.Single(File.ReadAllLines(runs));
    }
}
