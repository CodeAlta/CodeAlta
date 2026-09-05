using CodeAlta.Tui.App;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;

namespace CodeAlta.Tests;

[TestClass]
public sealed class ShellFrontendHostTests
{
    [TestMethod]
    public void Tick_DelegatesToLifecycle()
    {
        var lifecycle = new CapturingLifecycle { TickResult = TerminalLoopResult.Stop };
        var host = new ShellFrontendHost(lifecycle);

        var result = host.Tick(CancellationToken.None);

        Assert.AreEqual(TerminalLoopResult.Stop, result);
        Assert.AreEqual(1, lifecycle.TickCount);
    }

    [TestMethod]
    public async Task DisposeAsync_DisposesLifecycleResources()
    {
        var lifecycle = new CapturingLifecycle();
        var host = new ShellFrontendHost(lifecycle);

        await host.DisposeAsync();

        Assert.IsTrue(lifecycle.Disposed);
    }

    [TestMethod]
    public async Task DisposeAsync_DraftFlushFailureStillDisposesOwnedServices()
    {
        var failure = new IOException("Draft flush failed");
        var lifecycle = new CapturingLifecycle { FrontendFailure = failure };
        var host = new ShellFrontendHost(lifecycle);

        var reported = await Assert.ThrowsAsync<IOException>(async () => await host.DisposeAsync());

        Assert.AreSame(failure, reported);
        Assert.IsTrue(lifecycle.Disposed);
        Assert.IsTrue(lifecycle.OwnedServicesDisposed);
    }

    [TestMethod]
    public async Task DisposeAsync_ReportsBothFrontendAndOwnedCleanupFailures()
    {
        var frontendFailure = new IOException("Draft flush failed");
        var ownedFailure = new InvalidOperationException("Owned cleanup failed");
        var lifecycle = new CapturingLifecycle { FrontendFailure = frontendFailure, OwnedFailure = ownedFailure };
        var host = new ShellFrontendHost(lifecycle);

        var reported = await Assert.ThrowsAsync<AggregateException>(async () => await host.DisposeAsync());

        CollectionAssert.AreEqual(new Exception[] { frontendFailure, ownedFailure }, reported.InnerExceptions.ToArray());
        Assert.IsTrue(lifecycle.OwnedServicesDisposed);
    }

    private sealed class CapturingLifecycle : IShellFrontendHostLifecycle, IAsyncDisposable
    {
        public int TickCount { get; private set; }

        public bool Disposed { get; private set; }

        public bool OwnedServicesDisposed { get; private set; }

        public IAsyncDisposable? OwnedServices => this;

        public Exception? FrontendFailure { get; init; }

        public Exception? OwnedFailure { get; init; }

        public TerminalLoopResult TickResult { get; init; } = TerminalLoopResult.Continue;

        public void PrepareForRun()
        {
        }

        public Visual GetRoot() => new Placeholder();

        public TerminalLoopResult Tick(CancellationToken cancellationToken)
        {
            TickCount++;
            return TickResult;
        }

        public ValueTask DisposeFrontendAsync()
        {
            Disposed = true;
            if (FrontendFailure is not null)
            {
                return ValueTask.FromException(FrontendFailure);
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            OwnedServicesDisposed = true;
            return OwnedFailure is null ? ValueTask.CompletedTask : ValueTask.FromException(OwnedFailure);
        }
    }
}
