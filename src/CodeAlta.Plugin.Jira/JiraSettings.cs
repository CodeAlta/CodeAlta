using System.Collections.Immutable;
using CodeAlta.Plugins.Abstractions;
using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Serialization;

namespace CodeAlta.Plugin.Jira;

/// <summary>
/// What a project says of its Jira: the <c>[jira]</c> table of its <c>.alta/issues.toml</c> (the team's, committed),
/// completed by the one of its <c>.alta/local/issues.toml</c> (the user's, which git ignores).
/// </summary>
/// <param name="Site">The Atlassian site of the project, as a host name: <c>example.atlassian.net</c>.</param>
/// <param name="Project">
/// The key of the Jira project its issues are in: <c>ALTA</c>. With several Jira projects, the one new issues go to
/// and the one a number alone names.
/// </param>
/// <remarks>
/// <code>
/// # .alta/issues.toml
/// [jira]
/// site = "example.atlassian.net"
/// project = "ALTA"
/// projects = ["PLAT", "TEAM"]    # other Jira projects whose issues are listed with those of `project`
///
/// # .alta/local/issues.toml
/// [jira]
/// email = "me@example.com"       # only to sign in with an API token
/// token_env = "JIRA_API_TOKEN"   # the environment variable that holds the token
/// </code>
/// Without <c>project</c>, the first key of <c>projects</c> is the one new issues go to. A key the user's file writes
/// wins over the team's, and <c>enabled = false</c> in either turns Jira off. Jira is used by a project only: the
/// configuration of the user is not read. The identity of the space of the session gives the site, the email and the
/// token variable the project does not write: what the project writes wins. While the team's file has no <c>[jira]</c>
/// table, the <c>[plugins.jira]</c> table of the project's <c>.alta/config.toml</c> stands for it, as before, the user's file
/// on top (<see cref="Legacy"/>).
/// </remarks>
public sealed record JiraSettings(string Site, string Project)
{
    /// <summary>The most Jira projects one project lists; the keys after that are not read.</summary>
    public const int MaximumProjects = 20;

    /// <summary>The environment variables an API token is looked for in, when the project names none.</summary>
    public static readonly IReadOnlyList<string> TokenVariables = ["JIRA_API_TOKEN", "ATLASSIAN_API_TOKEN"];

    // The keys of [jira] that are settings: the views of the Issues tab and the view the user chose are kept in the same table.
    private static readonly ImmutableArray<string> SettingKeys = ["enabled", "site", "url", "project", "projects", "email", "token_env"];

    /// <summary>The environment variables the email of the account is looked for in, when the project names none.</summary>
    public static readonly IReadOnlyList<string> EmailVariables = ["JIRA_EMAIL", "ATLASSIAN_EMAIL"];

    /// <summary>Gets the email of the account an API token belongs to; null to read it from the environment.</summary>
    public string? Email { get; init; }

    /// <summary>Gets the name of the environment variable that holds an API token; null for the usual ones.</summary>
    public string? TokenVariable { get; init; }

    /// <summary>
    /// Gets the keys of the Jira projects whose issues are listed: <see cref="Project"/> first, then the others in
    /// the order the project wrote them, each once.
    /// </summary>
    public IReadOnlyList<string> Projects { get; init; } = [Project];

    /// <summary>
    /// Gets whether the settings were read from <c>[plugins.jira]</c> of the project's <c>.alta/config.toml</c>, where
    /// they were kept before <c>.alta/issues.toml</c> (the user's file of the issues may complete them): still read, to be moved.
    /// </summary>
    public bool Legacy { get; init; }

    /// <summary>Gets whether the project names several Jira projects.</summary>
    public bool HasSeveralProjects => Projects.Count > 1;

    /// <summary>Gets whether a key is the one of a Jira project the project names.</summary>
    /// <param name="project">The key, in upper case.</param>
    /// <returns>True when it is one of <see cref="Projects"/>.</returns>
    public bool Has(string project) => Projects.Contains(project, StringComparer.Ordinal);

    /// <summary>Gets the address of the issues of the project on the web: its Jira project, or a search of its Jira projects.</summary>
    /// <returns>The address.</returns>
    public string IssuesUrl()
        => HasSeveralProjects
            ? $"https://{Site}/issues/?jql={Uri.EscapeDataString("project in (" + string.Join(", ", Projects) + ") ORDER BY updated DESC")}"
            : BrowseUrl(Project);

    /// <inheritdoc />
    public bool Equals(JiraSettings? other)
        => other is not null && Site == other.Site && Project == other.Project && Email == other.Email && TokenVariable == other.TokenVariable && Legacy == other.Legacy
            && Projects.SequenceEqual(other.Projects, StringComparer.Ordinal);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Site, Project, Email, TokenVariable, Projects.Count);

    /// <summary>What says where the settings belong, when they were read from the legacy configuration.</summary>
    public const string LegacyNote = "The Jira of this project is in [plugins.jira] of .alta/config.toml, which is still read for now: move it to [jira] of .alta/issues.toml "
        + "(site, project, projects), and put email and token_env in [jira] of .alta/local/issues.toml, which git ignores.";

    /// <summary>What says how a project turns Jira on, when it does not.</summary>
    public const string NotConfiguredMessage = "This project does not use Jira. Its .alta/issues.toml needs [jira] with `site` (example.atlassian.net) and `project` "
        + "(the key of the Jira project), or `projects` (several keys).";

    /// <summary>Gets the address of an issue on the web.</summary>
    /// <param name="key">The key of the issue.</param>
    /// <returns>The address.</returns>
    public string BrowseUrl(string key) => $"https://{Site}/browse/{Uri.EscapeDataString(key)}";

    /// <summary>Gets the path of the file of the issues of a project the team shares.</summary>
    /// <param name="projectPath">The folder of the project.</param>
    /// <returns>The path of its <c>.alta/issues.toml</c>.</returns>
    public static string TeamPath(string projectPath) => Path.Combine(projectPath, ".alta", "issues.toml");

    /// <summary>Gets the path of the file of the issues of a project that is the user's alone.</summary>
    /// <param name="projectPath">The folder of the project.</param>
    /// <returns>The path of its <c>.alta/local/issues.toml</c>.</returns>
    public static string LocalPath(string projectPath) => Path.Combine(projectPath, ".alta", "local", "issues.toml");

    /// <summary>Gets the path of the configuration file of a project, where the settings were kept before <c>.alta/issues.toml</c>.</summary>
    /// <param name="projectPath">The folder of the project.</param>
    /// <returns>The path of its <c>.alta/config.toml</c>.</returns>
    /// <remarks>Legacy: read while the files of the issues have no <c>[jira]</c> table; to be removed.</remarks>
    public static string ConfigPath(string projectPath) => Path.Combine(projectPath, ".alta", "config.toml");

    /// <summary>Reads what a project says of its Jira.</summary>
    /// <param name="projectPath">The folder of the project.</param>
    /// <returns>The settings; null when the project does not use Jira, or does not say which site and project.</returns>
    public static JiraSettings? Read(string? projectPath) => Read(projectPath, null);

    /// <summary>Reads what a project says of its Jira, completed by the identity of the space of the work.</summary>
    /// <param name="projectPath">The folder of the project.</param>
    /// <param name="identity">The identity of the space; null for none.</param>
    /// <returns>The settings; null when the project does not use Jira, or no site and project are known.</returns>
    public static JiraSettings? Read(string? projectPath, PluginIdentity? identity)
    {
        if (string.IsNullOrWhiteSpace(projectPath)) return null;
        var team = ReadLayer(TeamPath(projectPath));
        var local = ReadLayer(LocalPath(projectPath));
        // A file that cannot be read says nothing safe: Jira is off rather than set by the other files (Problem says why).
        if (team.Problem is not null || local.Problem is not null) return null;
        // Legacy: while the team's file has no [jira], the table of .alta/config.toml stands for it, the user's file on top.
        var legacy = team.Settings is null ? LegacyTable(ReadFile(ConfigPath(projectPath))) : null;
        if (team.Settings is null && local.Settings is null && legacy is null) return null;
        var settings = FromTable(Merge(team.Settings ?? legacy, local.Settings), identity);
        return legacy is not null && settings is not null ? settings with { Legacy = true } : settings;
    }

    /// <summary>
    /// Says why the Jira of a project is off although a file of its issues exists: the file cannot be read, is not valid
    /// TOML, has a <c>jira</c> that is not a table, or an <c>enabled</c> that is not <c>true</c> or <c>false</c>.
    /// </summary>
    /// <param name="projectPath">The folder of the project.</param>
    /// <returns>What is wrong, with the path of the file; null when the files are readable.</returns>
    public static string? Problem(string? projectPath)
        => string.IsNullOrWhiteSpace(projectPath) ? null : ReadLayer(TeamPath(projectPath)).Problem ?? ReadLayer(LocalPath(projectPath)).Problem;

    /// <summary>
    /// Reads the settings from the texts of the files of the issues of a project: the <c>[jira]</c> table of the team's,
    /// completed key by key by the one of the user's.
    /// </summary>
    /// <param name="team">The text of <c>.alta/issues.toml</c>; null when there is none.</param>
    /// <param name="local">The text of <c>.alta/local/issues.toml</c>; null when there is none.</param>
    /// <param name="identity">The identity of the space; null for none.</param>
    /// <returns>The settings, as <see cref="Parse(string)"/> says; null too when neither file has a <c>[jira]</c> table of settings.</returns>
    public static JiraSettings? ParseIssues(string? team, string? local, PluginIdentity? identity)
    {
        var teamLayer = team is null ? default : Layer(team, "team");
        var localLayer = local is null ? default : Layer(local, "local");
        if (teamLayer.Problem is not null || localLayer.Problem is not null) return null;
        return teamLayer.Settings is null && localLayer.Settings is null ? null : FromTable(Merge(teamLayer.Settings, localLayer.Settings), identity);
    }

    /// <summary>Reads the settings from the text of a configuration file.</summary>
    /// <param name="toml">The text of the file.</param>
    /// <returns>
    /// The settings, marked <see cref="Legacy"/>; null when Jira is not enabled there, the site is missing or malformed,
    /// <c>project</c> is malformed, or no Jira project is named. A malformed key of <c>projects</c> is left out.
    /// </returns>
    public static JiraSettings? Parse(string toml) => Parse(toml, null);

    /// <summary>
    /// Reads the settings from the text of a configuration file, completed by the identity of a space: its site, its
    /// email and its token variable stand for the ones the file does not write.
    /// </summary>
    /// <param name="toml">The text of the file.</param>
    /// <param name="identity">The identity of the space; null for none.</param>
    /// <returns>The settings, as <see cref="Parse(string)"/> says; a site the file does not write is the one of the space.</returns>
    public static JiraSettings? Parse(string toml, PluginIdentity? identity)
    {
        ArgumentNullException.ThrowIfNull(toml);
        return LegacyTable(toml) is { } table && FromTable(table, identity) is { } settings ? settings with { Legacy = true } : null;
    }

    // The [plugins.jira] table of a legacy configuration; null when there is none, or when the text is not TOML.
    private static TomlTable? LegacyTable(string? toml)
    {
        if (toml is null) return null;
        TomlTable root;
        try { root = TomlSerializer.Deserialize<TomlTable>(toml) ?? []; }
        catch (Exception exception) when (exception is TomlException or InvalidOperationException or FormatException) { return null; }
        return root.TryGetValue("plugins", out var plugins) && plugins is TomlTable pluginTable && pluginTable.TryGetValue("jira", out var jira) && jira is TomlTable table ? table : null;
    }

    // The settings of a table of Jira: [jira] of the files of the issues, or [plugins.jira] of the legacy configuration.
    private static JiraSettings? FromTable(TomlTable table, PluginIdentity? identity)
    {
        if (table.TryGetValue("enabled", out var enabled) && enabled is false) return null;
        // A site the project writes wrong is not replaced by the one of the space: the project says it uses another.
        var written = Text(table, "site") ?? Text(table, "url");
        if ((written is null ? NormalizeSite(identity?.JiraSite) : NormalizeSite(written)) is not { } site) return null;
        var projects = new List<string>();
        if (Text(table, "project") is { } named)
        {
            if (NormalizeProject(named) is not { } project) return null;
            projects.Add(project);
        }

        if (table.TryGetValue("projects", out var list) && list is TomlArray keys)
        {
            foreach (var key in keys)
            {
                if (projects.Count < MaximumProjects && NormalizeProject(key as string) is { } project && !projects.Contains(project, StringComparer.Ordinal)) projects.Add(project);
            }
        }

        return projects.Count == 0 ? null : new(site, projects[0])
        {
            Projects = projects,
            Email = Text(table, "email") ?? Blank(identity?.JiraEmail),
            TokenVariable = Text(table, "token_env") ?? Blank(identity?.JiraTokenVariable),
        };
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

    // The [jira] table of a file of the issues when it has settings; null otherwise, or when the text is not TOML.
    // One file of the issues as read: its [jira] table of settings (null when it is absent or holds views only), or why it cannot be used.
    private readonly record struct IssuesLayer(TomlTable? Settings, string? Problem);

    private static IssuesLayer ReadLayer(string path)
    {
        if (!File.Exists(path)) return default;
        try
        {
            return Layer(ReadShared(path), path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new(null, $"{path} cannot be read: {exception.Message}");
        }
    }

    private static IssuesLayer Layer(string toml, string name)
    {
        TomlTable root;
        try { root = TomlSerializer.Deserialize<TomlTable>(toml) ?? []; }
        catch (Exception exception) when (exception is TomlException or InvalidOperationException or FormatException) { return new(null, $"{name} is not valid TOML: Jira is off until it is fixed."); }
        if (!root.TryGetValue("jira", out var jira)) return default;
        if (jira is not TomlTable table) return new(null, $"jira in {name} is not a table: Jira is off until it is fixed.");
        if (table.TryGetValue("enabled", out var enabled) && enabled is not bool) return new(null, $"enabled of [jira] in {name} is neither true nor false: Jira is off until it is fixed.");
        return new(SettingKeys.Any(table.ContainsKey) ? table : null, null);
    }

    // The team's settings with the user's on top, key by key; enabled = false in either turns Jira off. `url` is another
    // name of `site`: the site of each file is known before the user's replaces the team's.
    private static TomlTable Merge(TomlTable? team, TomlTable? local)
    {
        var merged = new TomlTable();
        foreach (var source in new[] { team, local })
        {
            if (source is null) continue;
            foreach (var key in SettingKeys)
            {
                if (key == "url" || !source.TryGetValue(key, out var value)) continue;
                if (key == "enabled" && merged.TryGetValue(key, out var off) && off is false) continue;
                merged[key] = value;
            }

            if (!source.ContainsKey("site") && source.TryGetValue("url", out var url)) merged["site"] = url;
        }

        return merged;
    }

    private static string? ReadFile(string path)
    {
        try
        {
            return File.Exists(path) ? ReadShared(path) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Read so that a writer is never refused meanwhile: the views of the Issues tab are saved in the same files.
    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static string? Text(TomlTable table, string key)
        => table.TryGetValue(key, out var value) && value is string text && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;
}
