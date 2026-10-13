using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using MyHomeSecurity.Core.Diagnostics;
using MyHomeSecurity.Core.Media;
using MyHomeSecurity.Core.Models;

namespace MyHomeSecurity.Core.Cameras;

/// <summary>How a stream is decoded and scaled.</summary>
/// <param name="MainStream">The camera's high-resolution stream, rather than its low-resolution substream.</param>
/// <param name="Width">Output width; the picture is scaled to fit and centred.</param>
/// <param name="Height">Output height.</param>
/// <param name="Fps">Frames per second to deliver (0 = as sent).</param>
/// <param name="KeyframesOnly">Decode only key frames (about one a second or two): the lightest option for many cameras.</param>
/// <param name="HardwareDecoding">Let ffmpeg use the graphics hardware to decode, if it can.</param>
public sealed record StreamOptions(bool MainStream, int Width, int Height, int Fps, bool KeyframesOnly, bool HardwareDecoding)
{
    public static StreamOptions ForGrid(AppSettings s) =>
        new(false, 640, 360, Math.Clamp(s.GridFps, 1, 30), s.GridKeyframesOnly, s.HardwareDecoding);

    public static StreamOptions ForLive(AppSettings s)
    {
        var height = Math.Clamp(s.LiveHeight, 240, 2160) & ~1;
        var width = ((height * 16 / 9) + 1) & ~1;
        return new(true, width, height, Math.Clamp(s.LiveFps, 0, 60), false, s.HardwareDecoding);
    }
}

public enum CameraStreamState
{
    Stopped,
    Connecting,
    Playing,
    Error,
}

/// <summary>
/// Live pictures from one camera, decoded by ffmpeg into raw BGRA frames of a fixed size.
///
/// To keep the load low, ffmpeg does all the work in one pass (hardware decoding where possible,
/// dropping frames, scaling to the size shown) and hands over raw pixels, so nothing is encoded or
/// decoded twice. Only the newest frame is kept; the screen copies it when it's ready to draw.
/// If the camera drops out, the stream reconnects by itself.
/// </summary>
public sealed class CameraStream : IDisposable
{
    private static readonly TimeSpan MinRetry = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxRetry = TimeSpan.FromSeconds(30);

    internal const string LoginRefused = "The camera refused the username or password.";

    private readonly CameraProfile _camera;
    private readonly FfmpegLocator _ffmpeg;
    private readonly RtspAuthProxy _proxy;
    private readonly object _frameLock = new();
    private byte[] _front;
    private byte[] _back;
    private long _frameNumber;
    private CancellationTokenSource? _cts;
    private Thread? _thread;
    private volatile CameraStreamState _state;
    private volatile string _statusMessage = "";

    public CameraStream(CameraProfile camera, StreamOptions options, FfmpegLocator? ffmpeg = null, RtspAuthProxy? proxy = null)
    {
        _camera = camera.Clone();
        Options = options;
        _ffmpeg = ffmpeg ?? FfmpegLocator.Default;
        _proxy = proxy ?? RtspAuthProxy.Shared;
        _front = new byte[FrameBytes];
        _back = new byte[FrameBytes];
    }

    public StreamOptions Options { get; }
    public Guid CameraId => _camera.Id;
    public int Width => Options.Width;
    public int Height => Options.Height;
    public int Stride => Options.Width * 4;
    public int FrameBytes => Stride * Options.Height;
    public CameraStreamState State => _state;
    public string StatusMessage => _statusMessage;
    public long FrameNumber => Interlocked.Read(ref _frameNumber);
    public bool IsRunning => _thread is not null;

    /// <summary>Raised on a background thread when a new frame is ready.</summary>
    public event Action? FrameArrived;

    /// <summary>Raised on a background thread when <see cref="State"/> or <see cref="StatusMessage"/> changes.</summary>
    public event Action? StateChanged;

    public void Start()
    {
        if (_thread is not null)
            return;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _thread = new Thread(() => RunLoop(token))
        {
            IsBackground = true,
            Name = $"Camera {_camera.DisplayName}",
        };
        _thread.Start();
    }

    public void Stop()
    {
        var cts = _cts;
        var thread = _thread;
        _cts = null;
        _thread = null;
        if (cts is null)
            return;
        cts.Cancel();
        // The thread kills ffmpeg on cancel; don't hold up the UI waiting for it.
        _ = Task.Run(() =>
        {
            thread?.Join(TimeSpan.FromSeconds(5));
            cts.Dispose();
        });
        SetState(CameraStreamState.Stopped, "");
    }

    public void Dispose() => Stop();

    /// <summary>
    /// Copies the newest frame into <paramref name="destination"/> (rows <paramref name="destinationStride"/> bytes
    /// apart) if it's newer than <paramref name="lastFrame"/>, which is then updated.
    /// </summary>
    public bool CopyLatestFrame(IntPtr destination, int destinationStride, ref long lastFrame)
    {
        lock (_frameLock)
        {
            if (_frameNumber == 0 || _frameNumber == lastFrame)
                return false;
            if (destinationStride == Stride)
            {
                Marshal.Copy(_front, 0, destination, FrameBytes);
            }
            else
            {
                var row = Math.Min(Stride, destinationStride);
                for (var y = 0; y < Height; y++)
                    Marshal.Copy(_front, y * Stride, destination + (y * destinationStride), row);
            }
            lastFrame = _frameNumber;
            return true;
        }
    }

    /// <summary>A copy of the newest frame (BGRA), or null if none has arrived.</summary>
    public byte[]? GetLatestFrame()
    {
        lock (_frameLock)
            return _frameNumber == 0 ? null : (byte[])_front.Clone();
    }

    private void SetState(CameraStreamState state, string message)
    {
        if (_state == state && _statusMessage == message)
            return;
        _state = state;
        _statusMessage = message;
        StateChanged?.Invoke();
    }

    private void RunLoop(CancellationToken ct)
    {
        var retry = MinRetry;
        while (!ct.IsCancellationRequested)
        {
            SetState(CameraStreamState.Connecting, "Connecting…");
            var gotFrames = false;
            string error;
            try
            {
                error = RunOnce(ct, () =>
                {
                    gotFrames = true;
                    retry = MinRetry;
                });
            }
            catch (Win32Exception)
            {
                error = "ffmpeg wasn't found. Install it, or put it in an \"ffmpeg\" folder next to the app.";
            }
            catch (Exception ex)
            {
                Log.Warn("stream", $"Stream for {_camera.DisplayName} failed.", ex);
                error = ex.Message;
            }
            if (ct.IsCancellationRequested)
                break;

            // Don't keep trying a refused login: cameras such as Tapo lock out all logins after
            // several failures, so retrying would keep the camera locked even for its own app.
            // The stream starts again when it's next shown or the camera is edited.
            if (error == LoginRefused)
            {
                Log.Info("stream", $"{_camera.DisplayName}: {error} Not retrying.");
                SetState(CameraStreamState.Error, $"{error} Check them in Edit camera. Some cameras block logins for a while after several refusals.");
                break;
            }

            Log.Info("stream", $"{_camera.DisplayName}: {error} Retrying in {retry.TotalSeconds:0}s.");
            SetState(CameraStreamState.Error, error);
            if (ct.WaitHandle.WaitOne(retry))
                break;
            if (!gotFrames)
                retry = TimeSpan.FromTicks(Math.Min(retry.Ticks * 2, MaxRetry.Ticks));
        }
    }

    /// <summary>Runs ffmpeg until it stops; returns a message saying why.</summary>
    private string RunOnce(CancellationToken ct, Action onFirstFrame)
    {
        var isMjpeg = _camera.Kind == CameraKind.Mjpeg;
        string? url = null;
        if (!isMjpeg)
            url = _proxy.Register(_camera.Host, _camera.Port, _camera.Username, _camera.Password, Options.MainStream || string.IsNullOrWhiteSpace(_camera.SubPath) ? _camera.MainPath : _camera.SubPath);

        using var process = _ffmpeg.Start(BuildArguments(Options, url, _ffmpeg.MajorVersion), keepInput: isMjpeg);
        var stderr = new StderrTail(process);
        using var kill = ct.Register(() => Kill(process));

        Task? pump = null;
        using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Exception? pumpError = null;
        if (isMjpeg)
        {
            pump = Task.Run(async () =>
            {
                try
                {
                    await PumpMjpegAsync(process, pumpCts.Token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    pumpError = ex;
                }
                finally
                {
                    try
                    {
                        process.StandardInput.Close();
                    }
                    catch (IOException)
                    {
                    }
                }
            });
        }

        var output = process.StandardOutput.BaseStream;
        var first = true;
        try
        {
            while (!ct.IsCancellationRequested && ReadFrame(output, _back))
            {
                lock (_frameLock)
                {
                    (_front, _back) = (_back, _front);
                    _frameNumber++;
                }
                if (first)
                {
                    first = false;
                    onFirstFrame();
                    SetState(CameraStreamState.Playing, "");
                }
                FrameArrived?.Invoke();
            }
        }
        catch (IOException)
        {
            // ffmpeg was killed.
        }
        finally
        {
            pumpCts.Cancel();
            Kill(process);
            // Once it has exited, the parameterless wait also reads stderr to the end, so the
            // reason it stopped isn't missed.
            if (process.WaitForExit(2000))
                process.WaitForExit();
            if (url is not null)
                _proxy.Revoke(url);
        }
        pump?.Wait(2000);

        if (pumpError is not null)
            return pumpError is HttpRequestException http ? $"Couldn't reach the camera: {http.Message}" : pumpError.Message;
        return DescribeFailure(stderr.Lines, first);
    }

    private async Task PumpMjpegAsync(Process process, CancellationToken ct)
    {
        var input = process.StandardInput.BaseStream;
        var interval = Options.Fps > 0 ? TimeSpan.FromSeconds(1.0 / Options.Fps) : TimeSpan.Zero;
        var last = Stopwatch.StartNew();
        var firstFrame = true;
        await MjpegSource.RunAsync(_camera, frame =>
        {
            // Drop frames we don't need before they're decoded at all.
            if (!firstFrame && last.Elapsed < interval)
                return;
            firstFrame = false;
            last.Restart();
            input.Write(frame);
            input.Flush();
        }, ct);
    }

    private static bool ReadFrame(Stream stream, byte[] buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = stream.Read(buffer, offset, buffer.Length - offset);
            if (read == 0)
                return false;
            offset += read;
        }
        return true;
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
        }
    }

    internal static List<string> BuildArguments(StreamOptions o, string? rtspUrl, int ffmpegMajorVersion)
    {
        var args = new List<string> { "-loglevel", "error" };
        if (o.HardwareDecoding)
            args.AddRange(["-hwaccel", "auto"]);
        if (!o.MainStream)
            args.AddRange(["-threads", "1"]);
        if (o.KeyframesOnly)
            args.AddRange(["-skip_frame", "nokey"]);
        args.AddRange(["-fflags", "nobuffer", "-flags", "low_delay"]);

        if (rtspUrl is null)
        {
            // MJPEG frames arrive on stdin, already thinned to the frame rate wanted.
            args.AddRange(["-f", "mjpeg", "-probesize", "32", "-analyzeduration", "0", "-i", "pipe:0"]);
        }
        else
        {
            args.AddRange(RtspInputArguments(ffmpegMajorVersion));
            args.AddRange(["-i", rtspUrl]);
        }

        var filters = new List<string>();
        if (rtspUrl is not null && !o.KeyframesOnly && o.Fps > 0)
            filters.Add($"fps={o.Fps}");
        var scaleFlags = o.MainStream ? "bilinear" : "fast_bilinear";
        filters.Add($"scale={o.Width}:{o.Height}:force_original_aspect_ratio=decrease:flags={scaleFlags}");
        filters.Add($"pad={o.Width}:{o.Height}:(ow-iw)/2:(oh-ih)/2");

        args.AddRange(["-an", "-sn", "-dn", "-vf", string.Join(',', filters), "-pix_fmt", "bgra", "-f", "rawvideo", "pipe:1"]);
        return args;
    }

    /// <summary>Input options for reading from the proxy: TCP, and give up if the camera goes quiet.</summary>
    internal static IEnumerable<string> RtspInputArguments(int ffmpegMajorVersion)
    {
        // ffmpeg 5 renamed the RTSP socket timeout from -stimeout to -timeout (microseconds).
        var timeoutOption = ffmpegMajorVersion is > 0 and < 5 ? "-stimeout" : "-timeout";
        return ["-rtsp_transport", "tcp", timeoutOption, "10000000"];
    }

    /// <summary>Turns ffmpeg's error output into something a person can act on.</summary>
    internal static string DescribeFailure(IReadOnlyList<string> stderr, bool neverPlayed)
    {
        var text = string.Join('\n', stderr);
        if (text.Contains("401", StringComparison.Ordinal) || text.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase))
            return LoginRefused;
        if (text.Contains("404", StringComparison.Ordinal))
            return "The camera has no stream at that path.";
        if (text.Contains("503", StringComparison.Ordinal) || text.Contains("Connection refused", StringComparison.OrdinalIgnoreCase))
            return "The camera can't be reached.";
        if (text.Contains("timed out", StringComparison.OrdinalIgnoreCase) || text.Contains("timeout", StringComparison.OrdinalIgnoreCase))
            return "The camera stopped answering.";
        var last = stderr.Count > 0 ? stderr[^1] : "";
        if (last.Length > 0)
            return last;
        return neverPlayed ? "The camera didn't send any pictures." : "The stream ended.";
    }

    /// <summary>Keeps the last lines ffmpeg wrote to stderr (it must be drained or ffmpeg blocks).</summary>
    internal sealed class StderrTail
    {
        private const int MaxLines = 20;
        private readonly Queue<string> _lines = new();

        public StderrTail(Process process)
        {
            process.ErrorDataReceived += (_, e) =>
            {
                if (string.IsNullOrWhiteSpace(e.Data))
                    return;
                lock (_lines)
                {
                    _lines.Enqueue(e.Data.Trim());
                    while (_lines.Count > MaxLines)
                        _lines.Dequeue();
                }
            };
            process.BeginErrorReadLine();
        }

        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (_lines)
                    return [.. _lines];
            }
        }
    }
}
