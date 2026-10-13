using System.Net;
using System.Net.Sockets;
using System.Text;
using MyHomeSecurity.Core.Cameras;

namespace MyHomeSecurity.Core.Tests;

/// <summary>Finding a camera's stream paths when it has none at the path entered.</summary>
public sealed class RtspPathFinderTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private readonly TcpListener _camera = new(IPAddress.Loopback, 0);
    private readonly List<string> _requested = [];
    private string[] _paths = [];
    private bool _refuseLogins;

    public RtspPathFinderTests()
    {
        _camera.Start();
        _ = Task.Run(RunCameraAsync);
    }

    private int CameraPort => ((IPEndPoint)_camera.LocalEndpoint).Port;

    public void Dispose() => _camera.Stop();

    [Fact]
    public async Task FindsHipcamPaths()
    {
        _paths = ["/11", "/12"];
        var found = await RtspProbe.FindPathsAsync("127.0.0.1", CameraPort, "admin", "secret", Timeout, "/stream1");

        Assert.NotNull(found);
        Assert.Equal(("/11", "/12"), (found.MainPath, found.SubPath));
        // The path already tried isn't asked for again.
        lock (_requested)
            Assert.DoesNotContain("/stream1", _requested);
    }

    [Fact]
    public async Task GridPathIsEmptyWhenOnlyTheMainStreamExists()
    {
        _paths = ["/h264Preview_01_main"];
        var found = await RtspProbe.FindPathsAsync("127.0.0.1", CameraPort, "admin", "secret", Timeout);

        Assert.NotNull(found);
        Assert.Equal(("/h264Preview_01_main", ""), (found.MainPath, found.SubPath));
    }

    [Fact]
    public async Task StopsAtTheFirstRefusedLogin()
    {
        _paths = ["/11", "/12"];
        _refuseLogins = true;
        var found = await RtspProbe.FindPathsAsync("127.0.0.1", CameraPort, "admin", "wrong", Timeout);

        Assert.Null(found);
        lock (_requested)
            Assert.Single(_requested.Distinct());
    }

    // Basic authentication; 404 for paths it doesn't have, 401 for every request if refusing.
    private async Task RunCameraAsync()
    {
        while (true)
        {
            TcpClient client;
            try { client = await _camera.AcceptTcpClientAsync(); }
            catch { return; }
            _ = Task.Run(async () =>
            {
                using (client)
                {
                    var stream = client.GetStream();
                    var reader = new StreamReader(stream, Encoding.ASCII);
                    try
                    {
                        while (true)
                        {
                            var requestLine = await reader.ReadLineAsync();
                            if (requestLine is null)
                                return;
                            string cseq = "1", auth = "";
                            for (var line = await reader.ReadLineAsync(); !string.IsNullOrEmpty(line); line = await reader.ReadLineAsync())
                            {
                                if (line.StartsWith("CSeq:", StringComparison.OrdinalIgnoreCase)) cseq = line[5..].Trim();
                                if (line.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase)) auth = line;
                            }
                            var path = new Uri(requestLine.Split(' ')[1]).PathAndQuery;
                            lock (_requested)
                                _requested.Add(path);

                            string response;
                            if (_refuseLogins || auth.Length == 0)
                                response = $"RTSP/1.0 401 Unauthorized\r\nCSeq: {cseq}\r\nWWW-Authenticate: Basic realm=\"Fake\"\r\n\r\n";
                            else if (!_paths.Contains(path))
                                response = $"RTSP/1.0 404 Not Found\r\nCSeq: {cseq}\r\n\r\n";
                            else
                            {
                                const string sdp = "v=0\r\ns=Fake\r\nm=video 0 RTP/AVP 96\r\na=rtpmap:96 H264/90000\r\n";
                                response = $"RTSP/1.0 200 OK\r\nCSeq: {cseq}\r\nServer: Hipcam RealServer/V1.0\r\nContent-Type: application/sdp\r\nContent-Length: {sdp.Length}\r\n\r\n{sdp}";
                            }
                            var bytes = Encoding.ASCII.GetBytes(response);
                            await stream.WriteAsync(bytes);
                        }
                    }
                    catch (IOException) { }
                }
            });
        }
    }
}
