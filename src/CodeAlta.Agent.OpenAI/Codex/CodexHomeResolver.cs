namespace CodeAlta.Agent.OpenAI.Codex;

internal static class CodexHomeResolver
{
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

        if (environment.TryGetValue("CODEX_HOME", out var codexHome) &&
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
}
