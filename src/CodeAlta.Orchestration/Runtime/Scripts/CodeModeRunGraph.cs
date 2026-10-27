using System.Globalization;
using System.Text;
using CodeAlta.Agent;

namespace CodeAlta.Orchestration.Runtime.Scripts;

/// <summary>A step of the graph of a run: what its script declares, and how far it went.</summary>
/// <param name="Id">The id that <c>step()</c> names.</param>
/// <param name="Title">The title of its box.</param>
/// <param name="After">The steps it runs after, resolved: empty at the start.</param>
/// <param name="Each">Whether it fans out.</param>
/// <param name="State"><c>pending</c>, <c>running</c>, <c>waiting</c>, <c>completed</c>, <c>failed</c>, <c>cancelled</c> or <c>interrupted</c>.</param>
/// <param name="Sessions">How many sessions it started.</param>
/// <param name="Declared">Whether its script declares it; a step that ran without being declared is drawn after the others.</param>
public sealed record CodeModeRunGraphStep(string Id, string Title, IReadOnlyList<string> After, bool Each, string State, int Sessions, bool Declared);

/// <summary>
/// The graph of a run, read from its records (<see cref="AgentWorkflowEvent"/>): the steps its scripts declare
/// (<c>meta.steps</c>) from the start, pending until they run, then the state each one reached; the steps that ran
/// without being declared come after them. The card and the tab of a run draw the same graph, and
/// <c>alta workflow show --format mermaid</c> writes it as Mermaid.
/// </summary>
public static class CodeModeRunGraph
{
    /// <summary>Reads the graph of a run from its records, in their order.</summary>
    /// <param name="records">The records of the run.</param>
    /// <returns>The steps, declared ones first, in their order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="records"/> is null.</exception>
    public static IReadOnlyList<CodeModeRunGraphStep> Read(IEnumerable<AgentWorkflowEvent> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var declared = new List<(string Id, string Title, IReadOnlyList<string> After, bool Each)>();
        var states = new Dictionary<string, string>(StringComparer.Ordinal);
        var sessions = new Dictionary<string, int>(StringComparer.Ordinal);
        var ran = new List<string>();
        string? waitingStep = null;
        string? runStatus = null;
        foreach (var record in records)
        {
            switch (record.Kind)
            {
                case AgentWorkflowEventKind.StepsDeclared when record.Steps is { } steps:
                    for (var index = 0; index < steps.Count; index++)
                    {
                        var step = steps[index];
                        if (declared.Exists(known => known.Id == step.Id)) continue;
                        // No `after`: after the step declared before it in the same script; an empty list: at the start.
                        var after = step.After ?? (index > 0 ? [steps[index - 1].Id] : []);
                        declared.Add((step.Id, string.IsNullOrWhiteSpace(step.Title) ? step.Id : step.Title, after, step.Each));
                    }

                    break;
                case AgentWorkflowEventKind.StepStarted when record.StepId is { } started:
                    states[started] = "running";
                    if (!ran.Contains(started)) ran.Add(started);
                    break;
                case AgentWorkflowEventKind.StepEnded when record.StepId is { } ended:
                    states[ended] = record.Status ?? "completed";
                    if (!ran.Contains(ended)) ran.Add(ended);
                    break;
                case AgentWorkflowEventKind.SessionStarted when record.StepId is { } owner:
                    sessions[owner] = sessions.GetValueOrDefault(owner) + 1;
                    break;
                case AgentWorkflowEventKind.WaitStarted when record.Status is "question" or "approval":
                    waitingStep = record.StepId;
                    break;
                case AgentWorkflowEventKind.WaitEnded:
                    waitingStep = null;
                    break;
                case AgentWorkflowEventKind.RunEnded:
                    runStatus = record.Status ?? "completed";
                    break;
            }
        }

        string StateOf(string id)
        {
            var state = states.GetValueOrDefault(id, "pending");
            if (state == "running" && runStatus is not null) return runStatus == "interrupted" ? "interrupted" : "cancelled";
            return state == "running" && waitingStep == id ? "waiting" : state;
        }

        var graph = declared.Select(step => new CodeModeRunGraphStep(step.Id, step.Title, step.After, step.Each, StateOf(step.Id), sessions.GetValueOrDefault(step.Id), true)).ToList();
        string? previous = graph.Count > 0 ? graph[^1].Id : null;
        foreach (var id in ran.Where(id => !declared.Exists(step => step.Id == id)))
        {
            graph.Add(new CodeModeRunGraphStep(id, id, previous is null ? [] : [previous], false, StateOf(id), sessions.GetValueOrDefault(id), false));
            previous = id;
        }

        return graph;
    }

    /// <summary>Writes the graph of a run as a Mermaid <c>flowchart</c>: a box per step with its state, an arrow per <c>after</c>.</summary>
    /// <param name="steps">The steps of the run (<see cref="Read"/>).</param>
    /// <returns>The Mermaid text, ending with a new line.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="steps"/> is null.</exception>
    public static string ToMermaid(IReadOnlyList<CodeModeRunGraphStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        var builder = new StringBuilder("flowchart LR\n");
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < steps.Count; index++)
        {
            ids[steps[index].Id] = "s" + index.ToString(CultureInfo.InvariantCulture);
        }

        foreach (var step in steps)
        {
            var label = new StringBuilder(step.Title);
            if (step.Each) label.Append(" ×");
            label.Append(" · ").Append(step.State);
            if (step.Sessions > 0) label.Append(" · ").Append(step.Sessions.ToString(CultureInfo.InvariantCulture)).Append(step.Sessions == 1 ? " session" : " sessions");
            builder.Append("  ").Append(ids[step.Id]).Append("[\"").Append(Escape(label.ToString())).Append("\"]:::").Append(step.State).Append('\n');
        }

        foreach (var step in steps)
        {
            foreach (var after in step.After)
            {
                if (ids.TryGetValue(after, out var from)) builder.Append("  ").Append(from).Append(" --> ").Append(ids[step.Id]).Append('\n');
            }
        }

        foreach (var (state, style) in Styles)
        {
            if (steps.Any(step => step.State == state)) builder.Append("  classDef ").Append(state).Append(' ').Append(style).Append('\n');
        }

        return builder.ToString();
    }

    // The look of each state, readable on a light and a dark page.
    private static readonly (string State, string Style)[] Styles =
    [
        ("pending", "fill:#f1f3f5,stroke:#adb5bd,color:#495057,stroke-dasharray:4 3"),
        ("running", "fill:#e7f5ff,stroke:#1c7ed6,color:#1864ab"),
        ("waiting", "fill:#fff4e6,stroke:#f08c00,color:#a85d00"),
        ("completed", "fill:#ebfbee,stroke:#2f9e44,color:#2b8a3e"),
        ("failed", "fill:#fff5f5,stroke:#e03131,color:#c92a2a"),
        ("cancelled", "fill:#f8f9fa,stroke:#868e96,color:#495057"),
        ("interrupted", "fill:#fff9db,stroke:#f59f00,color:#a87100"),
    ];

    // A label is a quoted Mermaid string: quotes, and what could open markup, are written as entities.
    private static string Escape(string text)
        => text.ReplaceLineEndings(" ").Replace("&", "#amp;", StringComparison.Ordinal).Replace("\"", "#quot;", StringComparison.Ordinal)
            .Replace("<", "#lt;", StringComparison.Ordinal).Replace(">", "#gt;", StringComparison.Ordinal);
}
