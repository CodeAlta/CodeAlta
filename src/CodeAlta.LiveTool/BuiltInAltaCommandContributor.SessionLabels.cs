using CodeAlta.Catalog;
using CodeAlta.Orchestration.Runtime;
using XenoAtom.CommandLine;

namespace CodeAlta.LiveTool;

// The labels of sessions: `alta session label`, and the `--label` of `alta session list` and `alta session create`. A
// session names labels for sessions (`alta label create --for sessions`) of its space or of every space.
internal sealed partial class BuiltInAltaCommandContributor
{
    private static Command CreateSessionLabelCommand(AltaCommandContext context)
    {
        string? sessionId = null;
        var add = new List<string>();
        var remove = new List<string>();
        var clear = false;
        var command = Leaf("label", "Show the labels of a session, or give it labels and take others.");
        command.Add("<session-id>?", "CodeAlta session id. Defaults to the calling agent session when omitted.", value => sessionId = value);
        command.Add("add=", "A label the session gets: id, name, path, or start of id; several separated by commas, or the option given again.", value => AddLabelReferences(add, value));
        command.Add("remove=", "A label the session loses; several separated by commas, or the option given again.", value => AddLabelReferences(remove, value));
        command.Add("clear", "Take every label from the session.", value => clear = value is not null);
        command.Add(async (_, _) => await HandleSessionLabelAsync(context, FirstNonEmpty(sessionId, context.Caller.SourceSessionId), add, remove, clear).ConfigureAwait(false));
        AddHelpText(
            command,
            "The labels of a session are labels for sessions, of its space or of every space: `alta label list --for sessions` lists them,",
            "and `alta label create <name> --for sessions` makes one. What a label stands for (its description) says when to give it.",
            "Without an option, prints the labels of the session. The CodeAlta window can group the sessions of a project by their labels.",
            "Examples:",
            "  `alta session label`",
            "  `alta session label --add bug`",
            "  `alta session label <session-id> --add audit,pr-review --remove triage`",
            "  `alta session label <session-id> --clear`");
        return command;
    }

    // Each label of a value of --add, --remove or --label: the value given, or the labels it separates with commas.
    private static void AddLabelReferences(List<string> references, string? value)
    {
        if (value is null) return;
        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!references.Contains(part, StringComparer.OrdinalIgnoreCase)) references.Add(part);
        }
    }

    private static async ValueTask<int> HandleSessionLabelAsync(AltaCommandContext context, string? sessionId, IReadOnlyList<string> add,
        IReadOnlyList<string> remove, bool clear)
    {
        const string CommandPath = "alta session label";
        context.ClaimSessionWork(sessionId);
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return UsageError(context, "usage.missingSession", "Session id is required outside a session caller.", CommandPath);
        }

        if (clear && add.Count > 0)
        {
            return UsageError(context, "usage.labelConflict", "Use either --clear or --add, not both.", CommandPath);
        }

        if (!context.TryGetRequired<SessionRuntimeService>(nameof(SessionRuntimeService), out var runtime)
            || !context.TryGetRequired<SpaceCatalog>(nameof(SpaceCatalog), out var spaces))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var infoResult = await ResolveSessionInfoAsync(context, sessionId, includeLocalState: true).ConfigureAwait(false);
        if (infoResult.ExitCode != AltaExitCodes.Success)
        {
            return infoResult.ExitCode;
        }

        var session = infoResult.Info!.Session;
        var all = await spaces.Labels.LoadAsync(context.CancellationToken).ConfigureAwait(false);
        var sessionSpaces = await SessionSpacesOfAsync(context, spaces, session).ConfigureAwait(false);
        var visible = SessionLabelsIn(all, sessionSpaces);
        var (adding, addExit) = ResolveSessionLabels(context, all, visible, add, CommandPath);
        if (adding is null)
        {
            return addExit;
        }

        // A label taken away may no longer be one the session sees (a label of another space, one deleted): its id is enough.
        var current = infoResult.Info.Labels;
        var removing = new List<string>();
        foreach (var reference in remove)
        {
            if (all.FirstOrDefault(label => string.Equals(label.Id, reference, StringComparison.OrdinalIgnoreCase)) is { } byId)
            {
                removing.Add(byId.Id);
                continue;
            }

            if (current.FirstOrDefault(id => string.Equals(id, reference, StringComparison.OrdinalIgnoreCase)) is { } named)
            {
                removing.Add(named);
                continue;
            }

            var (found, removeExit) = ResolveSessionLabels(context, all, visible, [reference], CommandPath);
            if (found is null) return removeExit;
            removing.AddRange(found.Select(static label => label.Id));
        }

        if (clear) removing.AddRange(current);
        var changed = adding.Count > 0 || removing.Count > 0;
        IReadOnlyList<string>? labels = current;
        if (changed)
        {
            try
            {
                labels = await runtime.SetSessionLabelsAsync(session.SessionId, [.. adding.Select(static label => label.Id)], removing, context.CancellationToken).ConfigureAwait(false);
            }
            catch (ArgumentException exception)
            {
                return UsageError(context, "label.invalid", exception.Message, CommandPath);
            }
            catch (SessionInUseException exception)
            {
                AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, "session.inUse", AltaExitCodes.Failure, exception.Message);
                return AltaExitCodes.Failure;
            }

            if (labels is null)
            {
                return NotFound(context, "session.notFound", $"Session '{sessionId}' was not found.");
            }
        }

        AltaJsonlWriter.WriteRecord(context.Stdout, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = "alta.session.labels",
            ["version"] = 1,
            ["correlationId"] = context.CorrelationId,
            ["sessionId"] = session.SessionId,
            ["labels"] = SessionLabelRecords(labels, all),
            ["changed"] = changed && !labels.SequenceEqual(current, StringComparer.Ordinal),
        });
        return AltaExitCodes.Success;
    }

    // The labels of a session for its records: each with its name, its path, its icon and its color; a label that no
    // longer exists is named by its id alone.
    private static object[] SessionLabelRecords(IReadOnlyList<string> ids, IReadOnlyList<LabelDescriptor> labels)
        => [.. ids.Select(id => labels.FirstOrDefault(label => label.Id == id) is { } label
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = label.Id,
                ["name"] = label.Name,
                ["path"] = LabelCatalog.PathOf(label, labels),
                ["icon"] = label.Icon,
                ["color"] = label.Color,
            }
            : new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = id, ["missing"] = true })];

    // The spaces a session is in: the one it records, or those of its project for a session created before sessions had one.
    private static async Task<IReadOnlyList<string>> SessionSpacesOfAsync(AltaCommandContext context, SpaceCatalog spaces, SessionViewDescriptor session)
    {
        var all = await spaces.LoadAsync(context.CancellationToken).ConfigureAwait(false);
        var project = session.Kind == SessionViewKind.GlobalSession || session.ProjectRef is null
            ? null
            : await spaces.Projects.GetByIdAsync(session.ProjectRef, context.CancellationToken).ConfigureAwait(false);
        return SpaceCatalog.SessionSpaces(session.SpaceId, project, all);
    }

    // The labels for sessions a session of these spaces can have: those of one of its spaces and those of every space.
    private static IReadOnlyList<LabelDescriptor> SessionLabelsIn(IReadOnlyList<LabelDescriptor> labels, IReadOnlyList<string>? spaceIds)
        => [.. labels.Where(label => label.IsForSessions && (spaceIds is null || label.Space is null || spaceIds.Contains(label.Space, StringComparer.Ordinal)))];

    // Labels for sessions by their references, among those a session sees; on failure, the error is written.
    private static (IReadOnlyList<LabelDescriptor>? Labels, int ExitCode) ResolveSessionLabels(AltaCommandContext context, IReadOnlyList<LabelDescriptor> all,
        IReadOnlyList<LabelDescriptor> visible, IReadOnlyList<string> references, string commandPath)
    {
        var resolved = new List<LabelDescriptor>(references.Count);
        bool Named(LabelDescriptor label, string text) => string.Equals(label.Id, text, StringComparison.OrdinalIgnoreCase)
            || string.Equals(label.Name, text, StringComparison.OrdinalIgnoreCase)
            || string.Equals(NormalizeLabelPath(LabelCatalog.PathOf(label, all)), NormalizeLabelPath(text), StringComparison.OrdinalIgnoreCase);
        foreach (var reference in references)
        {
            var text = reference.Trim();
            if (!visible.Any(label => Named(label, text) || label.Id.StartsWith(text, StringComparison.OrdinalIgnoreCase)))
            {
                // A label for projects is not one a session gets: say so rather than that none is found.
                return all.FirstOrDefault(label => !label.IsForSessions && Named(label, text)) is { } ofProjects
                    ? (null, UsageError(context, "label.invalid",
                        $"The label '{ofProjects.Name}' is for projects, not for sessions. List the labels for sessions with `alta label list --for sessions`, or create one with `alta label create <name> --for sessions`.", commandPath))
                    : (null, NotFound(context, "label.notFound",
                        $"No label for sessions has the id, the name or the path '{text}'. List them with `alta label list --for sessions`, or create one with `alta label create <name> --for sessions`."));
            }

            var (label, exitCode) = ResolveLabel(context, visible, text, commandPath);
            if (label is null) return (null, exitCode);
            if (!resolved.Contains(label)) resolved.Add(label);
        }

        return (resolved, AltaExitCodes.Success);
    }

    // The labels whose sessions `alta session list --label` lists: each label named and those under it. Null without --label.
    private static async Task<(IReadOnlySet<string>? Ids, int ExitCode)> ResolveSessionListLabelsAsync(AltaCommandContext context, IReadOnlyList<string> references)
    {
        if (references.Count == 0) return (null, AltaExitCodes.Success);
        if (!context.TryGetRequired<SpaceCatalog>(nameof(SpaceCatalog), out var spaces)) return (null, AltaExitCodes.ServiceUnavailable);
        var all = await spaces.Labels.LoadAsync(context.CancellationToken).ConfigureAwait(false);
        var own = await LabelCallerSpaceAsync(context, spaces).ConfigureAwait(false);
        var visible = SessionLabelsIn(all, own is null ? null : [own.Id]);
        var (labels, exitCode) = ResolveSessionLabels(context, all, visible, references, "alta session list");
        if (labels is null) return (null, exitCode);
        return (labels.SelectMany(label => LabelCatalog.Subtree(label.Id, all)).ToHashSet(StringComparer.Ordinal), AltaExitCodes.Success);
    }

    // The labels `alta session create --label` gives the new session, among those of its space; null without --label.
    private static async Task<(IReadOnlyList<string>? Ids, int ExitCode)> ResolveCreatedSessionLabelsAsync(AltaCommandContext context, IReadOnlyList<string> references,
        string? spaceId)
    {
        if (references.Count == 0) return ([], AltaExitCodes.Success);
        if (!context.TryGetRequired<SpaceCatalog>(nameof(SpaceCatalog), out var spaces)) return (null, AltaExitCodes.ServiceUnavailable);
        var all = await spaces.Labels.LoadAsync(context.CancellationToken).ConfigureAwait(false);
        var (labels, exitCode) = ResolveSessionLabels(context, all, SessionLabelsIn(all, [spaceId ?? SpaceDescriptor.DefaultId]), references, "alta session create");
        if (labels is null) return (null, exitCode);
        // Refused before anything is created: the session and its worktree would stay otherwise.
        return labels.Count > SessionRuntimeService.MaximumSessionLabels
            ? (null, UsageError(context, "label.invalid", $"A session has at most {SessionRuntimeService.MaximumSessionLabels} labels.", "alta session create"))
            : ([.. labels.Select(static label => label.Id)], AltaExitCodes.Success);
    }
}
