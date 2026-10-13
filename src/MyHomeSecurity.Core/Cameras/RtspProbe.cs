using System.Net.Sockets;
using System.Text;

namespace MyHomeSecurity.Core.Cameras;

public sealed record RtspProbeResult(bool Success, int StatusCode, string Message, string Sdp = "", string Server = "")
{
    /// <summary>True if the SDP describes a video stream.</summary>
    public bool HasVideo => Sdp.Contains("m=video", StringComparison.OrdinalIgnoreCase);
}

/// <summary>The stream paths a brand of camera uses.</summary>
public sealed record KnownStreamPaths(string Brand, string MainPath, string SubPath);

/// <summary>Small RTSP client for checking cameras, without starting ffmpeg.</summary>
public static class RtspProbe
{
    /// <summary>The stream paths the common brands use, tried in order when a camera has no stream at the path entered.</summary>
    public static IReadOnlyList<KnownStreamPaths> KnownPaths { get; } =
    [
        new("Tapo", "/stream1", "/stream2"),
        new("Hipcam (Ctronics and others)", "/11", "/12"),
        new("Hikvision", "/Streaming/Channels/101", "/Streaming/Channels/102"),
        new("Dahua, Amcrest and Lorex", "/cam/realmonitor?channel=1&subtype=0", "/cam/realmonitor?channel=1&subtype=1"),
        new("Reolink", "/h264Preview_01_main", "/h264Preview_01_sub"),
        new("Uniview", "/media/video1", "/media/video2"),
        new("Yoosee and similar", "/onvif1", "/onvif2"),
    ];

    /// <summary>
    /// Tries <see cref="KnownPaths"/> until the camera has a stream at one. Gives up at the first
    /// refused login or unanswered request: some cameras (Tapo) lock out all logins after several
    /// refusals, so only paths, never passwords, are guessed. The grid path is empty if that brand's
    /// low-resolution stream isn't there.
    /// </summary>
    public static async Task<KnownStreamPaths?> FindPathsAsync(string host, int port, string username, string password,
        TimeSpan timeout, string? alreadyTried = null, CancellationToken ct = default)
    {
        foreach (var known in KnownPaths)
        {
            if (string.Equals(known.MainPath, alreadyTried, StringComparison.OrdinalIgnoreCase))
                continue;
            var main = await DescribeAsync(host, port, username, password, known.MainPath, timeout, ct);
            if (main.Success)
            {
                var sub = await DescribeAsync(host, port, username, password, known.SubPath, timeout, ct);
                return sub.Success ? known : known with { SubPath = "" };
            }
            if (main.StatusCode is 0 or 401 or 403)
                return null;
        }
        return null;
    }

    /// <summary>Sends DESCRIBE (answering a password challenge) and reports what the camera said.</summary>
    public static async Task<RtspProbeResult> DescribeAsync(string host, int port, string username, string password, string path,
        TimeSpan timeout, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            using var client = new TcpClient { NoDelay = true };
            await client.ConnectAsync(host, port, cts.Token);
            var stream = client.GetStream();
            var reader = new RtspStreamReader(stream);
            var auth = new RtspAuthenticator(username, password);
            var uri = BuildUri(host, port, path);

            var cseq = 1;
            RtspMessage response;
            while (true)
            {
                var request = RtspMessage.Request("DESCRIBE", uri, cseq++);
                request.Set("Accept", "application/sdp");
                if (auth.GetHeader("DESCRIBE", uri) is { } header)
                    request.Set("Authorization", header);
                await stream.WriteAsync(request.ToBytes(), cts.Token);
                response = await ReadResponseAsync(reader, cts.Token)
                    ?? throw new IOException("The camera closed the connection.");
                if (response.StatusCode == 401 && !auth.HasChallenge && auth.HasCredentials && auth.Accept(response.GetAll("WWW-Authenticate")))
                    continue;
                break;
            }

            var server = response.Get("Server") ?? "";
            return response.StatusCode switch
            {
                200 => new RtspProbeResult(true, 200, "Connected.", Encoding.UTF8.GetString(response.Body), server),
                401 => new RtspProbeResult(false, 401, auth.HasCredentials ? "The camera refused the username or password." : "The camera needs a username and password.", Server: server),
                404 => new RtspProbeResult(false, 404, $"The camera has no stream at \"{path}\".", Server: server),
                _ => new RtspProbeResult(false, response.StatusCode, $"The camera replied: {response.StartLine}", Server: server),
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new RtspProbeResult(false, 0, "The camera didn't answer in time.");
        }
        catch (SocketException ex)
        {
            return new RtspProbeResult(false, 0, $"Couldn't connect: {ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return new RtspProbeResult(false, 0, $"Connection failed: {ex.Message}");
        }
    }

    /// <summary>True if something at host:port answers an RTSP OPTIONS request.</summary>
    public static async Task<bool> IsRtspAsync(string host, int port, TimeSpan timeout, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            using var client = new TcpClient { NoDelay = true };
            await client.ConnectAsync(host, port, cts.Token);
            var stream = client.GetStream();
            await stream.WriteAsync(RtspMessage.Request("OPTIONS", BuildUri(host, port, "/"), 1).ToBytes(), cts.Token);
            var response = await ReadResponseAsync(new RtspStreamReader(stream), cts.Token);
            return response is not null;
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or IOException or InvalidDataException)
        {
            return false;
        }
    }

    internal static string BuildUri(string host, int port, string path)
    {
        if (host.Contains(':') && !host.StartsWith('['))
            host = $"[{host}]";
        if (!path.StartsWith('/'))
            path = "/" + path;
        return $"rtsp://{host}:{port}{path}";
    }

    private static async Task<RtspMessage?> ReadResponseAsync(RtspStreamReader reader, CancellationToken ct)
    {
        while (await reader.ReadAsync(ct) is { } item)
        {
            if (item.Message is { IsResponse: true } response)
                return response;
        }
        return null;
    }
}
