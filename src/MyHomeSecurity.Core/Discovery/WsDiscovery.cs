using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
using MyHomeSecurity.Core.Diagnostics;

namespace MyHomeSecurity.Core.Discovery;

/// <summary>
/// ONVIF WS-Discovery: asks the local network (multicast 239.255.255.250:3702, never beyond the
/// router) which video devices are there. Only runs when the user asks.
/// </summary>
public static class WsDiscovery
{
    private static readonly IPEndPoint MulticastEndpoint = new(IPAddress.Parse("239.255.255.250"), 3702);

    public static async Task<List<DiscoveredCamera>> ProbeAsync(TimeSpan listenFor, CancellationToken ct = default)
    {
        var messageId = "uuid:" + Guid.NewGuid();
        var probe = Encoding.UTF8.GetBytes(BuildProbe(messageId));
        var found = new Dictionary<string, DiscoveredCamera>(StringComparer.OrdinalIgnoreCase);
        var tasks = new List<Task>();
        foreach (var address in LocalIPv4Addresses())
            tasks.Add(ProbeFromAsync(address, probe, listenFor, found, ct));
        if (tasks.Count == 0)
            tasks.Add(ProbeFromAsync(IPAddress.Any, probe, listenFor, found, ct));
        await Task.WhenAll(tasks);
        lock (found)
            return [.. found.Values];
    }

    private static async Task ProbeFromAsync(IPAddress local, byte[] probe, TimeSpan listenFor,
        Dictionary<string, DiscoveredCamera> found, CancellationToken ct)
    {
        try
        {
            using var udp = new UdpClient(new IPEndPoint(local, 0));
            udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
            if (!local.Equals(IPAddress.Any))
                udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());
            // UDP can drop packets; send twice.
            await udp.SendAsync(probe, MulticastEndpoint, ct);
            await Task.Delay(100, ct);
            await udp.SendAsync(probe, MulticastEndpoint, ct);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(listenFor);
            while (true)
            {
                UdpReceiveResult result;
                try
                {
                    result = await udp.ReceiveAsync(cts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    return;
                }
                foreach (var camera in ParseProbeMatches(Encoding.UTF8.GetString(result.Buffer), result.RemoteEndPoint.Address))
                {
                    lock (found)
                    {
                        if (found.TryGetValue(camera.Host, out var existing))
                            existing.Merge(camera);
                        else
                            found[camera.Host] = camera;
                    }
                }
            }
        }
        catch (SocketException ex)
        {
            Log.Warn("discovery", $"WS-Discovery from {local} failed: {ex.Message}");
        }
    }

    internal static string BuildProbe(string messageId) =>
        $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <e:Envelope xmlns:e="http://www.w3.org/2003/05/soap-envelope" xmlns:w="http://schemas.xmlsoap.org/ws/2004/08/addressing" xmlns:d="http://schemas.xmlsoap.org/ws/2005/04/discovery" xmlns:dn="http://www.onvif.org/ver10/network/wsdl">
          <e:Header>
            <w:MessageID>{messageId}</w:MessageID>
            <w:To e:mustUnderstand="true">urn:schemas-xmlsoap-org:ws:2005:04:discovery</w:To>
            <w:Action e:mustUnderstand="true">http://schemas.xmlsoap.org/ws/2005/04/discovery/Probe</w:Action>
          </e:Header>
          <e:Body>
            <d:Probe><d:Types>dn:NetworkVideoTransmitter</d:Types></d:Probe>
          </e:Body>
        </e:Envelope>
        """;

    /// <summary>Reads the cameras out of a ProbeMatches reply.</summary>
    internal static List<DiscoveredCamera> ParseProbeMatches(string xml, IPAddress? sender)
    {
        var result = new List<DiscoveredCamera>();
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException)
        {
            return result;
        }
        foreach (var match in doc.Descendants().Where(e => e.Name.LocalName == "ProbeMatch"))
        {
            var xaddrs = match.Elements().FirstOrDefault(e => e.Name.LocalName == "XAddrs")?.Value
                .Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
            // Prefer an address on the network the reply came from (devices may list several).
            var xaddr = xaddrs.FirstOrDefault(a => sender is not null && a.Contains("//" + sender + ":", StringComparison.Ordinal) || a.Contains("//" + sender + "/", StringComparison.Ordinal))
                ?? xaddrs.FirstOrDefault(a => Uri.TryCreate(a, UriKind.Absolute, out var u) && u.HostNameType == UriHostNameType.IPv4)
                ?? xaddrs.FirstOrDefault();
            string? host = null;
            if (xaddr is not null && Uri.TryCreate(xaddr, UriKind.Absolute, out var uri))
                host = uri.Host;
            host ??= sender?.ToString();
            if (string.IsNullOrEmpty(host))
                continue;

            var camera = new DiscoveredCamera { Host = host, OnvifAddress = xaddr, Source = "ONVIF" };
            var scopes = match.Elements().FirstOrDefault(e => e.Name.LocalName == "Scopes")?.Value
                .Split((char[])[' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries) ?? [];
            foreach (var scope in scopes)
            {
                if (TryScope(scope, "name", out var name))
                    camera.Name = name;
                else if (TryScope(scope, "hardware", out var hardware))
                    camera.Model = hardware;
                else if (TryScope(scope, "mfr", out var mfr) || TryScope(scope, "manufacturer", out mfr))
                    camera.Manufacturer = mfr;
            }
            result.Add(camera);
        }
        return result;
    }

    private static bool TryScope(string scope, string key, out string value)
    {
        const string prefix = "onvif://www.onvif.org/";
        value = "";
        if (!scope.StartsWith(prefix + key + "/", StringComparison.OrdinalIgnoreCase))
            return false;
        value = Uri.UnescapeDataString(scope[(prefix.Length + key.Length + 1)..]).Replace('_', ' ').Trim();
        return value.Length > 0;
    }

    /// <summary>IPv4 addresses of this computer's working, non-loopback network connections.</summary>
    public static List<IPAddress> LocalIPv4Addresses() =>
        [.. LocalIPv4Networks().Select(n => n.Address)];

    /// <summary>IPv4 address and prefix length of each working, non-loopback network connection.</summary>
    public static List<(IPAddress Address, int PrefixLength)> LocalIPv4Networks()
    {
        var result = new List<(IPAddress, int)>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;
                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ua.Address))
                        continue;
                    // Skip link-local (169.254.x.x): no DHCP, nothing to find.
                    var b = ua.Address.GetAddressBytes();
                    if (b[0] == 169 && b[1] == 254)
                        continue;
                    result.Add((ua.Address, ua.PrefixLength));
                }
            }
        }
        catch (NetworkInformationException ex)
        {
            Log.Warn("discovery", "Couldn't list network connections.", ex);
        }
        return result;
    }
}
