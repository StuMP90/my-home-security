using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MyHomeSecurity.Core.Diagnostics;

namespace MyHomeSecurity.Core.Cameras;

/// <summary>
/// A local RTSP proxy that keeps camera passwords away from ffmpeg.
///
/// Putting "rtsp://user:password@camera/stream1" on ffmpeg's command line would show the password to
/// every user of the computer (process lists show command lines). Instead the app registers the
/// camera here and gives ffmpeg "rtsp://127.0.0.1:port/TOKEN/stream1". The token is random and
/// works for one connection only: once ffmpeg has connected with it, it is gone. The proxy forwards
/// the session to the camera, answering its password challenge (Digest or Basic) itself.
/// </summary>
public sealed partial class RtspAuthProxy : IDisposable
{
    private static readonly Lazy<RtspAuthProxy> SharedInstance = new(() => new RtspAuthProxy(), LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    private readonly TcpListener _listener;
    private readonly ConcurrentDictionary<string, Registration> _tokens = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cts = new();

    public RtspAuthProxy()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoopAsync);
    }

    public static RtspAuthProxy Shared => SharedInstance.Value;

    public int Port { get; }

    /// <summary>Number of tokens not yet used (for tests).</summary>
    internal int PendingTokens => _tokens.Count;

    /// <summary>
    /// Registers a camera stream and returns the local URL for ffmpeg. The URL works for one connection.
    /// </summary>
    public string Register(string host, int port, string username, string password, string path)
    {
        PruneExpired();
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        _tokens[token] = new Registration(host, port, username, password, DateTime.UtcNow);
        if (!path.StartsWith('/'))
            path = "/" + path;
        return $"rtsp://127.0.0.1:{Port}/{token}{path}";
    }

    /// <summary>Forgets a URL that was never used (e.g. ffmpeg failed to start).</summary>
    public void Revoke(string url)
    {
        if (TryGetToken(url, out var token, out _))
            _tokens.TryRemove(token, out _);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        _tokens.Clear();
    }

    private void PruneExpired()
    {
        var now = DateTime.UtcNow;
        foreach (var (token, reg) in _tokens)
        {
            if (now - reg.Created > TokenLifetime)
                _tokens.TryRemove(token, out _);
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex)
            {
                Log.Warn("proxy", "Accept failed.", ex);
                continue;
            }
            _ = Task.Run(() => HandleAsync(client));
        }
    }

    /// <summary>Splits "rtsp://127.0.0.1:port/TOKEN/rest" into the token and "rtsp://127.0.0.1:port/TOKEN".</summary>
    internal static bool TryGetToken(string uri, out string token, out string localBase)
    {
        token = "";
        localBase = "";
        var scheme = uri.IndexOf("://", StringComparison.Ordinal);
        if (scheme < 0)
            return false;
        var slash = uri.IndexOf('/', scheme + 3);
        if (slash < 0)
            return false;
        var end = uri.IndexOf('/', slash + 1);
        if (end < 0)
            end = uri.Length;
        token = uri[(slash + 1)..end];
        localBase = uri[..end];
        return token.Length > 0;
    }

    private async Task HandleAsync(TcpClient client)
    {
        using var _ = client;
        client.NoDelay = true;
        var ct = _cts.Token;
        var clientStream = client.GetStream();
        var clientReader = new RtspStreamReader(clientStream);
        try
        {
            using var firstTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            firstTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            var first = await clientReader.ReadAsync(firstTimeout.Token);
            if (first?.Message is not { IsResponse: false } request)
                return;

            if (!TryGetToken(request.Uri, out var token, out var localBase) || !_tokens.TryRemove(token, out var reg)
                || DateTime.UtcNow - reg.Created > TokenLifetime)
            {
                Log.Warn("proxy", "Refused a connection with an unknown or used token.");
                var refusal = new RtspMessage("RTSP/1.0 404 Not Found");
                refusal.Set("CSeq", request.CSeq ?? "0");
                await clientStream.WriteAsync(refusal.ToBytes(), ct);
                return;
            }

            using var camera = new TcpClient { NoDelay = true };
            using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                connectTimeout.CancelAfter(ConnectTimeout);
                try
                {
                    await camera.ConnectAsync(reg.Host, reg.Port, connectTimeout.Token);
                }
                catch (Exception ex) when (ex is SocketException or OperationCanceledException)
                {
                    Log.Warn("proxy", $"Couldn't connect to camera {reg.Host}:{reg.Port}.", ex is SocketException ? ex : null);
                    var failure = new RtspMessage("RTSP/1.0 503 Service Unavailable");
                    failure.Set("CSeq", request.CSeq ?? "0");
                    await clientStream.WriteAsync(failure.ToBytes(), ct);
                    return;
                }
            }

            var session = new Session(reg, localBase, clientStream, camera.GetStream());
            await session.RunAsync(request, clientReader, ct);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException or InvalidDataException)
        {
            // Either side hung up.
        }
        catch (Exception ex)
        {
            Log.Error("proxy", "Proxy session failed.", ex);
        }
    }

    private sealed record Registration(string Host, int Port, string Username, string Password, DateTime Created);

    [GeneratedRegex(@"rtsp://[^/\s;,""]+", RegexOptions.IgnoreCase)]
    private static partial Regex RtspAuthorityRegex();

    /// <summary>One proxied connection: client (ffmpeg) on one side, camera on the other.</summary>
    private sealed class Session
    {
        private readonly Registration _reg;
        private readonly string _localBase;
        private readonly string _cameraBase;
        private readonly Stream _client;
        private readonly Stream _camera;
        private readonly SemaphoreSlim _cameraWrite = new(1, 1);
        private readonly SemaphoreSlim _clientWrite = new(1, 1);
        private readonly RtspAuthenticator _auth;
        private readonly ConcurrentDictionary<string, Pending> _pending = new(StringComparer.Ordinal);

        public Session(Registration reg, string localBase, Stream client, Stream camera)
        {
            _reg = reg;
            _localBase = localBase;
            var host = reg.Host.Contains(':') && !reg.Host.StartsWith('[') ? $"[{reg.Host}]" : reg.Host;
            _cameraBase = $"rtsp://{host}:{reg.Port}";
            _client = client;
            _camera = camera;
            _auth = new RtspAuthenticator(reg.Username, reg.Password);
        }

        public async Task RunAsync(RtspMessage firstRequest, RtspStreamReader clientReader, CancellationToken ct)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            await ForwardRequestAsync(firstRequest, linked.Token);
            var up = PumpClientAsync(clientReader, linked.Token);
            var down = PumpCameraAsync(new RtspStreamReader(_camera), linked.Token);
            await Task.WhenAny(up, down);
            linked.Cancel();
            _client.Close();
            _camera.Close();
            try
            {
                await Task.WhenAll(up, down);
            }
            catch
            {
                // Expected once one side has closed.
            }
        }

        private async Task PumpClientAsync(RtspStreamReader reader, CancellationToken ct)
        {
            while (await reader.ReadAsync(ct) is { } item)
            {
                if (item.Message is { } message)
                {
                    if (message.IsResponse)
                        await WriteCameraAsync(message.ToBytes(), ct); // reply to a camera request
                    else
                        await ForwardRequestAsync(message, ct);
                }
                else
                {
                    await WriteCameraAsync(item.Data, ct);
                }
            }
        }

        private async Task PumpCameraAsync(RtspStreamReader reader, CancellationToken ct)
        {
            while (await reader.ReadAsync(ct) is { } item)
            {
                if (item.Message is not { } message)
                {
                    await WriteClientAsync(item.Data, ct);
                    continue;
                }
                if (!message.IsResponse)
                {
                    await WriteClientAsync(RewriteToLocal(message).ToBytes(), ct);
                    continue;
                }

                var cseq = message.CSeq ?? "";
                _pending.TryRemove(cseq, out var pending);
                if (message.StatusCode == 401 && pending is { Retried: false } && _auth.HasCredentials
                    && _auth.Accept(message.GetAll("WWW-Authenticate")))
                {
                    var retry = pending.Request.Clone();
                    retry.Set("Authorization", _auth.GetHeader(retry.Method, retry.Uri)!);
                    _pending[cseq] = new Pending(pending.Request, Retried: true);
                    await WriteCameraAsync(retry.ToBytes(), ct);
                    continue;
                }
                if (message.StatusCode == 401)
                    Log.Warn("proxy", $"Camera {_reg.Host} refused the username or password.");
                await WriteClientAsync(RewriteToLocal(message).ToBytes(), ct);
            }
        }

        private async Task ForwardRequestAsync(RtspMessage request, CancellationToken ct)
        {
            var outgoing = request.Clone();
            if (outgoing.Uri.StartsWith(_localBase, StringComparison.Ordinal))
                outgoing.Uri = _cameraBase + outgoing.Uri[_localBase.Length..];
            else if (outgoing.Uri != "*")
                outgoing.Uri = RtspAuthorityRegex().Replace(outgoing.Uri, _cameraBase, 1);
            outgoing.Remove("Authorization");
            _pending[outgoing.CSeq ?? ""] = new Pending(outgoing, Retried: false);
            if (_auth.HasChallenge && _auth.GetHeader(outgoing.Method, outgoing.Uri) is { } header)
                outgoing.Set("Authorization", header);
            await WriteCameraAsync(outgoing.ToBytes(), ct);
        }

        /// <summary>Points any camera URLs in a message (Content-Base, RTP-Info, SDP) back at the proxy.</summary>
        private RtspMessage RewriteToLocal(RtspMessage message)
        {
            var copy = message.Clone();
            for (var i = 0; i < copy.Headers.Count; i++)
            {
                var (key, value) = copy.Headers[i];
                if (value.Contains("rtsp://", StringComparison.OrdinalIgnoreCase))
                    copy.Headers[i] = new(key, RtspAuthorityRegex().Replace(value, _localBase));
            }
            if (copy.Body.Length > 0 && (copy.Get("Content-Type") ?? "").Contains("sdp", StringComparison.OrdinalIgnoreCase))
            {
                var sdp = Encoding.UTF8.GetString(copy.Body);
                if (sdp.Contains("rtsp://", StringComparison.OrdinalIgnoreCase))
                    copy.Body = Encoding.UTF8.GetBytes(RtspAuthorityRegex().Replace(sdp, _localBase));
            }
            return copy;
        }

        private async Task WriteCameraAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            await _cameraWrite.WaitAsync(ct);
            try
            {
                await _camera.WriteAsync(data, ct);
            }
            finally
            {
                _cameraWrite.Release();
            }
        }

        private async Task WriteClientAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            await _clientWrite.WaitAsync(ct);
            try
            {
                await _client.WriteAsync(data, ct);
            }
            finally
            {
                _clientWrite.Release();
            }
        }

        private sealed record Pending(RtspMessage Request, bool Retried);
    }
}
