using System.Text.Json;
using CodeAlta.Catalog;
using CodeAlta.Desktop.WorkItems;
using CodeAlta.Desktop.Workflows;
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
    internal WorkflowsService(CodeModeScriptLibrary scripts, CodeModeRunRegistry runs, ProjectCatalog projects, ISessionStarter starter,
        Func<string, string, CancellationToken, Task<string?>> send, string epoch)
    {
        ArgumentNullException.ThrowIfNull(scripts);
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(starter);
        ArgumentNullException.ThrowIfNull(send);
        ArgumentException.ThrowIfNullOrWhiteSpace(epoch);
        (_scripts, _runs, _projects, _starter, _send, _epoch) = (scripts, runs, projects, starter, send, epoch);
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
        var scripts = await Task.Run(() => _scripts!.List(project?.ProjectPath), cancellationToken).ConfigureAwait(false);
        return new("ok", [.. scripts.Select(static script => new WorkflowScriptItem(script.Name, Scope(script.Scope), script.Title, script.Description,
            script.Problem, script.Path, Parameters(script.ParametersSchemaJson),
            [.. script.Steps.Select(static step => new WorkflowStepItem(step.Id, step.Title, step.After, step.Each))]))]);
    }

    /// <summary>The runs of scripts of the host: running ones, and those that ended in the last minutes.</summary>
    [NeoRpcMethod("runs")]
    public WorkflowRunsResponse Runs(WorkflowsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, []);
        return new("ok", [.. _runs!.List(null)
            .OrderByDescending(static run => run.Running).ThenByDescending(static run => run.StartedAt)
            .Select(static run => new WorkflowRunItem(run.Id, run.SessionId, run.Title, run.Running ? "running" : run.Status ?? "completed",
                run.StartedAt, run.EndedAt, run.CallId, run.Calls))]);
    }

    /// <summary>Starts a script of the library in a new session of a project. The answer comes once the session exists, or says why it did not start.</summary>
    [NeoRpcMethod("run")]
    public async Task<WorkflowRunResponse> RunAsync(WorkflowRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, null, null);
        if (!CodeModeScriptLibrary.IsName(request.Name) || !Identifier(request.ProjectId) || request.Arguments is { Length: > MaximumArgumentsLength })
            return new("invalid_request", null, null);
        var project = await _projects!.GetByIdAsync(request.ProjectId, cancellationToken).ConfigureAwait(false);
        if (project is null || project.Archived) return new("refused", null, "The project is no longer one of the projects of CodeAlta.");
        var (prompt, problem) = await Task.Run(() => (ScriptRunPrompt.Build(_scripts!, request.Name, request.Arguments, null, project.ProjectPath, out var problem), problem),
            cancellationToken).ConfigureAwait(false);
        if (prompt is null) return new("refused", null, problem);
        var title = _scripts!.Find(request.Name, project.ProjectPath)?.Title is { Length: > 0 } named ? named : request.Name;
        var started = await _starter!.StartAsync(project, title, _ => prompt, false, null, "workflow", new SessionStartModel { CodeMode = true }).ConfigureAwait(false);
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
        var failure = await _send!(request.SessionId, ScriptRunPrompt.SaveAsScript(request.RunId, request.Name), cancellationToken).ConfigureAwait(false);
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

    private string? Refuse(string? expectedEpoch)
        => _scripts is null ? "unavailable" : string.Equals(expectedEpoch, _epoch, StringComparison.Ordinal) ? null : "stale_epoch";

    private static bool Identifier(string? value) => value is { Length: > 0 and <= 256 } && !value.Any(char.IsControl);

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

/// <summary>Starts a script in a new session of a project.</summary>
/// <param name="Arguments">Its arguments, a JSON object; null for none.</param>
internal sealed record WorkflowRunRequest(string? ExpectedEpoch, string Name, string ProjectId, string? Arguments);

/// <param name="SessionId">The session that was created, also when the prompt was not sent.</param>
/// <param name="Message">Why the run did not start.</param>
internal sealed record WorkflowRunResponse(string Status, string? SessionId, string? Message);

/// <summary>Asks the session of a run to save it as a script of the library.</summary>
internal sealed record WorkflowSaveAsScriptRequest(string? ExpectedEpoch, string SessionId, string RunId, string Name);

/// <summary>Asks the session of a run to resume it, or to run it again over its recorded results.</summary>
/// <param name="From">The step from which it runs again, such as <c>merge#1</c>; null to resume, or with <paramref name="All"/>.</param>
/// <param name="All">Whether every recorded step gives its result again (a rerun with recorded results).</param>
internal sealed record WorkflowResumeRequest(string? ExpectedEpoch, string SessionId, string RunId, string? From, bool All);

/// <param name="Message">Why the request was refused.</param>
internal sealed record WorkflowMutationResponse(string Status, string? Message);
