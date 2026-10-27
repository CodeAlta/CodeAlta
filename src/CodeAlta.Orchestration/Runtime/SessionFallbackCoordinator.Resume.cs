using System.Globalization;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Catalog.ModelTiers;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>A resume a session waits for: the run that stopped on a limit goes on by itself at that time.</summary>
/// <param name="SessionId">The session.</param>
/// <param name="ResumeId">The identity of the resume, which its card and its actions name.</param>
/// <param name="ResumeAt">When the session resumes.</param>
/// <param name="ProviderKey">The provider whose limit resets first.</param>
/// <param name="ProviderName">The name of that provider.</param>
/// <param name="Attempt">How many times in a row the session resumes by itself, this one included.</param>
public sealed record SessionResumeSnapshot(string SessionId, string ResumeId, DateTimeOffset ResumeAt, string ProviderKey, string ProviderName, int Attempt);

/// <summary>
/// The resume after a limit: when the fallback finds no model of the tier to take over from a run that stopped on a
/// limit, and the settings ask for it, the session waits for the earliest reset among the providers of its tier, then
/// runs again through its tier in order, with the message of a fallback. A limit reached again schedules the next
/// resume, up to the number of resumes in a row the settings allow; a prompt sent to the session, or Cancel, ends the
/// wait. The parent of the session is told of the failure only when the wait ends without a resume.
/// </summary>
/// <remarks>
/// The schedule is the state of this coordinator, under its gate; the timers of the clock (<see cref="Time"/>) only
/// start the resume, which then runs as a fallback does, through the commands of the session. A resume is kept in the
/// <see cref="ResumeStore"/> while it waits, and armed again when the host starts (<see cref="RestoreResumesAsync"/>).
/// </remarks>
internal sealed partial class SessionFallbackCoordinator
{
    /// <summary>A reset is waited a little longer than the time the provider gives: its clock and ours differ.</summary>
    internal static readonly TimeSpan ResumeMargin = TimeSpan.FromSeconds(30);

    // A timer of the system cannot wait longer than about 49 days: a later resume is armed again when it fires early.
    private static readonly TimeSpan LongestTimer = TimeSpan.FromDays(30);

    private readonly Dictionary<string, PendingResume> _resumes = new(StringComparer.OrdinalIgnoreCase);
    // How many times in a row each session resumed by itself; a prompt of the user starts the count again.
    private readonly Dictionary<string, int> _resumesInARow = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets or sets where the resumes that wait are kept across a restart; none are kept when null.</summary>
    internal SessionResumeStore? ResumeStore { get; set; }

    /// <summary>Gets the resumes that wait.</summary>
    public IReadOnlyList<SessionResumeSnapshot> PendingResumes
    {
        get { lock (_gate) return [.. _resumes.Values.Select(static pending => pending.Snapshot)]; }
    }

    /// <summary>Gets the resume a session waits for.</summary>
    /// <param name="sessionId">The session.</param>
    /// <returns>The resume, or <see langword="null"/> when the session waits for none.</returns>
    public SessionResumeSnapshot? GetPendingResume(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        lock (_gate) return _resumes.TryGetValue(sessionId, out var pending) ? pending.Snapshot : null;
    }

    /// <summary>
    /// Cancels the resume a session waits for: its parent is told of the failure that stopped it, and its queue drains.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="resumeId">The resume, as its card names it.</param>
    /// <returns>Whether that resume was waiting.</returns>
    public async Task<bool> CancelResumeAsync(string sessionId, string resumeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(resumeId);
        if (Take(sessionId, resumeId, resetCount: true) is not { } pending) return false;
        await ForgetAsync(pending).ConfigureAwait(false);
        await WriteResumeCardAsync(pending, AgentSessionUpdateKind.Info, "Automatic resume cancelled.", "cancelled").ConfigureAwait(false);
        if (pending.Held is { } held) await held.ReleaseAsync().ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Resumes a session that waits for a resume at once, through its tier in order, whatever the limits CodeAlta knows
    /// of: the user may know better (credits bought, a limit reset earlier).
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="resumeId">The resume, as its card names it.</param>
    /// <returns>Whether that resume was waiting: it starts on its own.</returns>
    public bool ResumeNow(string sessionId, string resumeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(resumeId);
        return Fire(sessionId, resumeId, manual: true);
    }

    /// <summary>
    /// Tells the schedule that a prompt was admitted for a session: the session was given work, so the resume it waits
    /// for is cancelled, and a prompt of the user starts the count of the resumes in a row again. The prompts of the
    /// fallback and of the resume themselves change nothing.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="provenance">Who sent the prompt; <see langword="null"/> for the user.</param>
    internal void NoteSend(string sessionId, string? provenance)
    {
        if (string.Equals(provenance, AgentPromptProvenance.ModelFallback, StringComparison.Ordinal)) return;
        PendingResume? pending;
        lock (_gate)
        {
            if (provenance is null) _resumesInARow.Remove(sessionId);
            if (!_resumes.Remove(sessionId, out pending)) return;
            pending.Timer?.Dispose();
        }

        // The run of that prompt answers the parent, or fails on its own: the failure that stopped the session is not told.
        pending.Held?.Replace();
        Track(sessionId, async () =>
        {
            await ForgetAsync(pending).ConfigureAwait(false);
            await WriteResumeCardAsync(pending, AgentSessionUpdateKind.Info, "Automatic resume cancelled: the session was given a prompt.", "cancelled")
                .ConfigureAwait(false);
        });
    }

    /// <summary>Arms again the resumes the store kept from an earlier run of the host. A resume whose time passed starts now.</summary>
    /// <returns>A task that completes once they are armed.</returns>
    public async Task RestoreResumesAsync()
    {
        if (ResumeStore is not { } store || _closing.IsCancellationRequested) return;
        IReadOnlyList<SessionResumeRecord> records;
        try { records = await store.ReadAllAsync(_closing.Token).ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The host closes, or the database cannot be read: the resumes are lost, not the sessions.
            return;
        }

        var states = ProviderStates?.Invoke() ?? [];
        foreach (var record in records)
        {
            var pending = new PendingResume(record, DisplayName(record.ProviderKey, states), held: null);
            lock (_gate)
            {
                if (_closing.IsCancellationRequested) return;
                if (!_resumes.TryAdd(record.SessionId, pending)) continue;
                _resumesInARow[record.SessionId] = Math.Max(0, record.Attempt - 1);
            }

            Arm(pending);
        }
    }

    // Schedules the resume of a run that stopped on a limit with no model of its tier to take over, when the settings
    // ask for it. True when the run is settled here: a resume waits, or the resumes in a row are used up and the card
    // says so. The failure stays held while the resume waits.
    private async Task<bool> TryScheduleResumeAsync(RunFailureNotice notice, ModelTier tier, AgentFailureInfo failure,
        (string Provider, string Model, string Ref) from, IReadOnlyList<ModelProviderStateSnapshot> states, SessionRuntimeService.HeldRunFailure? held)
    {
        var fallback = notice.Scope.Fallback;
        if (!fallback.Resume || failure.Kind is not (AgentFailureKind.QuotaExhausted or AgentFailureKind.RateLimited)) return false;
        int attempt;
        lock (_gate)
        {
            if (_closing.IsCancellationRequested) return false;
            attempt = _resumesInARow.GetValueOrDefault(notice.SessionId) + 1;
        }

        var stopped = $"{Reason(from.Provider, from.Model, failure)}, and no other model of the {ModelTiers.NameOf(tier)} tier can take over.";
        if (attempt > fallback.MaxResumes)
        {
            var times = fallback.MaxResumes == 1 ? "once" : $"{fallback.MaxResumes} times";
            await WriteCardAsync(notice.SessionId, AgentSessionUpdateKind.Warning,
                $"{stopped} It resumed by itself {times} in a row: choose another model, or send again after the reset.",
                Details(notice, failure, from.Ref, to: null)).ConfigureAwait(false);
            if (held is not null) await held.ReleaseAsync().ConfigureAwait(false);
            return true;
        }

        var now = Time.GetUtcNow();
        var (reset, providerKey) = EarliestReset(notice, tier, states, now);
        var record = new SessionResumeRecord(notice.SessionId, Guid.NewGuid().ToString("N"), reset + ResumeMargin, providerKey, attempt, from.Ref,
            failure.Kind.ToString());
        var pending = new PendingResume(record, DisplayName(providerKey, states), held);
        PendingResume? replaced;
        lock (_gate)
        {
            if (_closing.IsCancellationRequested) return false;
            _resumes.Remove(notice.SessionId, out replaced);
            _resumes[notice.SessionId] = pending;
        }

        // A session waits for one resume: a later failure replaces the earlier one.
        if (replaced is not null)
        {
            replaced.Timer?.Dispose();
            replaced.Held?.Replace();
        }

        await WriteCardAsync(notice.SessionId, AgentSessionUpdateKind.Warning,
            $"{stopped} Resumes {When(record.ResumeAt)} with {pending.ProviderName}.",
            Details(notice, failure, from.Ref, to: null, writer => WriteResume(writer, pending))).ConfigureAwait(false);
        if (ResumeStore is { } store) await store.SaveAsync(record, CancellationToken.None).ConfigureAwait(false);
        Arm(pending);
        return true;
    }

    // The earliest time a provider of the tier that the session can take is no longer held for its limit, with that
    // provider; the provider of the failed run when no other one resets earlier.
    private (DateTimeOffset At, string ProviderKey) EarliestReset(RunFailureNotice notice, ModelTier tier, IReadOnlyList<ModelProviderStateSnapshot> states,
        DateTimeOffset now)
    {
        lock (_gate)
        {
            var best = (At: _exhaustedUntil.TryGetValue(notice.ProviderKey, out var own) && own > now ? own : now + UnknownResetHold, notice.ProviderKey);
            foreach (var candidate in ModelTierResolver.Candidates(notice.Scope.Tiers, tier))
            {
                var provider = candidate.Target.ProviderKey;
                if (!_exhaustedUntil.TryGetValue(provider, out var until) || until <= now || until >= best.At) continue;
                if (!CanTake(candidate.Target, notice, states)) continue;
                best = (until, provider);
            }

            return best;
        }
    }

    // The first target of the tier, in order, that the session can take now.
    private ModelTierCandidate? FindFirst(RunFailureNotice notice, ModelTier tier, IReadOnlyList<ModelProviderStateSnapshot> states, DateTimeOffset now)
    {
        foreach (var candidate in ModelTierResolver.Candidates(notice.Scope.Tiers, tier))
        {
            if (IsExhausted(candidate.Target.ProviderKey, now) || !CanTake(candidate.Target, notice, states)) continue;
            return candidate;
        }

        return null;
    }

    private void Arm(PendingResume pending)
    {
        lock (_gate)
        {
            if (_closing.IsCancellationRequested || !_resumes.TryGetValue(pending.Record.SessionId, out var current) || !ReferenceEquals(current, pending)) return;
            pending.Timer?.Dispose();
            var due = pending.Record.ResumeAt - Time.GetUtcNow();
            due = due < TimeSpan.Zero ? TimeSpan.Zero : due > LongestTimer ? LongestTimer : due;
            // The timer only starts the resume, never under the gate.
            pending.Timer = Time.CreateTimer(static state =>
            {
                var (owner, resume) = ((SessionFallbackCoordinator, PendingResume))state!;
                _ = Task.Run(() => owner.Fire(resume.Record.SessionId, resume.Record.ResumeId, manual: false));
            }, (this, pending), due, Timeout.InfiniteTimeSpan);
        }
    }

    // Starts a resume that waits; false when that resume no longer waits.
    private bool Fire(string sessionId, string resumeId, bool manual)
    {
        PendingResume? pending;
        lock (_gate)
        {
            if (!_resumes.TryGetValue(sessionId, out pending) || !string.Equals(pending.Record.ResumeId, resumeId, StringComparison.Ordinal)) return false;
            // A timer that fired before its time (a long wait, a clock that moved) is armed again.
            if (!manual && Time.GetUtcNow() < pending.Record.ResumeAt - TimeSpan.FromSeconds(1)) pending = null;
        }

        if (pending is null)
        {
            lock (_gate) pending = _resumes.GetValueOrDefault(sessionId);
            if (pending is not null) Arm(pending);
            return false;
        }

        if (Take(sessionId, resumeId, resetCount: manual) is not { } taken) return false;
        if (!manual)
        {
            lock (_gate) _resumesInARow[sessionId] = taken.Record.Attempt;
        }

        if (!Track(sessionId, () => RunResumeAsync(taken, manual)))
        {
            // The host closes: the store keeps the resume for its next start.
            return false;
        }

        return true;
    }

    // Removes the resume a session waits for, when it is the one named.
    private PendingResume? Take(string sessionId, string resumeId, bool resetCount)
    {
        lock (_gate)
        {
            if (!_resumes.TryGetValue(sessionId, out var pending) || !string.Equals(pending.Record.ResumeId, resumeId, StringComparison.Ordinal)) return null;
            _resumes.Remove(sessionId);
            pending.Timer?.Dispose();
            if (resetCount) _resumesInARow.Remove(sessionId);
            return pending;
        }
    }

    private async Task ForgetAsync(PendingResume pending)
    {
        if (ResumeStore is { } store) await store.RemoveAsync(pending.Record.SessionId, pending.Record.ResumeId, CancellationToken.None).ConfigureAwait(false);
    }

    // Runs the session again through its tier in order, as the fallback continues a run.
    private async Task RunResumeAsync(PendingResume pending, bool manual)
    {
        var sessionId = pending.Record.SessionId;
        var held = pending.Held;
        var failure = new AgentFailureInfo(Enum.TryParse<AgentFailureKind>(pending.Record.Reason, out var kind) ? kind : AgentFailureKind.QuotaExhausted);
        var states = ProviderStates?.Invoke() ?? [];
        (string Provider, string Model, string Ref) from = (pending.ProviderName, "its model", pending.Record.From);
        try
        {
            await ForgetAsync(pending).ConfigureAwait(false);
            var error = new AgentErrorEvent(new ModelProviderId(pending.Record.ProviderKey), sessionId, Time.GetUtcNow(), "The run stopped on a limit.", exception: (Exception?)null) { Failure = failure };
            RunFailureNotice? notice;
            try { notice = await _runtime.ReadResumeNoticeAsync(sessionId, error, _closing.Token).ConfigureAwait(false); }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
            {
                notice = null;
            }

            // A session that was deleted or archived meanwhile is not resumed.
            if (notice is null)
            {
                if (held is not null) await held.ReleaseAsync().ConfigureAwait(false);
                return;
            }

            from = Describe(notice.ProviderKey, notice.ModelId, notice.ReasoningEffort, states);
            var tier = notice.Tier ?? notice.Scope.Tiers.FindTierOf(notice.ProviderKey, notice.ModelId);
            if (tier is null)
            {
                await StopAsync("the session runs on no tier of models").ConfigureAwait(false);
                return;
            }

            if (!manual && notice.Scope.Fallback is not { Enabled: true, Resume: true })
            {
                await WriteResumeCardAsync(pending, AgentSessionUpdateKind.Info, "Automatic resume cancelled: it is turned off in the settings.", "cancelled")
                    .ConfigureAwait(false);
                if (held is not null) await held.ReleaseAsync().ConfigureAwait(false);
                return;
            }

            var now = Time.GetUtcNow();
            if (manual)
            {
                // The user asks for it now: the limits CodeAlta knows of the tier are tried again.
                lock (_gate)
                {
                    foreach (var candidate in ModelTierResolver.Candidates(notice.Scope.Tiers, tier.Value)) _exhaustedUntil.Remove(candidate.Target.ProviderKey);
                }
            }

            var next = FindFirst(notice, tier.Value, states, now);
            if (next is null)
            {
                // The limits did not reset yet: the next reset is waited for, as after a run that stopped on them.
                if (await TryScheduleResumeAsync(notice, tier.Value, failure, from, states, held).ConfigureAwait(false)) return;
                await StopAsync($"no model of the {ModelTiers.NameOf(tier.Value)} tier can take over").ConfigureAwait(false);
                return;
            }

            if (!await WaitForReleaseAsync(sessionId).ConfigureAwait(false))
            {
                await StopAsync("the session stayed busy").ConfigureAwait(false);
                return;
            }

            var to = ModelTierResolver.Describe(next, states);
            var reasoning = next.Reasoning;
            string? permissionMode = null;
            if (!string.Equals(next.Target.ProviderKey, notice.ProviderKey, StringComparison.OrdinalIgnoreCase))
            {
                permissionMode = KeepOrNarrow(notice, next.Target.ProviderKey);
                var switched = await SwitchProviderAsync(sessionId, next.Target.ProviderKey,
                    new SessionViewProviderCarryOver(next.Target.ModelId, reasoning, permissionMode)).ConfigureAwait(false);
                if (switched != "ok")
                {
                    await StopAsync($"the switch to {to.ProviderDisplayName} was refused ({switched})").ConfigureAwait(false);
                    return;
                }
            }

            await _runtime.SetSessionTierAsync(sessionId, tier, _closing.Token).ConfigureAwait(false);
            var toText = $"{to.ProviderDisplayName} {to.ModelDisplayName}{(reasoning is { } effort ? " " + CodeAltaConfigStore.FormatReasoningEffort(effort) : string.Empty)}";
            await WriteCardAsync(sessionId, AgentSessionUpdateKind.ProviderFallback,
                manual ? $"Resuming on {toText}, as asked." : $"The limit of {pending.ProviderName} has reset: resuming on {toText}.",
                Details(notice, failure, from.Ref, next.Format(), writer =>
                {
                    writer.WriteString("resumeId", pending.Record.ResumeId);
                    writer.WriteString("resume", manual ? "manual" : "automatic");
                })).ConfigureAwait(false);

            var admission = _commands.AdmitSend(new OwnedTextSendRequest($"resume:{sessionId}:{pending.Record.ResumeId}", sessionId, ResumePrompt(from, toText, manual))
            {
                Selection = new OwnedSessionSelection(next.Target.ProviderKey, notice.AgentPromptId ?? "default", next.Target.ModelId, reasoning)
                {
                    PermissionMode = permissionMode,
                },
                Provenance = AgentPromptProvenance.ModelFallback,
            });
            if (admission is not { Kind: OwnedSessionCommandAdmissionKind.Accepted, Receipt: { } receipt })
            {
                await StopAsync($"the resume was not admitted ({admission.Kind.ToString().ToLowerInvariant()})").ConfigureAwait(false);
                return;
            }

            var result = await receipt.Completion.ConfigureAwait(false);
            if (result.Outcome != OwnedSessionCommandOutcome.Completed && result.Code is "preparation_failed" or "permission_unavailable")
            {
                await StopAsync($"the resume could not start ({result.Code})").ConfigureAwait(false);
                return;
            }

            held?.Replace();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            await StopAsync(exception is OperationCanceledException or ObjectDisposedException ? "CodeAlta is closing" : exception.Message).ConfigureAwait(false);
        }

        async Task StopAsync(string why)
        {
            try
            {
                if (!_closing.IsCancellationRequested)
                {
                    await WriteResumeCardAsync(pending, AgentSessionUpdateKind.Warning,
                        $"The resume stopped: {why}. Choose another model, or send again later.", "stopped").ConfigureAwait(false);
                }
            }
            catch (Exception) { /* The card is not worth losing the failure for. */ }
            finally
            {
                if (held is not null) await held.ReleaseAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task WriteResumeCardAsync(PendingResume pending, AgentSessionUpdateKind kind, string message, string state)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("reason", pending.Record.Reason);
            writer.WriteString("from", pending.Record.From);
            writer.WriteString("resumeId", pending.Record.ResumeId);
            writer.WriteString("resume", state);
            writer.WriteEndObject();
        }

        try
        {
            await WriteCardAsync(pending.Record.SessionId, kind, message, JsonDocument.Parse(stream.ToArray()).RootElement.Clone()).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ObjectDisposedException or OperationCanceledException or IOException or InvalidOperationException)
        {
            // The session or the host went away: there is no timeline to tell.
        }
    }

    // What the card of a scheduled resume adds to the details of the failure: the frontends offer Cancel and Resume now.
    private static void WriteResume(Utf8JsonWriter writer, PendingResume pending)
    {
        writer.WriteString("resumeId", pending.Record.ResumeId);
        writer.WriteString("resume", "scheduled");
        writer.WriteString("resumeAt", pending.Record.ResumeAt.ToString("O", CultureInfo.InvariantCulture));
        writer.WriteString("resumeProvider", pending.Record.ProviderKey);
        writer.WriteString("resumeProviderName", pending.ProviderName);
        writer.WriteNumber("resumeAttempt", pending.Record.Attempt);
    }

    // When a resume happens, as a card says it, in the local time of the user.
    private string When(DateTimeOffset at)
    {
        var local = TimeZoneInfo.ConvertTime(at, Time.LocalTimeZone);
        var days = (local.Date - TimeZoneInfo.ConvertTime(Time.GetUtcNow(), Time.LocalTimeZone).Date).Days;
        var time = local.ToString("HH:mm", CultureInfo.InvariantCulture);
        return days switch
        {
            <= 0 => $"at {time}",
            1 => $"tomorrow at {time}",
            < 7 => $"on {local.ToString("dddd", CultureInfo.InvariantCulture)} at {time}",
            _ => $"on {local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} at {time}",
        };
    }

    private string DisplayName(string providerKey, IReadOnlyList<ModelProviderStateSnapshot> states)
        => states.FirstOrDefault(state => string.Equals(state.ProviderId.Value, providerKey, StringComparison.OrdinalIgnoreCase))?.Descriptor.DisplayName
           ?? _commands.SelectionProvider?.Invoke(new ModelProviderId(providerKey))?.DisplayName
           ?? providerKey;

    // What the card says when a session that stopped on a limit does not resume by itself.
    private static string NoResumeAdvice(AgentFailureInfo failure)
        => failure.ResetsAt is not null ? "Choose another model, or send again after the reset." : "Choose another model, or send again later.";

    private static string ResumePrompt((string Provider, string Model, string Ref) from, string to, bool manual)
        => $"""
            [CodeAlta resume]
            The last run stopped on {from.Provider} {from.Model} on a limit. {(manual ? "The user asked CodeAlta to resume it" : "The limit has reset")}: CodeAlta resumes this session on {to} to finish it.
            {ContinuationInstructions}
            """;

    // A resume that waits. Its timer and its held failure are owned by the coordinator, under its gate.
    private sealed class PendingResume(SessionResumeRecord record, string providerName, SessionRuntimeService.HeldRunFailure? held)
    {
        public SessionResumeRecord Record { get; } = record;
        public string ProviderName { get; } = providerName;

        /// <summary>The failure held back from the parent while the resume waits; none after a restart.</summary>
        public SessionRuntimeService.HeldRunFailure? Held { get; } = held;

        public ITimer? Timer { get; set; }

        public SessionResumeSnapshot Snapshot => new(Record.SessionId, Record.ResumeId, Record.ResumeAt, Record.ProviderKey, ProviderName, Record.Attempt);
    }
}
