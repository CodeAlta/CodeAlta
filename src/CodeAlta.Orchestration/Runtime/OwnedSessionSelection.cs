using System.Collections.Frozen;
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

    /// <summary>Gets the effort a session starts this model with, one of <see cref="Efforts"/>; null when the model reports none.</summary>
    public AgentReasoningEffort? StartEffort { get; init; }
}

/// <summary>Session-scoped next-send choices; does not confer authority to mutate a running turn.</summary>
/// <param name="Current">Persisted session selection. Its <see cref="OwnedSessionSelection.PermissionMode"/> is the mode chosen for the session, or null when it runs in the one of its provider.</param>
/// <param name="Prompts">Effective prompts in the host-resolved project scope.</param>
/// <param name="Models">Models exposed by the session provider.</param>
public sealed record OwnedSelectionChoices(OwnedSessionSelection Current, IReadOnlyList<OwnedPromptChoice> Prompts, IReadOnlyList<OwnedModelChoice> Models)
{
    /// <summary>Gets the permission modes a session of the provider can be given, empty when the provider has none.</summary>
    public IReadOnlyList<string> PermissionModes { get; init; } = [];

    /// <summary>Gets the permission mode the provider is configured with, or null when it leaves the mode to the provider itself.</summary>
    public string? DefaultPermissionMode { get; init; }

    /// <summary>Gets whether the session can be followed and driven from elsewhere (Claude Code's Remote Control).</summary>
    public bool SupportsRemoteControl { get; init; }
}

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
        var models = (observedOnly ? ObservedImageModels?.Invoke(new ModelProviderId(provider)) ?? [] : SelectionModels is null ? []
            : await SelectionModels(new ModelProviderId(provider), cancellationToken).ConfigureAwait(false))
            .Where(m => m.Id.Length <= 256).Take(128).ToArray();
        // What the session runs with, never "a default": a session without a model shows the one it starts with.
        var (modelId, effort) = StartingModel(provider, session.ModelId, session.ReasoningEffort, models);
        var configured = SelectionProvider?.Invoke(new ModelProviderId(provider));
        // The modes of the provider when it has some, as Claude Code does. Any other provider runs the tools of
        // CodeAlta, whose requests the host answers: where the mode of a session decides that, it offers the modes of the host.
        var modes = configured?.PermissionModes.Where(static mode => !ProviderOnlyPermissionModes.Contains(mode)).Take(16).ToArray() ?? [];
        if (modes.Length == 0 && _runtime.SessionPermissionModes) modes = [.. SessionPermissionModes.HostModes];
        return new(new(provider, session.AgentPromptId ?? "default", modelId, effort) { PermissionMode = session.PermissionMode }, prompts,
            models.Select(m => new OwnedModelChoice(m.Id, Bound(m.DisplayName ?? m.Id), m.SupportedReasoningEfforts?.ToArray() ?? [])
                { ImageInput = AgentImageInputCapability.Read(m), StartEffort = StartingModel(provider, m.Id, null, models).Effort }).ToArray())
        {
            PermissionModes = modes,
            // What a session without a mode runs in: the mode of its provider, else the one that names the policy of the host.
            DefaultPermissionMode = _runtime.SessionPermissionModes ? _runtime.GetDefaultPermissionMode(provider) : configured?.DefaultPermissionMode,
            SupportsRemoteControl = configured?.SupportsRemoteControl ?? false,
        };
    }

    // Modes a session is not given by itself. The plan mode of Claude Code ends with an approval CodeAlta does not
    // ask for: it stays a mode of the provider's configuration.
    private static readonly FrozenSet<string> ProviderOnlyPermissionModes = FrozenSet.ToFrozenSet(["plan"], StringComparer.Ordinal);

    internal static bool IsValidSelection(OwnedSelectionChoices choices, OwnedSessionSelection selection)
    {
        if (!string.Equals(choices.Current.ProviderKey, selection.ProviderKey, StringComparison.Ordinal)
            || !choices.Prompts.Any(p => p.Id == selection.AgentPromptId)) return false;
        if (selection.PermissionMode is { } mode && mode != OwnedSessionSelection.ProviderPermissionMode
            && !choices.PermissionModes.Contains(mode, StringComparer.Ordinal)) return false;
        // No model keeps the session's own, which the send completes with a model of the provider.
        if (selection.ModelId is null) return selection.ReasoningEffort is null;
        var model = choices.Models.FirstOrDefault(m => m.Id == selection.ModelId);
        return model is not null && (selection.ReasoningEffort is null || model.Efforts.Contains(selection.ReasoningEffort.Value));
    }

    // Completes what a session runs with among what its provider reports, as the terminal does. A session without a
    // model takes the provider's configured model when it is listed, else the first one listed. A model that reports
    // its efforts takes the session's effort when it supports it, else the provider's configured effort, High, its own
    // default or its first effort. A model the provider does not list is kept as it is saved.
    private (string? ModelId, AgentReasoningEffort? Effort) StartingModel(string provider, string? modelId,
        AgentReasoningEffort? effort, IReadOnlyList<AgentModelInfo> models)
    {
        var configured = SelectionProvider?.Invoke(new ModelProviderId(provider));
        var resolved = string.IsNullOrWhiteSpace(modelId) ? AgentModelDefaults.ResolveModelId(models, configured?.DefaultModelId) : modelId;
        var model = models.FirstOrDefault(m => string.Equals(m.Id, resolved, StringComparison.Ordinal));
        if (model?.SupportedReasoningEfforts is not { Count: > 0 } supported) return (resolved, effort);
        return (resolved, AgentModelDefaults.ResolveReasoningEffort(model,
            effort is { } saved && supported.Contains(saved) ? saved : configured?.DefaultReasoningEffort));
    }

    // Before a send. Only a missing model is worth waiting for the provider's catalog, since a send without one fails
    // at the provider; a missing effort is completed from what the provider already reported.
    private async Task<(string? ModelId, AgentReasoningEffort? Effort)> CompleteModelAsync(string provider, string? modelId,
        AgentReasoningEffort? effort, bool observedOnly)
    {
        var missing = string.IsNullOrWhiteSpace(modelId);
        if (!missing && effort is not null) return (modelId, effort);
        var id = new ModelProviderId(provider);
        var models = missing && !observedOnly && SelectionModels is not null
            ? await SelectionModels(id, CancellationToken.None).ConfigureAwait(false)
            : ObservedImageModels?.Invoke(id) ?? [];
        return StartingModel(provider, modelId, effort, models);
    }

    private static string Bound(string value) => value.Length <= 256 ? value : value[..256];
}
