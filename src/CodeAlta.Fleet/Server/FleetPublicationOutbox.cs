using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;

namespace CodeAlta.Fleet.Server;

/// <summary>
/// The outbox of one server link (<c>doc/fleet/protocol.md</c>, durability): the version of the facts of each session
/// (a new one each time its facts change), what the server acknowledged, the batch in flight, and the sequences of the
/// facts and inventory streams. It is kept in <c>fleet/servers/&lt;link&gt;/outbox.json</c>, so a restart resumes rather
/// than republishes. One writer: the loop of its <see cref="FleetConnector"/>.
/// </summary>
internal sealed class FleetPublicationOutbox
{
    private const int FileVersion = 1;

    private readonly string _path;
    private FleetOutboxFile _state;

    private FleetPublicationOutbox(string path, FleetOutboxFile state)
    {
        _path = path;
        _state = state;
    }

    /// <summary>Gets the cursors this outbox believes the server holds, sent in the hello.</summary>
    public FleetPublicationCursors Cursors => new(_state.FactsAcked, _state.InventoryAcked);

    /// <summary>Gets the last sequence of a batch of facts.</summary>
    public long FactsSequence => _state.FactsSequence;

    /// <summary>Gets whether a batch of facts waits for its acknowledgement.</summary>
    public bool HasInFlight => _state.InFlight is not null;

    /// <summary>Loads the outbox of a link, or starts an empty one; a file that does not read starts empty, so that everything is published again.</summary>
    public static FleetPublicationOutbox Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        FleetOutboxFile? state = null;
        try
        {
            state = JsonSerializer.Deserialize(File.ReadAllBytes(path), FleetServerJsonFiles.Default.FleetOutboxFile);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException or JsonException)
        {
            // Nothing kept: the server holds what it holds, and versions make a republication a no-op.
        }

        if (state is not { Version: FileVersion }) state = new FleetOutboxFile { Version = FileVersion };
        state.Sessions ??= new(StringComparer.Ordinal);
        return new FleetPublicationOutbox(path, state);
    }

    /// <summary>
    /// Takes the cursors of the welcome: a batch in flight that the server holds is acknowledged; a server behind what it
    /// acknowledged before (a restore) gets everything again; the sequences go on after the server's.
    /// </summary>
    public void Resume(FleetPublicationCursors? cursors)
    {
        var server = cursors ?? new FleetPublicationCursors(0, 0);
        if (_state.InFlight is { } batch && server.Facts >= batch.Sequence) Acknowledge(batch);
        _state.InFlight = null;
        if (server.Facts < _state.FactsAcked)
        {
            foreach (var session in _state.Sessions.Values) session.AckedVersion = 0;
        }

        _state.FactsAcked = server.Facts;
        _state.FactsSequence = Math.Max(_state.FactsSequence, server.Facts);
        if (server.Inventory < _state.InventoryAcked) _state.InventoryHash = null;
        _state.InventoryAcked = server.Inventory;
        _state.InventorySequence = Math.Max(_state.InventorySequence, server.Inventory);
        Save();
    }

    /// <summary>
    /// Records the sessions to publish as they are now, a new version for each whose facts changed, and gives the facts the
    /// server does not hold yet, at most <paramref name="limit"/>, the oldest changes first. With a metadata key, their
    /// titles are sealed into the facts: a version follows the title and the key, never the random bytes of a seal.
    /// </summary>
    public IReadOnlyList<FleetSessionFact> Pending(IReadOnlyList<FleetLocalSession> sessions, int limit, FleetMetadataKey? key = null)
    {
        var pending = new List<(FleetSessionFact Fact, DateTimeOffset UpdatedAt)>();
        var changed = false;
        foreach (var session in sessions)
        {
            var fact = new FleetSessionFact(session.SessionId, session.Epoch, 0, session.State, session.CreatedAt, session.UpdatedAt, session.ProviderType, session.Model,
                session.Counts);
            var title = key is null ? null : session.Title;
            var hash = Hash(fact, title is null ? null : $"{key!.Id}\n{title}");
            if (!_state.Sessions.TryGetValue(session.SessionId, out var kept))
            {
                _state.Sessions[session.SessionId] = kept = new FleetOutboxSession { Epoch = session.Epoch };
            }

            if (kept.Withdraw)
            {
                // Published again before its withdrawal was answered: the server may have deleted it, so it is sent again.
                kept.Withdraw = false;
                kept.AckedVersion = 0;
                changed = true;
            }

            if (kept.Hash != hash || kept.Epoch != session.Epoch)
            {
                kept.Version++;
                kept.Epoch = session.Epoch;
                kept.Hash = hash;
                changed = true;
            }

            if (kept.Version > kept.AckedVersion)
            {
                pending.Add((fact with { Version = kept.Version, SealedMetadata = title is null ? null : key!.Seal(session.SessionId, new FleetSessionMetadata(title)) },
                    session.UpdatedAt));
            }
        }

        if (changed) Save();
        return [.. pending.OrderBy(static item => item.UpdatedAt).Take(limit).Select(static item => item.Fact)];
    }

    /// <summary>Makes the next batch of facts and records it in flight.</summary>
    public FleetFactsPublish Batch(IReadOnlyList<FleetSessionFact> facts)
    {
        var sequence = ++_state.FactsSequence;
        _state.InFlight = new FleetOutboxBatch { Sequence = sequence, Versions = facts.ToDictionary(static fact => fact.SessionId, static fact => fact.Version, StringComparer.Ordinal) };
        Save();
        return new FleetFactsPublish(sequence, facts);
    }

    /// <summary>
    /// Takes the answer of a batch: its facts are held (or refused as stale, which a retry would not change), and the
    /// cursor of the server moves on.
    /// </summary>
    public void Acknowledged(FleetFactsPublishResult result)
    {
        if (_state.InFlight is { } batch && result.Ack >= batch.Sequence) Acknowledge(batch);
        _state.InFlight = null;
        _state.FactsAcked = Math.Max(_state.FactsAcked, result.Ack);
        Save();
    }

    /// <summary>Gives the publication of an inventory when it changed since the server acknowledged one, or null.</summary>
    public (FleetInventoryPublish Publication, string Hash)? Inventory(FleetMachineInventory inventory)
    {
        var hash = Base64Url.EncodeToString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(inventory, FleetServerJsonContext.Default.FleetMachineInventory)));
        if (hash == _state.InventoryHash) return null;
        var sequence = ++_state.InventorySequence;
        Save();
        return (new FleetInventoryPublish(sequence, inventory), hash);
    }

    /// <summary>Takes the acknowledgement of an inventory.</summary>
    public void InventoryAcknowledged(long ack, long sequence, string hash)
    {
        _state.InventoryAcked = Math.Max(_state.InventoryAcked, ack);
        if (ack >= sequence) _state.InventoryHash = hash;
        Save();
    }

    /// <summary>Forgets the sessions that are no longer published, so the outbox does not grow with the sessions that were; a withdrawal waiting stays.</summary>
    public void Retain(IReadOnlyCollection<string> sessionIds)
    {
        if (_state.Sessions.Count <= sessionIds.Count * 2 + 64) return;
        var keep = sessionIds.ToHashSet(StringComparer.Ordinal);
        foreach (var id in _state.Sessions.Where(pair => !pair.Value.Withdraw && !keep.Contains(pair.Key)).Select(static pair => pair.Key).ToArray()) _state.Sessions.Remove(id);
        Save();
    }

    /// <summary>
    /// Records that sessions of the instance are no longer published (made Private, their space unlinked): those this
    /// outbox published are to be withdrawn. Gives the withdrawals waiting, at most <paramref name="limit"/>.
    /// </summary>
    public IReadOnlyList<string> Withdrawals(IEnumerable<string> unpublished, int limit)
    {
        var changed = false;
        foreach (var id in unpublished)
        {
            if (!_state.Sessions.TryGetValue(id, out var kept) || kept.Withdraw) continue;
            kept.Withdraw = true;
            changed = true;
        }

        if (changed) Save();
        return [.. _state.Sessions.Where(static pair => pair.Value.Withdraw).Select(static pair => pair.Key).Order(StringComparer.Ordinal).Take(limit)];
    }

    /// <summary>Takes the answer of a withdrawal: the sessions are forgotten, so a later publication starts their versions again.</summary>
    public void Withdrawn(IReadOnlyList<string> sessionIds)
    {
        var changed = false;
        foreach (var id in sessionIds)
        {
            if (_state.Sessions.TryGetValue(id, out var kept) && kept.Withdraw) changed |= _state.Sessions.Remove(id);
        }

        if (changed) Save();
    }

    private void Acknowledge(FleetOutboxBatch batch)
    {
        foreach (var (id, version) in batch.Versions)
        {
            if (_state.Sessions.TryGetValue(id, out var kept)) kept.AckedVersion = Math.Max(kept.AckedVersion, version);
        }
    }

    // The hash of the facts, and of the sealed metadata in clear when there is some; the outbox keeps the hash, never the title.
    private static string Hash(FleetSessionFact fact, string? sealedInClear)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(fact, FleetServerJsonContext.Default.FleetSessionFact);
        if (sealedInClear is not null) bytes = [.. bytes, (byte)'\n', .. System.Text.Encoding.UTF8.GetBytes(sealedInClear)];
        return Base64Url.EncodeToString(SHA256.HashData(bytes));
    }

    private void Save() => FleetFiles.WriteAtomically(_path, JsonSerializer.SerializeToUtf8Bytes(_state, FleetServerJsonFiles.Default.FleetOutboxFile));
}
