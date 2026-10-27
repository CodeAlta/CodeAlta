using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Catalog.WorkItems;
using CodeAlta.Desktop.WorkItems;
using CodeAlta.Orchestration.Runtime.Scripts;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// The page's side of the Workflows tab: the scripts of the library a project sees, with their parameters and their
/// declared steps; the runs of scripts of the host; <b>Run…</b>, which starts a script in a new session of a project;
/// <b>Save as script…</b>, which asks the session of a run to turn it into a script of the library; and the resumes and
/// reruns of the card of a run, which ask its session too.
/// </summary>
[NeoRpcService("workflows", Version = 1)]
internal sealed class WorkflowsService
{
    /// <summary>The longest arguments a run accepts, in characters.</summary>
    internal const int MaximumArgumentsLength = 64 * 1024;

    private readonly CodeModeScriptLibrary? _scripts;
    private readonly CodeModeRunRegistry? _runs;
    private readonly ProjectCatalog? _projects;
    private readonly ISessionStarter? _starter;
    private readonly Func<string, string, CancellationToken, Task<string?>>? _send;
    private readonly Func<ProjectDescriptor?, CancellationToken, Task<SpaceFilter>>? _spaceOf;
    private readonly Func<string, string, CancellationToken, Task<IReadOnlyList<AgentWorkflowEvent>>>? _records;
    private readonly string? _epoch;

    /// <summary>Creates an unavailable service for launches without an owned host.</summary>
    internal WorkflowsService()
    {
    }

    /// <summary>Creates the service for an owned host.</summary>
    /// <param name="scripts">The library of scripts.</param>
    /// <param name="runs">The runs of the scripts of the host.</param>
    /// <param name="projects">The host's project catalog.</param>
    /// <param name="starter">Starts a session of a project from the window, in the space it shows.</param>
    /// <param name="send">Sends a prompt to a session, queued when it is busy: the session, the text; it gives why it was not sent, or null.</param>
    /// <param name="epoch">The host epoch that requests must name.</param>
    /// <param name="spaceOf">
    /// Gives the space the window shows, for a project or none: the tab lists and runs only the scripts it has
    /// (<c>[scripts.spaces]</c>); null for every script.
    /// </param>
    /// <param name="records">
    /// Reads the records of a run in the journal of its session (the session, the run), for the tab of the run; null for none.
    /// </param>
    internal WorkflowsService(CodeModeScriptLibrary scripts, CodeModeRunRegistry runs, ProjectCatalog projects, ISessionStarter starter,
        Func<string, string, CancellationToken, Task<string?>> send, string epoch, Func<ProjectDescriptor?, CancellationToken, Task<SpaceFilter>>? spaceOf = null,
        Func<string, string, CancellationToken, Task<IReadOnlyList<AgentWorkflowEvent>>>? records = null)
    {
        ArgumentNullException.ThrowIfNull(scripts);
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(starter);
        ArgumentNullException.ThrowIfNull(send);
        ArgumentException.ThrowIfNullOrWhiteSpace(epoch);
        (_scripts, _runs, _projects, _starter, _send, _epoch, _spaceOf, _records) = (scripts, runs, projects, starter, send, epoch, spaceOf, records);
    }

    /// <summary>The scripts of the library a project sees, or yours and the samples without a project.</summary>
    [NeoRpcMethod("scripts")]
    public async Task<WorkflowScriptsResponse> ScriptsAsync(WorkflowScriptsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, []);
        if (request.ProjectId is { } projectId && !Identifier(projectId)) return new("invalid_request", []);
        var project = request.ProjectId is null ? null : await _projects!.GetByIdAsync(request.ProjectId, cancellationToken).ConfigureAwait(false);
        if (request.ProjectId is not null && (project is null || project.Archived)) return new("not_found", []);
        var spaces = _spaceOf is null ? null : await _spaceOf(project, cancellationToken).ConfigureAwait(false);
        var scripts = await Task.Run(() => _scripts!.List(project?.ProjectPath, spaces), cancellationToken).ConfigureAwait(false);
        return new("ok", [.. scripts.Select(static script => new WorkflowScriptItem(script.Name, Scope(script.Scope), script.Title, script.Description,
            script.Problem, script.Path, Parameters(script.ParametersSchemaJson),
            [.. script.Steps.Select(static step => new WorkflowStepItem(step.Id, step.Title, step.After, step.Each))]))]);
    }

    /// <summary>The longest source of a script the tab shows, in characters.</summary>
    internal const int MaximumSourceLength = 512 * 1024;

    /// <summary>
    /// The source of a script of the library, to show it: the one of its scope that a project sees, or yours and the samples
    /// without a project.
    /// </summary>
    /// <summary>
    /// Deletes a script of a project or one of yours (<b>Delete</b> on its card). A sample of CodeAlta is not deleted; a
    /// script of yours or a sample of the same name is then the one the project runs.
    /// </summary>
    [NeoRpcMethod("delete")]
    public async Task<WorkflowMutationResponse> DeleteAsync(WorkflowDeleteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, null);
        if (!CodeModeScriptLibrary.IsName(request.Name) || request.Scope is not ("project" or "global")
            || (request.ProjectId is { } projectId && !Identifier(projectId)) || request.Scope == "project" && request.ProjectId is null)
            return new("invalid_request", null);
        var project = request.ProjectId is null ? null : await _projects!.GetByIdAsync(request.ProjectId, cancellationToken).ConfigureAwait(false);
        if (request.Scope == "project" && (project is null || project.Archived)) return new("refused", "The project is no longer one of the projects of CodeAlta.");
        var scope = request.Scope == "project" ? CodeModeScriptScope.Project : CodeModeScriptScope.Global;
        bool deleted;
        try { deleted = await Task.Run(() => _scripts!.Delete(request.Name, scope, project?.ProjectPath), cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { deleted = false; }
        return deleted ? new("ok", null) : new("refused", $"The script '{request.Name}' could not be deleted: it is no longer there, or its file is in use.");
    }

    [NeoRpcMethod("source")]
    public async Task<WorkflowSourceResponse> SourceAsync(WorkflowSourceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, null);
        if (!CodeModeScriptLibrary.IsName(request.Name) || (request.ProjectId is { } projectId && !Identifier(projectId))) return new("invalid_request", null);
        var project = request.ProjectId is null ? null : await _projects!.GetByIdAsync(request.ProjectId, cancellationToken).ConfigureAwait(false);
        if (request.ProjectId is not null && (project is null || project.Archived)) return new("not_found", null);
        var spaces = _spaceOf is null ? null : await _spaceOf(project, cancellationToken).ConfigureAwait(false);
        var script = await Task.Run(() => _scripts!.List(project?.ProjectPath, spaces)
            .FirstOrDefault(candidate => candidate.Name == request.Name && Scope(candidate.Scope) == request.Scope), cancellationToken).ConfigureAwait(false);
        if (script is null) return new("not_found", null);
        try
        {
            var source = await File.ReadAllTextAsync(script.Path, cancellationToken).ConfigureAwait(false);
            return source.Length > MaximumSourceLength ? new("refused", null) : new("ok", source);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new("not_found", null);
        }
    }

    /// <summary>
    /// The runs of scripts of the host: running ones, and those that ended, which the store keeps (the 100 newest, 14 days at
    /// most), also from before CodeAlta started again. Listing reads the store, never a journal.
    /// </summary>
    [NeoRpcMethod("runs")]
    public WorkflowRunsResponse Runs(WorkflowsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, []);
        return new("ok", [.. _runs!.ListKnown(null)
            .OrderByDescending(static run => run.Running).ThenByDescending(static run => run.StartedAt)
            .Select(ItemOf)]);
    }

    /// <summary>
    /// A run for its tab: the run, its records (the steps its script declares, its steps, the sessions it started), its
    /// script, its result, and its graph as Mermaid. A run CodeAlta no longer knows is <c>not_found</c>.
    /// </summary>
    [NeoRpcMethod("runDetail")]
    public async Task<WorkflowRunDetailResponse> RunDetailAsync(WorkflowRunDetailRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, null, [], null, null, null);
        if (!RunId(request.RunId)) return new("invalid_request", null, [], null, null, null);
        if (await _runs!.ReadDetailAsync(request.RunId).ConfigureAwait(false) is not { } detail) return new("not_found", null, [], null, null, null);
        IReadOnlyList<AgentWorkflowEvent> records = _records is null ? [] : await _records(detail.Info.SessionId, request.RunId, cancellationToken).ConfigureAwait(false);
        return new("ok", ItemOf(detail.Info), [.. records.Select(static record => new WorkflowRecordItem(record.Kind.ToString(), record.ChildSessionId ?? record.StepId,
                record.Status, record.Title ?? record.Message, record.ToJson(), record.Timestamp))],
            detail.Source, detail.Result, CodeModeRunGraph.ToMermaid(CodeModeRunGraph.Read(records)));
    }

    private static WorkflowRunItem ItemOf(CodeModeRunInfo run)
        => new(run.Id, run.SessionId, run.Title, run.Running ? "running" : run.Status ?? "completed", run.StartedAt, run.EndedAt, run.CallId, run.Calls);

    /// <summary>Starts a script of the library in a new session of a project. The answer comes once the session exists, or says why it did not start.</summary>
    [NeoRpcMethod("run")]
    public async Task<WorkflowRunResponse> RunAsync(WorkflowRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, null, null);
        if (!CodeModeScriptLibrary.IsName(request.Name) || !Identifier(request.ProjectId) || request.Arguments is { Length: > MaximumArgumentsLength }
            || request.ProviderId is not null && !Name(request.ProviderId) || request.ModelId is not null && (request.ProviderId is null || !Name(request.ModelId))
            || request.ReasoningEffort is not null && (request.ModelId is null || !Name(request.ReasoningEffort))
            || request.PermissionMode is not null && !Name(request.PermissionMode) || request.Sandbox is not null && !Name(request.Sandbox)
            || request.AgentPromptId is not null && !Name(request.AgentPromptId))
            return new("invalid_request", null, null);
        var project = await _projects!.GetByIdAsync(request.ProjectId, cancellationToken).ConfigureAwait(false);
        if (project is null || project.Archived) return new("refused", null, "The project is no longer one of the projects of CodeAlta.");
        var spaces = _spaceOf is null ? null : await _spaceOf(project, cancellationToken).ConfigureAwait(false);
        var (prompt, problem) = await Task.Run(() => (CodeModeScriptRunPrompt.Build(_scripts!, request.Name, request.Arguments, null, project.ProjectPath, spaces, out var problem), problem),
            cancellationToken).ConfigureAwait(false);
        if (prompt is null) return new("refused", null, problem);
        var title = _scripts!.Find(request.Name, project.ProjectPath)?.Title is { Length: > 0 } named ? named : request.Name;
        // The session runs the script and its model is the one of the sessions the script starts without a tier: what the
        // user chose in the form of the Agent chip, else the defaults.
        var asked = request.ProviderId is null ? null : new WorkItemSelection(request.ProviderId, request.ModelId, request.ReasoningEffort);
        var settings = SessionStartSettings.Of(request.AgentPromptId, request.PermissionMode, request.ReviewBeforeCommit, request.Sandbox, SessionOptionsChoice.Record(request.Options));
        var model = new SessionStartModel(asked) { CodeMode = true };
        var started = await _starter!.StartAsync(project, title, _ => prompt, false, null, "workflow", settings?.Apply(model) ?? model).ConfigureAwait(false);
        return started.Problem is null ? new("ok", started.SessionId, null) : new("refused", started.SessionId, started.Problem);
    }

    /// <summary>Asks the session of a run to turn the run into a script of the library, saved under a name.</summary>
    [NeoRpcMethod("saveAsScript")]
    public async Task<WorkflowMutationResponse> SaveAsScriptAsync(WorkflowSaveAsScriptRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, null);
        if (!Identifier(request.SessionId) || !RunId(request.RunId)) return new("invalid_request", null);
        if (!CodeModeScriptLibrary.IsName(request.Name)) return new("refused", $"'{request.Name}' is not a script name: lowercase letters, digits and hyphens, starting with a letter.");
        var failure = await _send!(request.SessionId, CodeModeScriptRunPrompt.SaveAsScript(request.RunId, request.Name), cancellationToken).ConfigureAwait(false);
        return failure is null ? new("ok", null) : new("refused", failure);
    }

    /// <summary>
    /// Asks the session of a run to resume it (<b>Resume</b> on the card of an interrupted run), to run it again over its
    /// recorded results (<b>Rerun with recorded results</b>), or from one of its steps (<b>Rerun from this step</b>).
    /// </summary>
    [NeoRpcMethod("resume")]
    public async Task<WorkflowMutationResponse> ResumeAsync(WorkflowResumeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, null);
        if (!Identifier(request.SessionId) || !RunId(request.RunId) || request.From is { Length: 0 or > 256 } || (request.From is not null && request.All))
            return new("invalid_request", null);
        if (_runs!.Store is not { } store) return new("refused", "This CodeAlta keeps no record of runs.");
        await _runs.WhenKeptAsync(request.RunId).WaitAsync(cancellationToken).ConfigureAwait(false);
        var run = await Task.Run(() => store.TryLoad(request.RunId), cancellationToken).ConfigureAwait(false);
        if (run is null || !string.Equals(run.SessionId, request.SessionId, StringComparison.OrdinalIgnoreCase))
            return new("refused", "CodeAlta no longer keeps this run.");
        if (_runs.FindAny(request.RunId)?.Running ?? run.EndedAt is null) return new("refused", "The run still runs.");
        var failure = await _send!(request.SessionId, CodeModeResume.Prompt(request.RunId, request.From, request.All), cancellationToken).ConfigureAwait(false);
        return failure is null ? new("ok", null) : new("refused", failure);
    }

    /// <summary>
    /// Stops a run that runs (<b>Stop</b> on its card or in its tab), with the sessions it started. Its result still comes
    /// back to its session, which is told that the user stopped it.
    /// </summary>
    [NeoRpcMethod("stop")]
    public WorkflowMutationResponse Stop(WorkflowStopRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, null);
        if (!RunId(request.RunId)) return new("invalid_request", null);
        if (_runs!.FindAny(request.RunId) is not { Running: true } run) return new("refused", "The run no longer runs.");
        run.Stop("the user");
        return new("ok", null);
    }

    private string? Refuse(string? expectedEpoch)
        => _scripts is null ? "unavailable" : string.Equals(expectedEpoch, _epoch, StringComparison.Ordinal) ? null : "stale_epoch";

    private static bool Identifier(string? value) => value is { Length: > 0 and <= 256 } && !value.Any(char.IsControl);

    // A provider, a model, an effort, a mode, a profile or an agent prompt: one trimmed line.
    private static bool Name(string value) => value is { Length: > 0 and <= 256 } && value == value.Trim() && !value.Any(char.IsControl);

    private static bool RunId(string? value) => value is { Length: > 0 and <= 64 } && value.All(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static string Scope(CodeModeScriptScope scope) => scope switch
    {
        CodeModeScriptScope.Project => "project",
        CodeModeScriptScope.Global => "global",
        _ => "builtin",
    };

    // A field per parameter: its JSON type, whether it is required, its description, and its default as JSON.
    private static IReadOnlyList<WorkflowScriptParameter> Parameters(string? schema)
    {
        if (schema is null) return [];
        using var document = JsonDocument.Parse(schema);
        var root = document.RootElement;
        var required = root.TryGetProperty("required", out var names) && names.ValueKind == JsonValueKind.Array
            ? names.EnumerateArray().Select(static name => name.GetString()).ToHashSet(StringComparer.Ordinal) : [];
        if (!root.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object) return [];
        return [.. properties.EnumerateObject().Select(property => new WorkflowScriptParameter(property.Name,
            Text(property.Value, "type"), required.Contains(property.Name), Text(property.Value, "description"),
            property.Value.ValueKind == JsonValueKind.Object && property.Value.TryGetProperty("default", out var value) ? value.GetRawText() : null))];

        static string? Text(JsonElement element, string name)
            => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
}

internal sealed record WorkflowsRequest(string? ExpectedEpoch);

/// <summary>Asks for the scripts of the library.</summary>
/// <param name="ProjectId">The project whose scripts come first; null for yours and the samples.</param>
internal sealed record WorkflowScriptsRequest(string? ExpectedEpoch, string? ProjectId);

/// <summary>The scripts of the library, by name.</summary>
internal sealed record WorkflowScriptsResponse(string Status, IReadOnlyList<WorkflowScriptItem> Scripts);

/// <summary>A script of the library, as the Workflows tab shows it.</summary>
/// <param name="Scope"><c>project</c>, <c>global</c> or <c>builtin</c>.</param>
/// <param name="Title">The title its <c>meta</c> gives, or null.</param>
/// <param name="Problem">Why it does not parse, or null.</param>
/// <param name="Path">Its file.</param>
internal sealed record WorkflowScriptItem(string Name, string Scope, string? Title, string? Description, string? Problem, string Path,
    IReadOnlyList<WorkflowScriptParameter> Parameters, IReadOnlyList<WorkflowStepItem> Steps);

/// <summary>A parameter of a script.</summary>
/// <param name="Type">Its JSON type, such as <c>integer</c>; null for any.</param>
/// <param name="Default">Its default value as JSON, or null.</param>
internal sealed record WorkflowScriptParameter(string Name, string? Type, bool Required, string? Description, string? Default);

/// <summary>A step a script declares.</summary>
/// <param name="After">The steps it runs after: null for the step before it, empty for the start.</param>
/// <param name="Each">Whether it fans out at run time.</param>
internal sealed record WorkflowStepItem(string Id, string Title, IReadOnlyList<string>? After, bool Each);

/// <summary>The runs of scripts of the host, running first, then newest first.</summary>
internal sealed record WorkflowRunsResponse(string Status, IReadOnlyList<WorkflowRunItem> Runs);

/// <summary>One run of a script.</summary>
/// <param name="State"><c>running</c>, <c>completed</c>, <c>failed</c> or <c>cancelled</c>.</param>
/// <param name="CallId">The <c>exec</c> call that runs it, or null.</param>
/// <param name="Calls">The calls it made.</param>
internal sealed record WorkflowRunItem(string Id, string SessionId, string? Title, string State, DateTimeOffset StartedAt, DateTimeOffset? EndedAt, string? CallId, int Calls);

/// <summary>Asks for a run, for its tab.</summary>
internal sealed record WorkflowRunDetailRequest(string? ExpectedEpoch, string RunId);

/// <summary>A run for its tab.</summary>
/// <param name="Records">Its records in the journal of its session, in their order, as the card of its call reads them.</param>
/// <param name="Source">Its script, or null when it is not kept.</param>
/// <param name="Result">What its session was given once it ended, or null.</param>
/// <param name="Mermaid">Its graph as a Mermaid flowchart, as <c>alta workflow show --format mermaid</c> writes it.</param>
internal sealed record WorkflowRunDetailResponse(string Status, WorkflowRunItem? Run, IReadOnlyList<WorkflowRecordItem> Records, string? Source, string? Result, string? Mermaid);

/// <summary>A record of a run, as a row of the history of its session gives it to its card.</summary>
/// <param name="Kind">What happened: <c>StepsDeclared</c>, <c>StepStarted</c>, <c>SessionEnded</c>...</param>
/// <param name="ActivityId">The session it started, or its step.</param>
/// <param name="Phase">Its status.</param>
/// <param name="Text">Its title or its message.</param>
/// <param name="Details">The record as JSON.</param>
internal sealed record WorkflowRecordItem(string Kind, string? ActivityId, string? Phase, string? Text, string Details, DateTimeOffset Timestamp);

/// <summary>Starts a script in a new session of a project.</summary>
/// <param name="Arguments">Its arguments, a JSON object; null for none.</param>
/// <param name="ProviderId">The provider the user chose for the session that runs it; null for the default.</param>
/// <param name="ModelId">The model the user chose, of that provider; null leaves it to the provider.</param>
/// <param name="ReasoningEffort">The reasoning effort the user chose, for that model; null leaves it to the model.</param>
/// <param name="AgentPromptId">The agent prompt the user chose; null for the default one of the space.</param>
/// <param name="PermissionMode">The permission mode the user chose; null for the one of the provider.</param>
/// <param name="ReviewBeforeCommit">Whether the user reviews the changes of the session before a commit; null follows the setting.</param>
/// <param name="Sandbox">The sandbox of the session, a profile or <c>off</c>; null for what the settings give.</param>
/// <param name="Options">The options of the session, such as whether it can start sessions of its own; null for the defaults.</param>
internal sealed record WorkflowRunRequest(string? ExpectedEpoch, string Name, string ProjectId, string? Arguments,
    string? ProviderId = null, string? ModelId = null, string? ReasoningEffort = null, string? AgentPromptId = null, string? PermissionMode = null,
    bool? ReviewBeforeCommit = null, string? Sandbox = null, SessionOptionsChoice? Options = null);

/// <param name="SessionId">The session that was created, also when the prompt was not sent.</param>
/// <param name="Message">Why the run did not start.</param>
internal sealed record WorkflowRunResponse(string Status, string? SessionId, string? Message);

/// <summary>Asks the session of a run to save it as a script of the library.</summary>
internal sealed record WorkflowSaveAsScriptRequest(string? ExpectedEpoch, string SessionId, string RunId, string Name);

/// <summary>Asks the session of a run to resume it, or to run it again over its recorded results.</summary>
/// <param name="From">The step from which it runs again, such as <c>merge#1</c>; null to resume, or with <paramref name="All"/>.</param>
/// <param name="All">Whether every recorded step gives its result again (a rerun with recorded results).</param>
internal sealed record WorkflowResumeRequest(string? ExpectedEpoch, string SessionId, string RunId, string? From, bool All);

/// <summary>Stops a run that runs.</summary>
internal sealed record WorkflowStopRequest(string? ExpectedEpoch, string RunId);

/// <summary>Asks for the source of a script of the library.</summary>
/// <param name="Scope"><c>project</c>, <c>global</c> or <c>builtin</c>: the one of the scripts of that name.</param>
/// <param name="ProjectId">The project whose scripts are read; null for yours and the samples.</param>
internal sealed record WorkflowSourceRequest(string? ExpectedEpoch, string Name, string Scope, string? ProjectId);

/// <summary>The source of a script, or null when it is not found or too long to show.</summary>
internal sealed record WorkflowSourceResponse(string Status, string? Source);

/// <param name="Message">Why the request was refused.</param>
internal sealed record WorkflowMutationResponse(string Status, string? Message);

/// <summary>Deletes a script of the library.</summary>
/// <param name="Scope"><c>project</c> or <c>global</c>: a sample is never deleted.</param>
/// <param name="ProjectId">The project of a script of a project; null for one of yours.</param>
internal sealed record WorkflowDeleteRequest(string? ExpectedEpoch, string Name, string Scope, string? ProjectId);
