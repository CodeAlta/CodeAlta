using System.Collections.Immutable;

namespace CodeAlta.Catalog.Logging;

/// <summary>A part of the application, as a user knows it, whose log messages are filtered and leveled together.</summary>
/// <param name="Id">The id written in the configuration (<c>[logging.features]</c>) and given to the log viewer.</param>
/// <param name="Name">The English name of the feature.</param>
/// <param name="Prefixes">The names that start the names of the loggers of the feature.</param>
public sealed record LogFeature(string Id, string Name, ImmutableArray<string> Prefixes);

/// <summary>
/// The features of the log: which one a logger belongs to, from its name alone. Loggers are named after what they do
/// (<c>CodeAlta.Issues.Git</c>, <c>CodeAlta.Agent.OpenAI</c>): a logger belongs to the feature with the longest prefix
/// of its name that ends on a dot or on the name itself, as XenoAtom.Logging chooses the level of a logger, so a level
/// set for a feature applies to the messages the viewer shows under it. A name no prefix matches is of
/// <see cref="System"/>.
/// </summary>
public static class LogFeatures
{
    /// <summary>The application itself: startup, windows, storage, and every logger no other feature names.</summary>
    public const string System = "system";

    /// <summary>The model providers: their sign-in, their models and their protocols.</summary>
    public const string Providers = "providers";

    /// <summary>The sessions: their runs, their history and their agents.</summary>
    public const string Sessions = "sessions";

    /// <summary>The tools agents call.</summary>
    public const string Tools = "tools";

    /// <summary>The issue trackers: GitHub, GitLab, Azure DevOps, Bitbucket and Jira.</summary>
    public const string IssueTrackers = "issues";

    /// <summary>Git and the hosting services of repositories.</summary>
    public const string Git = "git";

    /// <summary>The MCP servers and their apps.</summary>
    public const string Mcp = "mcp";

    /// <summary>The plugins.</summary>
    public const string Plugins = "plugins";

    /// <summary>The search of sessions, documents and skills.</summary>
    public const string Search = "search";

    /// <summary>Speech to prompt.</summary>
    public const string Speech = "speech";

    /// <summary>The mobile companion.</summary>
    public const string Mobile = "mobile";

    /// <summary>Gets every feature, in the order they are shown.</summary>
    public static ImmutableArray<LogFeature> All { get; } =
    [
        new(System, "System", ["CodeAlta"]),
        new(Providers, "Providers", ["CodeAlta.Agent", "CodeAlta.Provider", "CodeAlta.ProviderSelection", "CodeAlta.ProviderDefaults",
            "CodeAlta.ModelCatalog", "CodeAlta.App.ModelProviderInitialization", "CodeAlta.ClaudeCode", "CodeAlta.RawApi", "acp"]),
        new(Sessions, "Sessions", ["CodeAlta.Agent.Runtime", "CodeAlta.AgentSessionConnection", "CodeAlta.Sessions", "CodeAlta.ExternalSessions",
            "CodeAlta.CoordinatorAgents", "CodeAlta.AttachedDocuments", "CodeAlta.Desktop.History"]),
        new(Tools, "Tools", ["CodeAlta.Tools", "CodeAlta.ToolEndpoints", "CodeAlta.Desktop.ToolEndpoints", "CodeAlta.Cli.ToolEndpoints"]),
        new(IssueTrackers, "Issue trackers", ["CodeAlta.Issues", "CodeAlta.Plugin.builtin:jira"]),
        new(Git, "Git", ["CodeAlta.Git", "CodeAlta.Plugin.builtin:git"]),
        new(Mcp, "MCP", ["CodeAlta.Mcp", "CodeAlta.Desktop.Mcp", "CodeAlta.Desktop.McpApps", "CodeAlta.Plugin.builtin:mcp"]),
        new(Plugins, "Plugins", ["CodeAlta.Plugin", "CodeAlta.Plugins", "CodeAlta.Desktop.Plugins"]),
        new(Search, "Search", ["CodeAlta.Search"]),
        new(Speech, "Speech", ["CodeAlta.Desktop.Speech"]),
        new(Mobile, "Mobile", ["CodeAlta.Mobile", "CodeAlta.Plugin.builtin:mobile"]),
    ];

    /// <summary>Gets the feature of an id.</summary>
    /// <param name="id">The id of a feature, such as <c>issues</c>.</param>
    /// <returns>The feature, or <see langword="null"/> when no feature has this id.</returns>
    public static LogFeature? Find(string? id)
    {
        foreach (var feature in All)
        {
            if (string.Equals(feature.Id, id, StringComparison.Ordinal)) return feature;
        }

        return null;
    }

    /// <summary>Gets the id of the feature a logger belongs to.</summary>
    /// <param name="loggerName">The name of the logger, such as <c>CodeAlta.Issues.Git</c>.</param>
    /// <returns>The id of the feature; <see cref="System"/> for a name no feature names.</returns>
    public static string Of(string? loggerName)
    {
        if (string.IsNullOrEmpty(loggerName)) return System;
        var found = System;
        var length = 0;
        foreach (var feature in All)
        {
            foreach (var prefix in feature.Prefixes)
            {
                if (prefix.Length > length && Covers(prefix, loggerName))
                {
                    (found, length) = (feature.Id, prefix.Length);
                }
            }
        }

        return found;
    }

    private static bool Covers(string prefix, string name)
        => name.StartsWith(prefix, StringComparison.Ordinal) && (name.Length == prefix.Length || name[prefix.Length] == '.');
}
