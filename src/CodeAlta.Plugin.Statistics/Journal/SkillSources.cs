namespace CodeAlta.Plugin.Statistics.Journal;

/// <summary>
/// Where a skill that was used comes from, as the facts name it: <c>project</c>, <c>user</c>, <c>builtin</c>, <c>plugin</c>, or
/// <c>claude-code</c> for a skill of Claude Code itself. A use is counted as <c>&lt;source&gt;/&lt;name&gt;</c>, or as its name
/// alone when the journal does not say; a skill name holds no <c>/</c>.
/// </summary>
internal static class SkillSources
{
    /// <summary>The skills of a project: its <c>.alta/skills</c>, <c>.agents/skills</c> or <c>.github/skills</c>.</summary>
    public const string Project = "project";

    /// <summary>The skills of the user: <c>~/.alta/skills</c>, <c>~/.agents/skills</c>, <c>~/.copilot/skills</c>.</summary>
    public const string User = "user";

    /// <summary>The skills that come with CodeAlta.</summary>
    public const string Builtin = "builtin";

    /// <summary>The skills a plugin brings.</summary>
    public const string Plugin = "plugin";

    /// <summary>The skills of Claude Code, which its own <c>Skill</c> tool loads.</summary>
    public const string ClaudeCode = "claude-code";

    /// <summary>Names a use: the source, a slash, the name; the name alone without a source.</summary>
    /// <param name="source">The source, or null.</param>
    /// <param name="name">The name of the skill.</param>
    /// <returns>The name of the use.</returns>
    public static string Scoped(string? source, string name) => source is null ? name : source + "/" + name;

    /// <summary>Splits the name of a use into its source and the name of its skill.</summary>
    /// <param name="scoped">The name of the use.</param>
    /// <returns>The source, null when the use has none, and the name.</returns>
    public static (string? Source, string Name) Split(string scoped)
    {
        var slash = scoped.IndexOf('/');
        return slash < 0 ? (null, scoped) : (scoped[..slash], scoped[(slash + 1)..]);
    }

    /// <summary>The source of a <c>source_kind</c> of the catalog of skills (<c>SkillSourceKind</c>).</summary>
    /// <param name="kind">The kind, as the payload of an activation writes it.</param>
    /// <returns>The source; null for a kind that is none of them.</returns>
    public static string? OfKind(string? kind) => kind switch
    {
        "ProjectAlta" or "ProjectCommon" or "ProjectCopilot" => Project,
        "UserAlta" or "UserCommon" or "UserCopilot" => User,
        "Builtin" => Builtin,
        "Plugin" => Plugin,
        _ => null,
    };

    /// <summary>The source a path of a <c>SKILL.md</c> tells, by the folders the catalog of skills reads.</summary>
    /// <param name="path">The path.</param>
    /// <param name="home">The folder of the user.</param>
    /// <returns>The source; null for a path in none of those folders.</returns>
    public static string? OfPath(string path, string home)
    {
        var normalized = path.Replace('\\', '/');
        var user = home.Replace('\\', '/').TrimEnd('/') + "/";
        if (normalized.Contains("/BuiltinSkills/", StringComparison.OrdinalIgnoreCase))
        {
            return Builtin;
        }

        if (home.Length > 0 && normalized.StartsWith(user, StringComparison.OrdinalIgnoreCase))
        {
            var rest = normalized[user.Length..];
            if (rest.StartsWith(".alta/skills/", StringComparison.OrdinalIgnoreCase) || rest.StartsWith(".agents/skills/", StringComparison.OrdinalIgnoreCase)
                || rest.StartsWith(".copilot/skills/", StringComparison.OrdinalIgnoreCase))
            {
                return User;
            }

            if (rest.StartsWith(".alta/plugins/", StringComparison.OrdinalIgnoreCase))
            {
                return Plugin;
            }

            if (rest.StartsWith(".claude/skills/", StringComparison.OrdinalIgnoreCase))
            {
                return ClaudeCode;
            }
        }

        return normalized.Contains("/.alta/skills/", StringComparison.OrdinalIgnoreCase) || normalized.Contains("/.agents/skills/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/.github/skills/", StringComparison.OrdinalIgnoreCase)
            ? Project
            : null;
    }
}
