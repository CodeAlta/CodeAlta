using System.Collections.Frozen;
using CodeAlta.Agent;
using CodeAlta.Orchestration.Runtime.SystemPrompts;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>Bounded prompt metadata, without filesystem paths or prompt bodies.</summary>
/// <param name="Id">Effective prompt identifier.</param>
/// <param name="Name">Display name.</param>
public sealed record OwnedPromptChoice(string Id, string Name);

/// <summary>The prompts a new session can start with in its space.</summary>
/// <param name="Prompts">The prompts available in the space.</param>
/// <param name="DefaultPromptId">The one a new session starts with when none is chosen: the agent prompt of its space, else <c>default</c>.</param>
/// <param name="SpaceId">The space the session is in.</param>
public sealed record OwnedDraftPrompts(IReadOnlyList<OwnedPromptChoice> Prompts, string DefaultPromptId, string SpaceId);

/// <summary>Selectable model metadata.</summary>
/// <param name="Id">Provider model identifier.</param>
/// <param name="Name">Display name.</param>
/// <param name="Efforts">
/// Supported reasoning efforts. Empty when the model reports none: a selection of it then takes any effort, or none,
/// since its provider did not say which ones it has.
/// </param>
public sealed record OwnedModelChoice(string Id, string Name, IReadOnlyList<AgentReasoningEffort> Efforts)
{
    /// <summary>Gets observed image capability: true available, false unsupported, null unknown.</summary>
    public bool? ImageInput { get; init; }

    /// <summary>Gets the effort a session starts this model with, one of <see cref="Efforts"/>; null when the model reports none.</summary>
    public AgentReasoningEffort? StartEffort { get; init; }

    /// <summary>
    /// Gets whether <see cref="Efforts"/> is all the model has: its provider sends no other effort, so a model that lists
    /// none runs without one, whatever the session asked.
    /// </summary>
    public bool EffortsComplete { get; init; }

    /// <summary>
    /// Gets the other choices of the model, such as its context or a fast mode, with their values now: set for the model
    /// in every session of its provider (<see cref="OwnedSessionCommandService.SetModelOptionAsync"/>). Empty when the
    /// model or its provider has none.
    /// </summary>
    public IReadOnlyList<AgentModelOption> Options { get; init; } = [];

    /// <summary>Gets the sizes of context a session can use the model with (<see cref="AgentModelInfo.ContextSizes"/>); empty when it has one.</summary>
    public IReadOnlyList<long> ContextSizes { get; init; } = [];

    /// <summary>
    /// Gets the size of context of a session of the model that chose none: the one of its provider when the model has it,
    /// else its own; null for a model that has one size.
    /// </summary>
    public long? ContextSize { get; init; }
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

    /// <summary>
    /// Gets the tier of models the session runs on (<c>complex</c>, <c>standard</c> or <c>quick</c>), or null for a
    /// model chosen by hand.
    /// </summary>
    public string? Tier { get; init; }

    /// <summary>Gets whether CodeAlta chooses the model of each turn of the session (Adaptive); <see cref="Tier"/> is then the tier of its turns.</summary>
    public bool Adaptive { get; init; }
}

/// <summary>The outcome of completing a selection named by its parts (<see cref="OwnedSessionCommandService.CheckSelectionAsync"/>).</summary>
/// <param name="Status">
/// <see cref="Ok"/>, <see cref="Unavailable"/> (an unknown session, or a host that is closing), <see cref="InvalidModel"/>,
/// <see cref="InvalidEffort"/>, <see cref="InvalidPrompt"/> or <see cref="InvalidMode"/>: a part the session cannot run with.
/// </param>
/// <param name="Selection">The completed selection when <paramref name="Status"/> is <see cref="Ok"/>.</param>
/// <param name="Choices">The choices of the session it was checked against; null when they are unavailable.</param>
public sealed record OwnedSelectionCheck(string Status, OwnedSessionSelection? Selection, OwnedSelectionChoices? Choices)
{
    /// <summary>The selection can be sent.</summary>
    public const string Ok = "ok";

    /// <summary>The session is unknown, or the host is closing.</summary>
    public const string Unavailable = "unavailable";

    /// <summary>The provider of the session does not offer the model, or the session runs with a model it does not offer.</summary>
    public const string InvalidModel = "invalid_model";

    /// <summary>The model does not have the effort.</summary>
    public const string InvalidEffort = "invalid_effort";

    /// <summary>The session cannot run the agent prompt.</summary>
    public const string InvalidPrompt = "invalid_prompt";

    /// <summary>The provider of the session does not offer the permission mode, or offers none.</summary>
    public const string InvalidMode = "invalid_mode";
}

public sealed partial class OwnedSessionCommandService
{
    /// <summary>
    /// How many models of a provider the choices of a session list: every model a real provider reports (Azure
    /// OpenAI lists several hundred), since a send validates its model against them.
    /// </summary>
    internal const int MaximumModelChoices = 2048;

    /// <summary>
    /// How many agent prompts the choices of a session and of a new session list: every prompt of a space, including
    /// the ones a pack of prompts installs, since a send and <c>alta session set_agent</c> validate the prompt against them.
    /// </summary>
    internal const int MaximumPromptChoices = 1024;

    /// <summary>Reads effective prompt choices for a draft's exact catalog scope without creating a session or provider.</summary>
    /// <param name="scope">Exact project identity/path, or null for a global draft.</param>
    /// <param name="cancellationToken">Cancels the catalog read.</param>
    /// <returns>Bounded choices, or null when the scope or host is unavailable.</returns>
    /// <exception cref="OperationCanceledException">The caller cancels the read.</exception>
    public async Task<IReadOnlyList<OwnedPromptChoice>?> GetDraftPromptChoicesAsync(OwnedProjectReferenceScope? scope,
        CancellationToken cancellationToken = default)
        => (await GetDraftPromptsAsync(scope, spaceId: null, cancellationToken).ConfigureAwait(false))?.Prompts;

    /// <summary>
    /// Reads the prompts a new session can start with, in the space it is created in, and the one it starts with when
    /// none is chosen: the agent prompt the space starts its new sessions with, else <c>default</c>.
    /// </summary>
    /// <param name="scope">Exact project identity/path, or null for a global draft.</param>
    /// <param name="spaceId">
    /// The space the session is created in; null for none, where the session is in the only space of its project,
    /// else in the default space.
    /// </param>
    /// <param name="cancellationToken">Cancels the catalog read.</param>
    /// <returns>Bounded choices and the default one, or null when the scope or host is unavailable.</returns>
    /// <exception cref="OperationCanceledException">The caller cancels the read.</exception>
    /// <exception cref="InvalidDataException">The global configuration cannot be parsed.</exception>
    public async Task<OwnedDraftPrompts?> GetDraftPromptsAsync(OwnedProjectReferenceScope? scope, string? spaceId,
        CancellationToken cancellationToken = default)
    {
        lock (_gate) { if (_closed || _retained) return null; }
        var project = scope is null ? null : await ResolveReferenceProjectAsync(scope, cancellationToken).ConfigureAwait(false);
        if (scope is not null && !ReferenceScopeMatches(scope, project)) return null;
        var space = await _runtime.ResolveSpaceAsync(spaceId, project, cancellationToken).ConfigureAwait(false);
        lock (_gate) { if (_closed || _retained) return null; }
        cancellationToken.ThrowIfCancellationRequested();
        var prompts = _runtime.ListOwnedPrompts(project?.ProjectPath, _runtime.LoadSpaceFilter(space.Id)).Where(p => p.PromptName.Length <= 256).Take(MaximumPromptChoices)
            .Select(p => new OwnedPromptChoice(p.PromptName, Bound(p.DisplayName))).ToArray();
        return new(prompts, AgentPromptCatalog.ResolveNewSessionPromptId(null, [.. prompts.Select(static p => p.Id)], space), space.Id);
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
        // The prompts of the session's own space, whatever space the window shows.
        var space = await _runtime.GetSpaceFilterAsync(session.SpaceId, project, cancellationToken).ConfigureAwait(false);
        var prompts = _runtime.ListOwnedPrompts(project?.ProjectPath, space)
            .Where(p => p.PromptName.Length <= 256).Take(MaximumPromptChoices)
            .Select(p => new OwnedPromptChoice(p.PromptName, Bound(p.DisplayName))).ToArray();
        var provider = session.ResolvedProviderKey;
        var models = (observedOnly ? ObservedImageModels?.Invoke(new ModelProviderId(provider)) ?? [] : SelectionModels is null ? []
            : await SelectionModels(new ModelProviderId(provider), cancellationToken).ConfigureAwait(false))
            .Where(m => m.Id.Length <= 256).Take(MaximumModelChoices).ToArray();
        // What the session runs with, never "a default": a session without a model shows the one it starts with.
        var (modelId, effort) = StartingModel(provider, session.ModelId, session.ReasoningEffort, models);
        var configured = SelectionProvider?.Invoke(new ModelProviderId(provider));
        var (tier, adaptive) = await _runtime.GetSessionModelChoiceAsync(sessionId, cancellationToken).ConfigureAwait(false);
        // The modes of the provider when it has some, as Claude Code does. Any other provider runs the tools of
        // CodeAlta, whose requests the host answers: where the mode of a session decides that, it offers the modes of the host.
        var modes = configured?.PermissionModes.Where(static mode => !ProviderOnlyPermissionModes.Contains(mode)).Take(16).ToArray() ?? [];
        if (modes.Length == 0 && _runtime.SessionPermissionModes) modes = [.. SessionPermissionModes.HostModes];
        return new(new(provider, session.AgentPromptId ?? "default", modelId, effort) { PermissionMode = session.PermissionMode }, prompts,
            models.Select(m => new OwnedModelChoice(m.Id, Bound(m.DisplayName ?? m.Id), m.SupportedReasoningEfforts?.ToArray() ?? [])
                { ImageInput = AgentImageInputCapability.Read(m), StartEffort = StartingModel(provider, m.Id, null, models).Effort,
                  EffortsComplete = AgentReasoningEffortCapability.IsListComplete(m), Options = m.Options?.Take(16).ToArray() ?? [],
                  ContextSizes = m.ContextSizes?.Take(16).ToArray() ?? [],
                  ContextSize = m.ContextSizes is { Count: > 0 } ? m.ChooseContextSize(configured?.DefaultContextSize) ?? m.GetContextSize() : null }).ToArray())
        {
            PermissionModes = modes,
            // What a session without a mode runs in: the mode of its provider, else the one that names the policy of the host.
            DefaultPermissionMode = _runtime.SessionPermissionModes ? _runtime.GetDefaultPermissionMode(provider) : configured?.DefaultPermissionMode,
            SupportsRemoteControl = configured?.SupportsRemoteControl ?? false,
            Tier = tier is { } recorded ? Catalog.ModelTiers.ModelTiers.NameOf(recorded) : null,
            Adaptive = adaptive,
        };
    }

    /// <summary>
    /// Completes a selection named by its parts against the choices of a session, as the composer of the window
    /// completes the one it sends: what is not named is what the session runs with now, and a model named without an
    /// effort keeps the effort of the session when it has it, else takes the one it starts with. The provider stays
    /// the session's. Nothing is changed.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="modelId">A model of the provider of the session, or null to keep the session's.</param>
    /// <param name="reasoningEffort">An effort of the model, or null to keep the session's or take the model's.</param>
    /// <param name="agentPromptId">An agent prompt the session can run, or null to keep the session's.</param>
    /// <param name="permissionMode">
    /// A mode of <see cref="OwnedSelectionChoices.PermissionModes"/>, <see cref="OwnedSessionSelection.ProviderPermissionMode"/>
    /// for the one of the provider, or null to keep the session's.
    /// </param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The outcome, with the completed selection and the choices when it is <c>ok</c>.</returns>
    /// <exception cref="ArgumentException">The session identity is blank.</exception>
    /// <exception cref="OperationCanceledException">The read is canceled.</exception>
    public async Task<OwnedSelectionCheck> CheckSelectionAsync(string sessionId, string? modelId, AgentReasoningEffort? reasoningEffort,
        string? agentPromptId, string? permissionMode, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var choices = await GetSelectionChoicesAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (choices is null) return new(OwnedSelectionCheck.Unavailable, null, null);
        var current = choices.Current;
        var prompt = agentPromptId ?? current.AgentPromptId;
        if (!choices.Prompts.Any(p => p.Id == prompt)) return new(OwnedSelectionCheck.InvalidPrompt, null, choices);
        if (permissionMode is not null && (choices.PermissionModes.Count == 0 || permissionMode != OwnedSessionSelection.ProviderPermissionMode
            && !choices.PermissionModes.Contains(permissionMode, StringComparer.Ordinal))) return new(OwnedSelectionCheck.InvalidMode, null, choices);
        string? model = null;
        AgentReasoningEffort? effort = null;
        if (modelId is not null || reasoningEffort is not null)
        {
            model = modelId ?? current.ModelId;
            var offered = choices.Models.FirstOrDefault(m => m.Id == model);
            if (offered is null) return new(OwnedSelectionCheck.InvalidModel, null, choices);
            effort = reasoningEffort
                ?? (modelId is null || current.ReasoningEffort is { } own && offered.Efforts.Contains(own) ? current.ReasoningEffort : offered.StartEffort);
            if (reasoningEffort is { } named && offered.Efforts.Count > 0 && !offered.Efforts.Contains(named))
                return new(OwnedSelectionCheck.InvalidEffort, null, choices);
        }

        var selection = new OwnedSessionSelection(current.ProviderKey, prompt, model, effort) { PermissionMode = permissionMode };
        // What a send of the window checks again when it runs.
        return IsValidSelection(choices, selection) ? new(OwnedSelectionCheck.Ok, selection, choices) : new(OwnedSelectionCheck.InvalidEffort, null, choices);
    }

    /// <summary>
    /// Sets an option of a model of the provider of a session, such as its context or a fast mode: the provider sends
    /// it with every turn of the model from now on, in all its sessions. The session itself is not changed.
    /// </summary>
    /// <param name="sessionId">The session whose provider has the model.</param>
    /// <param name="modelId">A model of the provider.</param>
    /// <param name="optionId">An option of the model.</param>
    /// <param name="value">One of the values of the option.</param>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns>
    /// <see cref="OwnedSelectionCheck.Ok"/>; <see cref="OwnedSelectionCheck.Unavailable"/> for an unknown session or a
    /// provider that is not registered; <see cref="OwnedSelectionCheck.InvalidModel"/> for a provider that offers no
    /// option or does not list the model; <see cref="InvalidOption"/> for an option or a value the model does not have.
    /// </returns>
    /// <exception cref="ArgumentException">A name is blank.</exception>
    /// <exception cref="IOException">The provider could not keep the choice.</exception>
    /// <exception cref="OperationCanceledException">The change is canceled.</exception>
    public async Task<string> SetModelOptionAsync(string sessionId, string modelId, string optionId, string value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(optionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        lock (_gate) { if (_closed || _retained) return OwnedSelectionCheck.Unavailable; }
        var session = await _runtime.ResolveOwnedSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (session is null || ModelOptions is null) return OwnedSelectionCheck.Unavailable;
        try
        {
            return await ModelOptions(new ModelProviderId(session.ResolvedProviderKey), modelId, optionId, value, cancellationToken).ConfigureAwait(false) is null
                ? OwnedSelectionCheck.InvalidModel
                : OwnedSelectionCheck.Ok;
        }
        catch (ArgumentException)
        {
            return InvalidOption;
        }
        catch (KeyNotFoundException)
        {
            return OwnedSelectionCheck.Unavailable;
        }
    }

    /// <summary>The outcome of <see cref="SetModelOptionAsync"/> for an option or a value the model does not have.</summary>
    public const string InvalidOption = "invalid_option";

    // Modes a session is not given by itself. The plan mode of Claude Code ends with an approval CodeAlta does not
    // ask for: it stays a mode of the provider's configuration.
    internal static readonly FrozenSet<string> ProviderOnlyPermissionModes = FrozenSet.ToFrozenSet(["plan"], StringComparer.Ordinal);

    internal static bool IsValidSelection(OwnedSelectionChoices choices, OwnedSessionSelection selection)
    {
        if (!string.Equals(choices.Current.ProviderKey, selection.ProviderKey, StringComparison.Ordinal)
            || !choices.Prompts.Any(p => p.Id == selection.AgentPromptId)) return false;
        if (selection.PermissionMode is { } mode && mode != OwnedSessionSelection.ProviderPermissionMode
            && !choices.PermissionModes.Contains(mode, StringComparer.Ordinal)) return false;
        // No model keeps the session's own, which the send completes with a model of the provider.
        if (selection.ModelId is null) return selection.ReasoningEffort is null;
        var model = choices.Models.FirstOrDefault(m => m.Id == selection.ModelId);
        // A model that lists no effort takes the one it is given, as the alta tool, the automations and the work items
        // give it one: its provider did not say which efforts it has, and the turn passes the effort on.
        return model is not null && (selection.ReasoningEffort is null || model.Efforts.Count == 0
            || model.Efforts.Contains(selection.ReasoningEffort.Value));
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
