using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace CodeAlta.Agent.Cursor;

/// <summary>The executable and arguments that actually start one Cursor CLI verb.</summary>
/// <param name="FileName">The executable. A Windows Cursor shim is the versioned <c>node.exe</c>.</param>
/// <param name="Arguments">Arguments, including the verb. A shim also receives its <c>index.js</c>.</param>
/// <param name="InvokedAs">The shim file name stored in <c>CURSOR_INVOKED_AS</c>, or null when the executable is started directly.</param>
internal readonly record struct CursorCommandStart(string FileName, IReadOnlyList<string> Arguments, string? InvokedAs);

/// <summary>
/// Starts the Cursor agent CLI with a verb appended after the configured arguments.
/// </summary>
internal static class CursorProcess
{
    private static readonly Regex VersionDirectory = new(
        @"^(?<y>\d{4})\.(?<m>\d{1,2})\.(?<d>\d{1,2})(?:-(?<hh>\d{2})-(?<mm>\d{2})-(?<ss>\d{2}))?-[0-9a-fA-F]+$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Starts <c>login</c>, <c>status</c>, or <c>acp</c>. Device login sets <c>NO_OPEN_BROWSER=1</c>; other verbs clear it.
    /// </summary>
    /// <param name="launch">The executable and the arguments that precede the verb.</param>
    /// <param name="verb">The CLI verb.</param>
    /// <param name="deviceFlow"><see langword="true"/> to keep the CLI from opening a browser.</param>
    /// <returns>The started process. Standard input, output, and error are redirected.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="launch"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The process could not be started.</exception>
    public static Process Start(CursorCliLaunch launch, string verb, bool deviceFlow)
    {
        ArgumentNullException.ThrowIfNull(launch);
        ArgumentException.ThrowIfNullOrWhiteSpace(verb);
        var resolved = Resolve(launch, verb, Environment.GetEnvironmentVariable("PATH"), OperatingSystem.IsWindows());
        var start = new ProcessStartInfo
        {
            FileName = resolved.FileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (!string.IsNullOrWhiteSpace(launch.WorkingDirectory))
        {
            start.WorkingDirectory = launch.WorkingDirectory;
        }

        foreach (var argument in resolved.Arguments)
        {
            start.ArgumentList.Add(argument);
        }

        if (deviceFlow)
        {
            start.Environment["NO_OPEN_BROWSER"] = "1";
        }
        else
        {
            start.Environment.Remove("NO_OPEN_BROWSER");
        }

        if (resolved.InvokedAs is not null)
        {
            start.Environment["CURSOR_INVOKED_AS"] = resolved.InvokedAs;
        }

        return Process.Start(start) is { } process
            ? Configure(process)
            : throw new InvalidOperationException($"Could not start the Cursor agent CLI '{resolved.FileName}'.");
    }

    /// <summary>
    /// Resolves <paramref name="launch"/> to the process that should run <paramref name="verb"/>.
    /// On Windows, <c>agent</c> is often a <c>.cmd</c> shim and the first <c>agent.exe</c> on PATH may be a different tool.
    /// A Cursor shim is unwrapped to the <c>node.exe</c> and <c>index.js</c> that shim would run, so ACP stdio reaches the CLI.
    /// </summary>
    /// <param name="launch">The configured executable.</param>
    /// <param name="verb">The CLI verb appended after the configured arguments.</param>
    /// <param name="searchPath">The PATH used to resolve a bare command.</param>
    /// <param name="windowsShims"><see langword="true"/> to search for <c>.cmd</c> and <c>.ps1</c> shims.</param>
    /// <returns>The executable, arguments, and the shim name when one was unwrapped.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="launch"/> is null.</exception>
    /// <exception cref="InvalidOperationException">A Cursor shim has no Node runtime beside it.</exception>
    internal static CursorCommandStart Resolve(CursorCliLaunch launch, string verb, string? searchPath, bool windowsShims)
    {
        ArgumentNullException.ThrowIfNull(launch);
        ArgumentException.ThrowIfNullOrWhiteSpace(verb);
        var fileName = ResolveFile(launch.FileName, searchPath, windowsShims);
        var arguments = new List<string>();
        string? invokedAs = null;
        if (windowsShims && IsCursorShim(fileName))
        {
            var node = FindCursorNode(fileName)
                ?? throw new InvalidOperationException($"The Cursor agent CLI shim '{fileName}' does not contain its Node runtime.");
            invokedAs = Path.GetFileName(fileName);
            fileName = node.NodePath;
            arguments.Add(node.IndexPath);
        }

        arguments.AddRange(launch.Arguments);
        arguments.Add(verb);
        return new CursorCommandStart(fileName, arguments, invokedAs);
    }

    private static string ResolveFile(string fileName, string? searchPath, bool windowsShims)
    {
        if (Path.IsPathRooted(fileName) || fileName.Contains(Path.DirectorySeparatorChar) || fileName.Contains(Path.AltDirectorySeparatorChar))
        {
            return fileName;
        }

        string? fallback = null;
        if (string.IsNullOrWhiteSpace(searchPath))
        {
            return fileName;
        }

        foreach (var entry in searchPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = entry.Trim().Trim('"');
            if (directory.Length == 0)
            {
                continue;
            }

            foreach (var extension in windowsShims ? new[] { ".cmd", ".ps1", ".exe", "" } : new[] { "" })
            {
                var candidate = Path.Combine(directory, extension.Length == 0 ? fileName : fileName + extension);
                if (!File.Exists(candidate))
                {
                    continue;
                }

                if (IsCursorShim(candidate))
                {
                    return candidate;
                }

                fallback ??= candidate;
            }
        }

        return fallback ?? fileName;
    }

    private static bool IsCursorShim(string path)
    {
        var extension = Path.GetExtension(path);
        if (!extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var directory = Path.GetDirectoryName(path);
        if (directory is not null
            && (File.Exists(Path.Combine(directory, "cursor-agent.ps1")) || File.Exists(Path.Combine(directory, "cursor-agent.cmd"))))
        {
            return true;
        }

        try
        {
            using var stream = File.OpenRead(path);
            var length = (int)Math.Min(4096, stream.Length);
            var buffer = new byte[length];
            var read = stream.Read(buffer, 0, length);
            return Encoding.UTF8.GetString(buffer, 0, read).Contains("cursor-agent", StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static (string NodePath, string IndexPath)? FindCursorNode(string shimPath)
    {
        var directory = Path.GetDirectoryName(shimPath);
        if (directory is null)
        {
            return null;
        }

        var besideNode = Path.Combine(directory, "node.exe");
        var besideIndex = Path.Combine(directory, "index.js");
        if (File.Exists(besideNode) && File.Exists(besideIndex))
        {
            return (besideNode, besideIndex);
        }

        var versions = Path.Combine(directory, "versions");
        if (!Directory.Exists(versions))
        {
            return null;
        }

        string? bestDirectory = null;
        var bestKey = long.MinValue;
        foreach (var version in Directory.EnumerateDirectories(versions))
        {
            if (!VersionKey(Path.GetFileName(version), out var key) || key < bestKey)
            {
                continue;
            }

            var node = Path.Combine(version, "node.exe");
            var index = Path.Combine(version, "index.js");
            if (!File.Exists(node) || !File.Exists(index))
            {
                continue;
            }

            bestKey = key;
            bestDirectory = version;
        }

        return bestDirectory is null
            ? null
            : (Path.Combine(bestDirectory, "node.exe"), Path.Combine(bestDirectory, "index.js"));
    }

    private static bool VersionKey(string name, out long key)
    {
        key = 0;
        var match = VersionDirectory.Match(name);
        if (!match.Success)
        {
            return false;
        }

        var hour = match.Groups["hh"].Success ? long.Parse(match.Groups["hh"].Value) : 0;
        var minute = match.Groups["mm"].Success ? long.Parse(match.Groups["mm"].Value) : 0;
        var second = match.Groups["ss"].Success ? long.Parse(match.Groups["ss"].Value) : 0;
        key = long.Parse(match.Groups["y"].Value) * 10_000_000_000L
            + long.Parse(match.Groups["m"].Value) * 100_000_000L
            + long.Parse(match.Groups["d"].Value) * 1_000_000L
            + hour * 10_000L
            + minute * 100L
            + second;
        return true;
    }

    private static Process Configure(Process process)
    {
        process.StandardInput.AutoFlush = true;
        return process;
    }
}
