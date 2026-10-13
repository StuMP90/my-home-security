using MyHomeSecurity.Core.Models;

namespace MyHomeSecurity.Core.Discovery;

/// <summary>A camera (or something that looks like one) found on the local network.</summary>
public sealed class DiscoveredCamera
{
    public string Host { get; init; } = "";
    public CameraKind Kind { get; set; } = CameraKind.Rtsp;

    /// <summary>RTSP port, or the HTTP port for MJPEG cameras.</summary>
    public int Port { get; set; } = 554;

    /// <summary>ONVIF device service address, if the camera announced one (or answers on Tapo's port 2020).</summary>
    public string? OnvifAddress { get; set; }

    public string? Name { get; set; }
    public string? Manufacturer { get; set; }
    public string? Model { get; set; }

    /// <summary>How it was found: "ONVIF", "Network scan".</summary>
    public string Source { get; set; } = "";

    public string Description
    {
        get
        {
            var parts = new List<string>();
            var model = string.Join(' ', new[] { Manufacturer, Model }.Where(s => !string.IsNullOrWhiteSpace(s)));
            if (model.Length > 0)
                parts.Add(model);
            else if (!string.IsNullOrWhiteSpace(Name))
                parts.Add(Name);
            parts.Add(Kind == CameraKind.Mjpeg ? $"MJPEG on port {Port}" : $"RTSP on port {Port}");
            if (OnvifAddress is not null)
                parts.Add("ONVIF");
            return string.Join(" · ", parts);
        }
    }

    /// <summary>Copies in anything <paramref name="other"/> knows (same host) that this doesn't.</summary>
    public void Merge(DiscoveredCamera other)
    {
        OnvifAddress ??= other.OnvifAddress;
        Name ??= other.Name;
        Manufacturer ??= other.Manufacturer;
        Model ??= other.Model;
        if (Kind == CameraKind.Mjpeg && other.Kind == CameraKind.Rtsp)
        {
            Kind = CameraKind.Rtsp;
            Port = other.Port;
        }
        if (!Source.Contains(other.Source, StringComparison.Ordinal))
            Source = Source.Length == 0 ? other.Source : $"{Source}, {other.Source}";
    }
}
