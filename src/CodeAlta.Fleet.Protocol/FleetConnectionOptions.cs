namespace CodeAlta.Fleet.Protocol;

/// <summary>The limits and timings of a <see cref="FleetConnection"/>.</summary>
public sealed class FleetConnectionOptions
{
    /// <summary>Gets the largest frame either side may send, in bytes.</summary>
    public int MaxFrameBytes { get; init; } = FleetConnectionPolicy.DefaultMaxFrameBytes;

    /// <summary>Gets the period of the application ping, or <see cref="Timeout.InfiniteTimeSpan"/> to send none.</summary>
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromMilliseconds(FleetConnectionPolicy.DefaultHeartbeatMilliseconds);

    /// <summary>
    /// Gets how long the connection waits without receiving anything before it closes with
    /// <see cref="FleetErrorCodes.PeerTimeout"/>, or <see cref="Timeout.InfiniteTimeSpan"/> to wait forever.
    /// </summary>
    public TimeSpan DeadPeerTimeout { get; init; } = TimeSpan.FromMilliseconds(3 * FleetConnectionPolicy.DefaultHeartbeatMilliseconds);

    /// <summary>Gets the time a request waits for its response unless it says otherwise, or <see cref="Timeout.InfiniteTimeSpan"/>.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets how long a clean close waits for the peer's close before it aborts the transport.</summary>
    public TimeSpan CloseTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Gets the number of frames waiting to be sent before a sender waits.</summary>
    public int SendQueueCapacity { get; init; } = 256;

    /// <summary>Gets the number of incoming requests handled at once before the reader waits.</summary>
    public int MaxConcurrentIncomingRequests { get; init; } = 64;

    /// <summary>Gets the clock of the heartbeat and the timeouts.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>Creates the options of a connection from the policy of its handshake.</summary>
    /// <param name="policy">The policy of the challenge or the welcome.</param>
    /// <returns>Options with the policy's frame limit, its heartbeat, and a dead-peer timeout of three heartbeats.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="policy"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The policy's frame limit or heartbeat is not positive.</exception>
    public static FleetConnectionOptions FromPolicy(FleetConnectionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(policy.MaxFrameBytes, nameof(policy));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(policy.HeartbeatMilliseconds, nameof(policy));
        var heartbeat = TimeSpan.FromMilliseconds(policy.HeartbeatMilliseconds);
        return new FleetConnectionOptions { MaxFrameBytes = policy.MaxFrameBytes, HeartbeatInterval = heartbeat, DeadPeerTimeout = heartbeat * 3 };
    }

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxFrameBytes, nameof(MaxFrameBytes));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(SendQueueCapacity, nameof(SendQueueCapacity));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxConcurrentIncomingRequests, nameof(MaxConcurrentIncomingRequests));
        ArgumentNullException.ThrowIfNull(TimeProvider, nameof(TimeProvider));
        RequirePositiveOrInfinite(HeartbeatInterval, nameof(HeartbeatInterval));
        RequirePositiveOrInfinite(DeadPeerTimeout, nameof(DeadPeerTimeout));
        RequirePositiveOrInfinite(RequestTimeout, nameof(RequestTimeout));
        RequirePositiveOrInfinite(CloseTimeout, nameof(CloseTimeout));
    }

    internal static void RequirePositiveOrInfinite(TimeSpan value, string name)
    {
        if (value != Timeout.InfiniteTimeSpan && value <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(name, value, "A time must be positive or infinite.");
    }
}

/// <summary>The options of one request.</summary>
public sealed class FleetRequestOptions
{
    /// <summary>Gets the idempotency key of a side-effecting request, or <see langword="null"/>.</summary>
    public string? IdempotencyKey { get; init; }

    /// <summary>Gets the W3C <c>traceparent</c>, or <see langword="null"/> for the one of the current activity.</summary>
    public string? TraceParent { get; init; }

    /// <summary>Gets the time the request waits for its response, or <see langword="null"/> for the connection's.</summary>
    public TimeSpan? Timeout { get; init; }
}
