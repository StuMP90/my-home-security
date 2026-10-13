using System.Runtime.CompilerServices;
using MyHomeSecurity.Core;

namespace MyHomeSecurity.App.Tests;

internal static class TestEnvironment
{
    /// <summary>
    /// Runs before any test: points the data folder (logs included) at a temp folder, so test
    /// runs never write into the real one.
    /// </summary>
    [ModuleInitializer]
    internal static void UseTemporaryDataFolder() =>
        Environment.SetEnvironmentVariable(AppPaths.DataDirEnvironmentVariable,
            Path.Combine(Path.GetTempPath(), "myhomesecurity-test-data"));
}
