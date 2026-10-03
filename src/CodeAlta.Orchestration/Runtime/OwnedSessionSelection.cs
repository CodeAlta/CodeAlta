using CodeAlta.Agent;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>Bounded prompt metadata, without filesystem paths or prompt bodies.</summary>
/// <param name="Id">Effective prompt identifier.</param>
/// <param name="Name">Display name.</param>
public sealed record OwnedPromptChoice(string Id, string Name);

/// <summary>Selectable model metadata.</summary>
/// <param name="Id">Provider model identifier.</param>
/// <param name="Name">Display name.</param>
/// <param name="Efforts">Supported reasoning efforts.</param>
public sealed record OwnedModelChoice(string Id, string Name, IReadOnlyList<AgentReasoningEffort> Efforts)
{
    /// <summary>Gets observed image capability: true available, false unsupported, null unknown.</summary>
    public bool? ImageInput { get; init; }
}

/// <summary>Session-scoped next-send choices; does not confer authority to mutate a running turn.</summary>
/// <param name="Current">Persisted session selection.</param>
/// <param name="Prompts">Effective prompts in the host-resolved project scope.</param>
/// <param name="Models">Models exposed by the session provider.</param>
public sealed record OwnedSelectionChoices(OwnedSessionSelection Current, IReadOnlyList<OwnedPromptChoice> Prompts, IReadOnlyList<OwnedModelChoice> Models);

public sealed partial class OwnedSessionCommandService
{
    /// <summary>Reads effective prompt choices for a draft's exact catalog scope without creating a session or provider.</summary>
    /// <param name="scope">Exact project identity/path, or null for a global draft.</param>
    /// <param name="cancellationToken">Cancels the catalog read.</param>
    /// <returns>Bounded choices, or null when the scope or host is unavailable.</returns>
    /// <exception cref="OperationCanceledException">The caller cancels the read.</exception>
    public async Task<IReadOnlyList<OwnedPromptChoice>?> GetDraftPromptChoicesAsync(OwnedProjectReferenceScope? scope,
        CancellationToken cancellationToken = default)
    {
        lock (_gate) { if (_closed || _retained) return null; }
        var project = scope is null ? null : await ResolveReferenceProjectAsync(scope, cancellationToken).ConfigureAwait(false);
        if (scope is not null && !ReferenceScopeMatches(scope, project)) return null;
        lock (_gate) { if (_closed || _retained) return null; }
        cancellationToken.ThrowIfCancellationRequested();
        return _runtime.ListOwnedPrompts(project?.ProjectPath).Where(p => p.PromptName.Length <= 256).Take(64)
            .Select(p => new OwnedPromptChoice(p.PromptName, Bound(p.DisplayName))).ToArray();
    }

    /// <summary>Reads bounded choices for an existing session, probing its configured provider if needed.</summary>
    /// <exception cref="ArgumentException">The session identity is blank.</exception>
    /// <exception cref="OperationCanceledException">The caller cancels its read.</exception>
    public Task<OwnedSelectionChoices?> GetSelectionChoicesAsync(string sessionId, CancellationToken cancellationToken = default)
        => GetSelectionChoicesCoreAsync(sessionId, cancellationToken, observedOnly: false);

    /// <summary>Reads next-send choices from current provider observations without activation or probing.</summary>
    /// <exception cref="ArgumentException">The session identity is blank.</exception>
    /// <exception cref="OperationCanceledException">The read is canceled.</exception>
    public Task<OwnedSelectionChoices?> GetObservedSelectionChoicesAsync(string sessionId, CancellationToken cancellationToken = default)
        => GetSelectionChoicesCoreAsync(sessionId, cancellationToken, observedOnly: true);

    private async Task<OwnedSelectionChoices?> GetSelectionChoicesCoreAsync(string sessionId, CancellationToken cancellationToken, bool observedOnly)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        lock (_gate) { if (_closed || _retained) return null; }
        var session = await _runtime.ResolveOwnedSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (session is null) return null;
        var project = string.IsNullOrWhiteSpace(session.ProjectRef) ? null
            : await _projects.GetByIdAsync(session.ProjectRef, cancellationToken).ConfigureAwait(false);
        var prompts = _runtime.ListOwnedPrompts(project?.ProjectPath)
            .Where(p => p.PromptName.Length <= 256).Take(64)
            .Select(p => new OwnedPromptChoice(p.PromptName, Bound(p.DisplayName))).ToArray();
        var provider = session.ResolvedProviderKey;
        var models = observedOnly ? ObservedImageModels?.Invoke(new ModelProviderId(provider)) ?? [] : SelectionModels is null ? []
            : await SelectionModels(new ModelProviderId(provider), cancellationToken).ConfigureAwait(false);
        return new(new(provider, session.AgentPromptId ?? "default", session.ModelId, session.ReasoningEffort), prompts,
            models.Where(m => m.Id.Length <= 256).Take(128)
                .Select(m => new OwnedModelChoice(m.Id, Bound(m.DisplayName ?? m.Id), m.SupportedReasoningEfforts?.ToArray() ?? [])
                { ImageInput = AgentImageInputCapability.Read(m) }).ToArray());
    }

    internal static bool IsValidSelection(OwnedSelectionChoices choices, OwnedSessionSelection selection)
    {
        if (!string.Equals(choices.Current.ProviderKey, selection.ProviderKey, StringComparison.Ordinal)
            || !choices.Prompts.Any(p => p.Id == selection.AgentPromptId)) return false;
        if (selection.ModelId is null) return selection.ReasoningEffort is null;
        var model = choices.Models.FirstOrDefault(m => m.Id == selection.ModelId);
        return model is not null && (selection.ReasoningEffort is null || model.Efforts.Contains(selection.ReasoningEffort.Value));
    }

    private static string Bound(string value) => value.Length <= 256 ? value : value[..256];
}
