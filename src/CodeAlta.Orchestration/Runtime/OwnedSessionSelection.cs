using CodeAlta.Agent;
using CodeAlta.Orchestration.Runtime.SystemPrompts;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>Bounded prompt metadata, without filesystem paths or prompt bodies.</summary>
/// <param name="Id">Effective prompt identifier.</param>
/// <param name="Name">Display name.</param>
public sealed record OwnedPromptChoice(string Id, string Name);

/// <summary>Selectable model metadata.</summary>
/// <param name="Id">Provider model identifier.</param>
/// <param name="Name">Display name.</param>
/// <param name="Efforts">Supported reasoning efforts.</param>
public sealed record OwnedModelChoice(string Id, string Name, IReadOnlyList<AgentReasoningEffort> Efforts);

/// <summary>Session-scoped next-send choices; does not confer authority to mutate a running turn.</summary>
/// <param name="Current">Persisted session selection.</param>
/// <param name="Prompts">Effective prompts in the host-resolved project scope.</param>
/// <param name="Models">Models exposed by the session provider.</param>
public sealed record OwnedSelectionChoices(OwnedSessionSelection Current, IReadOnlyList<OwnedPromptChoice> Prompts, IReadOnlyList<OwnedModelChoice> Models);

/// <summary>Host-resolved project scope for an existing owned session; a null path denotes global scope.</summary>
/// <param name="ProjectDirectory">Catalog project path, or null for an unscoped session.</param>
/// <param name="ProjectId">Catalog project identity, or null for an unscoped session.</param>
public sealed record OwnedMcpScope(string? ProjectDirectory, string? ProjectId);

public sealed partial class OwnedSessionCommandService
{
    /// <summary>Resolves an owned session's exact catalog project; null result means unavailable, not global.</summary>
    /// <exception cref="ArgumentException">The session identity is blank.</exception>
    /// <exception cref="OperationCanceledException">The caller cancels the read.</exception>
    public async Task<OwnedMcpScope?> GetMcpScopeAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        lock (_gate) { if (_closed || _retained) return null; }
        var session = await _runtime.ResolveOwnedSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (session is null) return null;
        if (string.IsNullOrWhiteSpace(session.ProjectRef)) return new OwnedMcpScope(null, null);
        var project = await _projects.GetByIdAsync(session.ProjectRef, cancellationToken).ConfigureAwait(false);
        return project is null ? null : new OwnedMcpScope(project.ProjectPath, project.Id);
    }

    /// <summary>Reads effective prompts only in the resolved owned session's host-authorized project scope.</summary>
    /// <exception cref="ArgumentException">The session identity is blank.</exception>
    /// <exception cref="OperationCanceledException">The caller cancels its read.</exception>
    public async Task<IReadOnlyList<AgentPromptDescriptor>?> GetPromptCatalogAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        lock (_gate) { if (_closed || _retained) return null; }
        var session = await _runtime.ResolveOwnedSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (session is null) return null;
        var project = string.IsNullOrWhiteSpace(session.ProjectRef) ? null
            : await _projects.GetByIdAsync(session.ProjectRef, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(session.ProjectRef) && project is null) return null;
        return _runtime.ListOwnedPrompts(project?.ProjectPath);
    }

    /// <summary>Reads bounded choices for an existing session, probing its configured provider if needed.</summary>
    /// <exception cref="ArgumentException">The session identity is blank.</exception>
    /// <exception cref="OperationCanceledException">The caller cancels its read.</exception>
    public async Task<OwnedSelectionChoices?> GetSelectionChoicesAsync(string sessionId, CancellationToken cancellationToken = default)
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
        var models = SelectionModels is null ? []
            : await SelectionModels(new ModelProviderId(provider), cancellationToken).ConfigureAwait(false);
        return new(new(provider, session.AgentPromptId ?? "default", session.ModelId, session.ReasoningEffort), prompts,
            models.Where(m => m.Id.Length <= 256).Take(128)
                .Select(m => new OwnedModelChoice(m.Id, Bound(m.DisplayName ?? m.Id), m.SupportedReasoningEfforts?.ToArray() ?? [])).ToArray());
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
