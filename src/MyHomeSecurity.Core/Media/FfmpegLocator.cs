using System.Diagnostics;
using System.Text.RegularExpressions;
using MyHomeSecurity.Core.Diagnostics;

namespace MyHomeSecurity.Core.Media;

/// <summary>
/// Finds the ffmpeg executable. Order: MYHOMESECURITY_FFMPEG_DIR, the copy bundled with the app
/// (an "ffmpeg" folder next to it), then PATH.
/// </summary>
public sealed partial class FfmpegLocator
{
    public const string DirEnvironmentVariable = "MYHOMESECURITY_FFMPEG_DIR";

    private static readonly Lazy<FfmpegLocator> DefaultInstance = new(Find, LazyThreadSafetyMode.ExecutionAndPublication);
    private readonly Lazy<string> _version;

    public static FfmpegLocator Default => DefaultInstance.Value;

    public FfmpegLocator(string ffmpegPath)
    {
        FfmpegPath = ffmpegPath;
        _version = new Lazy<string>(QueryVersion, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public string FfmpegPath { get; }
    public bool IsAvailable => File.Exists(FfmpegPath);

    /// <summary>First line of "ffmpeg -version", or empty if it couldn't be run.</summary>
    public string Version => _version.Value;

    public static FfmpegLocator Find()
    {
        var exe = OperatingSystem.IsWindows() ? ".exe" : "";
        var candidates = new List<string>();
        var env = Environment.GetEnvironmentVariable(DirEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(env))
            candidates.Add(env);
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "ffmpeg"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "ffmpeg", "bin"));
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        candidates.AddRange(path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));

        foreach (var dir in candidates)
        {
            var ffmpeg = Path.Combine(dir, "ffmpeg" + exe);
            if (File.Exists(ffmpeg))
            {
                Log.Info("ffmpeg", $"Using ffmpeg in {dir}.");
                return new FfmpegLocator(ffmpeg);
            }
        }
        Log.Warn("ffmpeg", "ffmpeg not found.");
        return new FfmpegLocator("ffmpeg" + exe);
    }

    /// <summary>Starts ffmpeg with the given arguments (never through a shell).</summary>
    public Process Start(IEnumerable<string> args, bool keepInput = false)
    {
        var psi = new ProcessStartInfo(FfmpegPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-nostats");
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        return ChildProcess.Start(psi, keepInput, isFfmpeg: true);
    }

    private string QueryVersion()
    {
        try
        {
            using var p = Start(["-version"]);
            _ = p.StandardError.ReadToEndAsync();
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10_000);
            var line = output.Split('\n').FirstOrDefault()?.Trim() ?? "";
            Log.Info("ffmpeg", line);
            return line;
        }
        catch (Exception ex)
        {
            Log.Warn("ffmpeg", "ffmpeg -version failed.", ex);
            return "";
        }
    }

    /// <summary>Major version, e.g. 6 for "ffmpeg version 6.1.1-3ubuntu5". 0 if unknown (or a git build).</summary>
    public int MajorVersion
    {
        get
        {
            var m = VersionRegex().Match(Version);
            return m.Success && int.TryParse(m.Groups[1].Value, out var major) ? major : 0;
        }
    }

    [GeneratedRegex(@"version n?(\d+)\.")]
    private static partial Regex VersionRegex();
}
