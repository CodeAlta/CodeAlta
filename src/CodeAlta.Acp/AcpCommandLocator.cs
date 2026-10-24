namespace CodeAlta.Acp;

/// <summary>
/// Finds an executable on <c>PATH</c>, with the extensions of <c>PATHEXT</c> on Windows.
/// </summary>
public static class AcpCommandLocator
{
    /// <summary>Returns the full path of a command, or <see langword="null" /> when it is not found.</summary>
    /// <param name="command">A command name, such as <c>npx</c>, or a full path.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="command" /> is empty.</exception>
    public static string? FindCommandPath(string command)
        => FindCommandPath(command, Environment.GetEnvironmentVariable("PATH"), Environment.GetEnvironmentVariable("PATHEXT"), OperatingSystem.IsWindows());

    internal static string? FindCommandPath(string command, string? path, string? pathExtensions, bool windows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        if (Path.IsPathRooted(command))
        {
            return File.Exists(command) ? command : null;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var searchNames = BuildSearchNames(command.Trim(), pathExtensions, windows);
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var searchName in searchNames)
            {
                var candidate = Path.Combine(directory, searchName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    // On Windows a name without extension is looked for with the extensions of PATHEXT only, as the shell does: the
    // file of the bare name, such as the `npx` script for POSIX shells that Node.js installs beside `npx.cmd`, does not start.
    private static IReadOnlyList<string> BuildSearchNames(string command, string? pathExtensions, bool windows)
    {
        if (!windows || Path.HasExtension(command))
        {
            return [command];
        }

        var extensions = string.IsNullOrWhiteSpace(pathExtensions)
            ? [".exe", ".cmd", ".bat"]
            : pathExtensions.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return [.. extensions.Select(extension => command + extension)];
    }
}
