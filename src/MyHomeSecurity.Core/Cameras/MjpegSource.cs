using System.Net;
using MyHomeSecurity.Core.Models;

namespace MyHomeSecurity.Core.Cameras;

/// <summary>
/// Splits a byte stream (an HTTP multipart MJPEG stream, or anything else) into JPEG frames, from each
/// start-of-image marker (FF D8) to the next end-of-image marker (FF D9).
/// </summary>
public sealed class MjpegFrameSplitter
{
    private const int MaxFrameBytes = 16 * 1024 * 1024;
    private static readonly byte[] Soi = [0xFF, 0xD8];
    private static readonly byte[] Eoi = [0xFF, 0xD9];

    private byte[] _buffer = new byte[256 * 1024];
    private int _length;
    private int _frameStart = -1;
    private int _searchFrom;

    /// <summary>Adds data; calls <paramref name="onFrame"/> for each complete JPEG (valid only during the call).</summary>
    public void Push(ReadOnlySpan<byte> data, Action<ReadOnlySpan<byte>> onFrame)
    {
        if (_length + data.Length > _buffer.Length)
        {
            if (_length + data.Length > MaxFrameBytes)
            {
                // Garbage or a broken stream: start again.
                _length = 0;
                _frameStart = -1;
                _searchFrom = 0;
                if (data.Length > MaxFrameBytes)
                    return;
            }
            else
            {
                Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _length + data.Length));
            }
        }
        data.CopyTo(_buffer.AsSpan(_length));
        _length += data.Length;

        while (true)
        {
            if (_frameStart < 0)
            {
                var soi = _buffer.AsSpan(_searchFrom, _length - _searchFrom).IndexOf(Soi);
                if (soi < 0)
                {
                    // Keep a trailing FF in case the marker is split between pushes.
                    var keep = _length > 0 && _buffer[_length - 1] == 0xFF ? 1 : 0;
                    Compact(_length - keep);
                    _searchFrom = 0;
                    return;
                }
                _frameStart = _searchFrom + soi;
                _searchFrom = _frameStart + 2;
            }

            var eoi = _buffer.AsSpan(_searchFrom, _length - _searchFrom).IndexOf(Eoi);
            if (eoi < 0)
            {
                Compact(_frameStart);
                _searchFrom = Math.Max(_frameStart + 2, _length - 1);
                return;
            }
            var end = _searchFrom + eoi + 2;
            onFrame(_buffer.AsSpan(_frameStart, end - _frameStart));
            _frameStart = -1;
            _searchFrom = end;
        }
    }

    /// <summary>Drops the first <paramref name="count"/> bytes, shifting positions to match.</summary>
    private void Compact(int count)
    {
        if (count <= 0)
            return;
        Buffer.BlockCopy(_buffer, count, _buffer, 0, _length - count);
        _length -= count;
        if (_frameStart >= 0)
            _frameStart -= count;
        _searchFrom = Math.Max(0, _searchFrom - count);
    }
}

/// <summary>Reads JPEG frames from an HTTP MJPEG camera, inside the app (so the password never reaches ffmpeg).</summary>
public static class MjpegSource
{
    /// <summary>
    /// The stream URL. Older IP cameras (Foscam-style "videostream.cgi") take the login as query parameters;
    /// HTTP Basic/Digest is also offered via the handler.
    /// </summary>
    internal static Uri BuildUrl(CameraProfile camera)
    {
        var path = string.IsNullOrWhiteSpace(camera.MjpegPath) ? "/videostream.cgi" : camera.MjpegPath.Trim();
        if (!path.StartsWith('/'))
            path = "/" + path;
        var builder = new UriBuilder("http", camera.Host, camera.Port) { Path = path };
        var query = "";
        if (path.Contains('?'))
        {
            builder.Path = path[..path.IndexOf('?')];
            query = path[(path.IndexOf('?') + 1)..];
        }
        if (query.Contains("{user}", StringComparison.OrdinalIgnoreCase) || query.Contains("{password}", StringComparison.OrdinalIgnoreCase))
        {
            // Cameras that want other parameter names, e.g. "?user={user}&pwd={password}".
            query = query.Replace("{user}", Uri.EscapeDataString(camera.Username), StringComparison.OrdinalIgnoreCase)
                .Replace("{password}", Uri.EscapeDataString(camera.Password), StringComparison.OrdinalIgnoreCase);
        }
        else if (camera.Username.Length > 0 && !query.Contains("loginuse=", StringComparison.OrdinalIgnoreCase))
        {
            var login = $"loginuse={Uri.EscapeDataString(camera.Username)}&loginpas={Uri.EscapeDataString(camera.Password)}";
            query = query.Length == 0 ? login : query + "&" + login;
        }
        builder.Query = query;
        return builder.Uri;
    }

    /// <summary>Reads frames until cancelled or the camera hangs up. Throws on HTTP errors.</summary>
    public static async Task RunAsync(CameraProfile camera, Action<ReadOnlySpan<byte>> onFrame, CancellationToken ct)
    {
        using var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(10),
            UseProxy = false,
            Credentials = camera.Username.Length > 0 ? new NetworkCredential(camera.Username, camera.Password) : null,
            PreAuthenticate = false,
        };
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var response = await http.GetAsync(BuildUrl(camera), HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new UnauthorizedAccessException("The camera refused the username or password.");
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var splitter = new MjpegFrameSplitter();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
            idle.CancelAfter(TimeSpan.FromSeconds(15));
            int read;
            try
            {
                read = await stream.ReadAsync(buffer, idle.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException("The camera stopped sending pictures.");
            }
            if (read == 0)
                return;
            splitter.Push(buffer.AsSpan(0, read), onFrame);
        }
    }

    /// <summary>Gets one JPEG frame.</summary>
    public static async Task<byte[]> GrabFrameAsync(CameraProfile camera, TimeSpan timeout, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        byte[]? frame = null;
        try
        {
            await RunAsync(camera, f =>
            {
                if (frame is null)
                {
                    frame = f.ToArray();
                    cts.Cancel();
                }
            }, cts.Token);
        }
        catch (OperationCanceledException) when (frame is not null)
        {
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("The camera didn't send a picture in time.");
        }
        return frame ?? throw new IOException("The camera didn't send a picture.");
    }
}
