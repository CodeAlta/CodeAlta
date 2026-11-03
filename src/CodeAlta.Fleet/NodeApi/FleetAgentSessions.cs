using System.Text.Json;
using System.Text.Json.Serialization;
using CodeAlta.Fleet.Identity;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Fleet.NodeApi;

/// <summary>A session an agent of another machine started on this node: its lineage, and what it runs within.</summary>
/// <param name="ChildSessionId">The session on this node.</param>
/// <param name="ParentSessionId">The session of the agent, on the other machine.</param>
/// <param name="ParentDeviceId">The device that signed the request, which takes what the session says to its parent.</param>
/// <param name="ParentMachineName">The name of the machine of the parent.</param>
/// <param name="SpaceId">The space of the session.</param>
/// <param name="DisabledTools">The tools, or patterns over tool names, the session does without.</param>
/// <param name="Nonce">The nonce of the request that started it, never accepted again.</param>
/// <param name="CreatedAt">When it was started.</param>
public sealed record FleetAgentChild(string ChildSessionId, string ParentSessionId, string ParentDeviceId, string ParentMachineName, string SpaceId,
    IReadOnlyList<string> DisabledTools, string Nonce, DateTimeOffset CreatedAt);

/// <summary>
/// What a node keeps of the sessions agents of other machines start on it (<c>doc/fleet/sessions-across-machines.md</c>,
/// "Starting a session on another machine"): the policy of its owner, read again at each call; the lineage of each such
/// session, which keeps its restrictions across restarts and the nonce of its request; and the outbox of what those
/// sessions say to their parents, kept until the device of each parent takes and acknowledges it. A rate limit per device
/// and the nonces of prompts are held in memory.
/// </summary>
public sealed class FleetAgentSessions : IRemoteParentNotifications
{
    /// <summary>The most deliveries the outbox keeps; the oldest go first.</summary>
    public const int MaxDeliveries = 1000;

    /// <summary>The longest text of a delivery; a longer one is cut.</summary>
    public const int MaxDeliveryText = 64 * 1024;

    /// <summary>The most sessions the lineage keeps; the oldest go first.</summary>
    public const int MaxChildren = 2000;

    /// <summary>The most starts of one device in <see cref="RateWindow"/>.</summary>
    public const int MaxStartsPerWindow = 20;

    /// <summary>The most prompts of agents of one device in <see cref="RateWindow"/>.</summary>
    public const int MaxPromptsPerWindow = 120;

    /// <summary>The window of the rate limits.</summary>
    public static readonly TimeSpan RateWindow = TimeSpan.FromMinutes(10);

    private readonly Lock _gate = new();
    private readonly string _policyPath;
    private readonly string _statePath;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, Queue<DateTimeOffset>> _starts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<DateTimeOffset>> _prompts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _promptNonces = new(StringComparer.Ordinal);
    private FleetAgentState _state;

    /// <summary>Opens what the node keeps in its fleet folder.</summary>
    /// <param name="fleetRoot">The fleet folder of the node.</param>
    /// <param name="time">The clock; null for the system's.</param>
    /// <exception cref="ArgumentException"><paramref name="fleetRoot"/> is empty.</exception>
    public FleetAgentSessions(string fleetRoot, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fleetRoot);
        var folder = Path.Combine(fleetRoot, "agents");
        _policyPath = Path.Combine(folder, "policy.json");
        _statePath = Path.Combine(folder, "sessions.json");
        _time = time ?? TimeProvider.System;
        _state = Read(_statePath, FleetAgentJsonContext.Default.FleetAgentState) ?? new FleetAgentState([], []);
    }

    /// <summary>Gets the sessions agents of other machines started here, the oldest first.</summary>
    public IReadOnlyList<FleetAgentChild> Children { get { lock (_gate) return _state.Children; } }

    /// <summary>Reads the policy of the owner: an unreadable or missing file allows nothing.</summary>
    /// <returns>The policy.</returns>
    public FleetAgentPolicyInfo ReadPolicy()
    {
        var policy = Read(_policyPath, FleetAgentJsonContext.Default.FleetAgentPolicyInfo);
        return policy is null ? new FleetAgentPolicyInfo([]) : policy with { Spaces = [.. (policy.Spaces ?? []).Where(static space => !string.IsNullOrWhiteSpace(space))] };
    }

    /// <summary>Writes the policy of the owner.</summary>
    /// <param name="policy">The policy.</param>
    /// <exception cref="ArgumentNullException"><paramref name="policy"/> is null.</exception>
    /// <exception cref="IOException">The file could not be written.</exception>
    public void WritePolicy(FleetAgentPolicyInfo policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var normalized = policy with
        {
            Spaces = [.. policy.Spaces.Select(static space => space.Trim()).Where(static space => space.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)],
            Sandbox = string.IsNullOrWhiteSpace(policy.Sandbox) ? null : policy.Sandbox.Trim(),
        };
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_policyPath)!);
            FleetFiles.WriteAtomically(_policyPath, JsonSerializer.SerializeToUtf8Bytes(normalized, FleetAgentJsonContext.Default.FleetAgentPolicyInfo));
        }
    }

    /// <summary>Finds the lineage of a session.</summary>
    /// <param name="sessionId">The session.</param>
    /// <returns>Its lineage, or null for a session no agent of another machine started.</returns>
    public FleetAgentChild? Find(string sessionId)
    {
        lock (_gate) return _state.Children.FirstOrDefault(child => string.Equals(child.ChildSessionId, sessionId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Counts one start of a device against its rate limit.</summary>
    /// <param name="deviceId">The device.</param>
    /// <returns>Whether it is within the limit.</returns>
    public bool TryAdmitStart(string deviceId) => TryAdmit(_starts, deviceId, MaxStartsPerWindow);

    /// <summary>Counts one prompt of an agent of a device against its rate limit.</summary>
    /// <param name="deviceId">The device.</param>
    /// <returns>Whether it is within the limit.</returns>
    public bool TryAdmitPrompt(string deviceId) => TryAdmit(_prompts, deviceId, MaxPromptsPerWindow);

    /// <summary>Takes the nonce of a request once: a nonce a start or a prompt used already is refused.</summary>
    /// <param name="nonce">The nonce.</param>
    /// <returns>Whether it was never used.</returns>
    public bool TryUseNonce(string nonce)
    {
        ArgumentException.ThrowIfNullOrEmpty(nonce);
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (_state.Children.Any(child => child.Nonce == nonce)) return false;
            // A request is good for its skew either way: a nonce is kept twice as long, then cannot pass the time check.
            foreach (var expired in _promptNonces.Where(pair => pair.Value < now).Select(static pair => pair.Key).ToArray()) _promptNonces.Remove(expired);
            return _promptNonces.TryAdd(nonce, now + 2 * Protocol.FleetAgentRequestDocuments.MaxSkew);
        }
    }

    /// <summary>Records the lineage of a session an agent of another machine started.</summary>
    /// <param name="child">The lineage.</param>
    /// <exception cref="ArgumentNullException"><paramref name="child"/> is null.</exception>
    /// <exception cref="IOException">The record could not be written.</exception>
    public void Add(FleetAgentChild child)
    {
        ArgumentNullException.ThrowIfNull(child);
        lock (_gate)
        {
            var children = _state.Children.Append(child).ToList();
            if (children.Count > MaxChildren) children.RemoveRange(0, children.Count - MaxChildren);
            Save(_state with { Children = children });
        }
    }

    /// <inheritdoc />
    public Task EnqueueAsync(RemoteParentNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        lock (_gate)
        {
            // Only to the device that started the session, for the parent it named.
            var child = _state.Children.FirstOrDefault(value => string.Equals(value.ChildSessionId, notification.ChildSessionId, StringComparison.OrdinalIgnoreCase));
            if (child is null || !string.Equals(child.ParentSessionId, notification.ParentSessionId, StringComparison.OrdinalIgnoreCase)) return Task.CompletedTask;
            var body = notification.Body.Length > MaxDeliveryText ? notification.Body[..MaxDeliveryText] : notification.Body;
            var delivery = new FleetAgentStoredDelivery(child.ParentDeviceId, new FleetAgentDelivery(Guid.NewGuid().ToString("N"), child.ChildSessionId,
                notification.ChildProjectId, child.ParentSessionId, notification.Kind, body, notification.RunId, notification.ContentId, _time.GetUtcNow()));
            var deliveries = _state.Deliveries.Append(delivery).ToList();
            if (deliveries.Count > MaxDeliveries) deliveries.RemoveRange(0, deliveries.Count - MaxDeliveries);
            Save(_state with { Deliveries = deliveries });
        }

        return Task.CompletedTask;
    }

    /// <summary>Takes the deliveries of a device, oldest first, without forgetting them.</summary>
    /// <param name="deviceId">The device.</param>
    /// <param name="limit">The most to take.</param>
    /// <returns>The deliveries.</returns>
    public IReadOnlyList<FleetAgentDelivery> Take(string deviceId, int limit)
    {
        lock (_gate)
            return [.. _state.Deliveries.Where(value => value.DeviceId == deviceId).Take(Math.Clamp(limit, 1, 200)).Select(static value => value.Delivery)];
    }

    /// <summary>Forgets deliveries a device took and delivered.</summary>
    /// <param name="deviceId">The device.</param>
    /// <param name="ids">The deliveries.</param>
    /// <returns>How many were forgotten.</returns>
    /// <exception cref="IOException">The outbox could not be written.</exception>
    public int Acknowledge(string deviceId, IReadOnlyCollection<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        lock (_gate)
        {
            var set = ids.ToHashSet(StringComparer.Ordinal);
            var kept = _state.Deliveries.Where(value => value.DeviceId != deviceId || !set.Contains(value.Delivery.Id)).ToList();
            var removed = _state.Deliveries.Count - kept.Count;
            if (removed > 0) Save(_state with { Deliveries = kept });
            return removed;
        }
    }

    private bool TryAdmit(Dictionary<string, Queue<DateTimeOffset>> counts, string deviceId, int limit)
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (!counts.TryGetValue(deviceId, out var times)) counts[deviceId] = times = new Queue<DateTimeOffset>();
            while (times.Count > 0 && now - times.Peek() > RateWindow) times.Dequeue();
            if (times.Count >= limit) return false;
            times.Enqueue(now);
            return true;
        }
    }

    // Written before it is used: what a restart reads is what was kept.
    private void Save(FleetAgentState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
        FleetFiles.WriteAtomically(_statePath, JsonSerializer.SerializeToUtf8Bytes(state, FleetAgentJsonContext.Default.FleetAgentState));
        _state = state;
    }

    private static T? Read<T>(string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) where T : class
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllBytes(path), type) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}

/// <summary>A delivery and the device it is kept for.</summary>
/// <param name="DeviceId">The device that takes it.</param>
/// <param name="Delivery">The delivery.</param>
internal sealed record FleetAgentStoredDelivery(string DeviceId, FleetAgentDelivery Delivery);

/// <summary>What <see cref="FleetAgentSessions"/> writes.</summary>
/// <param name="Children">The lineage of the sessions.</param>
/// <param name="Deliveries">The outbox.</param>
internal sealed record FleetAgentState(IReadOnlyList<FleetAgentChild> Children, IReadOnlyList<FleetAgentStoredDelivery> Deliveries);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, WriteIndented = true)]
[JsonSerializable(typeof(FleetAgentState))]
[JsonSerializable(typeof(FleetAgentPolicyInfo))]
internal sealed partial class FleetAgentJsonContext : JsonSerializerContext;
