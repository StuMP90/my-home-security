using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace MyHomeSecurity.Core.Discovery;

public sealed record OnvifDeviceInfo(string? Manufacturer, string? Model, string? Firmware);

public sealed record OnvifProfile(string Token, string Name, int Width, int Height);

/// <summary>
/// Minimal ONVIF client (SOAP 1.2 with WS-Security UsernameToken PasswordDigest, so the password
/// itself is never sent). Used only while adding a camera, to find its stream paths.
/// </summary>
public sealed class OnvifClient : IDisposable
{
    private const string DeviceNs = "http://www.onvif.org/ver10/device/wsdl";
    private const string MediaNs = "http://www.onvif.org/ver10/media/wsdl";

    private readonly HttpClient _http;
    private readonly string _deviceAddress;
    private readonly string _username;
    private readonly string _password;
    private string? _mediaAddress;
    private TimeSpan _clockOffset;

    public OnvifClient(string deviceAddress, string username, string password, TimeSpan? timeout = null)
    {
        _deviceAddress = deviceAddress;
        _username = username;
        _password = password;
        _http = new HttpClient(new SocketsHttpHandler { UseProxy = false, UseCookies = false })
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(5),
        };
    }

    /// <summary>The usual device service address for a host (Tapo answers on port 2020).</summary>
    public static string DeviceAddressFor(string host, int port = 80) =>
        $"http://{(host.Contains(':') ? $"[{host}]" : host)}{(port == 80 ? "" : $":{port}")}/onvif/device_service";

    public async Task<OnvifDeviceInfo> GetDeviceInformationAsync(CancellationToken ct = default)
    {
        await SyncClockAsync(ct);
        var body = await CallAsync(_deviceAddress, $"<GetDeviceInformation xmlns=\"{DeviceNs}\"/>", ct);
        return ParseDeviceInformation(body);
    }

    public async Task<List<OnvifProfile>> GetProfilesAsync(CancellationToken ct = default)
    {
        var media = await GetMediaAddressAsync(ct);
        var body = await CallAsync(media, $"<GetProfiles xmlns=\"{MediaNs}\"/>", ct);
        return ParseProfiles(body);
    }

    public async Task<string?> GetStreamUriAsync(string profileToken, CancellationToken ct = default)
    {
        var media = await GetMediaAddressAsync(ct);
        var request = $"""
            <GetStreamUri xmlns="{MediaNs}">
              <StreamSetup>
                <Stream xmlns="http://www.onvif.org/ver10/schema">RTP-Unicast</Stream>
                <Transport xmlns="http://www.onvif.org/ver10/schema"><Protocol>RTSP</Protocol></Transport>
              </StreamSetup>
              <ProfileToken>{SecurityElement(profileToken)}</ProfileToken>
            </GetStreamUri>
            """;
        var body = await CallAsync(media, request, ct);
        return FindValue(body, "Uri");
    }

    private async Task<string> GetMediaAddressAsync(CancellationToken ct)
    {
        if (_mediaAddress is not null)
            return _mediaAddress;
        await SyncClockAsync(ct);
        try
        {
            var body = await CallAsync(_deviceAddress,
                $"<GetCapabilities xmlns=\"{DeviceNs}\"><Category>Media</Category></GetCapabilities>", ct);
            _mediaAddress = ParseMediaAddress(body, _deviceAddress);
        }
        catch (OnvifException)
        {
            _mediaAddress = _deviceAddress;
        }
        return _mediaAddress;
    }

    private bool _clockSynced;

    /// <summary>
    /// The digest includes a timestamp that cameras reject if their clock differs too much from ours;
    /// GetSystemDateAndTime needs no login, so use it to work out the difference.
    /// </summary>
    private async Task SyncClockAsync(CancellationToken ct)
    {
        if (_clockSynced)
            return;
        _clockSynced = true;
        try
        {
            var body = await CallAsync(_deviceAddress, $"<GetSystemDateAndTime xmlns=\"{DeviceNs}\"/>", ct, authenticate: false);
            var utc = ParseUtcDateTime(body);
            if (utc is not null)
                _clockOffset = utc.Value - DateTime.UtcNow;
        }
        catch (OnvifException)
        {
            // Not fatal; try with our own clock.
        }
    }

    private async Task<XElement> CallAsync(string address, string request, CancellationToken ct, bool authenticate = true)
    {
        var envelope = BuildEnvelope(request, authenticate && _username.Length > 0
            ? BuildSecurityHeader(_username, _password, DateTime.UtcNow + _clockOffset, RandomNumberGenerator.GetBytes(16))
            : "");
        using var content = new StringContent(envelope, Encoding.UTF8, "application/soap+xml");
        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsync(address, content, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new OnvifException($"Couldn't connect: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new OnvifException("The camera didn't answer in time.", ex);
        }
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            XDocument doc;
            try
            {
                doc = XDocument.Parse(text);
            }
            catch (System.Xml.XmlException ex)
            {
                throw new OnvifException(response.IsSuccessStatusCode
                    ? "The camera's reply wasn't valid ONVIF."
                    : $"The camera answered {(int)response.StatusCode} {response.ReasonPhrase}.", ex);
            }
            var body = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Body")
                ?? throw new OnvifException("The camera's reply had no SOAP body.");
            var fault = body.Elements().FirstOrDefault(e => e.Name.LocalName == "Fault");
            if (fault is not null)
            {
                var reason = FindValue(fault, "Text") ?? FindValue(fault, "faultstring") ?? "unknown error";
                var notAuthorized = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    || fault.Descendants().Any(e => e.Value.Contains("NotAuthorized", StringComparison.OrdinalIgnoreCase)
                        || e.Value.Contains("FailedAuthentication", StringComparison.OrdinalIgnoreCase));
                throw new OnvifException(notAuthorized ? "The camera rejected the username or password." : $"The camera reported: {reason}",
                    notAuthorized: notAuthorized);
            }
            if (!response.IsSuccessStatusCode)
                throw new OnvifException($"The camera answered {(int)response.StatusCode} {response.ReasonPhrase}.",
                    notAuthorized: response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);
            return body;
        }
    }

    internal static string BuildEnvelope(string body, string securityHeader) =>
        $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope">
          <s:Header>{securityHeader}</s:Header>
          <s:Body>{body}</s:Body>
        </s:Envelope>
        """;

    /// <summary>WS-Security UsernameToken: digest = Base64(SHA1(nonce + created + password)).</summary>
    internal static string BuildSecurityHeader(string username, string password, DateTime createdUtc, byte[] nonce)
    {
        var created = createdUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture);
        var digest = Convert.ToBase64String(SHA1.HashData([.. nonce, .. Encoding.UTF8.GetBytes(created + password)]));
        return $"""
            <Security s:mustUnderstand="1" xmlns="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd">
              <UsernameToken>
                <Username>{SecurityElement(username)}</Username>
                <Password Type="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-username-token-profile-1.0#PasswordDigest">{digest}</Password>
                <Nonce EncodingType="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-soap-message-security-1.0#Base64Binary">{Convert.ToBase64String(nonce)}</Nonce>
                <Created xmlns="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd">{created}</Created>
              </UsernameToken>
            </Security>
            """;
    }

    private static string SecurityElement(string s) => System.Security.SecurityElement.Escape(s) ?? "";

    internal static string? FindValue(XElement root, string localName) =>
        root.Descendants().FirstOrDefault(e => e.Name.LocalName == localName)?.Value.Trim();

    internal static OnvifDeviceInfo ParseDeviceInformation(XElement body) =>
        new(NullIfEmpty(FindValue(body, "Manufacturer")), NullIfEmpty(FindValue(body, "Model")), NullIfEmpty(FindValue(body, "FirmwareVersion")));

    internal static string ParseMediaAddress(XElement body, string fallback)
    {
        var media = body.Descendants().FirstOrDefault(e => e.Name.LocalName == "Media");
        var xaddr = media is null ? null : FindValue(media, "XAddr");
        return Uri.TryCreate(xaddr, UriKind.Absolute, out _) ? xaddr! : fallback;
    }

    internal static List<OnvifProfile> ParseProfiles(XElement body)
    {
        var result = new List<OnvifProfile>();
        foreach (var p in body.Descendants().Where(e => e.Name.LocalName == "Profiles"))
        {
            var token = p.Attribute("token")?.Value;
            if (string.IsNullOrEmpty(token))
                continue;
            var name = p.Elements().FirstOrDefault(e => e.Name.LocalName == "Name")?.Value.Trim() ?? token;
            var resolution = p.Descendants().FirstOrDefault(e => e.Name.LocalName == "VideoEncoderConfiguration")?
                .Descendants().FirstOrDefault(e => e.Name.LocalName == "Resolution");
            int.TryParse(resolution is null ? null : FindValue(resolution, "Width"), out var w);
            int.TryParse(resolution is null ? null : FindValue(resolution, "Height"), out var h);
            result.Add(new OnvifProfile(token, name, w, h));
        }
        return result;
    }

    internal static DateTime? ParseUtcDateTime(XElement body)
    {
        var utc = body.Descendants().FirstOrDefault(e => e.Name.LocalName == "UTCDateTime");
        if (utc is null)
            return null;
        int Get(string parent, string child)
        {
            var p = utc.Descendants().FirstOrDefault(e => e.Name.LocalName == parent);
            return int.TryParse(p is null ? null : FindValue(p, child), out var v) ? v : -1;
        }
        try
        {
            return new DateTime(Get("Date", "Year"), Get("Date", "Month"), Get("Date", "Day"),
                Get("Time", "Hour"), Get("Time", "Minute"), Get("Time", "Second"), DateTimeKind.Utc);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    public void Dispose() => _http.Dispose();
}

public sealed class OnvifException(string message, Exception? inner = null, bool notAuthorized = false) : Exception(message, inner)
{
    public bool NotAuthorized { get; } = notAuthorized;
}
