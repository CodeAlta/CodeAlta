using CodeAlta.Catalog;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Desktop;

/// <summary>
/// Applies the archive policies of sessions while the desktop runs: once the window restored what it shows, then every
/// hour, so that the runs of an automation are archived without a restart. It is joined before the host releases its
/// dependencies.
/// </summary>
internal sealed class DesktopSessionArchivePolicies : IAsyncDisposable
{
    /// <summary>How often the policies are applied once the window is restored.</summary>
    internal static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _work;
    private int _disposed;

    internal DesktopSessionArchivePolicies(CodeAltaHost host, Func<CancellationToken, Task<IReadOnlyDictionary<string, string>>>? automationNames)
        : this(new SessionArchivePolicyService(host.RuntimeService, new CodeAltaConfigStore(host.CatalogOptions), host.SpaceCatalog, host.RetentionProtection,
            automationNames, host.Commands), host.RetentionProtection, TimeSpan.FromSeconds(30), Interval)
    {
    }

    internal DesktopSessionArchivePolicies(SessionArchivePolicyService service, SessionRetentionProtection protection, TimeSpan restorationTimeout, TimeSpan interval)
    {
        Service = service;
        _work = RunAsync(protection, restorationTimeout, interval);
    }

    /// <summary>Gets the service that applies the policies.</summary>
    internal SessionArchivePolicyService Service { get; }

    private async Task RunAsync(SessionRetentionProtection protection, TimeSpan restorationTimeout, TimeSpan interval)
    {
        try
        {
            // Nothing is archived before the window said what it shows and the bookmarks were read: until then the pass keeps every session.
            try { await protection.WaitForReadyAsync(_shutdown.Token).WaitAsync(restorationTimeout, _shutdown.Token).ConfigureAwait(false); }
            catch (TimeoutException) { }
            using var timer = new PeriodicTimer(interval);
            do
            {
                await Service.RunOnceAsync(DateTimeOffset.UtcNow, _shutdown.Token).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(_shutdown.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            await _shutdown.CancelAsync().ConfigureAwait(false);
            await _work.ConfigureAwait(false);
            _shutdown.Dispose();
        }
        else await _work.ConfigureAwait(false);
    }
}
