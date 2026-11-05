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

    /// <summary>Gets the sessions whose send waits.</summary>
    public IReadOnlyCollection<string> SessionIds
    {
        get
        {
            lock (_gate) return [.. Load().Values.Select(static send => send.SessionId).Distinct(StringComparer.OrdinalIgnoreCase)];
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
            if (!known.ContainsKey(send.HoldId) && known.Count >= MaximumKept) throw new FleetHeldSendsFullException(known.Count);
            var written = send with { Text = _protection.ProtectText(send.Text), Title = send.Title is { } title ? _protection.ProtectText(title) : null };
            Save(known.SetItem(send.HoldId, send), _written.SetItem(send.HoldId, written));
        }
    }

    /// <summary>Forgets a send by the run its hold is bound to (<see cref="FleetHeldSend.HoldId"/>): sent, stopped, or of a session that is gone.</summary>
    public void Remove(string holdId)
    {
        lock (_gate)
        {
            var sends = Load();
            if (!sends.ContainsKey(holdId)) return;
            try { Save(sends.Remove(holdId), _written.Remove(holdId)); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Taken again at the next start, where its retry key makes it a replay of what was sent.
            }
        }
    }

    private ImmutableDictionary<string, FleetHeldSend> Load()
    {
        if (_sends is { } known) return known;
        var sends = ImmutableDictionary.CreateBuilder<string, FleetHeldSend>(StringComparer.Ordinal);
        var written = ImmutableDictionary.CreateBuilder<string, FleetHeldSend>(StringComparer.Ordinal);
        try
        {
            var read = JsonSerializer.Deserialize(File.ReadAllBytes(_path), FleetHeldSendsJsonContext.Default.FleetHeldSendsDocument)?.Sends ?? [];
            foreach (var send in read.Where(static send => send is { RunId.Length: > 0, SessionId.Length: > 0, Text.Length: > 0, Nonce.Length: > 0 }))
            {
                FleetHeldSend opened;
                try
                {
                    opened = send with { Text = _protection.UnprotectText(send.Text), Title = send.Title is { } title ? _protection.UnprotectText(title) : null };
                }
                catch (Exception exception) when (exception is FormatException or InvalidDataException or CodeAlta.Catalog.Secrets.SecretStoreUnavailableException)
                {
                    // A send this machine cannot open (another key, a damaged entry) is not sent: what cannot be read cannot leak.
                    continue;
                }

                sends[send.HoldId] = opened;
                written[send.HoldId] = send;
            }
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException or JsonException)
        {
            // None held; a file of an older format, which kept prompts in clear, is replaced at the next send held.
        }

        _written = written.ToImmutable();
        return _sends = sends.ToImmutable();
    }

    private void Save(ImmutableDictionary<string, FleetHeldSend> sends, ImmutableDictionary<string, FleetHeldSend> written)
    {
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
internal sealed partial class FleetHeldSendsJsonContext : JsonSerializerContext;
