namespace MyHomeSecurity.Core;

/// <summary>
/// Per-user locations. Settings, the vault and logs: Windows %LOCALAPPDATA%\MyHomeSecurity (redirected into
/// the package's private folder automatically when running as MSIX); Linux $XDG_CONFIG_HOME/myhomesecurity
/// or ~/.config/myhomesecurity. MYHOMESECURITY_DATA_DIR overrides it (used by the tests).
/// Snapshots and recordings default to Pictures/Videos, and can be changed in Settings.
/// </summary>
public static class AppPaths
{
    public const string DataDirEnvironmentVariable = "MYHOMESECURITY_DATA_DIR";

    public static string DataDirectory { get; } = Resolve();

    public static string VaultFile => Path.Combine(DataDirectory, "vault.json");
    public static string PictureDirectory => Path.Combine(DataDirectory, "pictures");
    public static string LogDirectory => Path.Combine(DataDirectory, "logs");

    public static string DefaultSnapshotDirectory =>
        Path.Combine(UserFolder(Environment.SpecialFolder.MyPictures, "Pictures"), "MyHomeSecurity");

    public static string DefaultRecordingDirectory =>
        Path.Combine(UserFolder(Environment.SpecialFolder.MyVideos, "Videos"), "MyHomeSecurity");

    private static string Resolve()
    {
        var overridden = Environment.GetEnvironmentVariable(DataDirEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
            return overridden;

        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyHomeSecurity");

        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var root = string.IsNullOrWhiteSpace(xdg)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config")
            : xdg;
        return Path.Combine(root, "myhomesecurity");
    }

    private static string UserFolder(Environment.SpecialFolder folder, string fallbackName)
    {
        var path = Environment.GetFolderPath(folder);
        return string.IsNullOrEmpty(path)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), fallbackName)
            : path;
    }
}
