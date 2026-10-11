using System.Runtime.CompilerServices;
using CodeAlta.Orchestration.Runtime;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

/// <summary>The page's all-space open/draft claims, acknowledged before exposing protected work.</summary>
[NeoRpcService("sessionRetention", Version = 1)]
internal sealed class SessionRetentionProtectionService
{
    internal const string Owner = "desktop-open-drafts";
    private const int MaximumIds = 32768;
    private readonly SessionRetentionProtection? _protection;
    private readonly Func<IReadOnlyCollection<string>, bool, bool>? _publish;
    private readonly Action<string>? _markUnknown;
    private readonly string? _epoch;
    private readonly object _gate = new();
    private readonly Dictionary<string, long> _revisions = new(StringComparer.Ordinal);
    private readonly HashSet<string> _ids = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _retiredPages = new(StringComparer.Ordinal);
    private string? _pageId;
    private string? _documentSessionId;
    private bool _watchActive;
    private long _lifetimeGeneration;
    private bool _invalidated;


    internal SessionRetentionProtectionService() { }

    internal SessionRetentionProtectionService(SessionRetentionProtection protection, string epoch)
    {
        ArgumentNullException.ThrowIfNull(protection);
        ArgumentException.ThrowIfNullOrWhiteSpace(epoch);
        (_protection, _epoch) = (protection, epoch);
        protection.MarkUnknown(Owner, "desktop_admission_unqualified");
    }

    /// <summary>Uses a host-registered window owner, registered unknown before its page/bridge is exposed.</summary>
    internal SessionRetentionProtectionService(string epoch, Func<IReadOnlyCollection<string>, bool, bool> publish, Action<string> markUnknown)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(epoch);
        ArgumentNullException.ThrowIfNull(publish);
        ArgumentNullException.ThrowIfNull(markUnknown);
        (_epoch, _publish, _markUnknown) = (epoch, publish, markUnknown);
        markUnknown("desktop_restoring");
    }

    /// <summary>
    /// Native navigation/bridge replacement calls this before admitting a new page. Old page callbacks can
    /// no longer certify restoration. Even a callback queued before the first publication cannot restore
    /// readiness after this latch: this window stays unknown for the single startup pass. Disconnect never
    /// releases the union. The host owns its bounded one-shot wait for all registered participants.
    /// </summary>
    internal void InvalidatePage()
    {
        lock (_gate)
        {
            if (_pageId is not null) _retiredPages.Add(_pageId);
            _pageId = null;
            _watchActive = false;
            _invalidated = true;
            _markUnknown?.Invoke("desktop_disconnected");
            _protection?.MarkUnknown(Owner, "desktop_disconnected");
        }
    }

    /// <summary>Binds a page to the trusted transport lifetime before any restoration can become ready.</summary>
    [NeoRpcMethod("watch")]
    public NeoRpcChannel<SessionRetentionProtectionResponse> Watch(SessionRetentionLifetimeRequest request, NeoRpcContext context, CancellationToken cancellationToken)
        => new(WatchLifetime(request, context.DocumentSessionId, cancellationToken), DesktopJsonContext.Default.SessionRetentionProtectionResponse);

    internal async IAsyncEnumerable<SessionRetentionProtectionResponse> WatchLifetime(SessionRetentionLifetimeRequest request, string documentSessionId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        CancellationTokenRegistration registration = default;
        var accepted = false;
        long generation = 0;
        lock (_gate)
        {
            if (_publish is not null && string.Equals(_epoch, request.ExpectedEpoch, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(documentSessionId) && !string.IsNullOrWhiteSpace(request.PageId)
                && request.PageId.Length <= 128 && !_watchActive && !_retiredPages.Contains(request.PageId)
                && (_pageId is null || _pageId == request.PageId))
            {
                _pageId = request.PageId;
                _documentSessionId = documentSessionId;
                _watchActive = true;
                generation = ++_lifetimeGeneration;
                MarkUnknown("desktop_restoring");
                // The effective enumerator token includes transport/channel cancellation. Register before
                // the first acknowledgement, not at resource disposal after a channel has drained.
                registration = cancellationToken.Register(() => InvalidateLifetime(generation));
                accepted = true;
            }
        }
        try
        {
            yield return new(accepted, accepted ? null : "lifetime_not_registered");
            if (accepted) await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (accepted) InvalidateLifetime(generation);
            registration.Dispose();
        }
    }

    private void InvalidateLifetime(long generation)
    {
        lock (_gate) if (_lifetimeGeneration == generation) InvalidatePage();
    }

    /// <summary>
    /// Publishes complete restored IDs and new claims. Claims are monotonic for the single startup pass: a closed
    /// tab, disconnected page or reload does not release them. Only a host-registered window may certify its
    /// own restoration; the legacy unbound constructor deliberately cannot authorize desktop-wide readiness.
    /// </summary>
    [NeoRpcMethod("publish")]
    public Task<SessionRetentionProtectionResponse> PublishAsync(SessionRetentionProtectionRequest request, NeoRpcContext context, CancellationToken cancellationToken)
        => PublishForDocumentAsync(request, context.DocumentSessionId, cancellationToken);

    internal Task<SessionRetentionProtectionResponse> PublishAsync(SessionRetentionProtectionRequest request, CancellationToken cancellationToken)
        => PublishForDocumentAsync(request, null, cancellationToken);

    internal Task<SessionRetentionProtectionResponse> PublishForDocumentAsync(SessionRetentionProtectionRequest request, string? documentSessionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_protection is null && _publish is null) return Result(false, "unavailable");
            if (!string.Equals(_epoch, request.ExpectedEpoch, StringComparison.Ordinal)) return Result(false, "stale_epoch");
            if (_publish is not null && (!_watchActive || !string.Equals(_documentSessionId, documentSessionId, StringComparison.Ordinal)))
                return Result(false, "lifetime_not_registered");
            if (string.IsNullOrWhiteSpace(request.PageId) || request.PageId.Length > 128 || request.Revision <= 0
                || request.SessionIds is null || request.SessionIds.Length > MaximumIds
                || request.SessionIds.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 256 || id != id.Trim()))
            {
                MarkUnknown("desktop_restore_ambiguous");
                return Result(false, "invalid_claims");
            }
            if (_retiredPages.Contains(request.PageId)) return Result(false, "stale_page");
            if (_pageId is not null && !string.Equals(_pageId, request.PageId, StringComparison.Ordinal))
                return Result(false, "page_not_registered");
            if (_revisions.TryGetValue(request.PageId, out var previous) && request.Revision <= previous) return Result(false, "stale_revision");
            if (!_revisions.ContainsKey(request.PageId) && _revisions.Count >= 32)
            {
                MarkUnknown("desktop_restore_ambiguous");
                return Result(false, "page_limit");
            }
            var next = new HashSet<string>(_ids, StringComparer.OrdinalIgnoreCase);
            next.UnionWith(request.SessionIds);
            if (next.Count > MaximumIds)
            {
                MarkUnknown("desktop_restore_ambiguous");
                return Result(false, "claim_limit");
            }
            var ready = request.Ready && !_invalidated;
            if (!(_publish?.Invoke(next, ready) ?? _protection!.Publish(Owner, next, false)))
            {
                return Result(false, "session_retiring");
            }
            _ids.UnionWith(next);
            _pageId = request.PageId;
            _revisions[request.PageId] = request.Revision;
            if (_publish is not null)
            {
                return Result(true, null);
            }
            MarkUnknown("desktop_admission_unqualified");
            return Result(!request.Ready, "desktop_admission_unqualified");
        }

        static Task<SessionRetentionProtectionResponse> Result(bool accepted, string? reason) => Task.FromResult(new SessionRetentionProtectionResponse(accepted, reason));
    }

    private void MarkUnknown(string reason)
    {
        _markUnknown?.Invoke(reason);
        _protection?.MarkUnknown(Owner, reason);
    }
}

/// <summary>A complete protection publication for one page generation of this host epoch.</summary>
internal sealed record SessionRetentionProtectionRequest(string ExpectedEpoch, string PageId, int Revision, string[] SessionIds, bool Ready);

/// <summary>Identifies a renderer page within the host-authenticated transport document lifetime.</summary>
internal sealed record SessionRetentionLifetimeRequest(string ExpectedEpoch, string PageId);

/// <summary>An acknowledgement is required before the caller exposes newly protected work.</summary>
internal sealed record SessionRetentionProtectionResponse(bool Accepted, string? Reason);
