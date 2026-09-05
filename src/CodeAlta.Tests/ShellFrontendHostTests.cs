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

    private sealed class CapturingLifecycle : IShellFrontendHostLifecycle
    {
        public int TickCount { get; private set; }

        public bool Disposed { get; private set; }

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
            return ValueTask.CompletedTask;
        }
    }
}
