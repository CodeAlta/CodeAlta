using System.Runtime.CompilerServices;

namespace CodeAlta.Tests;

/// <summary>
/// Points the temporary directory of the test process at its resolved path. CodeAlta does not manage prompts,
/// skills, projects or owned sessions below a linked directory, and the temporary directory of macOS is below
/// <c>/var</c>, a symbolic link to <c>/private/var</c>.
/// </summary>
internal static class LinkFreeTempDirectory
{
    // Linked into each test project whose fixtures need such a root; runs before any test code of the assembly.
    [ModuleInitializer]
    internal static void Use()
    {
        if (OperatingSystem.IsWindows()) return;
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        var resolved = Path.GetPathRoot(path)!;
        foreach (var name in path[resolved.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            resolved = Path.Combine(resolved, name);
            if (new DirectoryInfo(resolved).ResolveLinkTarget(returnFinalTarget: true) is { } target) resolved = target.FullName;
        }
        if (resolved != path) Environment.SetEnvironmentVariable("TMPDIR", resolved);
    }
}
