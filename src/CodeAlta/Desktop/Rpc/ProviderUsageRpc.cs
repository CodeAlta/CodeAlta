using System.Collections.Concurrent;
using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Hosting;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// Reads the usage of the subscription behind a configured provider (Codex, Copilot, Claude Code) for the Providers
/// page of Settings and for the context usage of a session: the limits of the plan, how much of each is used and
/// when each starts over.
/// </summary>
/// <remarks>
/// The provider is asked when the page asks, never on a timer, and an answer is kept for a minute so that a window
/// that is opened again does not ask again. Failures cross as status codes, never as the text of an exception.
/// </remarks>
[NeoRpcService("providerUsage", Version = 1)]
internal sealed class ProviderUsageService : IDisposable
{
    /// <summary>How long an answer is given again without asking the provider.</summary>
    internal static readonly TimeSpan Freshness = TimeSpan.FromSeconds(60);

    /// <summary>How long a failure, or an answer that was asked to be read again, is given again.</summary>
    internal static readonly TimeSpan ShortestInterval = TimeSpan.FromSeconds(5);

    /// <summary>Most limits sent for one provider.</summary>
    internal const int MaximumLimits = 16;

    private const int MaximumTextLength = 64;
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(45);
    private readonly ProviderUsageOperations? _operations;
    private readonly string? _epoch;
    private readonly HttpClient? _http;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>Creates an unavailable service for launches without an owned host.</summary>
    internal ProviderUsageService()
    {
    }

    /// <summary>Creates the service over literal operations.</summary>
    /// <param name="operations">The configuration and reading operations.</param>
    /// <param name="epoch">The host epoch that requests must name.</param>
    /// <exception cref="ArgumentNullException"><paramref name="operations"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="epoch"/> is blank.</exception>
    internal ProviderUsageService(ProviderUsageOperations operations, string epoch)
    {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentException.ThrowIfNullOrWhiteSpace(epoch);
        _operations = operations;
        _epoch = epoch;
    }

    /// <summary>Creates the service for an owned host.</summary>
    /// <param name="store">The configuration store of the owned global root.</param>
    /// <param name="stateRoot">The global root that holds provider credentials.</param>
    /// <param name="epoch">The host epoch that requests must name.</param>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="stateRoot"/> or <paramref name="epoch"/> is blank.</exception>
    internal ProviderUsageService(CodeAltaConfigStore store, string stateRoot, string epoch)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(epoch);
        var http = _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _operations = new(
            () => store.LoadGlobalProviderDefinitions(includeDisabled: true),
            (definition, cancellationToken) => ConfiguredProviderUsage.ReadAsync(definition, stateRoot, http, cancellationToken));
        _epoch = epoch;
    }

    /// <summary>Reads the usage of the subscription of one configured provider.</summary>
    [NeoRpcMethod("read")]
    public async Task<ProviderUsageResponse> ReadAsync(ProviderUsageRequest request, CancellationToken cancellationToken)
    {
        var (code, key, definition) = Resolve(request?.ExpectedEpoch, request?.Key);
        if (definition is null) return Empty(code, key, supported: false);
        if (!ConfiguredProviderUsage.Supports(definition.ProviderType)) return Empty("ok", key, supported: false);

        var entry = _entries.GetOrAdd(key!, static _ => new Entry());
        await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _operations!.Now();
            if (entry.Response is { } kept)
            {
                var age = now - entry.At;
                var lifetime = kept.Status == "ok" && request?.Refresh != true ? Freshness : ShortestInterval;
                if (age >= TimeSpan.Zero && age < lifetime) return kept;
            }

            ProviderUsageResponse response;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ReadTimeout);
            try
            {
                response = Project(key!, await _operations.Read(definition, timeout.Token).ConfigureAwait(false), ToolOf(definition.ProviderType));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // An unreachable service, an answer of another shape, a CLI that did not answer in time.
                response = Empty("failed", key, supported: true);
            }

            (entry.Response, entry.At) = (response, now);
            return response;
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _http?.Dispose();

    internal static ProviderUsageResponse Project(string key, AgentSubscriptionUsageReading reading, string? tool = null)
    {
        ArgumentNullException.ThrowIfNull(reading);
        if (reading.Status != AgentSubscriptionUsageReading.Ok || reading.Usage is not { } usage)
        {
            return reading.Status switch
            {
                AgentSubscriptionUsageReading.SignedOut => Empty("signed_out", key, supported: true),
                // The page names the tool that is missing, or that has to be signed in.
                AgentSubscriptionUsageReading.ToolMissing when tool is not null => Empty("tool_missing", key, supported: true) with { Tool = tool },
                AgentSubscriptionUsageReading.ToolSignedOut when tool is not null => Empty("tool_signed_out", key, supported: true) with { Tool = tool },
                _ => Empty("not_available", key, supported: true),
            };
        }

        var limits = usage.Limits
            .Where(static limit => Bound(limit.Id) is not null)
            .Take(MaximumLimits)
            .Select(static limit => new ProviderUsageLimit(
                Bound(limit.Id)!, Bound(limit.Name), Number(limit.UsedPercent, 999), limit.ResetsAt,
                limit.WindowMinutes is > 0 and <= 60L * 24 * 366 and var minutes ? (int)minutes : null,
                Number(limit.Used, int.MaxValue), Number(limit.Total, int.MaxValue), Bound(limit.Unit), limit.Unlimited, Number(limit.Remaining, int.MaxValue)))
            .ToArray();
        return limits.Length == 0
            ? Empty("not_available", key, supported: true)
            : new("ok", key, true, Bound(usage.Plan), limits, usage.ObservedAt);
    }

    private (string Code, string? Key, CodeAltaProviderDocument? Definition) Resolve(string? expectedEpoch, string? requestedKey)
    {
        if (_operations is null) return ("unavailable", null, null);
        if (!string.Equals(expectedEpoch, _epoch, StringComparison.Ordinal)) return ("stale_epoch", null, null);
        var key = requestedKey?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(key) || key.Length > 64 || !key.All(static character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
            return ("invalid", null, null);
        try
        {
            var definition = _operations.LoadDefinitions().FirstOrDefault(value => string.Equals(value.ProviderKey, key, StringComparison.OrdinalIgnoreCase));
            return definition is null ? ("unknown_provider", key, null) : ("ok", key, definition);
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException)
        {
            return ("config_invalid", key, null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return ("read_failed", key, null);
        }
    }

    private static ProviderUsageResponse Empty(string status, string? key, bool supported) => new(status, key, supported, null, [], null);

    // The tool a provider type is asked through: its own command-line program.
    private static string? ToolOf(string? providerType) => providerType switch
    {
        "codex" => "Codex CLI",
        "claude-code" => "Claude Code",
        _ => null,
    };

    // A name of a plan or of a limit is short text without control characters; anything else is left out.
    private static string? Bound(string? value)
        => string.IsNullOrWhiteSpace(value) || value.Length > MaximumTextLength || value.Any(char.IsControl) ? null : value.Trim();

    // Whole numbers cross to the page: a percentage and a count of requests or credits need no fraction.
    private static int? Number(double? value, int maximum)
        => value is { } number && double.IsFinite(number) && number >= 0 ? (int)Math.Min(Math.Round(number), maximum) : null;

    private sealed class Entry
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public ProviderUsageResponse? Response { get; set; }
        public DateTimeOffset At { get; set; }
    }
}

/// <summary>The operations behind <see cref="ProviderUsageService"/>; tests supply literal callbacks.</summary>
/// <param name="LoadDefinitions">Loads the type-completed provider definitions, including disabled ones.</param>
/// <param name="Read">Asks the provider of a definition for the usage of its subscription.</param>
internal sealed record ProviderUsageOperations(
    Func<IReadOnlyList<CodeAltaProviderDocument>> LoadDefinitions,
    Func<CodeAltaProviderDocument, CancellationToken, Task<AgentSubscriptionUsageReading>> Read)
{
    /// <summary>Gets the clock that ages the answers that are kept.</summary>
    public Func<DateTimeOffset> Now { get; init; } = static () => DateTimeOffset.UtcNow;
}

/// <summary>Asks for the usage of one provider. <c>Refresh</c> asks the provider again instead of giving the answer of the last minute.</summary>
internal sealed record ProviderUsageRequest(string? ExpectedEpoch, string? Key, bool Refresh);

/// <summary>
/// The usage of a subscription. <c>Supported</c> is false, with status <c>ok</c>, for a provider type that is not a
/// subscription whose usage can be read. Other statuses: <c>signed_out</c>, <c>tool_missing</c> and
/// <c>tool_signed_out</c> (the command-line program of the provider that is asked, named by <c>Tool</c>, is not
/// installed, or is not signed in with the account of the provider), <c>not_available</c> (the account gives no
/// usage), <c>failed</c>, and the codes of a provider that cannot be resolved.
/// </summary>
internal sealed record ProviderUsageResponse(string Status, string? Key, bool Supported, string? Plan,
    IReadOnlyList<ProviderUsageLimit> Limits, DateTimeOffset? ObservedAt, string? Tool = null);

/// <summary>
/// One limit: a window of time (<c>WindowMinutes</c>) or a quota, with how much is used in percent and, when the
/// provider counts them, in units (<c>requests</c> or <c>credits</c>).
/// </summary>
internal sealed record ProviderUsageLimit(string Id, string? Name, int? UsedPercent, DateTimeOffset? ResetsAt, int? WindowMinutes,
    int? Used, int? Total, string? Unit, bool Unlimited, int? Remaining);
