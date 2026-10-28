using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Orchestration.Runtime.Plugins;

public sealed partial class PluginOrchestrationBridge
{
    /// <summary>The kind of the payload the reducers of a manual compaction are given: the text of its plan.</summary>
    public const string ManualCompactionPayloadKind = "manual-compaction-plan";

    /// <summary>
    /// Gets whether a plugin that applies to a session hooks its compactions (<see cref="PluginBase.GetCompactionContributions"/>).
    /// </summary>
    /// <param name="session">The session.</param>
    /// <param name="spaceId">The space of the session; <see langword="null"/> applies no space.</param>
    /// <returns><see langword="true"/> when the hooks of a manual compaction call at least one plugin.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="session"/> is <see langword="null"/>.</exception>
    public bool HasCompactionHooks(SessionViewDescriptor session, string? spaceId)
    {
        ArgumentNullException.ThrowIfNull(session);
        return _getActivePlugins().Count > 0
            && _adapter.GetContributions<PluginCompactionContribution>(PluginPoint.Compaction, SessionScope(session, spaceId)).Count > 0;
    }

    /// <summary>
    /// Runs the hooks of plugins before a manual compaction of a session: the before-hooks, which can cancel it, then,
    /// unless one did, the instruction providers and the reducers.
    /// </summary>
    /// <param name="session">The session that is compacted.</param>
    /// <param name="spaceId">The space of the session; <see langword="null"/> applies no space.</param>
    /// <param name="compactionId">The identity of the compaction, given to every hook of it.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The reason the first plugin that cancelled gave, or the instructions the plugins add to the summary.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="session"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken"/> is cancelled.</exception>
    public async Task<PluginCompactionPreparation> BeforeManualCompactionAsync(
        SessionViewDescriptor session,
        string? spaceId,
        string? compactionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        var activePlugins = _getActivePlugins();
        if (activePlugins.Count == 0)
        {
            return PluginCompactionPreparation.Empty;
        }

        var options = SessionScope(session, spaceId);
        var seed = activePlugins[0];
        var planSummary = $"Manual compaction requested for '{session.Title}'.";
        var before = await _adapter.RunCompactionAsync(
            before: new PluginBeforeCompactionContext
            {
                Plugin = seed.Descriptor,
                Services = seed.RuntimeContext.Services,
                PlanSummary = planSummary,
                Scope = seed.RuntimeContext.Scope,
                ScopeProjectId = seed.RuntimeContext.ScopeProjectId,
                ScopeProjectPath = seed.RuntimeContext.ScopeProjectPath,
                ProjectId = options.ProjectId,
                ProjectPath = options.ProjectPath,
                SessionId = options.SessionId,
                SpaceId = options.SpaceId,
                ProviderId = options.ProviderId,
                Model = options.Model,
                CancellationToken = cancellationToken,
                CompactionId = compactionId,
                Metadata = Metadata(session),
            },
            options: options,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (before.BeforeResults.FirstOrDefault(static result => result.Cancel) is { } cancel)
        {
            return new PluginCompactionPreparation
            {
                CancelReason = string.IsNullOrWhiteSpace(cancel.Reason) ? "A plugin cancelled the compaction." : cancel.Reason.Trim(),
            };
        }

        var contributed = await _adapter.RunCompactionAsync(
            instructions: new PluginCompactionInstructionContext
            {
                Plugin = seed.Descriptor,
                Services = seed.RuntimeContext.Services,
                PreferredMaximumCharacters = 4_000,
                Scope = seed.RuntimeContext.Scope,
                ScopeProjectId = seed.RuntimeContext.ScopeProjectId,
                ScopeProjectPath = seed.RuntimeContext.ScopeProjectPath,
                ProjectId = options.ProjectId,
                ProjectPath = options.ProjectPath,
                SessionId = options.SessionId,
                SpaceId = options.SpaceId,
                ProviderId = options.ProviderId,
                Model = options.Model,
                CancellationToken = cancellationToken,
                CompactionId = compactionId,
                Metadata = Metadata(session),
            },
            reducer: new PluginCompactionReducerContext
            {
                Plugin = seed.Descriptor,
                Services = seed.RuntimeContext.Services,
                PayloadKind = ManualCompactionPayloadKind,
                Payload = planSummary,
                Scope = seed.RuntimeContext.Scope,
                ScopeProjectId = seed.RuntimeContext.ScopeProjectId,
                ScopeProjectPath = seed.RuntimeContext.ScopeProjectPath,
                ProjectId = options.ProjectId,
                ProjectPath = options.ProjectPath,
                SessionId = options.SessionId,
                SpaceId = options.SpaceId,
                ProviderId = options.ProviderId,
                Model = options.Model,
                CancellationToken = cancellationToken,
                CompactionId = compactionId,
                Metadata = Metadata(session),
            },
            options: options,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var instructions = string.Join(
            "\n\n",
            contributed.InstructionResults
                .Select(static result => result.Instructions?.Trim())
                .Where(static text => !string.IsNullOrEmpty(text)));
        return new PluginCompactionPreparation { Instructions = instructions.Length == 0 ? null : instructions };
    }

    /// <summary>
    /// Runs the after-hooks of plugins once a manual compaction of a session settled.
    /// </summary>
    /// <param name="session">The session that was compacted.</param>
    /// <param name="spaceId">The space of the session; <see langword="null"/> applies no space.</param>
    /// <param name="compactionId">The identity of the compaction its before-hooks were given.</param>
    /// <param name="succeeded">Whether the compaction succeeded.</param>
    /// <param name="summary">The summary it wrote when it succeeded, or the message of its failure; <see langword="null"/> when there is none.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes once every after-hook ran.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="session"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken"/> is cancelled.</exception>
    public async Task AfterManualCompactionAsync(
        SessionViewDescriptor session,
        string? spaceId,
        string? compactionId,
        bool succeeded,
        string? summary,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        var activePlugins = _getActivePlugins();
        if (activePlugins.Count == 0)
        {
            return;
        }

        var options = SessionScope(session, spaceId);
        var seed = activePlugins[0];
        await _adapter.RunCompactionAsync(
            after: new PluginAfterCompactionContext
            {
                Plugin = seed.Descriptor,
                Services = seed.RuntimeContext.Services,
                Succeeded = succeeded,
                Summary = summary,
                Scope = seed.RuntimeContext.Scope,
                ScopeProjectId = seed.RuntimeContext.ScopeProjectId,
                ScopeProjectPath = seed.RuntimeContext.ScopeProjectPath,
                ProjectId = options.ProjectId,
                ProjectPath = options.ProjectPath,
                SessionId = options.SessionId,
                SpaceId = options.SpaceId,
                ProviderId = options.ProviderId,
                Model = options.Model,
                CancellationToken = cancellationToken,
                CompactionId = compactionId,
                Metadata = Metadata(session),
            },
            options: options,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static Dictionary<string, string> Metadata(SessionViewDescriptor session)
        => new(StringComparer.Ordinal)
        {
            ["SessionTitle"] = session.Title,
            ["ProviderId"] = session.ResolvedProviderKey,
        };
}

/// <summary>
/// What the hooks of plugins decided before a manual compaction.
/// </summary>
public sealed record PluginCompactionPreparation
{
    /// <summary>Gets the preparation of a compaction no plugin cancelled or gave instructions to.</summary>
    public static PluginCompactionPreparation Empty { get; } = new();

    /// <summary>Gets the reason a plugin gave to cancel the compaction; <see langword="null"/> when none did.</summary>
    public string? CancelReason { get; init; }

    /// <summary>Gets the instructions the plugins add to the summary of the compaction; <see langword="null"/> for none.</summary>
    public string? Instructions { get; init; }
}
