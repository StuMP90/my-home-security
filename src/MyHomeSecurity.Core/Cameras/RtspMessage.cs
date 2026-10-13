using System.Globalization;
using System.Text;

namespace MyHomeSecurity.Core.Cameras;

/// <summary>An RTSP request or response: start line, headers (in order) and body.</summary>
internal sealed class RtspMessage
{
    public RtspMessage(string startLine)
    {
        StartLine = startLine;
    }

    public string StartLine { get; set; }
    public List<KeyValuePair<string, string>> Headers { get; } = [];
    public byte[] Body { get; set; } = [];

    public bool IsResponse => StartLine.StartsWith("RTSP/", StringComparison.OrdinalIgnoreCase);

    /// <summary>Status code of a response, or 0.</summary>
    public int StatusCode
    {
        get
        {
            if (!IsResponse)
                return 0;
            var parts = StartLine.Split(' ', 3);
            return parts.Length > 1 && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var code) ? code : 0;
        }
    }

    /// <summary>Method of a request ("DESCRIBE"...), or empty.</summary>
    public string Method => IsResponse ? "" : StartLine.Split(' ', 3)[0];

    /// <summary>URI of a request, or empty.</summary>
    public string Uri
    {
        get
        {
            var parts = StartLine.Split(' ', 3);
            return !IsResponse && parts.Length > 1 ? parts[1] : "";
        }
        set
        {
            var parts = StartLine.Split(' ', 3);
            StartLine = parts.Length == 3 ? $"{parts[0]} {value} {parts[2]}" : $"{parts[0]} {value} RTSP/1.0";
        }
    }

    public string? CSeq => Get("CSeq");

    public string? Get(string name)
    {
        foreach (var h in Headers)
        {
            if (string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase))
                return h.Value;
        }
        return null;
    }

    public IEnumerable<string> GetAll(string name) =>
        Headers.Where(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase)).Select(h => h.Value);

    public void Set(string name, string value)
    {
        for (var i = 0; i < Headers.Count; i++)
        {
            if (string.Equals(Headers[i].Key, name, StringComparison.OrdinalIgnoreCase))
            {
                Headers[i] = new(Headers[i].Key, value);
                return;
            }
        }
        Headers.Add(new(name, value));
    }

    public void Remove(string name) =>
        Headers.RemoveAll(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase));

    public RtspMessage Clone()
    {
        var copy = new RtspMessage(StartLine) { Body = Body };
        copy.Headers.AddRange(Headers);
        return copy;
    }

    public byte[] ToBytes()
    {
        var sb = new StringBuilder();
        sb.Append(StartLine).Append("\r\n");
        foreach (var h in Headers)
        {
            if (string.Equals(h.Key, "Content-Length", StringComparison.OrdinalIgnoreCase))
                continue;
            sb.Append(h.Key).Append(": ").Append(h.Value).Append("\r\n");
        }
        if (Body.Length > 0)
            sb.Append("Content-Length: ").Append(Body.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        sb.Append("\r\n");
        var head = Encoding.UTF8.GetBytes(sb.ToString());
        if (Body.Length == 0)
            return head;
        var all = new byte[head.Length + Body.Length];
        head.CopyTo(all, 0);
        Body.CopyTo(all, head.Length);
        return all;
    }

    public static RtspMessage Request(string method, string uri, int cseq)
    {
        var m = new RtspMessage($"{method} {uri} RTSP/1.0");
        m.Set("CSeq", cseq.ToString(CultureInfo.InvariantCulture));
        m.Set("User-Agent", "MyHomeSecurity");
        return m;
    }
}

/// <summary>One item read from an RTSP connection: a message, or an interleaved ($) data frame.</summary>
internal readonly struct RtspItem
{
    public RtspItem(RtspMessage message)
    {
        Message = message;
        Data = default;
    }

    public RtspItem(ReadOnlyMemory<byte> data)
    {
        Message = null;
        Data = data;
    }

    public RtspMessage? Message { get; }

    /// <summary>The whole interleaved frame, including its 4-byte header. Valid until the next read.</summary>
    public ReadOnlyMemory<byte> Data { get; }
}

/// <summary>Reads RTSP messages and interleaved frames from a stream, with its own buffer.</summary>
internal sealed class RtspStreamReader
{
    private const int MaxHeaderBytes = 32 * 1024;
    private const int MaxBodyBytes = 1024 * 1024;
    private static readonly byte[] HeaderEnd = "\r\n\r\n"u8.ToArray();

    private readonly Stream _stream;
    private byte[] _buffer = new byte[128 * 1024];
    private int _start;
    private int _end;

    public RtspStreamReader(Stream stream)
    {
        _stream = stream;
    }

    private int Available => _end - _start;

    /// <summary>The next item, or null at the end of the stream.</summary>
    public async ValueTask<RtspItem?> ReadAsync(CancellationToken ct)
    {
        if (!await EnsureAsync(1, ct))
            return null;

        if (_buffer[_start] == (byte)'$')
        {
            if (!await EnsureAsync(4, ct))
                return null;
            var length = (_buffer[_start + 2] << 8) | _buffer[_start + 3];
            if (!await EnsureAsync(4 + length, ct))
                return null;
            var data = new ReadOnlyMemory<byte>(_buffer, _start, 4 + length);
            _start += 4 + length;
            return new RtspItem(data);
        }

        int headerLength;
        while (true)
        {
            var at = _buffer.AsSpan(_start, Available).IndexOf(HeaderEnd);
            if (at >= 0)
            {
                headerLength = at + HeaderEnd.Length;
                break;
            }
            if (Available >= MaxHeaderBytes)
                throw new InvalidDataException("RTSP header too long.");
            if (!await FillAsync(ct))
                return null;
        }

        var text = Encoding.UTF8.GetString(_buffer, _start, headerLength - HeaderEnd.Length);
        _start += headerLength;
        var lines = text.Split("\r\n");
        // Tolerate stray blank lines between messages.
        var first = 0;
        while (first < lines.Length && lines[first].Length == 0)
            first++;
        if (first >= lines.Length)
            return await ReadAsync(ct);

        var message = new RtspMessage(lines[first]);
        for (var i = first + 1; i < lines.Length; i++)
        {
            var colon = lines[i].IndexOf(':');
            if (colon <= 0)
                continue;
            message.Headers.Add(new(lines[i][..colon].Trim(), lines[i][(colon + 1)..].Trim()));
        }

        if (int.TryParse(message.Get("Content-Length"), NumberStyles.None, CultureInfo.InvariantCulture, out var bodyLength) && bodyLength > 0)
        {
            if (bodyLength > MaxBodyBytes)
                throw new InvalidDataException("RTSP body too long.");
            if (!await EnsureAsync(bodyLength, ct))
                return null;
            message.Body = _buffer.AsSpan(_start, bodyLength).ToArray();
            _start += bodyLength;
        }
        return new RtspItem(message);
    }

    private async ValueTask<bool> EnsureAsync(int count, CancellationToken ct)
    {
        while (Available < count)
        {
            if (!await FillAsync(ct))
                return false;
        }
        return true;
    }

    private async ValueTask<bool> FillAsync(CancellationToken ct)
    {
        if (_start > 0)
        {
            Buffer.BlockCopy(_buffer, _start, _buffer, 0, Available);
            _end -= _start;
            _start = 0;
        }
        if (_end == _buffer.Length)
            Array.Resize(ref _buffer, _buffer.Length * 2);
        var read = await _stream.ReadAsync(_buffer.AsMemory(_end), ct);
        if (read == 0)
            return false;
        _end += read;
        return true;
    }
}
