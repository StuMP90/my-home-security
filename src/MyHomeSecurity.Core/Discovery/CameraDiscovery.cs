using MyHomeSecurity.Core.Diagnostics;
using MyHomeSecurity.Core.Models;

namespace MyHomeSecurity.Core.Discovery;

/// <summary>What ONVIF said about a camera's streams, ready to put into a <see cref="CameraProfile"/>.</summary>
public sealed record StreamPaths(string MainPath, string SubPath, int Port, string? Manufacturer, string? Model);

public static class CameraDiscovery
{
    /// <summary>WS-Discovery and a scan of the local networks together, merged by address.</summary>
    public static async Task<List<DiscoveredCamera>> FindAsync(IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var onvifTask = WsDiscovery.ProbeAsync(TimeSpan.FromSeconds(3), ct);
        var scanTask = SubnetScanner.ScanAsync(progress, ct: ct);
        await Task.WhenAll(onvifTask, scanTask);

        var merged = new Dictionary<string, DiscoveredCamera>(StringComparer.OrdinalIgnoreCase);
        foreach (var camera in scanTask.Result.Concat(onvifTask.Result))
        {
            if (merged.TryGetValue(camera.Host, out var existing))
                existing.Merge(camera);
            else
                merged[camera.Host] = camera;
        }
        Log.Info("discovery", $"Found {merged.Count} possible camera(s).");
        return [.. merged.Values];
    }

    /// <summary>
    /// Asks the camera over ONVIF for its stream addresses (needs the camera's login). The
    /// highest-resolution profile becomes the main stream and the lowest the grid stream.
    /// Returns null if the camera doesn't speak ONVIF; throws <see cref="OnvifException"/> if it
    /// rejects the login.
    /// </summary>
    public static async Task<StreamPaths?> GetStreamPathsAsync(string deviceAddress, string username, string password,
        CancellationToken ct = default)
    {
        using var client = new OnvifClient(deviceAddress, username, password);
        OnvifDeviceInfo? info = null;
        try
        {
            info = await client.GetDeviceInformationAsync(ct);
        }
        catch (OnvifException ex) when (!ex.NotAuthorized)
        {
            // Some cameras don't implement it; carry on.
        }
        var profiles = await client.GetProfilesAsync(ct);
        if (profiles.Count == 0)
            return null;
        var ordered = profiles.OrderByDescending(p => p.Width * p.Height).ToList();
        var main = await client.GetStreamUriAsync(ordered[0].Token, ct);
        if (main is null || !Uri.TryCreate(main, UriKind.Absolute, out var mainUri))
            return null;
        var sub = ordered.Count > 1 ? await client.GetStreamUriAsync(ordered[^1].Token, ct) : null;
        var subPath = sub is not null && Uri.TryCreate(sub, UriKind.Absolute, out var subUri) ? PathOf(subUri) : "";
        return new StreamPaths(PathOf(mainUri), subPath == PathOf(mainUri) ? "" : subPath,
            mainUri.IsDefaultPort || mainUri.Port <= 0 ? 554 : mainUri.Port, info?.Manufacturer, info?.Model);
    }

    /// <summary>Path and query, with any credentials a camera might embed left out.</summary>
    internal static string PathOf(Uri uri) => uri.PathAndQuery.Length == 0 ? "/" : uri.PathAndQuery;

    /// <summary>A new camera profile pre-filled from what discovery found (Tapo paths otherwise; Test connection finds other brands' paths).</summary>
    public static CameraProfile ToProfile(DiscoveredCamera found)
    {
        var profile = new CameraProfile
        {
            Host = found.Host,
            Kind = found.Kind,
            Port = found.Port,
            Manufacturer = found.Manufacturer,
            Model = found.Model,
            Name = found.Name ?? found.Model ?? "",
        };
        if (found.Kind == CameraKind.Mjpeg)
            profile.MjpegPath = "/videostream.cgi";
        return profile;
    }
}
