using CodeAlta.Catalog;

namespace CodeAlta.Cli;

/// <summary>
/// The lease of the sessions <c>altacli</c> runs, in the leases folder of the state root (owner kind <c>altacli</c>): a
/// session another process runs (another <c>altacli</c>, CodeAlta Desktop or TUI) is refused as in use, exit code 5.
/// </summary>
/// <remarks>The one place the command line takes leases, through the hook of the headless driver.</remarks>
internal static class CliSessionLeases
{
    /// <summary>The hook of <see cref="Orchestration.Hosting.HeadlessSessionDriverOptions.AcquireSessionLease"/> for a state root.</summary>
    public static Func<string, CancellationToken, ValueTask<IAsyncDisposable?>> For(CatalogOptions catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var store = new SessionLeaseStore(catalog);
        var owner = new SessionLeaseOwner(SessionLeaseOwner.CliKind, Guid.NewGuid().ToString("N"));
        return (sessionId, _) => ValueTask.FromResult<IAsyncDisposable?>(store.TryAcquire(sessionId, owner) is { } lease ? new Held(lease) : null);
    }

    private sealed class Held(SessionLease lease) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            lease.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
