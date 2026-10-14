using System.Text;

namespace CodeAlta.Agent.Claude;

/// <summary>
/// Where Claude Code keeps the conversations of a working directory: one folder per directory under
/// <c>projects</c> of its configuration folder, named from the path of the directory.
/// </summary>
/// <remarks>
/// The name is computed as the CLI computes it. The CLI also reads <c>CLAUDE_CODE_PROJECT_DIR_NAME</c> for the name
/// when <c>CLAUDE_CONFIG_DIR</c> is set; that override is not followed here, so a profile using it is not found.
/// </remarks>
public static class ClaudeCodeProjectFolders
{
    /// <summary>The variable that moves the configuration folder of Claude Code.</summary>
    public const string ConfigDirectoryVariable = "CLAUDE_CONFIG_DIR";

    /// <summary>The longest folder name the CLI writes before shortening it with a hash.</summary>
    public const int MaxNameLength = 200;

    /// <summary>
    /// Resolves the configuration folder of Claude Code from the environment of this process.
    /// </summary>
    /// <returns>The value of <c>CLAUDE_CONFIG_DIR</c>, else <c>.claude</c> in the home folder.</returns>
    public static string ResolveConfigRoot() => ResolveConfigRoot(Environment.GetEnvironmentVariable);

    /// <summary>
    /// Resolves the configuration folder of Claude Code: <c>CLAUDE_CONFIG_DIR</c>, else <c>.claude</c> in the home
    /// folder. The folder may be a link: it is not resolved.
    /// </summary>
    /// <param name="environment">Reads a variable of the environment the CLI inherits.</param>
    /// <returns>The path of the configuration folder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="environment"/> is null.</exception>
    public static string ResolveConfigRoot(Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var folder = environment(ConfigDirectoryVariable);
        return string.IsNullOrWhiteSpace(folder)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude")
            : folder;
    }

    /// <summary>
    /// Expands the configuration folder a provider is given (<c>config_dir</c>): a leading <c>~</c> is the home folder,
    /// and a relative path is made full. Blank is no folder of its own.
    /// </summary>
    /// <param name="value">The folder as the configuration writes it.</param>
    /// <param name="home">The home folder.</param>
    /// <returns>The full path of the folder, or null when <paramref name="value"/> is blank.</returns>
    /// <exception cref="ArgumentException">The folder is not a valid path.</exception>
    public static string? ExpandConfigDirectory(string? value, string? home)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var path = value.Trim();
        if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(home))
            {
                throw new ArgumentException($"The folder '{path}' starts with ~ and the home folder is not known.", nameof(home));
            }

            path = Path.Combine(home, path[1..].TrimStart('/', '\\'));
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    /// <summary>Gets the folder that holds the project folders of a configuration folder.</summary>
    /// <param name="configRoot">The configuration folder of Claude Code.</param>
    /// <returns>The <c>projects</c> folder.</returns>
    /// <exception cref="ArgumentException"><paramref name="configRoot"/> is null or blank.</exception>
    public static string GetProjectsFolder(string configRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configRoot);
        return Path.Combine(configRoot, "projects");
    }

    /// <summary>Gets the folder where the CLI keeps the conversations started in a working directory.</summary>
    /// <param name="configRoot">The configuration folder of Claude Code.</param>
    /// <param name="workingDirectory">The working directory the CLI is started in.</param>
    /// <returns>The project folder; it may not exist.</returns>
    /// <exception cref="ArgumentException">An argument is null or blank.</exception>
    public static string GetProjectFolder(string configRoot, string workingDirectory)
        => Path.Combine(GetProjectsFolder(configRoot), GetFolderName(workingDirectory));

    /// <summary>
    /// Gets the name of the project folder of a working directory: every UTF-16 unit that is not an ASCII letter or
    /// digit becomes <c>-</c>, and a name longer than <see cref="MaxNameLength"/> is cut there and followed by
    /// <c>-</c> and the base 36 hash of the path.
    /// </summary>
    /// <param name="workingDirectory">
    /// The working directory as given to the CLI. It is NFC-normalized and loses a trailing separator, as the CLI's
    /// own path does; links are not resolved.
    /// </param>
    /// <returns>The folder name; its case is the case of the path.</returns>
    /// <exception cref="ArgumentException"><paramref name="workingDirectory"/> is null or blank.</exception>
    public static string GetFolderName(string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        var path = Path.TrimEndingDirectorySeparator(workingDirectory).Normalize(NormalizationForm.FormC);
        var name = string.Create(path.Length, path, static (span, source) =>
        {
            for (var index = 0; index < source.Length; index++)
            {
                span[index] = char.IsAsciiLetterOrDigit(source[index]) ? source[index] : '-';
            }
        });

        return name.Length <= MaxNameLength
            ? name
            : $"{name.AsSpan(0, MaxNameLength)}-{ToBase36(Math.Abs((long)HashCode(path)))}";
    }

    /// <summary>Tells whether two folder names are the same folder on this system.</summary>
    /// <param name="left">A folder name.</param>
    /// <param name="right">Another folder name.</param>
    /// <returns>True when they name the same folder: case is ignored on Windows.</returns>
    public static bool AreSameFolderName(string? left, string? right)
        => string.Equals(left, right, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    // The 32-bit hash of a Java string, over UTF-16 units, as the CLI computes it.
    internal static int HashCode(string value)
    {
        var hash = 0;
        foreach (var unit in value)
        {
            hash = unchecked((hash << 5) - hash + unit);
        }

        return hash;
    }

    internal static string ToBase36(long value)
    {
        if (value == 0)
        {
            return "0";
        }

        Span<char> buffer = stackalloc char[16];
        var position = buffer.Length;
        while (value > 0)
        {
            var digit = (int)(value % 36);
            buffer[--position] = (char)(digit < 10 ? '0' + digit : 'a' + digit - 10);
            value /= 36;
        }

        return new string(buffer[position..]);
    }
}
