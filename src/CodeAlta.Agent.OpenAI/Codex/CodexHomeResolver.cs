namespace CodeAlta.Agent.OpenAI.Codex;

internal static class CodexHomeResolver
{
    /// <summary>The variable that moves the folder of the Codex CLI.</summary>
    public const string Variable = "CODEX_HOME";

    public static string ResolveCodexHome(
        IReadOnlyDictionary<string, string?>? environment = null,
        string? userProfile = null,
        string? home = null)
    {
        environment ??= Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(
                static entry => (string)entry.Key,
                static entry => entry.Value?.ToString(),
                StringComparer.OrdinalIgnoreCase);

        if (environment.TryGetValue(Variable, out var codexHome) &&
            !string.IsNullOrWhiteSpace(codexHome))
        {
            return codexHome.Trim();
        }

        if (OperatingSystem.IsWindows())
        {
            userProfile ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(userProfile, ".codex");
        }

        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".codex");
    }

    /// <summary>
    /// Expands the folder a provider gives its CLI (<c>codex_home</c>): a leading <c>~</c> is the home folder, and a
    /// relative path is made full. Blank is no folder of its own.
    /// </summary>
    public static string? ExpandCodexHome(string? value, string? home = null)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var path = value.Trim();
        if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
        {
            home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            path = Path.Combine(home, path[1..].TrimStart('/', '\\'));
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }
}
