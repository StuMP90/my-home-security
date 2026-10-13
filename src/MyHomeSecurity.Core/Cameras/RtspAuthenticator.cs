using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MyHomeSecurity.Core.Cameras;

/// <summary>
/// Answers RTSP authentication challenges (Digest MD5, or Basic) for one connection.
/// After the first challenge, every request gets an Authorization header.
/// </summary>
internal sealed class RtspAuthenticator
{
    private readonly string _username;
    private readonly string _password;
    private string? _scheme;
    private string _realm = "";
    private string _nonce = "";
    private string? _opaque;
    private string? _qop;
    private string? _algorithm;
    private int _nonceCount;

    public RtspAuthenticator(string username, string password)
    {
        _username = username ?? "";
        _password = password ?? "";
    }

    public bool HasCredentials => _username.Length > 0 || _password.Length > 0;
    public bool HasChallenge => _scheme is not null;

    /// <summary>Takes the WWW-Authenticate headers of a 401. False if none can be answered.</summary>
    public bool Accept(IEnumerable<string> challenges)
    {
        string? basic = null;
        foreach (var challenge in challenges)
        {
            var space = challenge.IndexOf(' ');
            var scheme = space < 0 ? challenge : challenge[..space];
            if (scheme.Equals("Digest", StringComparison.OrdinalIgnoreCase))
            {
                var p = ParseParameters(space < 0 ? "" : challenge[(space + 1)..]);
                var algorithm = p.GetValueOrDefault("algorithm", "MD5");
                if (!algorithm.Equals("MD5", StringComparison.OrdinalIgnoreCase))
                    continue;
                _scheme = "Digest";
                _realm = p.GetValueOrDefault("realm", "");
                _nonce = p.GetValueOrDefault("nonce", "");
                p.TryGetValue("opaque", out _opaque);
                p.TryGetValue("algorithm", out _algorithm);
                _qop = p.TryGetValue("qop", out var qop) && qop.Split(',').Any(q => q.Trim().Equals("auth", StringComparison.OrdinalIgnoreCase)) ? "auth" : null;
                _nonceCount = 0;
                return true;
            }
            if (scheme.Equals("Basic", StringComparison.OrdinalIgnoreCase))
                basic = challenge;
        }
        if (basic is null)
            return false;
        _scheme = "Basic";
        return true;
    }

    /// <summary>The Authorization header value for a request, or null before any challenge.</summary>
    public string? GetHeader(string method, string uri)
    {
        if (_scheme == "Basic")
            return "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_username}:{_password}"));
        if (_scheme != "Digest")
            return null;

        var ha1 = Md5Hex($"{_username}:{_realm}:{_password}");
        var ha2 = Md5Hex($"{method}:{uri}");
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"Digest username=\"{_username}\", realm=\"{_realm}\", nonce=\"{_nonce}\", uri=\"{uri}\"");
        if (_qop is not null)
        {
            _nonceCount++;
            var nc = _nonceCount.ToString("x8", CultureInfo.InvariantCulture);
            var cnonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
            var response = Md5Hex($"{ha1}:{_nonce}:{nc}:{cnonce}:{_qop}:{ha2}");
            sb.Append(CultureInfo.InvariantCulture, $", response=\"{response}\", qop={_qop}, nc={nc}, cnonce=\"{cnonce}\"");
        }
        else
        {
            sb.Append(CultureInfo.InvariantCulture, $", response=\"{Md5Hex($"{ha1}:{_nonce}:{ha2}")}\"");
        }
        if (_opaque is not null)
            sb.Append(CultureInfo.InvariantCulture, $", opaque=\"{_opaque}\"");
        // Only echoed when the camera named it: Tapo cameras refuse an answer with an algorithm
        // their challenge didn't mention.
        if (_algorithm is not null)
            sb.Append(CultureInfo.InvariantCulture, $", algorithm={_algorithm}");
        return sb.ToString();
    }

    internal static string Md5Hex(string text) =>
#pragma warning disable CA5351 // RTSP Digest is defined with MD5; it's what cameras speak.
        Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(text)));
#pragma warning restore CA5351

    /// <summary>Parses "a=1, b=\"x, y\"" into a dictionary (keys lower case).</summary>
    internal static Dictionary<string, string> ParseParameters(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && (text[i] == ',' || char.IsWhiteSpace(text[i])))
                i++;
            var keyStart = i;
            while (i < text.Length && text[i] != '=' && text[i] != ',')
                i++;
            var key = text[keyStart..i].Trim();
            if (i >= text.Length || text[i] == ',')
            {
                if (key.Length > 0)
                    result[key] = "";
                continue;
            }
            i++; // '='
            while (i < text.Length && char.IsWhiteSpace(text[i]))
                i++;
            string value;
            if (i < text.Length && text[i] == '"')
            {
                var sb = new StringBuilder();
                i++;
                while (i < text.Length && text[i] != '"')
                {
                    if (text[i] == '\\' && i + 1 < text.Length)
                        i++;
                    sb.Append(text[i]);
                    i++;
                }
                i++; // closing quote
                value = sb.ToString();
            }
            else
            {
                var valueStart = i;
                while (i < text.Length && text[i] != ',')
                    i++;
                value = text[valueStart..i].Trim();
            }
            if (key.Length > 0)
                result[key] = value;
        }
        return result;
    }
}
