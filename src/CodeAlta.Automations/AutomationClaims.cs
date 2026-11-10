using System.Text.Json;
using System.Text.Json.Serialization;
using CodeAlta.Core.IO;

namespace CodeAlta.Automations;

/// <summary>
/// Claims the occurrences of automations, so that each one runs once (<c>doc/fleet/orchestration.md</c>, "Placement and
/// dispatch"): one run per (automation, due time), whoever fires it and however often.
/// </summary>
internal interface IAutomationClaims
{
    /// <summary>Claims an occurrence for a run of this host, with a lease.</summary>
    /// <param name="automationId">The automation.</param>
    /// <param name="occurrence">What is due: a schedule's trigger and due time.</param>
    /// <param name="runId">The run that would take it.</param>
    /// <param name="now">The time of the claim.</param>
    /// <returns>Whether the run may start: false when the occurrence was already claimed, by this host or another.</returns>
    bool TryClaim(string automationId, string occurrence, string runId, DateTimeOffset now);

    /// <summary>Extends the leases of the runs of this host still in progress.</summary>
    /// <param name="now">The time of the renewal.</param>
    void Renew(DateTimeOffset now);

    /// <summary>Records that the run of a claim ended: its lease is no longer renewed, and the claim stays.</summary>
    /// <param name="runId">The run.</param>
    /// <param name="now">The time it ended.</param>
    void Release(string runId, DateTimeOffset now);

    /// <summary>
    /// Confirms a claim with whoever else must know of it before its run starts (the server of an automation it gave),
    /// once the claim of this host was taken.
    /// </summary>
    /// <param name="automationId">The automation.</param>
    /// <param name="occurrence">What is due.</param>
    /// <param name="runId">The run that took it.</param>
    /// <param name="cancellationToken">Cancels the confirmation.</param>
    /// <returns>Why the run does not start (another run took that time elsewhere); null when it starts.</returns>
    ValueTask<string?> ConfirmAsync(string automationId, string occurrence, string runId, CancellationToken cancellationToken) => ValueTask.FromResult<string?>(null);

    /// <summary>
    /// Whether the runs of an automation are shared by the machines of a pool (<c>runs_on = { tags = [...] }</c>): each due time
    /// and each event runs only once the server granted its claim to this machine, and one another machine of the pool took is
    /// not recorded here.
    /// </summary>
    /// <param name="automationId">The automation.</param>
    /// <returns>Whether it is pooled.</returns>
    bool IsPooled(string automationId) => false;

    /// <summary>
    /// Claims an event of a pooled automation, here and at the server, before its run starts and before the event is taken as
    /// seen: an event this machine cannot claim now (the server did not answer, or the machine lets the others of its pool look
    /// first) is looked at again later.
    /// </summary>
    /// <param name="automationId">The automation.</param>
    /// <param name="occurrence">The event, as <c>EventOccurrence</c> names it.</param>
    /// <param name="runId">The run that would take it, unless an earlier claim of it waits for an answer: that run claims again.</param>
    /// <param name="now">The time of the claim.</param>
    /// <param name="cancellationToken">Cancels the claim.</param>
    /// <returns>Whether the run starts, another machine runs the event, or it is looked at again later; and the run that claimed.</returns>
    ValueTask<AutomationEventClaim> ClaimEventAsync(string automationId, string occurrence, string runId, DateTimeOffset now, CancellationToken cancellationToken)
        => ValueTask.FromResult(new AutomationEventClaim(TryClaim(automationId, occurrence, runId, now) ? AutomationPoolClaim.Granted : AutomationPoolClaim.Taken, runId));

    /// <summary>
    /// Keeps the claim of a run granted for an event that did not start after all (its automation became busy, or the service
    /// paused): the event is looked at again, and the same run claims it again, which the server grants again to it.
    /// </summary>
    /// <param name="runId">The run.</param>
    void Hold(string runId)
    {
    }
}

/// <summary>What the claim of an event of a pooled automation gave, and the run that claimed it.</summary>
/// <param name="Outcome">Whether the run starts, another machine runs the event, or it is looked at again later.</param>
/// <param name="RunId">The run that claimed: the one asked, or the one of an earlier claim whose answer was lost.</param>
internal readonly record struct AutomationEventClaim(AutomationPoolClaim Outcome, string RunId);

/// <summary>What the claim of an event of a pooled automation gave.</summary>
internal enum AutomationPoolClaim
{
    /// <summary>This machine runs it.</summary>
    Granted,

    /// <summary>Another run took it, here or on another machine: it is seen, and runs no more.</summary>
    Taken,

    /// <summary>It is looked at again later: the server did not answer, or the machine lets the others of its pool look first.</summary>
    Later,
}

/// <summary>The answers of a claim that the service reads.</summary>
internal static class AutomationClaimAnswers
{
    /// <summary>
    /// What <see cref="IAutomationClaims.ConfirmAsync"/> answers when another machine of the pool of the automation took the
    /// time: the run is not recorded on this machine.
    /// </summary>
    internal const string TakenInPool = "Another machine of its pool runs it.";
}

/// <summary>
/// The claims of one host, kept in a file of its state root. A claim is never taken again, even once its lease has
/// expired: runs are at most once, since an agent may have changed things already. The lease tells whether the host
/// that holds a claim is still at work on it (a later coordinator reads it before it gives the occurrence elsewhere).
/// </summary>
internal sealed class AutomationClaimStore : IAutomationClaims
{
    /// <summary>How long a lease lasts without a renewal.</summary>
    internal static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);

    /// <summary>How long a claim is kept, so that a due time missed and caught up later is still known.</summary>
    internal static readonly TimeSpan KeptFor = TimeSpan.FromDays(14);

    /// <summary>The most claims kept.</summary>
    internal const int MaximumClaims = 4096;

    private static readonly TimeSpan RenewEvery = TimeSpan.FromMinutes(1);
    private const int MaximumFileBytes = 4 * 1024 * 1024;

    private readonly Lock _gate = new();
    private readonly string _path;
    private readonly string _holder;
    private readonly ClaimFile _file;
    private DateTimeOffset _renewedAt;

    /// <summary>Opens the claims kept in a file; a file that is missing or cannot be read starts with none.</summary>
    /// <param name="path">The file.</param>
    /// <param name="holder">Who claims: the id of this host.</param>
    internal AutomationClaimStore(string path, string holder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(holder);
        (_path, _holder) = (path, holder);
        _file = Load(path) ?? new ClaimFile();
    }

    /// <summary>The claim of an occurrence, or null.</summary>
    internal AutomationClaim? Find(string automationId, string occurrence)
    {
        lock (_gate) return _file.Claims.GetValueOrDefault(Key(automationId, occurrence));
    }

    /// <inheritdoc />
    public bool TryClaim(string automationId, string occurrence, string runId, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(automationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(occurrence);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        lock (_gate)
        {
            var key = Key(automationId, occurrence);
            if (_file.Claims.ContainsKey(key)) return false;
            Prune(now);
            _file.Claims[key] = new AutomationClaim(automationId, occurrence, runId, _holder, now, now + LeaseDuration);
            Save();
            return true;
        }
    }

    /// <inheritdoc />
    public void Renew(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (now - _renewedAt < RenewEvery && now >= _renewedAt) return;
            _renewedAt = now;
            var changed = false;
            foreach (var (key, claim) in _file.Claims.ToArray())
            {
                if (claim.EndedAt is not null || claim.Holder != _holder) continue;
                _file.Claims[key] = claim with { LeaseUntil = now + LeaseDuration };
                changed = true;
            }

            if (changed) Save();
        }
    }

    /// <inheritdoc />
    public void Release(string runId, DateTimeOffset now)
    {
        lock (_gate)
        {
            foreach (var (key, claim) in _file.Claims.ToArray())
            {
                if (claim.RunId != runId || claim.EndedAt is not null) continue;
                _file.Claims[key] = claim with { EndedAt = now, LeaseUntil = now };
                Save();
                return;
            }
        }
    }

    /// <inheritdoc />
    public void Hold(string runId) => MarkPending(runId, true);

    /// <summary>
    /// Marks the claim of a run as waiting for an answer, or no longer: a pending claim is the one its occurrence is claimed again
    /// with (<see cref="Find"/>), so that a grant whose answer was lost is asked again by the same run, never by another.
    /// </summary>
    /// <param name="runId">The run.</param>
    /// <param name="pending">Whether it waits.</param>
    internal void MarkPending(string runId, bool pending)
    {
        lock (_gate)
        {
            foreach (var (key, claim) in _file.Claims.ToArray())
            {
                if (claim.RunId != runId || claim.Pending == pending) continue;
                _file.Claims[key] = claim with { Pending = pending };
                Save();
                return;
            }
        }
    }

    // Under the gate: forgets the claims older than they are kept, then the oldest ones beyond the most kept.
    private void Prune(DateTimeOffset now)
    {
        foreach (var (key, claim) in _file.Claims.ToArray())
        {
            if (claim.EndedAt is not null && now - claim.ClaimedAt > KeptFor) _file.Claims.Remove(key);
        }

        if (_file.Claims.Count < MaximumClaims) return;
        foreach (var (key, _) in _file.Claims.Where(static pair => pair.Value.EndedAt is not null).OrderBy(static pair => pair.Value.ClaimedAt)
            .Take(_file.Claims.Count - MaximumClaims + 1).ToArray())
            _file.Claims.Remove(key);
    }

    private static string Key(string automationId, string occurrence) => automationId + "|" + occurrence;

    private static ClaimFile? Load(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > MaximumFileBytes) return null;
            using var stream = file.OpenRead();
            if (JsonSerializer.Deserialize(stream, AutomationClaimJsonContext.Default.ClaimFile) is not { Version: 1 } read) return null;
            read.Claims = (read.Claims ?? []).Where(static pair => pair.Value is { AutomationId.Length: > 0, Occurrence.Length: > 0, RunId.Length: > 0, Holder.Length: > 0 })
                .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
            return read;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    // Under the gate. A claim that cannot be written still holds for this run of the host.
    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            SafeFile.WriteAllBytes(_path, JsonSerializer.SerializeToUtf8Bytes(_file, AutomationClaimJsonContext.Default.ClaimFile));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    internal sealed class ClaimFile
    {
        public int Version { get; set; } = 1;

        public Dictionary<string, AutomationClaim> Claims { get; set; } = new(StringComparer.Ordinal);
    }
}

/// <summary>One occurrence of an automation claimed by a host.</summary>
/// <param name="AutomationId">The automation.</param>
/// <param name="Occurrence">What was due.</param>
/// <param name="RunId">The run that took it.</param>
/// <param name="Holder">The host that claimed it.</param>
/// <param name="ClaimedAt">When it was claimed.</param>
/// <param name="LeaseUntil">Until when the holder is known to be at work on it.</param>
internal sealed record AutomationClaim(string AutomationId, string Occurrence, string RunId, string Holder, DateTimeOffset ClaimedAt, DateTimeOffset LeaseUntil)
{
    /// <summary>When the run ended; null while it is in progress (or its host stopped before it ended).</summary>
    public DateTimeOffset? EndedAt { get; init; }

    /// <summary>Whether the claim waits for an answer of a server that did not give one: its occurrence is claimed again by this run.</summary>
    public bool Pending { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AutomationClaimStore.ClaimFile))]
internal sealed partial class AutomationClaimJsonContext : JsonSerializerContext;
