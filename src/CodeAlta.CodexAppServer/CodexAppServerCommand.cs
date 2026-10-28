namespace CodeAlta.CodexAppServer;

/// <summary>What starts an app-server: the executable, its arguments, and the folder its package puts on <c>PATH</c>.</summary>
/// <param name="FileName">The full path of the executable.</param>
/// <param name="Arguments">The arguments that start the app-server: none for <c>codex-app-server</c>, <c>app-server</c> for the <c>codex</c> CLI.</param>
/// <param name="PathDirectory">The folder to put first on the <c>PATH</c> of its commands, or <see langword="null"/>.</param>
/// <param name="IsPinned">Whether it is the pinned package CodeAlta downloaded.</param>
public sealed record CodexAppServerCommand(string FileName, IReadOnlyList<string> Arguments, string? PathDirectory, bool IsPinned)
{
    /// <summary>The session source the app-server is started with: CodeAlta, not an editor of OpenAI.</summary>
    public const string SessionSource = "codealta";

    /// <summary>Gets the arguments of a start over standard streams, with the session source of CodeAlta.</summary>
    public IReadOnlyList<string> StartArguments => [.. Arguments, "--session-source", SessionSource];

    /// <summary>Gets the command of the pinned package.</summary>
    /// <param name="installation">The installation.</param>
    /// <returns>The command.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="installation"/> is null.</exception>
    public static CodexAppServerCommand FromInstallation(CodexAppServerInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);
        return new CodexAppServerCommand(installation.Executable, [], installation.PathDirectory, IsPinned: true);
    }

    /// <summary>
    /// Gets the command of a program the user named: a full path, or a name found on <c>PATH</c>. A file named
    /// <c>codex-app-server</c> runs as it is; any other is the <c>codex</c> CLI, started with <c>app-server</c>.
    /// </summary>
    /// <param name="command">The program.</param>
    /// <returns>The command, or <see langword="null"/> when the program is not found.</returns>
    public static CodexAppServerCommand? FromUserCommand(string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        if (ResolveExecutable(command.Trim()) is not { } path)
        {
            return null;
        }

        var name = Path.GetFileNameWithoutExtension(path);
        var standalone = string.Equals(name, "codex-app-server", StringComparison.OrdinalIgnoreCase);
        return new CodexAppServerCommand(path, standalone ? [] : ["app-server"], null, IsPinned: false);
    }

    /// <summary>Reads the version of Codex from the user agent the app-server gives in its answer to <c>initialize</c>.</summary>
    /// <param name="userAgent">Such as <c>codealta/0.165.0 (Mac OS 27.0.1; arm64) …</c>.</param>
    /// <returns>The version, or <see langword="null"/>.</returns>
    public static string? VersionFromUserAgent(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
        {
            return null;
        }

        var slash = userAgent.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0)
        {
            return null;
        }

        var rest = userAgent.AsSpan(slash + 1);
        var end = rest.IndexOfAny(' ', '(');
        var version = (end < 0 ? rest : rest[..end]).Trim().ToString();
        return version.Length > 0 && char.IsAsciiDigit(version[0]) ? version : null;
    }

    private static string? ResolveExecutable(string command)
    {
        if (Path.IsPathFullyQualified(command) || command.Contains(Path.DirectorySeparatorChar) || command.Contains(Path.AltDirectorySeparatorChar))
        {
            var full = Path.GetFullPath(command);
            return FindWithExtensions(full);
        }

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (FindWithExtensions(Path.Combine(directory, command)) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static string? FindWithExtensions(string path)
    {
        if (File.Exists(path))
        {
            return path;
        }

        if (!OperatingSystem.IsWindows() || Path.HasExtension(path))
        {
            return null;
        }

        foreach (var extension in (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            if (File.Exists(path + extension))
            {
                return path + extension;
            }
        }

        return null;
    }
}

/// <summary>The <c>CODEX_HOME</c> folders CodeAlta owns: one per provider, so that several accounts are several providers.</summary>
public static class CodexAppServerHome
{
    /// <summary>Gets the home of a provider: <c>&lt;user data&gt;/plugin-data/codex-cli/&lt;provider key&gt;</c>.</summary>
    /// <param name="userDataDirectory">The folder of the data of CodeAlta (<c>~/.alta</c>).</param>
    /// <param name="providerKey">The key of the provider.</param>
    /// <returns>The full path of the folder.</returns>
    /// <exception cref="ArgumentException">An argument is null or blank.</exception>
    public static string GetDefault(string userDataDirectory, string providerKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userDataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);
        var name = new string([.. providerKey.Trim().Select(static character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' ? character : '_')]);
        return Path.GetFullPath(Path.Combine(userDataDirectory, "plugin-data", "codex-cli", name.Trim('.')));
    }

    /// <summary>Gets the home of a provider: the folder its settings name, else <see cref="GetDefault"/>.</summary>
    /// <param name="configured">The folder the settings name, or <see langword="null"/>; <c>~</c> is the home folder of the user.</param>
    /// <param name="userDataDirectory">The folder of the data of CodeAlta.</param>
    /// <param name="providerKey">The key of the provider.</param>
    /// <returns>The full path of the folder.</returns>
    public static string Resolve(string? configured, string userDataDirectory, string providerKey)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return GetDefault(userDataDirectory, providerKey);
        }

        var value = configured.Trim();
        if (value == "~" || value.StartsWith("~/", StringComparison.Ordinal) || value.StartsWith("~\\", StringComparison.Ordinal))
        {
            value = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), value.Length > 2 ? value[2..] : string.Empty);
        }

        return Path.GetFullPath(value);
    }
}
