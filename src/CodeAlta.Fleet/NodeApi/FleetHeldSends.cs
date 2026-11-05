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
/// move). One writer: the node service.
/// </summary>
internal sealed class FleetHeldSends
{
    private const int MaximumKept = 512;

    private readonly string _path;
    private readonly Lock _gate = new();
    private ImmutableDictionary<string, FleetHeldSend>? _sends;

    public FleetHeldSends(string path) => _path = path;

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
    public void Add(FleetHeldSend send)
    {
        lock (_gate)
        {
            var sends = Load().SetItem(send.HoldId, send);
            while (sends.Count > MaximumKept) sends = sends.Remove(sends.Values.MinBy(static held => held.HeldAt)!.HoldId);
            Save(sends);
        }
    }

    /// <summary>Forgets a send by the run its hold is bound to (<see cref="FleetHeldSend.HoldId"/>): sent, stopped, or of a session that is gone.</summary>
    public void Remove(string holdId)
    {
        lock (_gate)
        {
            var sends = Load();
            if (!sends.ContainsKey(holdId)) return;
            try { Save(sends.Remove(holdId)); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Taken again at the next start, where its retry key makes it a replay of what was sent.
            }
        }
    }

    private ImmutableDictionary<string, FleetHeldSend> Load()
    {
        if (_sends is { } known) return known;
        try
        {
            var read = JsonSerializer.Deserialize(File.ReadAllBytes(_path), FleetHeldSendsJsonContext.Default.ListFleetHeldSend) ?? [];
            known = read.Where(static send => send is { RunId.Length: > 0, SessionId.Length: > 0, Text.Length: > 0, Nonce.Length: > 0 })
                .GroupBy(static send => send.HoldId, StringComparer.Ordinal).ToImmutableDictionary(static group => group.Key, static group => group.Last(), StringComparer.Ordinal);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException or JsonException)
        {
            known = ImmutableDictionary.Create<string, FleetHeldSend>(StringComparer.Ordinal);
        }

        return _sends = known;
    }

    private void Save(ImmutableDictionary<string, FleetHeldSend> sends)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var staging = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllBytes(staging, JsonSerializer.SerializeToUtf8Bytes(sends.Values.ToList(), FleetHeldSendsJsonContext.Default.ListFleetHeldSend));
        File.Move(staging, _path, overwrite: true);
        _sends = sends;
    }
}

[JsonSerializable(typeof(List<FleetHeldSend>))]
internal sealed partial class FleetHeldSendsJsonContext : JsonSerializerContext;
