using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;

namespace CodeAlta.Views;

internal sealed class CodeAltaRootView : Padder
{
    private readonly Action<TerminalApp> _configureApp;
    private Action<CodeAltaRootView, TerminalApp>? _attachedToApp;

    public CodeAltaRootView(Visual content, Action<TerminalApp> configureApp)
        : base(content)
    {
        _configureApp = configureApp ?? throw new ArgumentNullException(nameof(configureApp));
    }

    protected override void OnAttachedToApp(TerminalApp app)
    {
        base.OnAttachedToApp(app);
        _configureApp(app);
        _attachedToApp?.Invoke(this, app);
    }

    internal void AddAttachedToAppCallback(Action<CodeAltaRootView, TerminalApp> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        _attachedToApp += callback;
    }
}
