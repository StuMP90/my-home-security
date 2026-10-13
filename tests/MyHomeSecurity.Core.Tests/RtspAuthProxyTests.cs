using System.Net;
using System.Net.Sockets;
using System.Text;
using MyHomeSecurity.Core.Cameras;

namespace MyHomeSecurity.Core.Tests;

/// <summary>
/// The proxy against a fake camera that demands Digest authentication: ffmpeg's side never
/// sees the password, and each local URL works once.
/// </summary>
public sealed class RtspAuthProxyTests : IDisposable
{
    private const string Password = "pa55word-secret";
    private readonly TcpListener _camera = new(IPAddress.Loopback, 0);
    private readonly RtspAuthProxy _proxy = new();
    private readonly List<string> _cameraSaw = [];

    public RtspAuthProxyTests()
    {
        _camera.Start();
        _ = Task.Run(RunCameraAsync);
    }

    private int CameraPort => ((IPEndPoint)_camera.LocalEndpoint).Port;

    public void Dispose()
    {
        _proxy.Dispose();
        _camera.Stop();
    }

    [Fact]
    public async Task AnswersTheCamerasChallengeAndHidesThePassword()
    {
        var url = _proxy.Register("127.0.0.1", CameraPort, "admin", Password, "stream1");
        Assert.StartsWith($"rtsp://127.0.0.1:{_proxy.Port}/", url);
        Assert.DoesNotContain(Password, url);

        var (status, text) = await DescribeAsync(url);

        Assert.Equal(200, status);
        Assert.DoesNotContain(Password, text);
        // The SDP's camera address is rewritten to point back at the proxy.
        Assert.Contains(url[..url.LastIndexOf('/')], text);
        Assert.DoesNotContain($"127.0.0.1:{CameraPort}", text);
        lock (_cameraSaw)
            Assert.Contains(_cameraSaw, line => line == $"DESCRIBE rtsp://127.0.0.1:{CameraPort}/stream1 RTSP/1.0");
    }

    [Fact]
    public async Task EachUrlWorksOnce()
    {
        var url = _proxy.Register("127.0.0.1", CameraPort, "admin", Password, "/stream1");
        Assert.Equal(1, _proxy.PendingTokens);

        Assert.Equal(200, (await DescribeAsync(url)).Status);
        Assert.Equal(0, _proxy.PendingTokens);
        Assert.Equal(404, (await DescribeAsync(url)).Status);
    }

    [Fact]
    public async Task UnknownTokensAreRefused()
    {
        Assert.Equal(404, (await DescribeAsync($"rtsp://127.0.0.1:{_proxy.Port}/not-a-token/stream1")).Status);
    }

    [Fact]
    public async Task RevokedUrlsAreRefused()
    {
        var url = _proxy.Register("127.0.0.1", CameraPort, "admin", Password, "/stream1");
        _proxy.Revoke(url);
        Assert.Equal(404, (await DescribeAsync(url)).Status);
    }

    private async Task<(int Status, string Text)> DescribeAsync(string url)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _proxy.Port);
        var stream = client.GetStream();
        var request = RtspMessage.Request("DESCRIBE", url, 1);
        request.Set("Accept", "application/sdp");
        await stream.WriteAsync(request.ToBytes());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var reply = (await new RtspStreamReader(stream).ReadAsync(timeout.Token))?.Message
            ?? throw new InvalidOperationException("No reply.");
        return (reply.StatusCode, Encoding.UTF8.GetString(reply.ToBytes()));
    }

    /// <summary>A minimal camera: 401 with a Digest challenge, then 200 with an SDP once the response is right.</summary>
    private async Task RunCameraAsync()
    {
        while (true)
        {
            TcpClient client;
            try
            {
                client = await _camera.AcceptTcpClientAsync();
            }
            catch
            {
                return;
            }
            _ = Task.Run(async () =>
            {
                using var _ = client;
                var stream = client.GetStream();
                var reader = new RtspStreamReader(stream);
                try
                {
                    while (await reader.ReadAsync(CancellationToken.None) is { Message: { } request })
                    {
                        lock (_cameraSaw)
                            _cameraSaw.Add(request.StartLine);
                        var reply = Authorised(request)
                            ? new RtspMessage("RTSP/1.0 200 OK")
                            {
                                Body = Encoding.UTF8.GetBytes(
                                    $"v=0\r\na=control:rtsp://127.0.0.1:{CameraPort}/stream1/trackID=1\r\n"),
                            }
                            : new RtspMessage("RTSP/1.0 401 Unauthorized");
                        reply.Set("CSeq", request.CSeq ?? "0");
                        if (reply.StatusCode == 200)
                            reply.Set("Content-Type", "application/sdp");
                        else
                            reply.Set("WWW-Authenticate", "Digest realm=\"Fake Camera\", nonce=\"abc\"");
                        await stream.WriteAsync(reply.ToBytes());
                    }
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                }
            });
        }
    }

    private static bool Authorised(RtspMessage request)
    {
        var header = request.Get("Authorization");
        if (header is null || !header.StartsWith("Digest ", StringComparison.Ordinal))
            return false;
        var p = RtspAuthenticator.ParseParameters(header["Digest ".Length..]);
        var ha1 = RtspAuthenticator.Md5Hex($"admin:Fake Camera:{Password}");
        var ha2 = RtspAuthenticator.Md5Hex($"{request.Method}:{p["uri"]}");
        return p["response"] == RtspAuthenticator.Md5Hex($"{ha1}:abc:{ha2}");
    }
}
