using CodeAlta.Catalog;

namespace CodeAlta.Orchestration.Runtime.SystemPrompts;

/// <summary>
/// Reads the custom agents of GitHub Copilot as agent prompts: <c>.github/agents/&lt;id&gt;.agent.md</c> of a project and
/// <c>~/.copilot/agents/&lt;id&gt;.agent.md</c> of the user. The text of the file is the prompt, and its <c>name</c> and
/// <c>description</c> present it. What the file says about tools, model and handoffs is for Copilot and is not read.
/// </summary>
internal static class CopilotAgentFiles
{
    /// <summary>The precedence of the agents of the user: below every prompt of CodeAlta, which wins on the same id.</summary>
    internal const int UserPrecedence = -2;

    /// <summary>The precedence of the agents of a project: above the ones of the user, below every prompt of CodeAlta.</summary>
    internal const int ProjectPrecedence = -1;

    private const string AgentSuffix = ".agent.md";
    private const string MarkdownSuffix = ".md";
    private const int MaximumLength = 256 * 1024;
    private const int MaximumIdLength = 128;

    /// <summary>One custom agent: the id is the name of its file.</summary>
    internal sealed record Agent(string Id, string Name, string? Description, string Body, string Path);

    /// <summary>Gets the folders that exist, the one of the user first.</summary>
    internal static IEnumerable<(AgentPromptSourceKind Kind, int Precedence, string Folder)> Folders(SystemPromptContentRoots roots)
    {
        if (roots.CopilotUserAgentsRoot is { } user && Directory.Exists(user))
        {
            yield return (AgentPromptSourceKind.CopilotUser, UserPrecedence, user);
        }

        if (roots.ProjectPromptResourcesTrusted && roots.CopilotProjectAgentsRoot is { } project && Directory.Exists(project))
        {
            yield return (AgentPromptSourceKind.CopilotProject, ProjectPrecedence, project);
        }
    }

    /// <summary>Reads every agent of a folder, by id. A file that cannot be read or has no text is left out.</summary>
    internal static IReadOnlyList<Agent> ReadAll(string folder)
    {
        string[] files;
        try
        {
            files = Directory.GetFiles(folder, "*" + MarkdownSuffix, SearchOption.TopDirectoryOnly);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        var agents = new Dictionary<string, Agent>(StringComparer.OrdinalIgnoreCase);
        // `<id>.agent.md` comes before `<id>.md`, the older name of the same file.
        foreach (var file in files.OrderByDescending(static file => file.EndsWith(AgentSuffix, StringComparison.OrdinalIgnoreCase)).ThenBy(static file => file, StringComparer.OrdinalIgnoreCase))
        {
            if (IdOf(file) is { } id && !agents.ContainsKey(id) && ReadFile(id, file) is { } agent)
            {
                agents.Add(id, agent);
            }
        }

        return [.. agents.Values.OrderBy(static agent => agent.Id, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Reads the agent of an id in a folder, or null.</summary>
    internal static Agent? Read(string folder, string id)
    {
        if (!IsId(id))
        {
            return null;
        }

        foreach (var suffix in (string[])[AgentSuffix, MarkdownSuffix])
        {
            var path = Path.Combine(folder, id + suffix);
            if (File.Exists(path) && IdOf(path) is not null && ReadFile(id, path) is { } agent)
            {
                return agent;
            }
        }

        return null;
    }

    private static string? IdOf(string path)
    {
        var name = Path.GetFileName(path);
        var id = name.EndsWith(AgentSuffix, StringComparison.OrdinalIgnoreCase) ? name[..^AgentSuffix.Length]
            : name.EndsWith(MarkdownSuffix, StringComparison.OrdinalIgnoreCase) ? name[..^MarkdownSuffix.Length]
            : null;
        // A folder of agents often has a README beside them.
        return id is not null && IsId(id) && !string.Equals(id, "readme", StringComparison.OrdinalIgnoreCase) ? id : null;
    }

    private static bool IsId(string id)
    {
        if (id.Length is 0 or > MaximumIdLength)
        {
            return false;
        }

        try
        {
            PromptResourceStore.ValidateId(id);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static Agent? ReadFile(string id, string path)
    {
        string text;
        try
        {
            var info = new FileInfo(path);
            if (info.LinkTarget is not null || info.Length > MaximumLength)
            {
                return null;
            }

            text = File.ReadAllText(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var (header, body) = SplitHeader(text);
        body = body.Trim();
        if (body.Length == 0)
        {
            return null;
        }

        return new Agent(id, Text(header.GetValueOrDefault("name")) ?? id, Text(header.GetValueOrDefault("description")), body, Path.GetFullPath(path));
    }

    // The header of an agent file is YAML with lists and maps (tools, handoffs, MCP servers). Only its top-level
    // scalars are read: a `name` nested in one of the maps is not the name of the agent.
    private static (Dictionary<string, string> Header, string Body) SplitHeader(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').TrimStart('﻿');
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal))
        {
            return (values, normalized);
        }

        var end = normalized.IndexOf("\n---\n", 3, StringComparison.Ordinal);
        if (end < 0)
        {
            // A header that closes on the last line leaves no text.
            return (values, normalized.EndsWith("\n---", StringComparison.Ordinal) ? string.Empty : normalized);
        }

        var lines = normalized[4..(end + 1)].Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (line.Length == 0 || char.IsWhiteSpace(line[0]) || line[0] is '#' or '-')
            {
                continue;
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }

            var value = line[(colon + 1)..].Trim();
            if (value is ">" or "|" or ">-" or "|-" or ">+" or "|+")
            {
                // A block of text over several lines: its indented lines, as one line.
                var block = new List<string>();
                while (index + 1 < lines.Length && (lines[index + 1].Length == 0 || char.IsWhiteSpace(lines[index + 1][0])))
                {
                    block.Add(lines[++index].Trim());
                }

                value = string.Join(' ', block.Where(static part => part.Length > 0));
            }
            else
            {
                value = PromptFileFormat.DecodeScalar(value);
            }

            values[line[..colon].Trim()] = value;
        }

        return (values, normalized[(end + 5)..]);
    }

    private static string? Text(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
