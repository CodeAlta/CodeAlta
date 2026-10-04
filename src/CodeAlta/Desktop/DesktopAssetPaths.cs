using System.Runtime.InteropServices;
using NeoAstra;

namespace CodeAlta.Desktop;

/// <summary>
/// Says so, in plain words, when the application is installed too deep for Windows. The page's files are
/// read by path, and Windows refuses a path of 260 characters or more: the window would otherwise stay on
/// its start-up screen without a word, with only some of its files loading.
/// </summary>
internal static class DesktopAssetPaths
{
    /// <summary>The shortest path length Windows refuses (its limit counts the terminating character).</summary>
    internal const int WindowsLimit = 260;

    /// <summary>The longest full path among the application's page files; empty when it has none.</summary>
    internal static string Longest(string root, IEnumerable<string> relativePaths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(relativePaths);
        var longest = string.Empty;
        foreach (var relative in relativePaths)
        {
            var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            if (path.Length > longest.Length) longest = path;
        }
        return longest;
    }

    /// <summary>What to tell the user when <paramref name="longest"/> is beyond the limit; null when it is not.</summary>
    internal static string? Problem(string root, string longest)
    {
        if (longest.Length < WindowsLimit) return null;
        return $"CodeAlta is installed in a folder whose path is too long for Windows.\n\n{root}\n\n"
            + $"Its files need paths of up to {longest.Length} characters, and Windows stops at {WindowsLimit - 1}. "
            + "Install CodeAlta in a folder with a shorter path, then start it again.";
    }

    /// <summary>
    /// Checks the installed page files against the limit on Windows; beyond it, the user is told and the
    /// start ends.
    /// </summary>
    /// <exception cref="PathTooLongException">The application's files cannot be read where they are installed.</exception>
    internal static void EnsureUsable(string root, NeoAssetManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!OperatingSystem.IsWindows()) return;
        var problem = Problem(root, Longest(root, manifest.Assets.Select(static asset => asset.Path)));
        if (problem is null) return;
        try { _ = MessageBoxW(0, problem, "CodeAlta", 0x10 /* MB_ICONERROR */); }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException) { /* The log and the exit code still say it. */ }
        throw new PathTooLongException(problem.ReplaceLineEndings(" "));
    }

    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(nint owner, string text, string caption, uint type);
}
