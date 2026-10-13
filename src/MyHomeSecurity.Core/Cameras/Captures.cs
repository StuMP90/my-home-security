using System.Globalization;
using MyHomeSecurity.Core.Models;

namespace MyHomeSecurity.Core.Cameras;

public enum CaptureKind
{
    Snapshot,
    Recording,
}

public sealed record CaptureFile(string Path, CaptureKind Kind, DateTime Modified, long Size)
{
    public string Name => System.IO.Path.GetFileName(Path);
}

/// <summary>Names and lists snapshot and recording files.</summary>
public static class Captures
{
    public static readonly string[] SnapshotExtensions = [".jpg", ".jpeg", ".png"];
    public static readonly string[] RecordingExtensions = [".mp4", ".mkv"];

    /// <summary>A new file path: "&lt;camera&gt;-yyyyMMdd-HHmmss.ext" in <paramref name="folder"/>, never an existing file.</summary>
    public static string NewPath(string folder, CameraProfile camera, string extension, DateTime? now = null)
    {
        Directory.CreateDirectory(folder);
        var stamp = (now ?? DateTime.Now).ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var stem = $"{SafeName(camera.DisplayName)}-{stamp}";
        var path = Path.Combine(folder, stem + extension);
        for (var n = 2; File.Exists(path); n++)
            path = Path.Combine(folder, $"{stem}-{n}{extension}");
        return path;
    }

    /// <summary>A camera name made safe for a file name on Windows and Linux.</summary>
    public static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['<', '>', ':', '"', '/', '\\', '|', '?', '*']).ToHashSet();
        var chars = name.Trim().Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray();
        var safe = new string(chars).Trim('.', ' ');
        return safe.Length == 0 ? "camera" : safe;
    }

    /// <summary>Snapshots and recordings in the configured folders, newest first.</summary>
    public static List<CaptureFile> List(AppSettings settings)
    {
        var result = new List<CaptureFile>();
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        Add(settings.EffectiveSnapshotFolder);
        Add(settings.EffectiveRecordingFolder);
        return [.. result.OrderByDescending(f => f.Modified)];

        void Add(string folder)
        {
            if (!Directory.Exists(folder))
                return;
            foreach (var file in new DirectoryInfo(folder).EnumerateFiles())
            {
                var ext = file.Extension.ToLowerInvariant();
                CaptureKind kind;
                if (SnapshotExtensions.Contains(ext))
                    kind = CaptureKind.Snapshot;
                else if (RecordingExtensions.Contains(ext))
                    kind = CaptureKind.Recording;
                else
                    continue;
                if (seen.Add(file.FullName))
                    result.Add(new CaptureFile(file.FullName, kind, file.LastWriteTime, file.Length));
            }
        }
    }
}
