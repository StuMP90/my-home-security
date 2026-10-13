using System.Reflection;

namespace MyHomeSecurity.App;

/// <summary>Version shown in the app. CI stamps it from the release tag (see packaging/ci-version.sh).</summary>
public static class AppInfo
{
    /// <summary>Full version as stamped by the SDK, e.g. "1.0.7+644f262…" (used in the log).</summary>
    public static string FullVersion { get; } =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    /// <summary>e.g. "1.0.7".</summary>
    public static string Version => FullVersion.Split('+')[0];

    /// <summary>e.g. "My Home Security 1.0.7".</summary>
    public static string DisplayVersion => $"My Home Security {Version}";
}
