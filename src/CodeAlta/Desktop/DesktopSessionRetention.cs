using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Plugin.Statistics;

namespace CodeAlta.Desktop;

/// <summary>Owns the one desktop startup pass and joins it before the host releases its dependencies.</summary>
internal sealed class DesktopSessionRetention : IAsyncDisposable
{
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task<SessionRetentionStatus> _work;
    private int _disposed;
    private int _applicationNavigated;

    internal SessionRetentionService Service { get; }
    internal SessionRetentionProtectionService ProtectionRpc { get; }

    internal DesktopSessionRetention(CodeAltaHost host, string epoch)
        : this(host.Commands, host.RuntimeService, new CodeAltaConfigStore(host.CatalogOptions),
            host.PluginRuntime.ActivePlugins.Select(static plugin => plugin.Instance).OfType<StatisticsPlugin>()
                .SingleOrDefault()?.RetirementArchive, epoch, host.RetentionProtection)
    {
    }

    // Explicit dependencies allow isolated-profile tests. The active built-in Statistics participant is borrowed:
    // never create a parallel history engine, archive store or profile here.
    internal DesktopSessionRetention(OwnedSessionCommandService commands, SessionRuntimeService runtime,
        CodeAltaConfigStore config, ISessionStatisticsRetirementArchive? archive, string epoch,
        SessionRetentionProtection protection)
        : this(commands, runtime, config, archive, epoch, protection, TimeSpan.FromSeconds(30))
    {
    }

    internal DesktopSessionRetention(OwnedSessionCommandService commands, SessionRuntimeService runtime,
        CodeAltaConfigStore config, ISessionStatisticsRetirementArchive? archive, string epoch,
        SessionRetentionProtection protection, TimeSpan restorationTimeout)
    {
        // The owned desktop creates exactly one session-capable native window and refuses popup windows.
        // This host-bound participant exists before its bridge/page; renderer page IDs cannot register owners.
        ProtectionRpc = new(epoch,
            (ids, ready) => protection.Publish(SessionRetentionProtectionService.Owner, ids, ready),
            reason => protection.MarkUnknown(SessionRetentionProtectionService.Owner, reason));
        Service = new(commands, runtime, protection, archive);
        var now = DateTimeOffset.UtcNow;
        var cutoff = ReadCutoff(config, now);
        _work = StartAfterRestorationAsync(protection, archive is not null && cutoff is not null && cutoff < now,
            now, cutoff, restorationTimeout);
    }

    internal void BeforeMainFrameNavigation(Uri destination)
    {
        // The one initial application document is allowed to restore. Any later reload/replacement keeps
        // this startup pass fail-closed, even when callbacks from the old bridge arrive afterward.
        if (Interlocked.Exchange(ref _applicationNavigated, 1) != 0
            || destination.AbsolutePath != DesktopApplication.ApplicationDocument.AbsolutePath)
            ProtectionRpc.InvalidatePage();
    }

    private async Task<SessionRetentionStatus> StartAfterRestorationAsync(SessionRetentionProtection protection,
        bool needsRestoration, DateTimeOffset now, DateTimeOffset? cutoff, TimeSpan restorationTimeout)
    {
        if (needsRestoration)
        {
            // Wait for the first complete restoration, not a snapshot taken before the page exists. This is
            // a readiness observation, not cancelable ownership of a worker. One startup pass only: neither
            // later policy edits nor reconnects restart it. Unknown/missing owners cannot authorize deletion.
            try { await protection.WaitForReadyAsync(_shutdown.Token).WaitAsync(restorationTimeout, _shutdown.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { /* RunOnce reports shutdown without touching artifacts. */ }
            catch (TimeoutException) { /* RunOnce reports the remaining unknown owner; no retry on reconnect. */ }
        }
        return await Service.RunOnceAsync(now, cutoff, _shutdown.Token).ConfigureAwait(false);
    }

    internal static SessionRetentionProtection CreateProtection()
    {
        var protection = new SessionRetentionProtection();
        protection.SealParticipants(["external-work", SessionRetentionProtectionService.Owner]);
        // Composition replaces external-work only after installing every original-lifetime producer.
        // The desktop owner is released independently by the qualified all-space restoration protocol.
        protection.MarkUnknown("external-work", "external_admission_unqualified");
        protection.MarkUnknown(SessionRetentionProtectionService.Owner, "desktop_admission_unqualified");
        return protection;
    }

    // Called only after DesktopAltaTools, reminders, model initialization and RPC producers are installed.
    // Their original claims remain in this registry; qualifying composition does not empty those claims
    // or certify the independently registered native window. A borrowed mismatch stays unknown too.
    internal static bool CompleteExternalRegistration(CodeAltaHost host, SessionRetentionProtection registered)
        => ReferenceEquals(host.RetentionProtection, registered)
            && host.PluginRuntime.IsStarted
            && ReferenceEquals(host.PluginRuntime.SessionRetentionAdmission, registered)
            && registered.Publish("external-work", [], true);

    internal static DateTimeOffset? ReadCutoff(CodeAltaConfigStore config, DateTimeOffset now)
    {
        try
        {
            var policy = config.LoadGlobalStorageSettings().SessionRetention;
            return policy.TryGetCutoff(now, out var cutoff, out var error) ? cutoff : error is null ? null : now;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            // An unrepresentable cutoff is the runtime's invalid_cutoff outcome, never a destructive default.
            return now;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            ProtectionRpc.InvalidatePage();
            await _shutdown.CancelAsync().ConfigureAwait(false);
            await _work.ConfigureAwait(false);
            _shutdown.Dispose();
        }
        else await _work.ConfigureAwait(false);
    }
}
