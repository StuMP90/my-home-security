using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Platform;
using MyHomeSecurity.App.Controls;
using MyHomeSecurity.Core.Cameras;
using MyHomeSecurity.Core.Media;
using MyHomeSecurity.Core.Models;

namespace MyHomeSecurity.App.Tests;

public sealed class VideoViewTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mhs-video-" + Guid.NewGuid().ToString("N"));

    public VideoViewTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [AvaloniaFact]
    public async Task TheLastPictureIsShownAfterTheStreamStops()
    {
        // A stand-in ffmpeg (a shell script, so Linux and macOS only) that sends one red 16×8 frame.
        if (OperatingSystem.IsWindows())
            return;
        var script = Path.Combine(_dir, "ffmpeg");
        File.WriteAllText(script, """
            #!/bin/sh
            case "$*" in *-version*) echo "ffmpeg version 7.1"; exit 0;; esac
            i=0
            while [ $i -lt 128 ]; do printf '\000\000\377\377'; i=$((i+1)); done
            sleep 30
            """);
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        using var proxy = new RtspAuthProxy();
        var camera = new CameraProfile { Name = "Front Door", Host = "192.0.2.1", Username = "admin", Password = "secret" };
        var stream = new CameraStream(camera, new StreamOptions(false, 16, 8, 5, false, false), new FfmpegLocator(script), proxy);
        stream.Start();
        for (var i = 0; i < 100 && stream.FrameNumber == 0; i++)
            await Task.Delay(50);
        var frame = VideoView.LatestFrameOf(stream);
        stream.Dispose();

        Assert.NotNull(frame);
        Assert.Equal(new Avalonia.PixelSize(16, 8), frame.PixelSize);

        // No stream any more: the view draws the saved picture instead of black.
        var view = new VideoView { Placeholder = frame };
        var window = new Window { Width = 64, Height = 32, Content = view };
        window.Show();
        using var shot = window.CaptureRenderedFrame()!;
        using var buffer = shot.Lock();
        var pixel = new byte[4];
        Marshal.Copy(buffer.Address + (16 * buffer.RowBytes) + (32 * 4), pixel, 0, 4);
        var (red, green, blue) = buffer.Format == PixelFormat.Rgba8888
            ? (pixel[0], pixel[1], pixel[2])
            : (pixel[2], pixel[1], pixel[0]);
        Assert.Equal((255, 0, 0), (red, green, blue));
        window.Close();
    }
}
