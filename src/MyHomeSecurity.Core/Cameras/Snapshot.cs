using System.ComponentModel;
using MyHomeSecurity.Core.Diagnostics;
using MyHomeSecurity.Core.Media;
using MyHomeSecurity.Core.Models;

namespace MyHomeSecurity.Core.Cameras;

/// <summary>Takes a full-resolution still from a camera's main stream.</summary>
public static class Snapshot
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <summary>Saves a snapshot and returns its path.</summary>
    public static async Task<string> TakeAsync(CameraProfile camera, AppSettings settings, FfmpegLocator? ffmpeg = null,
        RtspAuthProxy? proxy = null, CancellationToken ct = default)
    {
        ffmpeg ??= FfmpegLocator.Default;
        proxy ??= RtspAuthProxy.Shared;
        var extension = settings.SnapshotFormat == SnapshotFormat.Png ? ".png" : ".jpg";
        var path = Captures.NewPath(settings.EffectiveSnapshotFolder, camera, extension);

        if (camera.Kind == CameraKind.Mjpeg)
        {
            var jpeg = await MjpegSource.GrabFrameAsync(camera, Timeout, ct);
            if (settings.SnapshotFormat == SnapshotFormat.Jpeg)
                await File.WriteAllBytesAsync(path, jpeg, ct);
            else
                await RunFfmpegAsync(ffmpeg, ["-loglevel", "error", "-f", "mjpeg", "-i", "pipe:0", "-frames:v", "1", "-update", "1", "-y", path], jpeg, ct);
        }
        else
        {
            var url = proxy.Register(camera.Host, camera.Port, camera.Username, camera.Password, camera.MainPath);
            try
            {
                var args = new List<string> { "-loglevel", "error" };
                args.AddRange(CameraStream.RtspInputArguments(ffmpeg.MajorVersion));
                args.AddRange(["-i", url, "-an", "-frames:v", "1"]);
                if (settings.SnapshotFormat == SnapshotFormat.Jpeg)
                    args.AddRange(["-q:v", "2"]);
                args.AddRange(["-update", "1", "-y", path]);
                await RunFfmpegAsync(ffmpeg, args, null, ct);
            }
            finally
            {
                proxy.Revoke(url);
            }
        }

        if (!File.Exists(path) || new FileInfo(path).Length == 0)
        {
            TryDelete(path);
            throw new IOException("The camera didn't send a picture.");
        }
        Log.Info("snapshot", $"Saved a snapshot from {camera.DisplayName}.");
        return path;
    }

    private static async Task RunFfmpegAsync(FfmpegLocator ffmpeg, List<string> args, byte[]? input, CancellationToken ct)
    {
        System.Diagnostics.Process process;
        try
        {
            process = ffmpeg.Start(args, keepInput: input is not null);
        }
        catch (Win32Exception)
        {
            throw new IOException("ffmpeg wasn't found.");
        }
        using (process)
        {
            var stderr = new CameraStream.StderrTail(process);
            _ = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, ct);
            if (input is not null)
            {
                await process.StandardInput.BaseStream.WriteAsync(input, ct);
                process.StandardInput.Close();
            }
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(Timeout);
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }
                if (ct.IsCancellationRequested)
                    throw;
                throw new TimeoutException("The camera didn't send a picture in time.");
            }
            if (process.ExitCode != 0)
                throw new IOException(CameraStream.DescribeFailure(stderr.Lines, neverPlayed: true));
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}
