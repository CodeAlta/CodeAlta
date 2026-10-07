using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Plugins.Abstractions;

// A card in the timeline of a session, computed from its events. It is shown and never sent to the model.
[Plugin("timeline-card", DisplayName = "Turn summary", Description = "Shows the tool calls and the changed files of each turn as a card in the timeline.")]
public sealed class TurnSummaryPlugin : PluginBase
{
    public override IEnumerable<PluginSessionEventProjectionContribution> GetSessionEventProjections()
    {
        yield return new PluginSessionEventProjectionContribution { Name = "turn-summary", ProjectAsync = ProjectAsync };
    }

    private static ValueTask<IReadOnlyList<PluginDerivedSessionEvent>> ProjectAsync(PluginSessionEventProjectionContext context, CancellationToken cancellationToken)
    {
        var cards = new List<PluginDerivedSessionEvent>();
        // The events of one turn have the same run id. A tool call is an activity that ends as Completed or Failed.
        var turns = context.Events.OfType<AgentActivityEvent>()
            .Where(static activity => activity.RunId is not null && activity.Kind == AgentActivityKind.ToolCall && activity.Phase == AgentActivityPhase.Completed)
            .GroupBy(static activity => activity.RunId!.Value.Value);
        foreach (var turn in turns)
        {
            var tools = turn.GroupBy(static activity => activity.Name ?? "tool").OrderByDescending(static tool => tool.Count()).ToArray();
            var files = turn.SelectMany(static activity => ModifiedFiles(activity.Details)).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray();
            var calls = turn.Count();
            var sections = new List<PluginDerivedSessionEventDetailSection>
            {
                new() { Header = "By tool", Markdown = string.Join('\n', tools.Select(static tool => $"- `{tool.Key}`: {tool.Count()}")) },
            };
            if (files.Length > 0)
            {
                sections.Add(new() { Header = "Changed files", Markdown = string.Join('\n', files.Select(static file => $"- `{file}`")) });
            }

            cards.Add(new PluginDerivedSessionEvent
            {
                // The same id for the same turn: the card is updated, not added again.
                EventId = $"turn-summary:{context.SessionId}:{turn.Key}",
                Timestamp = turn.Max(static activity => activity.Timestamp),
                // The bold start is the title of the card. The terminal application shows the Markdown.
                Markdown = $"**Turn** · {calls} tool calls · {files.Length} files changed",
                // The desktop application shows the fragment after the title.
                Html = $"""<span class="alta-tag">{calls} tool calls</span> <span class="alta-tag{(files.Length > 0 ? " alta-warning" : "")}">{files.Length} files changed</span>""",
                DetailSections = sections,
            });
        }

        return ValueTask.FromResult<IReadOnlyList<PluginDerivedSessionEvent>>(cards);
    }

    // The details of a tool call are JSON: toolName, arguments, readFiles and modifiedFiles (full paths), and result once it ended.
    private static IEnumerable<string> ModifiedFiles(JsonElement? details)
        => details is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty("modifiedFiles", out var files) && files.ValueKind == JsonValueKind.Array
            ? files.EnumerateArray().Select(static file => file.GetString() ?? "").Where(static file => file.Length > 0)
            : [];
}
