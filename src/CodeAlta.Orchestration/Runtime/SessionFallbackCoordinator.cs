using System.Globalization;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Catalog.ModelTiers;
using ModelTierTarget = CodeAlta.Catalog.ModelTiers.ModelTierTarget;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>A fallback a session made: when, why, and from which model to which.</summary>
/// <param name="SessionId">The session.</param>
/// <param name="At">When the session went on with the other model.</param>
/// <param name="Reason">Why: the kind of failure of the run.</param>
/// <param name="From">The model left, as <c>provider:model@reasoning</c>.</param>
/// <param name="To">The model taken, as <c>provider:model@reasoning</c>.</param>
/// <param name="Message">The sentence the timeline shows.</param>
public sealed record SessionFallbackRecord(string SessionId, DateTimeOffset At, AgentFailureKind Reason, string From, string To, string Message);

/// <summary>
/// Goes on with the next target of a session's tier of models when a run fails on its provider: an exhausted quota or
/// allowance, a long rate limit, or a provider that stays unavailable after its retries. Only the targets the user
/// listed in the tier are taken; the permission mode of the session is kept or narrowed, never widened.
/// </summary>
/// <remarks>
/// The decision is made on the actor of the session, with the error of the run; the switch runs afterwards, once
/// the send of the failed run has released the session. The parent of the session is not told of a failure it went
/// on from, and its queue waits for the run that continues. When nothing can be taken, the failure goes through with
/// a card that says what to do.
/// </remarks>
internal sealed class SessionFallbackCoordinator
{
    /// <summary>A rate limit shorter than this is waited out by the provider: it is not a reason to change model.</summary>
    internal static readonly TimeSpan ShortRateLimit = TimeSpan.FromSeconds(60);

    // How long a provider whose quota is exhausted is skipped when it does not say when it resets.
    private static readonly TimeSpan UnknownResetHold = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ReleaseWait = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ReleasePoll = TimeSpan.FromMilliseconds(50);

    private readonly OwnedSessionCommandService _commands;
    private readonly SessionRuntimeService _runtime;
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _exhaustedUntil = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SessionFallbackRecord> _last = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Task> _running = [];
    private readonly CancellationTokenSource _closing = new();

    internal SessionFallbackCoordinator(OwnedSessionCommandService commands, SessionRuntimeService runtime)
    {
        _commands = commands;
        _runtime = runtime;
    }

    /// <summary>Gets or sets the clock; the system clock unless a test sets another.</summary>
    internal TimeProvider Time { get; set; } = TimeProvider.System;

    /// <summary>Gets or sets the states of the configured providers; every enabled provider counts as ready when null.</summary>
    internal Func<IReadOnlyList<ModelProviderStateSnapshot>>? ProviderStates { get; set; }

    /// <summary>Gets the last fallback of each session that made one since the start of the host.</summary>
    public IReadOnlyList<SessionFallbackRecord> LastFallbacks
    {
        get { lock (_gate) return [.. _last.Values]; }
    }

    /// <summary>Waits for the fallbacks that run and stops new ones.</summary>
    public async Task CloseAsync()
    {
        Task[] running;
        lock (_gate)
        {
            if (!_closing.IsCancellationRequested) _closing.Cancel();
            running = [.. _running];
        }

        try { await Task.WhenAll(running).ConfigureAwait(false); }
        catch (Exception) { /* Each fallback released what it held. */ }
    }

    /// <summary>
    /// Decides, on the actor of the session, whether its failed run goes on with another model. Never waits.
    /// </summary>
    /// <param name="notice">The failed run.</param>
    /// <returns>What starts the fallback, or <see langword="null"/> to let the failure through.</returns>
    public Action<SessionRuntimeService.HeldRunFailure>? TryClaim(RunFailureNotice notice)
    {
        ArgumentNullException.ThrowIfNull(notice);
        if (_closing.IsCancellationRequested || notice.Error.Failure is not { } failure) return null;
        var settings = _runtime.LoadModelSettings();
        if (!settings.Fallback.Enabled || !IsTrigger(settings.Fallback, failure)) return null;
        var tier = notice.Tier ?? settings.Tiers.FindTierOf(notice.ProviderKey, notice.ModelId);
        if (tier is null) return null;
        var now = Time.GetUtcNow();
        if (failure.Kind is AgentFailureKind.QuotaExhausted or AgentFailureKind.RateLimited)
        {
            var until = failure.ResetsAt is { } reset && reset > now ? reset
                : failure.RetryAfterSeconds is > 0 and var seconds ? now.AddSeconds(seconds)
                : now + UnknownResetHold;
            lock (_gate) _exhaustedUntil[notice.ProviderKey] = until;
        }

        var states = ProviderStates?.Invoke() ?? [];
        var next = FindNext(settings.Tiers, tier.Value, notice, failure, states, now);
        return held => Start(notice, tier.Value, next, states, held);
    }

    /// <summary>Gets whether a failure is one the settings fall back on.</summary>
    internal static bool IsTrigger(ModelFallbackSettings settings, AgentFailureInfo failure)
        => failure.Kind switch
        {
            AgentFailureKind.QuotaExhausted => settings.OnQuota,
            // A short limit with a Retry-After is waited out, not left.
            AgentFailureKind.RateLimited => settings.OnQuota && !(failure.RetryAfterSeconds is { } seconds && seconds < ShortRateLimit.TotalSeconds),
            AgentFailureKind.Unavailable => settings.OnUnavailable,
            _ => false,
        };

    /// <summary>
    /// Finds the step of the tier that comes after the failed model: the next one in order, but no other model of a
    /// provider whose quota is exhausted, and a lower reasoning of the same model only when the provider was unavailable.
    /// </summary>
    internal ModelTierCandidate? FindNext(ModelTierSettings tiers, ModelTier tier, RunFailureNotice notice, AgentFailureInfo failure,
        IReadOnlyList<ModelProviderStateSnapshot> states, DateTimeOffset now)
    {
        var candidates = ModelTierResolver.Candidates(tiers, tier);
        var current = -1;
        for (var index = 0; index < candidates.Count; index++)
        {
            if (candidates[index].Is(notice.ProviderKey, notice.ModelId, notice.ReasoningEffort))
            {
                current = index;
                break;
            }
        }

        var quota = failure.Kind is AgentFailureKind.QuotaExhausted or AgentFailureKind.RateLimited;
        for (var index = current + 1; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            var sameProvider = string.Equals(candidate.Target.ProviderKey, notice.ProviderKey, StringComparison.OrdinalIgnoreCase);
            if (sameProvider && quota) continue;
            if (sameProvider && candidate.Is(notice.ProviderKey, notice.ModelId, notice.ReasoningEffort) && candidate.Reasoning == notice.ReasoningEffort) continue;
            if (IsExhausted(candidate.Target.ProviderKey, now)) continue;
            // A queue that waits cannot follow the session to another provider: the switch is refused then.
            if (!sameProvider && notice.HasQueuedPrompts) continue;
            if (!IsUsable(candidate.Target, states)) continue;
            return candidate;
        }

        return null;
    }

    private bool IsExhausted(string providerKey, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_exhaustedUntil.TryGetValue(providerKey, out var until)) return false;
            if (until > now) return true;
            _exhaustedUntil.Remove(providerKey);
            return false;
        }
    }

    // A provider that was not probed yet may still answer: only one that is known not to is skipped.
    private bool IsUsable(ModelTierTarget target, IReadOnlyList<ModelProviderStateSnapshot> states)
    {
        var descriptor = _commands.SelectionProvider?.Invoke(new ModelProviderId(target.ProviderKey));
        if (descriptor is not { IsEnabled: true }) return false;
        var state = states.FirstOrDefault(state => string.Equals(state.ProviderId.Value, target.ProviderKey, StringComparison.OrdinalIgnoreCase));
        return state is null
            || state.Availability is ModelProviderAvailability.Unknown or ModelProviderAvailability.Probing
            || ModelTierResolver.IsReady(target, states);
    }

    private void Start(RunFailureNotice notice, ModelTier tier, ModelTierCandidate? next, IReadOnlyList<ModelProviderStateSnapshot> states,
        SessionRuntimeService.HeldRunFailure held)
    {
        Task task;
        lock (_gate)
        {
            if (_closing.IsCancellationRequested)
            {
                task = held.ReleaseAsync();
                return;
            }

            task = Task.Run(() => RunAsync(notice, tier, next, states, held));
            _running.Add(task);
        }

        _ = task.ContinueWith(completed => { lock (_gate) _running.Remove(completed); }, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task RunAsync(RunFailureNotice notice, ModelTier tier, ModelTierCandidate? next,
        IReadOnlyList<ModelProviderStateSnapshot> states, SessionRuntimeService.HeldRunFailure held)
    {
        var failure = notice.Error.Failure!;
        var from = Describe(notice.ProviderKey, notice.ModelId, notice.ReasoningEffort, states);
        try
        {
            if (next is null)
            {
                await WriteCardAsync(notice.SessionId, AgentSessionUpdateKind.Warning,
                    $"{Reason(from.Provider, failure)}, and no other model of the {ModelTiers.NameOf(tier)} tier can take over. " +
                    (failure.ResetsAt is not null ? "Choose another model, or send again after the reset." : "Choose another model, or send again later."),
                    Details(notice, failure, from.Ref, to: null)).ConfigureAwait(false);
                await held.ReleaseAsync().ConfigureAwait(false);
                return;
            }

            if (!await WaitForReleaseAsync(notice.SessionId).ConfigureAwait(false))
            {
                await FailAsync("the session stayed busy").ConfigureAwait(false);
                return;
            }

            var to = ModelTierResolver.Describe(next, states);
            var model = next.Target.ModelId;
            var reasoning = next.Reasoning;
            var sameProvider = string.Equals(next.Target.ProviderKey, notice.ProviderKey, StringComparison.OrdinalIgnoreCase);
            string? permissionMode = null;
            if (!sameProvider)
            {
                permissionMode = KeepOrNarrow(notice, next.Target.ProviderKey);
                var switched = await SwitchProviderAsync(notice.SessionId, next.Target.ProviderKey,
                    new SessionViewProviderCarryOver(model, reasoning, permissionMode)).ConfigureAwait(false);
                if (switched != "ok")
                {
                    await FailAsync($"the switch to {to.ProviderDisplayName} was refused ({switched})").ConfigureAwait(false);
                    return;
                }
            }

            await _runtime.SetSessionTierAsync(notice.SessionId, tier, _closing.Token).ConfigureAwait(false);
            var toText = $"{to.ProviderDisplayName} {to.ModelDisplayName}{(reasoning is { } effort ? " " + CodeAltaConfigStore.FormatReasoningEffort(effort) : string.Empty)}";
            var message = $"{Reason(from.Provider, failure)}, continuing on {toText}.";
            await WriteCardAsync(notice.SessionId, AgentSessionUpdateKind.ProviderFallback, message, Details(notice, failure, from.Ref, next.Format()))
                .ConfigureAwait(false);
            lock (_gate)
            {
                _last[notice.SessionId] = new SessionFallbackRecord(notice.SessionId, Time.GetUtcNow(), failure.Kind, from.Ref, next.Format(), message);
            }

            var admission = _commands.AdmitSend(new OwnedTextSendRequest(
                $"fallback:{notice.SessionId}:{notice.Error.RunId?.Value}",
                notice.SessionId,
                ContinuationPrompt(from, toText, failure))
            {
                Selection = new OwnedSessionSelection(next.Target.ProviderKey, notice.AgentPromptId ?? "default", model, reasoning)
                {
                    PermissionMode = permissionMode,
                },
            });
            if (admission is not { Kind: OwnedSessionCommandAdmissionKind.Accepted, Receipt: { } receipt })
            {
                await FailAsync($"the continuation was not admitted ({admission.Kind.ToString().ToLowerInvariant()})").ConfigureAwait(false);
                return;
            }

            // A run that starts tells its own end, an answer or another failure; one that never starts lets the
            // failure through.
            var result = await receipt.Completion.ConfigureAwait(false);
            if (result.Outcome != OwnedSessionCommandOutcome.Completed && result.Code is "preparation_failed" or "permission_unavailable")
            {
                await FailAsync($"the continuation could not start ({result.Code})").ConfigureAwait(false);
                return;
            }

            held.Replace();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            await FailAsync(exception is OperationCanceledException or ObjectDisposedException ? "CodeAlta is closing" : exception.Message).ConfigureAwait(false);
        }

        async Task FailAsync(string why)
        {
            try
            {
                if (!_closing.IsCancellationRequested)
                {
                    await WriteCardAsync(notice.SessionId, AgentSessionUpdateKind.Warning,
                        $"{Reason(from.Provider, failure)}; the fallback stopped: {why}. Choose another model, or send again later.",
                        Details(notice, failure, from.Ref, next?.Format())).ConfigureAwait(false);
                }
            }
            catch (Exception) { /* The card is not worth losing the failure for. */ }
            finally
            {
                await held.ReleaseAsync().ConfigureAwait(false);
            }
        }
    }

    // The send of the failed run holds the session until its run is cleaned up.
    private async Task<bool> WaitForReleaseAsync(string sessionId)
    {
        var deadline = Time.GetUtcNow() + ReleaseWait;
        while (_commands.IsSessionHeldForFallback(sessionId))
        {
            if (Time.GetUtcNow() >= deadline) return false;
            await Task.Delay(ReleasePoll, _closing.Token).ConfigureAwait(false);
        }

        return true;
    }

    private async Task<string> SwitchProviderAsync(string sessionId, string providerKey, SessionViewProviderCarryOver carryOver)
    {
        // The runtime may still settle what the failed run left (its last events): it is asked again for a while.
        var deadline = Time.GetUtcNow() + ReleaseWait;
        while (true)
        {
            var context = await _commands.GetProviderSelectionAsync(sessionId).ConfigureAwait(false);
            var result = context is null ? "busy" : await _commands.SelectProviderAsync(context, providerKey, carryOver).ConfigureAwait(false);
            if (result is not ("busy" or "session_in_use" or "stale_selection" or "stale_attachment") || Time.GetUtcNow() >= deadline) return result;
            await Task.Delay(ReleasePoll, _closing.Token).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Gets the permission mode the session takes on the provider it falls back to: the one it had when the provider
    /// offers it, else the widest the provider offers that is not wider than it; never a wider one.
    /// </summary>
    internal string? KeepOrNarrow(RunFailureNotice notice, string targetProviderKey)
    {
        var target = _commands.PermissionModesOf(targetProviderKey);
        if (target.Modes.Count == 0) return null;
        var current = notice.PermissionMode ?? _commands.PermissionModesOf(notice.ProviderKey).Default ?? target.Default;
        if (current is not null && target.Modes.Contains(current, StringComparer.Ordinal)) return current;
        var rank = PermissionRank(current);
        return target.Modes
                   .Where(mode => PermissionRank(mode) <= rank)
                   .OrderByDescending(PermissionRank)
                   .FirstOrDefault()
               ?? target.Modes.OrderBy(PermissionRank).First();
    }

    // How much a mode lets run without the user, from the narrowest: the plan and the modes that refuse what is not
    // allowed, the default, file edits, the automatic approval, then everything. A mode not known here counts as the
    // default.
    internal static int PermissionRank(string? mode)
        => mode switch
        {
            "plan" or "dontAsk" => 0,
            null or "default" => 1,
            SessionPermissionModes.AcceptEdits => 2,
            "auto" => 3,
            SessionPermissionModes.Bypass => 4,
            _ => 1,
        };

    private async Task WriteCardAsync(string sessionId, AgentSessionUpdateKind kind, string message, JsonElement details)
    {
        var session = await _runtime.ResolveOwnedSessionAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
        if (session is null) return;
        await _runtime.AppendSessionEventAsync(session, new AgentSessionUpdateEvent(
            new ModelProviderId(session.ResolvedProviderKey),
            sessionId,
            Time.GetUtcNow(),
            null,
            kind,
            message,
            details), CancellationToken.None).ConfigureAwait(false);
    }

    private static JsonElement Details(RunFailureNotice notice, AgentFailureInfo failure, string from, string? to)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("reason", failure.Kind.ToString());
            writer.WriteString("from", from);
            if (to is not null) writer.WriteString("to", to);
            if (failure.ResetsAt is { } reset) writer.WriteString("resetsAt", reset.ToString("O", CultureInfo.InvariantCulture));
            if (notice.Error.RunId is { } runId) writer.WriteString("failedRunId", runId.Value);
            writer.WriteEndObject();
        }

        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    private (string Provider, string Model, string Ref) Describe(string providerKey, string? modelId, AgentReasoningEffort? reasoning,
        IReadOnlyList<ModelProviderStateSnapshot> states)
    {
        var state = states.FirstOrDefault(state => string.Equals(state.ProviderId.Value, providerKey, StringComparison.OrdinalIgnoreCase));
        var provider = state?.Descriptor.DisplayName
                       ?? _commands.SelectionProvider?.Invoke(new ModelProviderId(providerKey))?.DisplayName
                       ?? providerKey;
        var model = state?.Models.FirstOrDefault(model => string.Equals(model.Id, modelId, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? modelId ?? "its model";
        var reference = modelId is null ? providerKey : $"{providerKey}:{modelId}";
        if (reasoning is { } effort) reference += "@" + CodeAltaConfigStore.FormatReasoningEffort(effort);
        return (provider, model, reference);
    }

    private static string Reason(string provider, AgentFailureInfo failure)
    {
        var until = failure.ResetsAt is { } reset
            ? " until " + reset.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)
            : string.Empty;
        return failure.Kind switch
        {
            AgentFailureKind.QuotaExhausted => $"{provider} quota reached{until}",
            AgentFailureKind.RateLimited => $"{provider} rate limited{until}",
            _ => $"{provider} unavailable",
        };
    }

    private static string ContinuationPrompt((string Provider, string Model, string Ref) from, string to, AgentFailureInfo failure)
        => $"""
            [CodeAlta fallback]
            The last run stopped on {from.Provider} {from.Model} ({Reason(from.Provider, failure)}). CodeAlta moved this session to {to} to finish the task.
            Continue the task from where it stopped. A tool call that was running when it stopped may or may not have taken effect: check the current state (files, git status, the output of the command) before you run it again, and do not redo what is already done.
            This message comes from CodeAlta, not from the user.
            """;
}
