namespace MyHomeSecurity.Core.Models;

/// <summary>Everything the app stores, all of it inside the encrypted vault.</summary>
public sealed class VaultData
{
    public List<CameraProfile> Cameras { get; set; } = new();
    public HomeAssistantSettings HomeAssistant { get; set; } = new();
    public AppSettings Settings { get; set; } = new();
}

public sealed class AppSettings
{
    /// <summary>Minutes without input before the app locks. 0 = never.</summary>
    public int AutoLockMinutes { get; set; } = 30;

    /// <summary>Use the GPU to decode video (VAAPI/QSV/NVDEC on Linux, D3D11/DXVA on Windows) when available.</summary>
    public bool HardwareDecoding { get; set; } = true;

    /// <summary>Frames per second for grid tiles. Lower is lighter.</summary>
    public int GridFps { get; set; } = 5;

    /// <summary>
    /// Grid tiles decode key frames only (typically one every 1–2 seconds). Uses a fraction of the
    /// CPU of full decoding, at the cost of a slideshow-like grid.
    /// </summary>
    public bool GridKeyframesOnly { get; set; }

    /// <summary>Frames per second for the single-camera view. 0 = the camera's own rate.</summary>
    public int LiveFps { get; set; } = 15;

    /// <summary>Size the single-camera view is decoded to (its height; width follows at 16:9).</summary>
    public int LiveHeight { get; set; } = 1080;

    /// <summary>Show the grid even while a single camera is open. Off: the grid stops, saving CPU.</summary>
    public bool KeepGridRunning { get; set; }

    public string? SnapshotFolder { get; set; }
    public string? RecordingFolder { get; set; }
    public RecordingFormat RecordingFormat { get; set; } = RecordingFormat.Mp4;
    public SnapshotFormat SnapshotFormat { get; set; } = SnapshotFormat.Jpeg;

    /// <summary>Recordings stop on their own after this many minutes. 0 = no limit.</summary>
    public int MaxRecordingMinutes { get; set; } = 60;

    public string EffectiveSnapshotFolder =>
        string.IsNullOrWhiteSpace(SnapshotFolder) ? AppPaths.DefaultSnapshotDirectory : SnapshotFolder;

    public string EffectiveRecordingFolder =>
        string.IsNullOrWhiteSpace(RecordingFolder) ? AppPaths.DefaultRecordingDirectory : RecordingFolder;
}

public enum RecordingFormat
{
    /// <summary>Fragmented MP4: plays everywhere, survives the app being killed mid-recording.</summary>
    Mp4,
    /// <summary>Matroska: keeps the camera's audio as-is.</summary>
    Mkv,
}

public enum SnapshotFormat
{
    Jpeg,
    Png,
}
