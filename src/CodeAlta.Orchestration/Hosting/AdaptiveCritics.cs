using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Orchestration.Hosting;

/// <summary>
/// The reviews of Adaptive, through a headless driver of the host: each critic of a level reviews the change of a turn in
/// a child session of its own, on its model, without the tools that write files, without sessions of its own, in the
/// folder of the reviewed session. Its answer goes to Adaptive, never to the reviewed session as a prompt; the session
/// stays in the tree of the reviewed one, where the user can open it.
/// </summary>
internal sealed class AdaptiveCritics : IAsyncDisposable
{
    // The tools a review does without: it reads and runs tests, and writes nothing.
    internal static readonly IReadOnlyList<string> ReadOnlyTools =
        ["write_file", "replace_in_file", "apply_patch", "delete_file_or_dir", "rename_file_or_dir", "web", "alta"];

    private readonly CodeAltaHost _host;
    private readonly HeadlessSessionDriver _driver;

    /// <summary>Creates the reviews of a host.</summary>
    /// <param name="host">The host, borrowed.</param>
    public AdaptiveCritics(CodeAltaHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
        _driver = new HeadlessSessionDriver(host);
    }

    /// <summary>Runs a review: opens the session of the critic, asks it, and closes it.</summary>
    /// <param name="request">The review.</param>
    /// <param name="cancellationToken">Stops the review.</param>
    /// <returns>The answer of the critic, or why it gave none.</returns>
    public async Task<AdaptiveCriticAnswer> ReviewAsync(AdaptiveCriticRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var runtime = _host.RuntimeService;
        var reviewed = await runtime.TryGetResumableSessionAsync(request.SessionId, cancellationToken).ConfigureAwait(false);
        if (reviewed is null) return new(null, "the reviewed session is not known");
        var project = reviewed.ProjectRef is { } projectId ? await _host.ProjectCatalog.GetByIdAsync(projectId, cancellationToken).ConfigureAwait(false) : null;
        var opened = await _driver.OpenAsync(new HeadlessSessionRequest
        {
            ProjectFolder = project?.ProjectPath ?? reviewed.WorktreeDirectory ?? reviewed.WorkingDirectory,
            WorktreeDirectory = project is null ? null : reviewed.WorktreeDirectory,
            ModelRef = request.Critic.Format(),
            Title = $"Adaptive review · {request.Critic.ModelId}",
            SessionOptions = new AgentSessionOptions { SubSessions = false },
            DisabledToolPatterns = [.. runtime.GetDisabledToolPatterns(request.SessionId).Union(ReadOnlyTools, StringComparer.OrdinalIgnoreCase)],
            ParentSessionId = request.SessionId,
            CreatedBy = new AltaActorProvenance
            {
                // As the sessions of a script: the answer comes back here, never to the reviewed session. The host
                // creates it, not the agent of the reviewed session: a session without sub-sessions is reviewed too, and
                // what it narrows (its tools) is given above.
                Kind = AltaActorProvenance.WorkflowKind,
                SourceProjectId = reviewed.ProjectRef,
                CorrelationId = "adaptive-review:" + request.SessionId,
                PromptProvenance = AgentPromptProvenance.AdaptiveReview,
                CreatedAt = DateTimeOffset.UtcNow,
            },
        }, cancellationToken).ConfigureAwait(false);
        if (opened.Session is not { } session) return new(null, opened.Message ?? opened.Status.ToString());
        try
        {
            var run = await _driver.RunAsync(session, new HeadlessPrompt(request.Prompt) { Provenance = AgentPromptProvenance.AdaptiveReview },
                request.Timeout, cancellationToken).ConfigureAwait(false);
            return run.Status == HeadlessRunStatus.Completed
                ? new(run.FinalMessage, null)
                : new(run.FinalMessage, run.FailureMessage ?? run.Status.ToString().ToLowerInvariant());
        }
        finally
        {
            await _driver.CloseAsync(session).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _driver.DisposeAsync();
}
