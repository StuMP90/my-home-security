using System.Net;
using System.Net.Sockets;
using MyHomeSecurity.Core.Cameras;
using MyHomeSecurity.Core.Models;

namespace MyHomeSecurity.Core.Discovery;

/// <summary>
/// Looks for cameras that don't answer WS-Discovery by trying the usual camera ports on every
/// address of this computer's local networks (at most a /24 each). Only runs when the user asks.
/// </summary>
public static class SubnetScanner
{
    public const int RtspPort = 554;
    public const int TapoOnvifPort = 2020;
    public static readonly int[] HttpPorts = [80, 81, 8080];

    public static async Task<List<DiscoveredCamera>> ScanAsync(IProgress<double>? progress = null,
        TimeSpan? connectTimeout = null, int concurrency = 64, CancellationToken ct = default)
    {
        var timeout = connectTimeout ?? TimeSpan.FromMilliseconds(300);
        var self = WsDiscovery.LocalIPv4Networks().Select(n => n.Address).ToHashSet();
        var hosts = WsDiscovery.LocalIPv4Networks()
            .SelectMany(n => HostsToScan(n.Address, n.PrefixLength))
            .Where(a => !self.Contains(a))
            .Distinct()
            .ToList();

        var found = new List<DiscoveredCamera>();
        var done = 0;
        using var gate = new SemaphoreSlim(concurrency);
        var tasks = hosts.Select(async host =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var camera = await ProbeHostAsync(host, timeout, ct);
                if (camera is not null)
                    lock (found)
                        found.Add(camera);
            }
            finally
            {
                gate.Release();
                progress?.Report((double)Interlocked.Increment(ref done) / hosts.Count);
            }
        }).ToList();
        await Task.WhenAll(tasks);
        return [.. found.OrderBy(c => IPAddress.TryParse(c.Host, out var a) ? SortKey(a) : uint.MaxValue)];
    }

    /// <summary>Tries one address: RTSP first (confirmed by an OPTIONS request), then HTTP for MJPEG cameras.</summary>
    public static async Task<DiscoveredCamera?> ProbeHostAsync(IPAddress host, TimeSpan timeout, CancellationToken ct)
    {
        var address = host.ToString();
        if (await IsOpenAsync(host, RtspPort, timeout, ct)
            && await RtspProbe.IsRtspAsync(address, RtspPort, TimeSpan.FromSeconds(2), ct))
        {
            var camera = new DiscoveredCamera { Host = address, Kind = CameraKind.Rtsp, Port = RtspPort, Source = "Network scan" };
            if (await IsOpenAsync(host, TapoOnvifPort, timeout, ct))
                camera.OnvifAddress = OnvifClient.DeviceAddressFor(address, TapoOnvifPort);
            return camera;
        }
        // HTTP alone means nothing (routers, printers...), so only offer it if it serves the MJPEG CGI.
        foreach (var port in HttpPorts)
        {
            if (await IsOpenAsync(host, port, timeout, ct) && await LooksLikeMjpegCameraAsync(address, port, ct))
                return new DiscoveredCamera { Host = address, Kind = CameraKind.Mjpeg, Port = port, Source = "Network scan" };
        }
        return null;
    }

    /// <summary>
    /// True if the MJPEG path exists (any answer other than 404, including 401 which needs the
    /// login). Nothing is sent but the request line: no credentials.
    /// </summary>
    private static async Task<bool> LooksLikeMjpegCameraAsync(string host, int port, CancellationToken ct)
    {
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, UseCookies = false, AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(2),
        };
        try
        {
            using var response = await http.GetAsync($"http://{host}:{port}/videostream.cgi", HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return true;
            var type = response.Content.Headers.ContentType?.MediaType ?? "";
            return response.IsSuccessStatusCode && type.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return false;
        }
    }

    public static async Task<bool> IsOpenAsync(IPAddress host, int port, TimeSpan timeout, CancellationToken ct)
    {
        using var socket = new Socket(host.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await socket.ConnectAsync(host, port, cts.Token);
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>
    /// Every host address in the address's network; networks bigger than a /24 are limited to the
    /// /24 the computer is in, so a scan never sweeps more than 254 addresses per connection.
    /// </summary>
    internal static IEnumerable<IPAddress> HostsToScan(IPAddress address, int prefixLength)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
            yield break;
        // Only private ranges: never scan someone else's network.
        if (!IsPrivate(address))
            yield break;
        var prefix = Math.Clamp(prefixLength, 24, 30);
        var ip = SortKey(address);
        var mask = uint.MaxValue << (32 - prefix);
        var network = ip & mask;
        var broadcast = network | ~mask;
        for (var a = network + 1; a < broadcast; a++)
            yield return new IPAddress([(byte)(a >> 24), (byte)(a >> 16), (byte)(a >> 8), (byte)a]);
    }

    internal static bool IsPrivate(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return b.Length == 4 && (b[0] == 10 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 192 && b[1] == 168);
    }

    private static uint SortKey(IPAddress a)
    {
        var b = a.GetAddressBytes();
        return b.Length == 4 ? (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]) : uint.MaxValue;
    }
}
