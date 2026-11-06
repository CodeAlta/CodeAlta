using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Catalog.ModelTiers;
using CodeAlta.Orchestration.Runtime;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

internal sealed partial class GlobalConfigService
{
    private const int MaximumTierTargets = 16;

    /// <summary>Gets the states of the configured providers, which say what each tier gives now; none when null.</summary>
    internal Func<IReadOnlyList<ModelProviderStateSnapshot>>? ProviderStates { get; init; }

    /// <summary>Gets the spaces, whose files can set tiers of their own; none when null.</summary>
    internal SpaceCatalog? Spaces { get; init; }

    /// <summary>Gets what a session records of its space; none when null.</summary>
    internal Func<string, CancellationToken, Task<SessionSpaceRecord?>>? SessionSpace { get; init; }

    /// <summary>
    /// Reads the tiers of models, the model each gives now, and the fallback. With a space (or a session, read as its
    /// space), the tiers are those of the space: its own when its file sets some, the global ones otherwise, and what
    /// each gives is taken among the providers the space has.
    /// </summary>
    [NeoRpcMethod("modelTiers")]
    public async Task<GlobalConfigModelTiersResponse> ModelTiersAsync(GlobalConfigModelTiersRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_store is null) return new("unavailable", [], null, null);
        if (!string.Equals(request.ExpectedEpoch, _epoch, StringComparison.Ordinal)) return new("stale_epoch", [], null, null);
        ModelSettings settings;
        SpaceAssignments assignments;
        try
        {
            lock (_gate)
            {
                settings = _store.LoadGlobalModelSettings();
                assignments = _store.LoadGlobalSpaceAssignments();
            }
        }
        catch (InvalidDataException exception)
        {
            // Tiers the file writes wrong are shown as none, with what is wrong: saving here writes them again.
            return new("invalid", ToItems(ModelTierScope.None), ToItem(ModelFallbackSettings.Default), Bound(exception.Message));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new("read_failed", [], null, null);
        }

        var (status, space) = await FindSpaceAsync(request.SpaceId, request.SessionId, cancellationToken).ConfigureAwait(false);
        if (status is not null) return new(status, [], null, null);
        // The global reading is for no space: every provider counts there.
        var scope = ModelTierScope.Of(settings, space, space is null ? SpaceAssignments.Empty : assignments);
        return new("ok", ToItems(scope), ToItem(scope.Fallback), null)
        {
            Source = scope.FromSpace ? "space" : "global", SpaceId = space?.Id,
            Adaptive = ToItem(scope.Adaptive), AdaptiveReady = scope.IsAdaptiveReady,
            AdaptiveProposal = Propose(scope),
        };
    }

    // The levels Adaptive proposes for the providers that are ready (in the space, with a space), a subscription first.
    private IReadOnlyList<AdaptiveLevelSetting>? Propose(ModelTierScope scope)
    {
        var candidates = new List<AdaptiveProposals.Candidate>();
        foreach (var state in ProviderStates?.Invoke() ?? [])
        {
            if (state.Availability != ModelProviderAvailability.Ready || !state.Descriptor.IsEnabled) continue;
            var key = state.ProviderId.Value;
            if (scope.SpaceId is not null && !scope.IsProviderAvailable(key)) continue;
            var subscription = state.Descriptor.ProviderType is "codex" or "codex-cli" or "claude-code";
            candidates.AddRange(state.Models.Select(model => new AdaptiveProposals.Candidate(key, model.Id, subscription, model.DisplayName)));
        }

        var proposal = AdaptiveProposals.Propose(candidates);
        return proposal.Count == 0 ? null : [.. proposal.Select(ToItem)];
    }

    /// <summary>
    /// Sets up Adaptive with its levels, or takes it out with none: for each level, the model of its turns and the critics
    /// that review its changes, and the level a session takes when none is chosen. The reply reads the global tiers again.
    /// </summary>
    [NeoRpcMethod("saveAdaptive")]
    public async Task<GlobalConfigModelTiersResponse> SaveAdaptiveAsync(GlobalConfigSaveAdaptiveRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_store is null) return new("unavailable", [], null, null);
        if (!string.Equals(request.ExpectedEpoch, _epoch, StringComparison.Ordinal)) return new("stale_epoch", [], null, null);
        AdaptiveModelSettings? adaptive = null;
        if (request.Adaptive is { } chosen)
        {
            if (chosen.Levels is null || chosen.Levels.Count > AdaptiveLevels.All.Count) return new("invalid", [], null, "The levels of Adaptive are not complete.");
            var document = new CodeAltaModelAdaptiveDocument { Level = chosen.Level };
            foreach (var level in chosen.Levels)
            {
                if (level is null || AdaptiveLevels.Parse(level.Level) is not { } name || level.Model is null || level.Critics is null
                    || level.Critics.Count > AdaptiveLevelSettings.MaximumCritics)
                    return new("invalid", [], null, "A level of Adaptive names no level, no model, or too many critics.");
                var written = new CodeAltaModelAdaptiveLevelDocument { Model = Written(level.Model), Critics = [.. level.Critics.Select(Written)] };
                switch (name)
                {
                    case AdaptiveLevel.Low: document.Low = written; break;
                    case AdaptiveLevel.Medium: document.Medium = written; break;
                    case AdaptiveLevel.High: document.High = written; break;
                    default: document.Max = written; break;
                }
            }

            var errors = ModelSettings.Validate(new CodeAltaModelSettingsDocument { Adaptive = document });
            if (errors.Count > 0) return new("invalid", [], null, Bound(string.Join(" ", errors)));
            adaptive = AdaptiveModelSettings.Read(document);
        }

        lock (_gate)
        {
            try
            {
                _store.SaveGlobalModelSettings(_store.LoadGlobalModelSettings() with { Adaptive = adaptive });
            }
            catch (InvalidDataException exception)
            {
                // The tiers the file writes wrong are fixed on the tiers page first.
                return new("invalid", [], null, Bound(exception.Message));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or FormatException)
            {
                return new("write_failed", [], null, null);
            }
        }

        return await ModelTiersAsync(new(request.ExpectedEpoch), cancellationToken).ConfigureAwait(false);
    }

    // Adaptive as the file sets it up, read under the gate; none when the file cannot be read, which a save then fixes.
    private AdaptiveModelSettings? LoadAdaptive()
    {
        try { return _store!.LoadGlobalModelSettings().Adaptive; }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Saves the tiers of models and the fallback, or with a space the tiers and the fallback of that space
    /// (<c>UseGlobal</c> gives it the global ones again). A target that is not one is refused with what is wrong.
    /// </summary>
    [NeoRpcMethod("saveModelTiers")]
    public async Task<GlobalConfigModelTiersResponse> SaveModelTiersAsync(GlobalConfigSaveModelTiersRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_store is null) return new("unavailable", [], null, null);
        if (!string.Equals(request.ExpectedEpoch, _epoch, StringComparison.Ordinal)) return new("stale_epoch", [], null, null);
        if (request.SpaceId is not null && Spaces is null) return new("unavailable", [], null, null);
        if (request.SpaceId is not null && request.UseGlobal)
            return await SaveSpaceTiersAsync(request.SpaceId, null, cancellationToken).ConfigureAwait(false);
        if (request.Tiers is null || request.Fallback is null || request.Tiers.Count > CodeAlta.Catalog.ModelTiers.ModelTiers.All.Count)
            return new("invalid", [], null, "The tiers are not complete.");
        var document = new CodeAltaModelTiersDocument();
        foreach (var list in request.Tiers)
        {
            if (list?.Targets is null || CodeAlta.Catalog.ModelTiers.ModelTiers.Parse(list.Tier) is not { } tier || list.Targets.Count > MaximumTierTargets)
                return new("invalid", [], null, "The tiers are not complete.");
            var written = new List<string>();
            foreach (var target in list.Targets)
            {
                if (target is null || string.IsNullOrWhiteSpace(target.ProviderKey) || target.ProviderKey.Length > 256
                    || string.IsNullOrWhiteSpace(target.ModelId) || target.ModelId.Length > 256 || target.Reasoning is null || target.Reasoning.Count > 8)
                    return new("invalid", [], null, "A target of a tier names no provider or no model.");
                var efforts = new List<string>();
                foreach (var effort in target.Reasoning)
                {
                    if (!Enum.TryParse<AgentReasoningEffort>(effort, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
                        return new("invalid", [], null, $"'{effort}' is not a reasoning effort.");
                    efforts.Add(parsed.ToString().ToLowerInvariant());
                }

                written.Add(efforts.Count == 0 ? $"{target.ProviderKey.Trim()}:{target.ModelId.Trim()}" : $"{target.ProviderKey.Trim()}:{target.ModelId.Trim()}@{string.Join(',', efforts)}");
            }

            switch (tier)
            {
                case ModelTier.Complex: document.Complex = written; break;
                case ModelTier.Standard: document.Standard = written; break;
                default: document.Quick = written; break;
            }
        }

        ModelSettings settings;
        try
        {
            settings = ModelSettings.Read(new CodeAltaModelSettingsDocument
            {
                Tiers = document,
                Fallback = new CodeAltaModelFallbackDocument
                {
                    Enabled = request.Fallback.Enabled,
                    On = [.. Triggers(request.Fallback)],
                    Resume = request.Fallback.Resume,
                    MaxResumes = request.Fallback.MaxResumes,
                },
            });
        }
        catch (InvalidDataException exception)
        {
            return new("invalid", [], null, Bound(exception.Message));
        }

        // The tiers and the fallback of a space go to its file.
        if (request.SpaceId is not null)
            return await SaveSpaceTiersAsync(request.SpaceId, settings, cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            try
            {
                // The tiers and the fallback are saved here: Adaptive stays as the file has it.
                _store.SaveGlobalModelSettings(settings with { Adaptive = LoadAdaptive() });
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or FormatException)
            {
                return new("write_failed", [], null, null);
            }
        }

        return await ModelTiersAsync(new(request.ExpectedEpoch), cancellationToken).ConfigureAwait(false);

        static IEnumerable<string> Triggers(ModelFallbackSetting fallback)
        {
            if (fallback.OnQuota) yield return ModelFallbackSettings.QuotaTrigger;
            if (fallback.OnUnavailable) yield return ModelFallbackSettings.UnavailableTrigger;
        }
    }

    // Writes the tiers and the fallback of a space, or none for the global ones; the reply reads the space again. A
    // space without tiers of its own takes the global fallback. One with tiers keeps the global fallback until it is
    // changed there: its file gets a fallback when it differs from the global one, and keeps it once it has one.
    private async Task<GlobalConfigModelTiersResponse> SaveSpaceTiersAsync(string spaceId, ModelSettings? models, CancellationToken cancellationToken)
    {
        var global = ModelFallbackSettings.Default;
        try { lock (_gate) global = _store!.LoadGlobalModelSettings().Fallback; }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException) { }

        SpaceChange change;
        try
        {
            change = await Spaces!.UpdateSettingsAsync(spaceId, settings =>
            {
                var sessions = settings.NewSessions ?? new SpaceNewSessions();
                var tiers = models is null ? null : SpaceModelTiers.From(models.Tiers);
                var fallback = tiers is null ? null : sessions.Fallback is not null || models!.Fallback != global ? models!.Fallback : null;
                return settings with { NewSessions = sessions with { Tiers = tiers, Fallback = fallback } is { IsEmpty: false } kept ? kept : null };
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new("write_failed", [], null, null);
        }

        return change.Status switch
        {
            SpaceChangeStatus.Ok => await ModelTiersAsync(new(_epoch!) { SpaceId = spaceId }, cancellationToken).ConfigureAwait(false),
            SpaceChangeStatus.NotFound => new("not_found", [], null, change.Message),
            SpaceChangeStatus.Invalid => new("invalid", [], null, Bound(change.Message)),
            _ => new("conflict", [], null, Bound(change.Message)),
        };
    }

    // The space a reading is for: the one named, or the one of a session; none without either. A status says why there is none.
    private async Task<(string? Status, SpaceDescriptor? Space)> FindSpaceAsync(string? spaceId, string? sessionId, CancellationToken cancellationToken)
    {
        if (spaceId is null && sessionId is null) return (null, null);
        if (Spaces is null) return (null, null);
        try
        {
            if (spaceId is not null)
            {
                return await Spaces.GetAsync(spaceId, cancellationToken).ConfigureAwait(false) is { } named ? (null, named) : ("not_found", null);
            }

            var record = SessionSpace is null ? null : await SessionSpace(sessionId!, cancellationToken).ConfigureAwait(false);
            if (record is null) return ("not_found", null);
            return (null, await Spaces.ResolveSessionSpaceAsync(record.SpaceId, record.ProjectId, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            return ("read_failed", null);
        }
    }

    private IReadOnlyList<ModelTierList> ToItems(ModelTierScope scope)
    {
        var states = ProviderStates?.Invoke() ?? [];
        return [.. CodeAlta.Catalog.ModelTiers.ModelTiers.All.Select(tier => new ModelTierList(
            CodeAlta.Catalog.ModelTiers.ModelTiers.NameOf(tier),
            [.. scope.Tiers.Get(tier).Select(static target => new ModelTierTargetItem(target.ProviderKey, target.ModelId, [.. target.Reasoning.Select(static effort => effort.ToString())]))])
        {
            Choice = ModelTierResolver.Resolve(scope, tier, states) is { } choice
                ? new ModelTierChoiceItem(choice.ProviderKey, choice.ModelId, choice.Reasoning?.ToString(), choice.ProviderDisplayName, choice.ModelDisplayName)
                : null,
        })];
    }

    private static ModelFallbackSetting ToItem(ModelFallbackSettings fallback)
        => new(fallback.Enabled, fallback.OnQuota, fallback.OnUnavailable, fallback.Resume, fallback.MaxResumes);

    private AdaptiveSetting? ToItem(AdaptiveModelSettings? adaptive)
        => adaptive is null ? null : new(AdaptiveLevels.NameOf(adaptive.DefaultLevel), [.. adaptive.Levels.Select(ToItem)]);

    // A level with the names of its model when its provider is ready, as a tier gives the model it gives now, and the
    // names of its critics.
    private AdaptiveLevelSetting ToItem(AdaptiveLevelSettings level)
    {
        var states = ProviderStates?.Invoke() ?? [];
        var state = StateOf(states, level.Model);
        var model = ModelOf(state, level.Model);
        return new(AdaptiveLevels.NameOf(level.Level), ToItem(level.Model), [.. level.Critics.Select(ToItem)])
        {
            Choice = state is { Availability: ModelProviderAvailability.Ready } && (model is not null || state.Models.Count == 0)
                ? new ModelTierChoiceItem(level.Model.ProviderKey, level.Model.ModelId, level.Model.PreferredReasoning?.ToString(),
                    state.Descriptor.DisplayName, model?.DisplayName ?? level.Model.ModelId)
                : null,
            CriticNames = [.. level.Critics.Select(critic => ModelOf(StateOf(states, critic), critic)?.DisplayName ?? critic.ModelId)],
        };

        static ModelProviderStateSnapshot? StateOf(IReadOnlyList<ModelProviderStateSnapshot> states, ModelTierTarget target)
            => states.FirstOrDefault(candidate => string.Equals(candidate.ProviderId.Value, target.ProviderKey, StringComparison.OrdinalIgnoreCase));

        static AgentModelInfo? ModelOf(ModelProviderStateSnapshot? state, ModelTierTarget target)
            => state?.Models.FirstOrDefault(candidate => string.Equals(candidate.Id, target.ModelId, StringComparison.OrdinalIgnoreCase));
    }

    private static ModelTierTargetItem ToItem(ModelTierTarget target)
        => new(target.ProviderKey, target.ModelId, [.. target.Reasoning.Select(static effort => effort.ToString())]);

    // A model of a level as the configuration writes it; what is not valid is told by the validation of the table.
    private static string Written(ModelTierTargetItem? target)
    {
        if (target is null) return string.Empty;
        var efforts = (target.Reasoning ?? []).Select(static effort => Enum.TryParse<AgentReasoningEffort>(effort, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed.ToString().ToLowerInvariant() : effort).ToArray();
        var model = $"{target.ProviderKey?.Trim()}:{target.ModelId?.Trim()}";
        return efforts.Length == 0 ? model : $"{model}@{string.Join(',', efforts)}";
    }
}

/// <summary>Sets up Adaptive, or takes it out.</summary>
/// <param name="ExpectedEpoch">The host epoch.</param>
/// <param name="Adaptive">The levels of Adaptive, or null to take it out.</param>
internal sealed record GlobalConfigSaveAdaptiveRequest(string ExpectedEpoch, AdaptiveSetting? Adaptive);

/// <summary>How CodeAlta runs the turns of a session on Adaptive: its levels, and the level a session takes when none is chosen.</summary>
/// <param name="Level">The default level: <c>low</c>, <c>medium</c>, <c>high</c> or <c>max</c>.</param>
/// <param name="Levels">The levels that are set, from the cheapest.</param>
internal sealed record AdaptiveSetting(string Level, IReadOnlyList<AdaptiveLevelSetting> Levels);

/// <summary>A level of Adaptive: the model of its turns, and the critics that review its changes of code.</summary>
/// <param name="Level"><c>low</c>, <c>medium</c>, <c>high</c> or <c>max</c>.</param>
/// <param name="Model">The model of the turns, with at most one reasoning effort.</param>
/// <param name="Critics">The critics, each with at most one reasoning effort; none for a recheck.</param>
internal sealed record AdaptiveLevelSetting(string Level, ModelTierTargetItem Model, IReadOnlyList<ModelTierTargetItem> Critics)
{
    /// <summary>Gets the model of the turns with its names, when its provider is ready (or for a request: null).</summary>
    public ModelTierChoiceItem? Choice { get; init; }

    /// <summary>Gets the names of the critics, in order, as their providers name them (or for a request: null).</summary>
    public IReadOnlyList<string>? CriticNames { get; init; }
}

/// <summary>Asks for the tiers of models.</summary>
/// <param name="ExpectedEpoch">The host epoch.</param>
internal sealed record GlobalConfigModelTiersRequest(string ExpectedEpoch)
{
    /// <summary>Gets the space whose tiers are read; null for the global ones.</summary>
    public string? SpaceId { get; init; }

    /// <summary>Gets a session whose space's tiers are read, when no space is named.</summary>
    public string? SessionId { get; init; }
}

/// <summary>Saves the tiers of models and the fallback.</summary>
/// <param name="ExpectedEpoch">The host epoch.</param>
/// <param name="Tiers">Each tier with its targets in order.</param>
/// <param name="Fallback">When a session goes on with the next target of its tier.</param>
internal sealed record GlobalConfigSaveModelTiersRequest(string ExpectedEpoch, IReadOnlyList<ModelTierList> Tiers, ModelFallbackSetting Fallback)
{
    /// <summary>Gets the space whose tiers are saved, in its file; null for the global ones of the settings.</summary>
    public string? SpaceId { get; init; }

    /// <summary>Gets whether the space takes the global tiers again: its own are removed.</summary>
    public bool UseGlobal { get; init; }
}

/// <summary>
/// <c>ok</c> with the tiers; <c>invalid</c> with what is wrong (a save that was refused, or tiers the file writes
/// wrong, then shown as none); <c>unavailable</c>, <c>stale_epoch</c>, <c>read_failed</c> or <c>write_failed</c>.
/// </summary>
/// <param name="Status">The status.</param>
/// <param name="Tiers">Complex, standard and quick, in this order.</param>
/// <param name="Fallback">The fallback, when the tiers were read.</param>
/// <param name="Message">What is wrong, for <c>invalid</c>.</param>
internal sealed record GlobalConfigModelTiersResponse(string Status, IReadOnlyList<ModelTierList> Tiers, ModelFallbackSetting? Fallback, string? Message)
{
    /// <summary>Gets where the tiers come from: <c>space</c> for the space's own, <c>global</c> otherwise.</summary>
    public string Source { get; init; } = "global";

    /// <summary>Gets the space the tiers were read for, or null.</summary>
    public string? SpaceId { get; init; }

    /// <summary>Gets the choices of Adaptive, or null when it is not set up.</summary>
    public AdaptiveSetting? Adaptive { get; init; }

    /// <summary>Gets whether a session of the scope can be put on Adaptive: it is set up with a level.</summary>
    public bool AdaptiveReady { get; init; }

    /// <summary>
    /// Gets the levels Adaptive proposes for the providers that are ready, as tested in November 2026; null when none of
    /// the models a proposal knows is there, and the user then chooses the models.
    /// </summary>
    public IReadOnlyList<AdaptiveLevelSetting>? AdaptiveProposal { get; init; }
}

/// <summary>A tier of models: its targets in order of preference, and the model it gives now.</summary>
/// <param name="Tier"><c>complex</c>, <c>standard</c> or <c>quick</c>.</param>
/// <param name="Targets">The targets in order.</param>
internal sealed record ModelTierList(string Tier, IReadOnlyList<ModelTierTargetItem> Targets)
{
    /// <summary>Gets the first target that is ready now, or null when none is (or for a request).</summary>
    public ModelTierChoiceItem? Choice { get; init; }
}

/// <summary>A target of a tier: a model of a configured provider with its reasoning efforts in order.</summary>
/// <param name="ProviderKey">The key of the provider.</param>
/// <param name="ModelId">The model.</param>
/// <param name="Reasoning">The reasoning efforts in order (Low, Medium, High...); empty for the provider's own.</param>
internal sealed record ModelTierTargetItem(string ProviderKey, string ModelId, IReadOnlyList<string> Reasoning);

/// <summary>The model a tier gives now.</summary>
/// <param name="ProviderKey">The key of the provider.</param>
/// <param name="ModelId">The model.</param>
/// <param name="Reasoning">The reasoning effort, or null for the provider's own.</param>
/// <param name="ProviderName">The name of the provider.</param>
/// <param name="ModelName">The name of the model.</param>
internal sealed record ModelTierChoiceItem(string ProviderKey, string ModelId, string? Reasoning, string ProviderName, string ModelName);

/// <summary>When a session goes on with the next target of its tier.</summary>
/// <param name="Enabled">Whether the fallback is on.</param>
/// <param name="OnQuota">On an exhausted quota or a long rate limit.</param>
/// <param name="OnUnavailable">On a provider that stays unavailable.</param>
/// <param name="Resume">Whether a session that stopped on a limit, with no other model to take over, resumes when the limit resets.</param>
/// <param name="MaxResumes">How many times in a row a session resumes by itself, from 1 to 50.</param>
internal sealed record ModelFallbackSetting(bool Enabled, bool OnQuota, bool OnUnavailable, bool Resume, int MaxResumes);
