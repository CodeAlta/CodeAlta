using System.Reflection;
using XenoAtom.Terminal;
using XenoAtom.Terminal.Backends;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Hosting;

namespace CodeAlta.Tests;

// No default application, provider startup, filesystem discovery or native terminal.
internal sealed class PermissionTerminalFixture : IDisposable
{
    private readonly IDisposable _terminal;
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;

    public PermissionTerminalFixture()
    {
        var terminal = Terminal.Open(new InMemoryTerminalBackend(new TerminalSize(120, 40)),
            new TerminalOptions { ImplicitStartInput = true }, force: true);
        _terminal = terminal;
        App = new TerminalApp(new TextBlock("Permission fixture"), terminal.Instance,
            new TerminalAppOptions { HostKind = TerminalHostKind.Fullscreen });
        Invoke("BeginRun");
        // Async test work uses its explicit controlled dispatcher, not an unpumped terminal context.
        SynchronizationContext.SetSynchronizationContext(null);
    }

    public TerminalApp App { get; }
    public int DialogCount => App.Root.EnumerateVisualsDepthFirst().OfType<Dialog>().Count();
    public void Tick() => Invoke("Tick", [null]);
    public void Key(TerminalKey key) => Invoke("DispatchKeyEvent", new TerminalKeyEvent { Key = key }, true);
    public void Dispose()
    {
        try { Invoke("EndRun"); }
        finally
        {
            try { App.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            finally
            {
                _terminal.Dispose();
                SynchronizationContext.SetSynchronizationContext(_context);
            }
        }
    }

    private void Invoke(string name, params object?[] arguments)
        => typeof(TerminalApp).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(App, arguments);
}
