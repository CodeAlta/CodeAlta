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
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        if (Path.IsPathRooted(command))
        {
            return File.Exists(command) ? command : null;
        }

        var searchNames = BuildSearchNames(command.Trim());
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

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

    private static IReadOnlyList<string> BuildSearchNames(string command)
    {
        if (!OperatingSystem.IsWindows() || Path.HasExtension(command))
        {
            return [command];
        }

        var pathExtensions = Environment.GetEnvironmentVariable("PATHEXT");
        var extensions = string.IsNullOrWhiteSpace(pathExtensions)
            ? [".exe", ".cmd", ".bat"]
            : pathExtensions.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return [command, .. extensions.Select(extension => command + extension)];
    }
}
