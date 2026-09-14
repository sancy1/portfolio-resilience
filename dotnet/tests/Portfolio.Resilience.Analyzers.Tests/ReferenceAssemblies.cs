// filepath: tests/Portfolio.Resilience.Analyzers.Tests/ReferenceAssemblies.cs
// layer: Tests | package: Portfolio.Resilience.Analyzers.Tests | since: v0.7.1
// purpose: Locates .NET reference assemblies for analyzer tests on any platform.
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Tests      : HttpClientBypassAnalyzerTests, MisconfigurationAnalyzerTests (shared helper)
//   Depends on : System.Runtime.InteropServices.RuntimeEnvironment
//   See also   : docs/analyzers.md
// -----------------------------------------------------------------------------

using System.Runtime.InteropServices;

namespace Portfolio.Resilience.Analyzers.Tests;

/// <summary>
/// Finds the .NET reference packs used to construct test compilations for
/// the analyzer tests. Works on Windows, Linux, and macOS.
/// </summary>
internal static class ReferenceAssemblies
{
    /// <summary>
    /// Returns the absolute path to the <c>packs</c> folder inside the .NET
    /// installation (for example, <c>C:\Program Files\dotnet\packs</c> on
    /// Windows or <c>/usr/share/dotnet/packs</c> on Linux).
    /// </summary>
    public static string GetDotnetPacksRoot()
    {
        var dotnetRoot = GetDotnetRoot();
        return Path.Combine(dotnetRoot, "packs");
    }

    /// <summary>
    /// Locates the .NET installation root that contains the <c>packs</c>
    /// folder.
    /// </summary>
    public static string GetDotnetRoot()
    {
        // 1. DOTNET_ROOT env var (set on CI, sometimes locally).
        var envRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrEmpty(envRoot)
            && Directory.Exists(Path.Combine(envRoot, "packs")))
        {
            return envRoot;
        }

        // 2. Derive from the currently-running runtime. This works on every OS
        //    and does not depend on the environment.
        var runtimeDir = RuntimeEnvironment.GetRuntimeDirectory();
        var dir = new DirectoryInfo(runtimeDir);
        for (var i = 0; i < 4 && dir?.Parent is not null; i++)
        {
            dir = dir.Parent;
            if (dir is not null
                && Directory.Exists(Path.Combine(dir.FullName, "packs")))
            {
                return dir.FullName;
            }
        }

        // 3. Fallback for Windows when GetRuntimeDirectory is unusual.
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrEmpty(programFiles))
        {
            var candidate = Path.Combine(programFiles, "dotnet");
            if (Directory.Exists(Path.Combine(candidate, "packs")))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            $"Cannot locate the .NET installation root. " +
            $"Runtime directory was '{runtimeDir}'.");
    }
}
