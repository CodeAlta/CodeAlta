using System.Net;
using System.Net.Sockets;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Server.Services;
using CodeAlta.Server.Storage;
using Microsoft.AspNetCore.Http;

namespace CodeAlta.Server.Identity;

/// <summary>How many requests a key may make in a window.</summary>
/// <param name="Permits">The requests a window admits.</param>
/// <param name="Window">The length of a window.</param>
public sealed record RateLimit(int Permits, TimeSpan Window);

/// <summary>The actions whose requests are limited, before the server knows the caller's device.</summary>
public static class RateLimitedActions
{
    /// <summary>The setup of a server with its one-time code.</summary>
    public const string Setup = "setup";

    /// <summary>The methods of sign-in.</summary>
    public const string SignInMethods = "sign_in.methods";

    /// <summary>The start of a sign-in.</summary>
    public const string SignInStart = "sign_in.start";

    /// <summary>The completion of a sign-in with the code the browser brought back.</summary>
    public const string SignInComplete = "sign_in.complete";

    /// <summary>A poll of a sign-in by device code.</summary>
    public const string SignInPoll = "sign_in.poll";

    /// <summary>A step of the passkey ceremony of the server's page.</summary>
    public const string Passkey = "sign_in.passkey";

    /// <summary>An enrollment with an enrollment token or the grant of a sign-in.</summary>
    public const string Enroll = "enroll";

    /// <summary>A recovery with a recovery key.</summary>
    public const string Recover = "recover";

    /// <summary>An event pushed to the ingress of a hook.</summary>
    public const string HookIngress = "hook.ingress";
}

/// <summary>
/// The limits of the requests the server takes from devices it does not know yet (<c>doc/fleet/security-and-privacy.md</c>,
/// rate limits): per address of the peer, and per person where a request names one before it is proved.
/// </summary>
public sealed class AuthenticationRateLimits
{
    /// <summary>Gets the limit of setup attempts per address.</summary>
    public RateLimit Setup { get; init; } = new(10, TimeSpan.FromMinutes(1));

    /// <summary>Gets the limit of reads of the sign-in methods per address.</summary>
    public RateLimit SignInMethods { get; init; } = new(60, TimeSpan.FromMinutes(1));

    /// <summary>Gets the limit of sign-in starts per address.</summary>
    public RateLimit SignInStart { get; init; } = new(20, TimeSpan.FromMinutes(1));

    /// <summary>Gets the limit of sign-in completions per address.</summary>
    public RateLimit SignInComplete { get; init; } = new(30, TimeSpan.FromMinutes(1));

    /// <summary>Gets the limit of device-code polls per address (GitHub asks for one every 5 seconds per sign-in).</summary>
    public RateLimit SignInPoll { get; init; } = new(60, TimeSpan.FromMinutes(1));

    /// <summary>Gets the limit of the steps of passkey ceremonies (options and assertions) per address.</summary>
    public RateLimit Passkey { get; init; } = new(30, TimeSpan.FromMinutes(1));

    /// <summary>Gets the limit of enrollments (enrollment tokens, grants of sign-ins) per address.</summary>
    public RateLimit Enroll { get; init; } = new(20, TimeSpan.FromMinutes(1));

    /// <summary>Gets the limit of recovery attempts per address.</summary>
    public RateLimit Recover { get; init; } = new(10, TimeSpan.FromMinutes(1));

    /// <summary>Gets the limit of recovery attempts per person (the person of the recovery key named).</summary>
    public RateLimit RecoverPerPerson { get; init; } = new(5, TimeSpan.FromMinutes(15));

    /// <summary>Gets the limit of events pushed to the hooks' ingress per address (each hook also takes 60 a minute at most).</summary>
    public RateLimit HookIngress { get; init; } = new(600, TimeSpan.FromMinutes(1));

    /// <summary>
    /// Gets the slow path of a person whose limit is reached: one request is still admitted per this interval, so that a
    /// flood in the name of a person (the owner of the server among others) slows them down and never locks them out.
    /// </summary>
    public TimeSpan PersonSlowPath { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Gets how many keys are counted at once; past it, the oldest windows are dropped first.</summary>
    public int MaxKeys { get; init; } = 50_000;

    /// <summary>Gets the limit of an action per address.</summary>
    /// <param name="action">The action (<see cref="RateLimitedActions"/>).</param>
    /// <returns>The limit.</returns>
    /// <exception cref="ArgumentException">The action is not limited.</exception>
    public RateLimit ForAddress(string action) => action switch
    {
        RateLimitedActions.Setup => Setup,
        RateLimitedActions.SignInMethods => SignInMethods,
        RateLimitedActions.SignInStart => SignInStart,
        RateLimitedActions.SignInComplete => SignInComplete,
        RateLimitedActions.SignInPoll => SignInPoll,
        RateLimitedActions.Passkey => Passkey,
        RateLimitedActions.Enroll => Enroll,
        RateLimitedActions.Recover => Recover,
        RateLimitedActions.HookIngress => HookIngress,
        _ => throw new ArgumentException($"'{action}' is not a limited action.", nameof(action)),
    };

    /// <summary>Gets the limit of an action per person.</summary>
    /// <param name="action">The action (<see cref="RateLimitedActions"/>).</param>
    /// <returns>The limit.</returns>
    /// <exception cref="ArgumentException">The action is not limited per person.</exception>
    public RateLimit ForPerson(string action) => action switch
    {
        RateLimitedActions.Recover => RecoverPerPerson,
        _ => throw new ArgumentException($"'{action}' is not limited per person.", nameof(action)),
    };
}

/// <summary>What the limiter decided for a request.</summary>
/// <param name="Allowed">Whether the request goes on.</param>
/// <param name="RetryAfter">When a refused request may be sent again.</param>
/// <param name="FirstRefusal">Whether this is the first refusal of its window, the one the audit log records.</param>
public readonly record struct RateDecision(bool Allowed, TimeSpan RetryAfter, bool FirstRefusal);

/// <summary>The metadata of an endpoint whose requests are limited per address before anything else reads them.</summary>
/// <param name="action">The action (<see cref="RateLimitedActions"/>).</param>
internal sealed class RateLimitedAttribute(string action) : Attribute
{
    public string Action { get; } = action;
}

/// <summary>
/// Counts the requests of each address and person in fixed windows, in memory: a restart forgets them. An IPv6 address
/// counts by its /64, which one machine usually holds whole. A window ends by itself, so no address or person is refused
/// for longer than one window; a person also has a slow path (<see cref="AuthenticationRateLimits.PersonSlowPath"/>).
/// One instance per server; no state is static.
/// </summary>
public sealed class AuthenticationRateLimiter
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;

    /// <summary>Initializes a new instance of the <see cref="AuthenticationRateLimiter"/> class.</summary>
    /// <param name="limits">The limits.</param>
    /// <param name="time">The clock.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public AuthenticationRateLimiter(AuthenticationRateLimits limits, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(time);
        Limits = limits;
        _time = time;
    }

    /// <summary>Gets the limits.</summary>
    public AuthenticationRateLimits Limits { get; }

    /// <summary>Counts a request of an address.</summary>
    /// <param name="action">The action.</param>
    /// <param name="address">The address of the peer; null counts as one unknown address.</param>
    /// <returns>Whether it goes on.</returns>
    public RateDecision TryAcquireAddress(string action, IPAddress? address)
        => Acquire($"{action}|a|{AddressKey(address)}", Limits.ForAddress(action), slowPath: null);

    /// <summary>Counts a request in the name of a person.</summary>
    /// <param name="action">The action.</param>
    /// <param name="personId">The person.</param>
    /// <returns>Whether it goes on.</returns>
    /// <exception cref="ArgumentException"><paramref name="personId"/> is null or empty.</exception>
    public RateDecision TryAcquirePerson(string action, string personId)
    {
        ArgumentException.ThrowIfNullOrEmpty(personId);
        return Acquire($"{action}|p|{personId}", Limits.ForPerson(action), Limits.PersonSlowPath);
    }

    /// <summary>Counts a request of an address and refuses it past the limit, auditing the first refusal of each window.</summary>
    /// <param name="action">The action.</param>
    /// <param name="origin">Where the request comes from.</param>
    /// <param name="store">The audit log.</param>
    /// <returns>A task that completes when the request may go on.</returns>
    /// <exception cref="CoordinatorException">The address made too many requests (429, with when to retry).</exception>
    public Task RequireAddressAsync(string action, RequestOrigin origin, ICoordinatorStore store)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(store);
        var decision = TryAcquireAddress(action, origin.ClientAddress);
        return decision.Allowed ? Task.CompletedTask : RefuseAsync(action, decision, origin, store, null);
    }

    /// <summary>Counts a request in the name of a person and refuses it past the limit, auditing the first refusal of each window.</summary>
    /// <param name="action">The action.</param>
    /// <param name="personId">The person.</param>
    /// <param name="origin">Where the request comes from.</param>
    /// <param name="store">The audit log.</param>
    /// <returns>A task that completes when the request may go on.</returns>
    /// <exception cref="CoordinatorException">Too many requests named the person (429, with when to retry).</exception>
    public Task RequirePersonAsync(string action, string personId, RequestOrigin origin, ICoordinatorStore store)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(store);
        var decision = TryAcquirePerson(action, personId);
        return decision.Allowed ? Task.CompletedTask : RefuseAsync(action, decision, origin, store, personId);
    }

    /// <summary>Gets the key an address counts by: itself for IPv4, its /64 for IPv6.</summary>
    /// <param name="address">The address, or null.</param>
    /// <returns>The key.</returns>
    public static string AddressKey(IPAddress? address)
    {
        if (address is null) return "unknown";
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetworkV6 || IPAddress.IsLoopback(address)) return address.ToString();
        Span<byte> bytes = stackalloc byte[16];
        address.TryWriteBytes(bytes, out _);
        bytes[8..].Clear();
        return new IPAddress(bytes).ToString() + "/64";
    }

    private async Task RefuseAsync(string action, RateDecision decision, RequestOrigin origin, ICoordinatorStore store, string? personId)
    {
        // One record per key and window: a flood must not grow the audit log by its own size.
        if (decision.FirstRefusal)
        {
            await store.AppendAuditAsync(new AuditEntry(action, CoordinatorErrorCodes.RateLimited, personId, null, personId is null ? "address" : "person",
                origin.SourceAddress, origin.RequestId), CancellationToken.None).ConfigureAwait(false);
        }

        var seconds = Math.Max(1, (int)Math.Ceiling(decision.RetryAfter.TotalSeconds));
        throw new CoordinatorException(StatusCodes.Status429TooManyRequests, CoordinatorErrorCodes.RateLimited,
            $"Too many attempts: try again in {seconds} s.", FleetErrorHints.Retry) { RetryAfterSeconds = seconds };
    }

    private RateDecision Acquire(string key, RateLimit limit, TimeSpan? slowPath)
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (!_buckets.TryGetValue(key, out var bucket) || now >= bucket.Start + limit.Window)
            {
                if (bucket is null && _buckets.Count >= Math.Max(1, Limits.MaxKeys)) Sweep(now);
                bucket = new Bucket { Start = now };
                _buckets[key] = bucket;
            }

            if (bucket.Count < limit.Permits)
            {
                bucket.Count++;
                bucket.LastAdmitted = now;
                return new RateDecision(true, TimeSpan.Zero, false);
            }

            if (slowPath is { } interval && now - bucket.LastAdmitted >= interval)
            {
                bucket.LastAdmitted = now;
                return new RateDecision(true, TimeSpan.Zero, false);
            }

            var first = !bucket.Refused;
            bucket.Refused = true;
            var retry = bucket.Start + limit.Window - now;
            if (slowPath is { } path && bucket.LastAdmitted + path - now < retry) retry = bucket.LastAdmitted + path - now;
            return new RateDecision(false, retry, first);
        }
    }

    // Drops the windows that ended; when every window is still running, the oldest half goes.
    private void Sweep(DateTimeOffset now)
    {
        var longest = new[] { Limits.Setup, Limits.SignInMethods, Limits.SignInStart, Limits.SignInComplete, Limits.SignInPoll, Limits.Passkey, Limits.Enroll, Limits.Recover,
            Limits.RecoverPerPerson, Limits.HookIngress }.Max(static limit => limit.Window);
        foreach (var pair in _buckets.Where(pair => now >= pair.Value.Start + longest).ToArray()) _buckets.Remove(pair.Key);
        if (_buckets.Count < Limits.MaxKeys) return;
        foreach (var pair in _buckets.OrderBy(static pair => pair.Value.Start).Take(_buckets.Count / 2).ToArray()) _buckets.Remove(pair.Key);
    }

    private sealed class Bucket
    {
        public DateTimeOffset Start { get; init; }

        public int Count { get; set; }

        public DateTimeOffset LastAdmitted { get; set; }

        public bool Refused { get; set; }
    }
}
