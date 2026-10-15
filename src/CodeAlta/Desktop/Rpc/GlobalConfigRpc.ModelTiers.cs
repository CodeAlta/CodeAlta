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
        return new("ok", ToItems(scope), ToItem(scope.Fallback), null) { Source = scope.FromSpace ? "space" : "global", SpaceId = space?.Id };
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
                _store.SaveGlobalModelSettings(settings);
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
        => new(fallback.Enabled, fallback.OnQuota, fallback.OnUnavailable);
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
internal sealed record ModelFallbackSetting(bool Enabled, bool OnQuota, bool OnUnavailable);
