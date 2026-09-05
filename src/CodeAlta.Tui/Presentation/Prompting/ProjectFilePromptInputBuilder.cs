using System.Text;
using CodeAlta.Agent;
using CodeAlta.Catalog;

namespace CodeAlta.Tui.Presentation.Prompting;

internal static class ProjectFilePromptInputBuilder
{
    public static async Task<ProjectFilePromptInputResult> BuildAsync(
        string prompt,
        string? projectRoot,
        IProjectFileSearchService searchService,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(searchService);

        var tokens = ProjectFilePromptReferenceParser.Parse(prompt);
        if (tokens.Count == 0)
        {
            return new ProjectFilePromptInputResult(prompt, AgentInput.Text(prompt), []);
        }

        var builder = new StringBuilder(prompt.Length);
        var attachments = new List<AgentInputItem>();
        var resolvedReferences = new List<ProjectFileResolution>();
        var cursor = 0;
        foreach (var token in tokens)
        {
            builder.Append(prompt, cursor, token.StartIndex - cursor);

            if (token.Kind == ProjectFilePromptTokenKind.EscapedAt)
            {
                builder.Append('@');
                cursor = token.StartIndex + token.Length;
                continue;
            }

            if (string.IsNullOrWhiteSpace(projectRoot) ||
                token.IsMalformed ||
                string.IsNullOrWhiteSpace(token.LookupText))
            {
                builder.Append(token.RawText);
                cursor = token.StartIndex + token.Length;
                continue;
            }

            ProjectFileResolution resolution;
            try
            {
                resolution = await searchService.ResolveAsync(
                    new ProjectFileResolveQuery(projectRoot, token.LookupText!, token.LineRange),
                    cancellationToken);
            }
            catch (ArgumentException)
            {
                builder.Append(token.RawText);
                cursor = token.StartIndex + token.Length;
                continue;
            }

            if (!resolution.IsResolved || resolution.Item is null)
            {
                builder.Append(token.RawText);
                cursor = token.StartIndex + token.Length;
                continue;
            }

            builder.Append(ProjectFilePromptReferenceFormatter.BuildMarkdownLink(resolution, token.DisplayText));
            attachments.Add(CreateAttachment(resolution));
            resolvedReferences.Add(resolution);
            cursor = token.StartIndex + token.Length;
        }

        builder.Append(prompt, cursor, prompt.Length - cursor);
        var normalizedPrompt = builder.ToString();
        if (attachments.Count == 0)
        {
            return new ProjectFilePromptInputResult(normalizedPrompt, AgentInput.Text(normalizedPrompt), []);
        }

        var items = new List<AgentInputItem>(attachments.Count + 1)
        {
            new AgentInputItem.Text(normalizedPrompt),
        };
        items.AddRange(attachments);
        return new ProjectFilePromptInputResult(normalizedPrompt, new AgentInput(items), resolvedReferences);
    }

    private static AgentInputItem CreateAttachment(ProjectFileResolution resolution)
    {
        var item = resolution.Item!;
        var lineRange = item.Kind == ProjectFileSearchItemKind.File && resolution.LineRange is not null
            ? new AgentLineRange(resolution.LineRange.StartLine, resolution.LineRange.EndLine)
            : null;

        return item.Kind switch
        {
            ProjectFileSearchItemKind.File => new AgentInputItem.File(item.FullPath, item.RelativePath, lineRange),
            ProjectFileSearchItemKind.Directory => new AgentInputItem.Directory(item.FullPath, item.RelativePath),
            _ => throw new ArgumentOutOfRangeException(nameof(item.Kind)),
        };
    }
}
