using XenoAtom.Terminal.UI.Controls;

namespace CodeAlta.Views;

internal sealed class ChatPromptEditor : PromptEditor
{
    private readonly Action<string> _onAccepted;

    public ChatPromptEditor(Action<string> onAccepted)
    {
        ArgumentNullException.ThrowIfNull(onAccepted);
        _onAccepted = onAccepted;
    }

    protected override void OnAccepted(PromptEditorAcceptedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        _onAccepted(e.Text);
        base.OnAccepted(e);
    }
}