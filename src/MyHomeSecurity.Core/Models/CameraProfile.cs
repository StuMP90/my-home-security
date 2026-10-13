namespace MyHomeSecurity.Core.Models;

public enum CameraKind
{
    /// <summary>RTSP (Tapo, most ONVIF cameras): a main (high-resolution) stream and a sub (low-resolution) stream.</summary>
    Rtsp,
    /// <summary>Older HTTP cameras that serve an MJPEG stream (videostream.cgi).</summary>
    Mjpeg,
}

public sealed class CameraProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public CameraKind Kind { get; set; } = CameraKind.Rtsp;
    public string Host { get; set; } = "";

    /// <summary>RTSP port (554) or HTTP port for MJPEG cameras.</summary>
    public int Port { get; set; } = 554;

    public string Username { get; set; } = "";
    public string Password { get; set; } = "";

    /// <summary>RTSP path of the high-resolution stream, e.g. "/stream1" on Tapo.</summary>
    public string MainPath { get; set; } = "/stream1";

    /// <summary>RTSP path of the low-resolution stream used in the grid, e.g. "/stream2" on Tapo. Empty = use the main stream.</summary>
    public string SubPath { get; set; } = "/stream2";

    /// <summary>MJPEG stream path (with no credentials: they are added at connection time).</summary>
    public string MjpegPath { get; set; } = "/videostream.cgi";

    public bool Enabled { get; set; } = true;

    /// <summary>From ONVIF, when discovered that way. Informational only.</summary>
    public string? Manufacturer { get; set; }
    public string? Model { get; set; }

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Host : Name;

    public CameraProfile Clone() => (CameraProfile)MemberwiseClone();
}
