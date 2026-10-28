using CodeAlta.CodexAppServer;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugin.CodexCli;

/// <summary>The settings of a Codex CLI provider, as its section of the configuration gives them.</summary>
/// <param name="Command">The <c>codex</c> or <c>codex-app-server</c> the user named, or <see langword="null"/> for the pinned package.</param>
/// <param name="ConfiguredHome">The <c>CODEX_HOME</c> the user named, or <see langword="null"/> for the default one.</param>
/// <param name="Environment">Variables set for the app-server.</param>
internal sealed record CodexCliSettings(string? Command, string? ConfiguredHome, IReadOnlyDictionary<string, string> Environment)
{
    /// <summary>The provider type.</summary>
    public const string ProviderType = "codex-cli";

    /// <summary>The key of the command.</summary>
    public const string CommandKey = "command";

    /// <summary>The key of the home.</summary>
    public const string HomeKey = "codex_home";

    /// <summary>The key of the variables.</summary>
    public const string EnvironmentKey = "env";

    /// <summary>
    /// Variables CodeAlta sets itself: a provider that sets them would undo what keeps the home of CodeAlta its own.
    /// </summary>
    private static readonly string[] ReservedVariables = ["CODEX_HOME", "CODEX_SQLITE_HOME"];

    /// <summary>Reads the settings of a provider.</summary>
    /// <param name="settings">The section of the provider.</param>
    public static CodexCliSettings Read(PluginProviderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var environment = settings.GetMap(EnvironmentKey)
            .Where(static pair => !ReservedVariables.Contains(pair.Key, StringComparer.OrdinalIgnoreCase))
            .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
        return new CodexCliSettings(settings.GetString(CommandKey), settings.GetString(HomeKey), environment);
    }

    /// <summary>Checks the settings of a provider.</summary>
    /// <param name="settings">The section of the provider.</param>
    /// <returns>The problems, each in a sentence.</returns>
    public static IReadOnlyList<string> Validate(PluginProviderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var problems = new List<string>();
        foreach (var name in settings.GetMap(EnvironmentKey).Keys)
        {
            if (ReservedVariables.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                problems.Add($"{name} is set by CodeAlta: use the Codex home setting instead.");
            }
        }

        if (settings.GetString(HomeKey) is { } home)
        {
            string full;
            try
            {
                full = CodexAppServerHome.Resolve(home, Path.GetTempPath(), settings.ProviderKey);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                problems.Add($"The Codex home '{home}' is not a valid folder.");
                return problems;
            }

            var userCodex = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), ".codex");
            if (string.Equals(Path.TrimEndingDirectorySeparator(full), userCodex, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
            {
                problems.Add("The Codex home cannot be ~/.codex: CodeAlta writes the config.toml of its home, which would replace the one of your Codex.");
            }
        }

        return problems;
    }

    /// <summary>Gets the home of a provider.</summary>
    /// <param name="userDataDirectory">The folder of the data of CodeAlta.</param>
    /// <param name="providerKey">The key of the provider.</param>
    public string ResolveHome(string userDataDirectory, string providerKey) => CodexAppServerHome.Resolve(ConfiguredHome, userDataDirectory, providerKey);
}
