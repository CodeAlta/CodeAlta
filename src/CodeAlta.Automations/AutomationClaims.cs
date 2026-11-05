using System.Text.Json;
using System.Text.Json.Serialization;

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
            var staged = _path + ".tmp";
            using (var stream = File.Create(staged)) JsonSerializer.Serialize(stream, _file, AutomationClaimJsonContext.Default.ClaimFile);
            File.Move(staged, _path, overwrite: true);
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
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AutomationClaimStore.ClaimFile))]
internal sealed partial class AutomationClaimJsonContext : JsonSerializerContext;
