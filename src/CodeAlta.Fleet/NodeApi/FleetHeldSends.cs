using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeAlta.Fleet.NodeApi;

/// <summary>A send of the node API that a budget holds until its owner continues it: what is sent then, kept across restarts.</summary>
/// <param name="RunId">The retry key of the send, which the runtime knows it by.</param>
/// <param name="SessionId">The session.</param>
/// <param name="Text">The prompt.</param>
/// <param name="Title">What the run is, for the owner on this machine.</param>
/// <param name="Nonce">The nonce of the hold the server records, asked again with the same run after a restart.</param>
/// <param name="ProviderKey">The provider of the selection of the send; null for none.</param>
/// <param name="AgentPromptId">The agent prompt of the selection; null for none.</param>
/// <param name="ModelId">The model of the selection; null for the session's.</param>
/// <param name="ReasoningEffort">The reasoning effort of the selection; null for the session's.</param>
/// <param name="HeldAt">When it was held.</param>
internal sealed record FleetHeldSend(string RunId, string SessionId, string Text, string? Title, string Nonce, string? ProviderKey, string? AgentPromptId, string? ModelId,
    string? ReasoningEffort, DateTimeOffset HeldAt)
{
    /// <summary>Gets the run the budgets admit when it is not the retry key: the session, for the first prompt of a new session.</summary>
    public string? BudgetRunId { get; init; }

    /// <summary>Gets the run the hold is bound to, asked again with the same nonce after a restart.</summary>
    [JsonIgnore]
    public string HoldId => BudgetRunId ?? RunId;
}

/// <summary>
/// The sends a budget holds (<c>doc/fleet/observability.md</c>, budgets and alerts), kept in the fleet folder of the node before
/// the node answers that a send waits: a restart takes them again, and their sessions wait for a person meanwhile (they do not
/// move). One writer: the node service. A prompt may hold the value of a secret a person pasted: its text and its title are kept
/// protected as the secrets of the machine are (<see cref="CodeAlta.Catalog.Secrets.LocalSecretStore.ProtectText"/>), never in clear.
/// </summary>
internal sealed class FleetHeldSends
{
    /// <summary>The most sends held at once: one more is refused before the node answers that it waits.</summary>
    internal const int MaximumKept = 512;

    private readonly string _path;
    private readonly CodeAlta.Catalog.Secrets.LocalSecretStore _protection;
    private readonly Lock _gate = new();
    private ImmutableDictionary<string, FleetHeldSend>? _sends;
    // The sends as the file keeps them, their text and title protected: each one is protected once, when it is added.
    private ImmutableDictionary<string, FleetHeldSend> _written = ImmutableDictionary.Create<string, FleetHeldSend>(StringComparer.Ordinal);

    /// <summary>Initializes a new instance of the <see cref="FleetHeldSends"/> class.</summary>
    /// <param name="path">The file of the sends.</param>
    /// <param name="protection">The store of the machine, whose protection the texts of the sends are kept with.</param>
    public FleetHeldSends(string path, CodeAlta.Catalog.Secrets.LocalSecretStore protection)
    {
        _path = path;
        _protection = protection;
    }

    /// <summary>
    /// Gets how many sends are held that this machine cannot open now (its protected store is away, or holds another key): they stay
    /// in the file as they were written, their sessions wait, and each reading tries to open them again.
    /// </summary>
    public int Blocked
    {
        get
        {
            lock (_gate)
            {
                var opened = Load();
                return _written.Keys.Count(holdId => !opened.ContainsKey(holdId));
            }
        }
    }

    /// <summary>Gets the sessions whose send waits, those of the sends this machine cannot open now included.</summary>
    public IReadOnlyCollection<string> SessionIds
    {
        get
        {
            lock (_gate)
            {
                Load();
                return [.. _written.Values.Select(static send => send.SessionId).Distinct(StringComparer.OrdinalIgnoreCase)];
            }
        }
    }

    /// <summary>Lists the sends that wait.</summary>
    public IReadOnlyList<FleetHeldSend> List()
    {
        lock (_gate) return [.. Load().Values.OrderBy(static send => send.HeldAt)];
    }

    /// <summary>Keeps a send that waits, written before the node says it waits.</summary>
    /// <exception cref="IOException">It could not be written.</exception>
    /// <exception cref="CodeAlta.Catalog.Secrets.SecretStoreUnavailableException">The protected store of the machine cannot protect it.</exception>
    /// <exception cref="FleetHeldSendsFullException">The node holds <see cref="MaximumKept"/> sends already: none is evicted for it.</exception>
    public void Add(FleetHeldSend send)
    {
        lock (_gate)
        {
            var known = Load();
            // Every send held counts, those this machine cannot open now too.
            if (!_written.ContainsKey(send.HoldId) && _written.Count >= MaximumKept) throw new FleetHeldSendsFullException(_written.Count);
            Save(known.SetItem(send.HoldId, send), _written.SetItem(send.HoldId, Protect(send)));
        }
    }

    /// <summary>Forgets a send by the run its hold is bound to (<see cref="FleetHeldSend.HoldId"/>): sent, stopped, or of a session that is gone.</summary>
    public void Remove(string holdId)
    {
        lock (_gate)
        {
            var sends = Load();
            if (!sends.ContainsKey(holdId) && !_written.ContainsKey(holdId)) return;
            try { Save(sends.Remove(holdId), _written.Remove(holdId)); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Taken again at the next start, where its retry key makes it a replay of what was sent.
            }
        }
    }

    private ImmutableDictionary<string, FleetHeldSend> Load()
    {
        if (_sends is { } known) return _written.Count == known.Count ? known : _sends = OpenBlocked(known);
        var sends = ImmutableDictionary.CreateBuilder<string, FleetHeldSend>(StringComparer.Ordinal);
        var written = ImmutableDictionary.CreateBuilder<string, FleetHeldSend>(StringComparer.Ordinal);
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(_path);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            _written = written.ToImmutable();
            return _sends = sends.ToImmutable();
        }

        try
        {
            if (IsList(bytes))
            {
                // The file of an earlier version: its sends in clear. They are still held, protected now, and the file in clear is
                // replaced; a machine that cannot protect them yet keeps them held, and the file as it is, until it can.
                var legacy = JsonSerializer.Deserialize(bytes, FleetHeldSendsJsonContext.Default.ListFleetHeldSend) ?? [];
                foreach (var send in legacy.Where(static send => send is { RunId.Length: > 0, SessionId.Length: > 0, Text.Length: > 0, Nonce.Length: > 0 }))
                    sends[send.HoldId] = send;
                try
                {
                    foreach (var send in sends.Values) written[send.HoldId] = Protect(send);
                    Save(sends.ToImmutable(), written.ToImmutable());
                    return _sends!;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException
                    or CodeAlta.Catalog.Secrets.SecretStoreUnavailableException)
                {
                    _written = ImmutableDictionary.Create<string, FleetHeldSend>(StringComparer.Ordinal);
                    return _sends = sends.ToImmutable();
                }
            }

            var read = JsonSerializer.Deserialize(bytes, FleetHeldSendsJsonContext.Default.FleetHeldSendsDocument)?.Sends ?? [];
            foreach (var send in read.Where(static send => send is { RunId.Length: > 0, SessionId.Length: > 0, Text.Length: > 0, Nonce.Length: > 0 }))
            {
                // Kept in the file whether this machine opens it or not: a send it cannot open now (its protected store is away) is
                // not sent, and not lost either.
                written[send.HoldId] = send;
                try
                {
                    sends[send.HoldId] = send with { Text = _protection.UnprotectText(send.Text), Title = send.Title is { } title ? _protection.UnprotectText(title) : null };
                }
                catch (Exception exception) when (exception is FormatException or InvalidDataException or CodeAlta.Catalog.Secrets.SecretStoreUnavailableException)
                {
                    // What cannot be read is not sent, and cannot leak.
                }
            }
        }
        catch (JsonException)
        {
            // A file that does not read is kept aside for the owner of the machine, never overwritten by the next send held.
            try { File.Move(_path, _path + ".unreadable-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture), overwrite: false); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Left where it is: nothing is written over it until it can be moved.
                throw new IOException($"The sends held in '{_path}' do not read and could not be kept aside.", exception);
            }

            sends.Clear();
            written.Clear();
        }

        _written = written.ToImmutable();
        return _sends = sends.ToImmutable();
    }

    // Tries again to open the sends this machine could not open: those that open now are held as the others.
    private ImmutableDictionary<string, FleetHeldSend> OpenBlocked(ImmutableDictionary<string, FleetHeldSend> opened)
    {
        foreach (var (holdId, send) in _written)
        {
            if (opened.ContainsKey(holdId)) continue;
            try
            {
                opened = opened.SetItem(holdId, send with { Text = _protection.UnprotectText(send.Text), Title = send.Title is { } title ? _protection.UnprotectText(title) : null });
            }
            catch (Exception exception) when (exception is FormatException or InvalidDataException or CodeAlta.Catalog.Secrets.SecretStoreUnavailableException)
            {
                // Still blocked: tried again at the next reading.
            }
        }

        return opened;
    }

    // The protected form of a send, as the file keeps it.
    private FleetHeldSend Protect(FleetHeldSend send)
        => send with { Text = _protection.ProtectText(send.Text), Title = send.Title is { } title ? _protection.ProtectText(title) : null };

    // Whether a file is the list an earlier version wrote, not the document of this one.
    private static bool IsList(ReadOnlySpan<byte> bytes)
    {
        var text = bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) ? bytes[3..] : bytes;
        foreach (var b in text)
        {
            if (b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') continue;
            return b == (byte)'[';
        }

        return false;
    }

    private void Save(ImmutableDictionary<string, FleetHeldSend> sends, ImmutableDictionary<string, FleetHeldSend> written)
    {
        // A send held but not protected yet (taken from the file of an earlier version while the protected store was away) is
        // protected now: none is dropped from the file.
        foreach (var (holdId, send) in sends)
        {
            if (!written.ContainsKey(holdId)) written = written.SetItem(holdId, Protect(send));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var staging = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllBytes(staging, JsonSerializer.SerializeToUtf8Bytes(new FleetHeldSendsDocument { Sends = [.. written.Values] },
            FleetHeldSendsJsonContext.Default.FleetHeldSendsDocument));
        File.Move(staging, _path, overwrite: true);
        _sends = sends;
        _written = written;
    }
}

/// <summary>Thrown when the node holds as many sends as it keeps: the send is refused, none already held is evicted.</summary>
internal sealed class FleetHeldSendsFullException : InvalidOperationException
{
    public FleetHeldSendsFullException(int count)
        : base($"The node already holds {count} sends for their budgets: no more is held until one is continued or stopped.")
    {
    }
}

// The file of the sends: their text and title protected.
internal sealed class FleetHeldSendsDocument
{
    [JsonPropertyName("format")]
    public int Format { get; set; } = 2;

    [JsonPropertyName("sends")]
    public List<FleetHeldSend> Sends { get; set; } = [];
}

[JsonSerializable(typeof(FleetHeldSendsDocument))]
[JsonSerializable(typeof(List<FleetHeldSend>))]
internal sealed partial class FleetHeldSendsJsonContext : JsonSerializerContext;
