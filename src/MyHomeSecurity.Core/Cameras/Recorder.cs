using System.ComponentModel;
using System.Diagnostics;
using MyHomeSecurity.Core.Diagnostics;
using MyHomeSecurity.Core.Media;
using MyHomeSecurity.Core.Models;

namespace MyHomeSecurity.Core.Cameras;

/// <summary>
/// Records a camera's main stream to a file. RTSP video is copied as sent (no re-encoding, so it costs
/// almost nothing); audio becomes AAC. MP4 files are written in fragments, so a recording cut short
/// (power cut, crash) still plays. MJPEG cameras are encoded to H.264.
/// </summary>
public sealed class Recorder : IDisposable
{
    private readonly CameraProfile _camera;
    private readonly Process _process;
    private readonly CameraStream.StderrTail _stderr;
    private readonly CancellationTokenSource _pumpCts = new();
    private readonly string? _proxyUrl;
    private readonly RtspAuthProxy _proxy;
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _stopping;

    private Recorder(CameraProfile camera, string path, Process process, string? proxyUrl, RtspAuthProxy proxy)
    {
        _camera = camera;
        FilePath = path;
        _process = process;
        _proxyUrl = proxyUrl;
        _proxy = proxy;
        _stderr = new CameraStream.StderrTail(process);
        _ = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
        StartedAt = DateTime.Now;
    }

    public Guid CameraId => _camera.Id;
    public string CameraName => _camera.DisplayName;
    public string FilePath { get; }
    public DateTime StartedAt { get; }
    public TimeSpan Elapsed => _elapsed.Elapsed;
    public bool IsRunning => !_finished.Task.IsCompleted;

    /// <summary>Why the recording ended badly, or null.</summary>
    public string? Error { get; private set; }

    public Task Completion => _finished.Task;

    /// <summary>Raised (on a background thread) when the recording has ended, for any reason.</summary>
    public event Action<Recorder>? Finished;

    public static Recorder Start(CameraProfile camera, AppSettings settings, FfmpegLocator? ffmpeg = null, RtspAuthProxy? proxy = null)
    {
        ffmpeg ??= FfmpegLocator.Default;
        proxy ??= RtspAuthProxy.Shared;
        camera = camera.Clone();
        var extension = settings.RecordingFormat == RecordingFormat.Mkv ? ".mkv" : ".mp4";
        var path = Captures.NewPath(settings.EffectiveRecordingFolder, camera, extension);
        var isMjpeg = camera.Kind == CameraKind.Mjpeg;
        var url = isMjpeg ? null : proxy.Register(camera.Host, camera.Port, camera.Username, camera.Password, camera.MainPath);

        Process process;
        try
        {
            process = ffmpeg.Start(BuildArguments(settings, url, path, ffmpeg.MajorVersion), keepInput: true);
        }
        catch (Win32Exception)
        {
            if (url is not null)
                proxy.Revoke(url);
            throw new IOException("ffmpeg wasn't found.");
        }

        var recorder = new Recorder(camera, path, process, url, proxy);
        Log.Info("record", $"Recording {camera.DisplayName}.");
        if (isMjpeg)
            _ = Task.Run(recorder.PumpMjpegAsync);
        _ = Task.Run(recorder.WaitAsync);
        return recorder;
    }

    internal static List<string> BuildArguments(AppSettings settings, string? rtspUrl, string path, int ffmpegMajorVersion)
    {
        var args = new List<string> { "-loglevel", "error" };
        if (rtspUrl is null)
        {
            args.AddRange(["-f", "mjpeg", "-use_wallclock_as_timestamps", "1", "-i", "pipe:0",
                "-c:v", "libx264", "-preset", "veryfast", "-crf", "23", "-pix_fmt", "yuv420p"]);
        }
        else
        {
            args.AddRange(CameraStream.RtspInputArguments(ffmpegMajorVersion));
            args.AddRange(["-i", rtspUrl, "-map", "0:v:0", "-map", "0:a:0?", "-c:v", "copy", "-c:a", "aac", "-b:a", "64k"]);
        }
        if (settings.MaxRecordingMinutes > 0)
            args.AddRange(["-t", (settings.MaxRecordingMinutes * 60).ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        if (settings.RecordingFormat == RecordingFormat.Mkv)
            args.AddRange(["-f", "matroska"]);
        else
            args.AddRange(["-movflags", "+frag_keyframe+empty_moov+default_base_moof", "-f", "mp4"]);
        args.AddRange(["-y", path]);
        return args;
    }

    /// <summary>Ends the recording cleanly (the file is finished properly) and waits for it.</summary>
    public async Task StopAsync()
    {
        if (!IsRunning)
            return;
        _stopping = true;
        try
        {
            if (_proxyUrl is null)
            {
                await _pumpCts.CancelAsync(); // MJPEG: end of input ends ffmpeg
            }
            else
            {
                await _process.StandardInput.WriteAsync('q');
                await _process.StandardInput.FlushAsync();
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
        if (await Task.WhenAny(_finished.Task, Task.Delay(TimeSpan.FromSeconds(10))) != _finished.Task)
        {
            Kill();
            await _finished.Task;
        }
    }

    public void Dispose()
    {
        if (IsRunning)
        {
            _stopping = true;
            _pumpCts.Cancel();
            Kill();
        }
    }

    private void Kill()
    {
        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
        }
    }

    private async Task PumpMjpegAsync()
    {
        var input = _process.StandardInput.BaseStream;
        try
        {
            await MjpegSource.RunAsync(_camera, frame =>
            {
                input.Write(frame);
                input.Flush();
            }, _pumpCts.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (!_stopping)
                Error = ex.Message;
        }
        finally
        {
            try
            {
                _process.StandardInput.Close();
            }
            catch (IOException)
            {
            }
        }
    }

    private async Task WaitAsync()
    {
        try
        {
            await _process.WaitForExitAsync();
            _elapsed.Stop();
            if (_proxyUrl is not null)
                _proxy.Revoke(_proxyUrl);
            var hasFile = File.Exists(FilePath) && new FileInfo(FilePath).Length > 0;
            if (!hasFile)
            {
                Error ??= CameraStream.DescribeFailure(_stderr.Lines, neverPlayed: true);
                try
                {
                    File.Delete(FilePath);
                }
                catch (IOException)
                {
                }
            }
            else if (_process.ExitCode != 0 && !_stopping && Error is null)
            {
                Error = "The recording stopped early: " + CameraStream.DescribeFailure(_stderr.Lines, neverPlayed: false);
            }
            Log.Info("record", $"Recording of {_camera.DisplayName} ended after {Elapsed:hh\\:mm\\:ss}{(Error is null ? "." : ": " + Error)}");
        }
        finally
        {
            _process.Dispose();
            _pumpCts.Dispose();
            _finished.TrySetResult();
            Finished?.Invoke(this);
        }
    }
}
