using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;

namespace CodeAlta.Fleet.Server;

/// <summary>
/// The outbox of the statistics facts of one server link (<c>doc/fleet/statistics.md</c>, publication): for each session, the
/// revision of its facts last read, the hash and the version of its set (a new version each time its rows change), what the
/// server acknowledged; the batch in flight, the sequences and the generation. Kept in
/// <c>fleet/servers/&lt;link&gt;/statistics.json</c>, so a restart resumes rather than republishes; an outbox that is lost
/// starts a new generation, and the server replaces everything this machine published before. One writer: the loop of its
/// <see cref="FleetConnector"/>.
/// </summary>
internal sealed class FleetStatisticsOutbox
{
    private const int FileVersion = 1;

    private readonly string _path;
    private readonly FleetStatisticsOutboxFile _state;
    private bool _dirty;

    private FleetStatisticsOutbox(string path, FleetStatisticsOutboxFile state)
    {
        _path = path;
        _state = state;
    }

    /// <summary>Gets the generation of the publications of this outbox.</summary>
    public string Generation => _state.Generation!;

    /// <summary>Gets the last sequence the server acknowledged, sent in the hello.</summary>
    public long Acked => _state.Acked;

    /// <summary>Gets the number of sessions the outbox knows.</summary>
    public int Count => _state.Sessions.Count;

    /// <summary>Loads the outbox of a link, or starts an empty one with a new generation; a file that does not read starts a new one too.</summary>
    /// <param name="path">The file.</param>
    /// <returns>The outbox.</returns>
    public static FleetStatisticsOutbox Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        FleetStatisticsOutboxFile? state = null;
        try
        {
            state = JsonSerializer.Deserialize(File.ReadAllBytes(path), FleetStatisticsOutboxJson.Default.FleetStatisticsOutboxFile);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException or JsonException)
        {
            // Nothing kept: a new generation, which the server takes as the replacement of what this machine published.
        }

        if (state is not { Version: FileVersion } || string.IsNullOrEmpty(state.Generation))
        {
            state = new FleetStatisticsOutboxFile { Version = FileVersion, Generation = Guid.NewGuid().ToString("N") };
        }

        state.Sessions ??= new(StringComparer.Ordinal);
        var outbox = new FleetStatisticsOutbox(path, state);
        return outbox;
    }

    /// <summary>
    /// Takes the cursor of the welcome: a batch in flight that the server holds is acknowledged; a server behind what it
    /// acknowledged before (a restore) gets every set again.
    /// </summary>
    /// <param name="serverAck">The last sequence the server holds of this device.</param>
    public void Resume(long serverAck)
    {
        if (_state.InFlight is { } batch && serverAck >= batch.Sequence) Acknowledge(batch);
        _state.InFlight = null;
        if (serverAck < _state.Acked)
        {
            foreach (var session in _state.Sessions.Values) session.Acked = 0;
        }

        _state.Acked = serverAck;
        _state.Sequence = Math.Max(_state.Sequence, serverAck);
        Save();
    }

    /// <summary>Gets whether the rows of a session must be read: its facts, its epoch or its project changed, or its set waits for the server.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="revision">The revision of its facts.</param>
    /// <param name="epoch">Its epoch.</param>
    /// <param name="projectKey">The key of its project.</param>
    /// <returns>Whether to read its rows.</returns>
    public bool NeedsRows(string sessionId, string revision, long epoch, string? projectKey)
        => !_state.Sessions.TryGetValue(sessionId, out var kept) || kept.Withdraw || kept.Version > kept.Acked || kept.Revision != revision || kept.Epoch != epoch
            || kept.ProjectKey != projectKey;

    /// <summary>
    /// Records the rows of a session as they are now: a new version when they changed. Gives the set to send when the server
    /// does not hold it yet, or null. A session that never had a row publishes nothing; one that had rows and has none
    /// sends an empty set, which takes its rows back.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="revision">The revision of its facts the rows were read at.</param>
    /// <param name="epoch">Its epoch.</param>
    /// <param name="projectKey">The key of its project.</param>
    /// <param name="rows">Its rows.</param>
    /// <returns>The set to send, or null.</returns>
    public FleetStatisticsSet? Record(string sessionId, string revision, long epoch, string? projectKey, IReadOnlyList<FleetStatisticsRow> rows)
    {
        if (!_state.Sessions.TryGetValue(sessionId, out var kept))
        {
            _state.Sessions[sessionId] = kept = new FleetStatisticsOutboxSession();
        }

        if (kept.Withdraw)
        {
            // Published again before its withdrawal was answered: the server may have deleted it, so it is sent again.
            kept.Withdraw = false;
            kept.Acked = 0;
        }

        var set = new FleetStatisticsSet(sessionId, epoch, 0, projectKey, rows);
        var hash = Hash(set);
        if (kept.Hash != hash)
        {
            if (kept.Version > 0 || rows.Count > 0) kept.Version++;
            kept.Hash = hash;
        }

        kept.Revision = revision;
        kept.Epoch = epoch;
        kept.ProjectKey = projectKey;
        _dirty = true;
        return kept.Version > kept.Acked ? set with { Version = kept.Version } : null;
    }

    /// <summary>Makes the next batch of sets and records it in flight.</summary>
    /// <param name="sets">The sets.</param>
    /// <returns>The publication.</returns>
    public FleetStatisticsPublish Batch(IReadOnlyList<FleetStatisticsSet> sets)
    {
        var sequence = ++_state.Sequence;
        _state.InFlight = new FleetStatisticsOutboxBatch { Sequence = sequence, Versions = sets.ToDictionary(static set => set.SessionId, static set => set.Version, StringComparer.Ordinal) };
        Save();
        return new FleetStatisticsPublish(sequence, Generation, sets);
    }

    /// <summary>Takes the answer of a batch: its sets are held (or refused as stale, which a retry would not change).</summary>
    /// <param name="result">The answer.</param>
    public void Acknowledged(FleetStatisticsPublishResult result)
    {
        if (_state.InFlight is { } batch && result.Ack >= batch.Sequence) Acknowledge(batch);
        _state.InFlight = null;
        _state.Acked = Math.Max(_state.Acked, result.Ack);
        Save();
    }

    /// <summary>
    /// Records that sessions are no longer published (made Private, their space unlinked): those whose set the server may
    /// hold are to be withdrawn, the others forgotten. Gives the withdrawals waiting, at most <paramref name="limit"/>.
    /// </summary>
    /// <param name="unpublished">The sessions of the instance not published.</param>
    /// <param name="limit">The most withdrawals.</param>
    /// <returns>The sessions to withdraw.</returns>
    public IReadOnlyList<string> Withdrawals(IEnumerable<string> unpublished, int limit)
    {
        foreach (var id in unpublished)
        {
            if (!_state.Sessions.TryGetValue(id, out var kept) || kept.Withdraw) continue;
            if (kept.Version == 0) _state.Sessions.Remove(id);
            else kept.Withdraw = true;
            _dirty = true;
        }

        SaveIfChanged();
        return [.. _state.Sessions.Where(static pair => pair.Value.Withdraw).Select(static pair => pair.Key).Order(StringComparer.Ordinal).Take(limit)];
    }

    /// <summary>Takes the answer of a withdrawal: the sessions are forgotten, so a later publication starts their versions again.</summary>
    /// <param name="sessionIds">The sessions withdrawn.</param>
    public void Withdrawn(IReadOnlyList<string> sessionIds)
    {
        foreach (var id in sessionIds)
        {
            if (_state.Sessions.TryGetValue(id, out var kept) && kept.Withdraw) _dirty |= _state.Sessions.Remove(id);
        }

        SaveIfChanged();
    }

    /// <summary>Forgets the sessions gone from the instance, so the outbox does not grow with the sessions that were; a withdrawal waiting stays.</summary>
    /// <param name="sessionIds">The sessions of the instance now.</param>
    public void Retain(IReadOnlyCollection<string> sessionIds)
    {
        if (_state.Sessions.Count <= sessionIds.Count * 2 + 64) return;
        var keep = sessionIds.ToHashSet(StringComparer.Ordinal);
        foreach (var id in _state.Sessions.Where(pair => !pair.Value.Withdraw && !keep.Contains(pair.Key)).Select(static pair => pair.Key).ToArray()) _state.Sessions.Remove(id);
        _dirty = true;
        SaveIfChanged();
    }

    /// <summary>Writes the outbox when something changed since it was last written.</summary>
    public void SaveIfChanged()
    {
        if (_dirty) Save();
    }

    private void Acknowledge(FleetStatisticsOutboxBatch batch)
    {
        foreach (var (id, version) in batch.Versions)
        {
            if (_state.Sessions.TryGetValue(id, out var kept)) kept.Acked = Math.Max(kept.Acked, version);
        }
    }

    // The hash of the set without its version: the epoch, the project key and the rows, in the order the store gives them.
    private static string Hash(FleetStatisticsSet set)
        => Base64Url.EncodeToString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(set, FleetStatisticsJsonContext.Default.FleetStatisticsSet)));

    private void Save()
    {
        FleetFiles.WriteAtomically(_path, JsonSerializer.SerializeToUtf8Bytes(_state, FleetStatisticsOutboxJson.Default.FleetStatisticsOutboxFile));
        _dirty = false;
    }
}

/// <summary>The file of a <see cref="FleetStatisticsOutbox"/>.</summary>
internal sealed class FleetStatisticsOutboxFile
{
    public int Version { get; set; }

    public string? Generation { get; set; }

    public long Sequence { get; set; }

    public long Acked { get; set; }

    public FleetStatisticsOutboxBatch? InFlight { get; set; }

    public Dictionary<string, FleetStatisticsOutboxSession> Sessions { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>A batch of statistics sets the server has not answered yet.</summary>
internal sealed class FleetStatisticsOutboxBatch
{
    public long Sequence { get; set; }

    public Dictionary<string, long> Versions { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>What the statistics outbox knows of one session.</summary>
internal sealed class FleetStatisticsOutboxSession
{
    public string? Revision { get; set; }

    public long Epoch { get; set; }

    public string? ProjectKey { get; set; }

    public long Version { get; set; }

    public long Acked { get; set; }

    public string? Hash { get; set; }

    /// <summary>Whether the session is no longer published and its set waits to be withdrawn from the server.</summary>
    public bool Withdraw { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(FleetStatisticsOutboxFile))]
internal sealed partial class FleetStatisticsOutboxJson : JsonSerializerContext;
