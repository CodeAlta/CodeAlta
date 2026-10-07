using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Serialization;

namespace CodeAlta.Plugin.Jira;

/// <summary>
/// What a project says of its Jira: the <c>[plugins.jira]</c> table of its <c>.alta/config.toml</c>.
/// </summary>
/// <param name="Site">The Atlassian site of the project, as a host name: <c>example.atlassian.net</c>.</param>
/// <param name="Project">The key of the Jira project its issues are in: <c>ALTA</c>.</param>
/// <remarks>
/// <code>
/// [plugins.jira]
/// enabled = true
/// site = "example.atlassian.net"
/// project = "ALTA"
/// email = "me@example.com"       # only to sign in with an API token
/// token_env = "JIRA_API_TOKEN"   # the environment variable that holds the token
/// </code>
/// Jira is used by a project only: the table of the configuration of the user is not read.
/// </remarks>
public sealed record JiraSettings(string Site, string Project)
{
    /// <summary>The environment variables an API token is looked for in, when the project names none.</summary>
    public static readonly IReadOnlyList<string> TokenVariables = ["JIRA_API_TOKEN", "ATLASSIAN_API_TOKEN"];

    /// <summary>The environment variables the email of the account is looked for in, when the project names none.</summary>
    public static readonly IReadOnlyList<string> EmailVariables = ["JIRA_EMAIL", "ATLASSIAN_EMAIL"];

    /// <summary>Gets the email of the account an API token belongs to; null to read it from the environment.</summary>
    public string? Email { get; init; }

    /// <summary>Gets the name of the environment variable that holds an API token; null for the usual ones.</summary>
    public string? TokenVariable { get; init; }

    /// <summary>Gets the address of an issue on the web.</summary>
    /// <param name="key">The key of the issue.</param>
    /// <returns>The address.</returns>
    public string BrowseUrl(string key) => $"https://{Site}/browse/{Uri.EscapeDataString(key)}";

    /// <summary>Gets the path of the configuration file of a project.</summary>
    /// <param name="projectPath">The folder of the project.</param>
    /// <returns>The path of its <c>.alta/config.toml</c>.</returns>
    public static string ConfigPath(string projectPath) => Path.Combine(projectPath, ".alta", "config.toml");

    /// <summary>Reads what a project says of its Jira.</summary>
    /// <param name="projectPath">The folder of the project.</param>
    /// <returns>The settings; null when the project does not use Jira, or does not say which site and project.</returns>
    public static JiraSettings? Read(string? projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath)) return null;
        try
        {
            var path = ConfigPath(projectPath);
            return File.Exists(path) ? Parse(File.ReadAllText(path)) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Reads the settings from the text of a configuration file.</summary>
    /// <param name="toml">The text of the file.</param>
    /// <returns>The settings; null when Jira is not enabled there, or the site or the project is missing or malformed.</returns>
    public static JiraSettings? Parse(string toml)
    {
        ArgumentNullException.ThrowIfNull(toml);
        TomlTable root;
        try { root = TomlSerializer.Deserialize<TomlTable>(toml) ?? []; }
        catch (Exception exception) when (exception is TomlException or InvalidOperationException or FormatException) { return null; }
        if (!root.TryGetValue("plugins", out var plugins) || plugins is not TomlTable pluginTable
            || !pluginTable.TryGetValue("jira", out var jira) || jira is not TomlTable table) return null;
        if (table.TryGetValue("enabled", out var enabled) && enabled is false) return null;
        if (NormalizeSite(Text(table, "site") ?? Text(table, "url")) is not { } site || NormalizeProject(Text(table, "project")) is not { } project) return null;
        return new(site, project) { Email = Text(table, "email"), TokenVariable = Text(table, "token_env") };
    }

    /// <summary>Reduces what a user writes for a site (an address, a host) to its host name.</summary>
    /// <param name="value">What was written.</param>
    /// <returns>The host name in lower case; null when it is not one.</returns>
    public static string? NormalizeSite(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        if (text.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return null;
            text = uri.Host;
        }

        text = text.TrimEnd('/').ToLowerInvariant();
        return text.Length is > 0 and <= 253 && Uri.CheckHostName(text) == UriHostNameType.Dns && text.Contains('.', StringComparison.Ordinal) ? text : null;
    }

    /// <summary>Checks the key of a project: a letter, then letters, digits or underscores.</summary>
    /// <param name="value">What was written.</param>
    /// <returns>The key in upper case; null when it is not one.</returns>
    public static string? NormalizeProject(string? value)
    {
        var text = value?.Trim().ToUpperInvariant();
        return text is { Length: > 0 and <= 32 } && char.IsAsciiLetter(text[0]) && text.All(static character => char.IsAsciiLetterOrDigit(character) || character == '_') ? text : null;
    }

    private static string? Text(TomlTable table, string key)
        => table.TryGetValue(key, out var value) && value is string text && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;
}
