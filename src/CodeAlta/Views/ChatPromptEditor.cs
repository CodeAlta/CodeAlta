using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Input;

namespace CodeAlta.Views;

internal sealed class ChatPromptEditor : PromptEditor
{
    private readonly Action<string> _onAccepted;
    private readonly Action? _onOpenHelp;
    private readonly Action? _onOpenCommandPalette;

    public ChatPromptEditor(
        Action<string> onAccepted,
        Action? onOpenHelp = null,
        Action? onOpenCommandPalette = null)
    {
        ArgumentNullException.ThrowIfNull(onAccepted);
        _onAccepted = onAccepted;
        _onOpenHelp = onOpenHelp;
        _onOpenCommandPalette = onOpenCommandPalette;
    }

    protected override void OnAccepted(PromptEditorAcceptedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        _onAccepted(e.Text);
        base.OnAccepted(e);
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (!e.Handled && TryHandleTransientShortcutInput(e.Text))
        {
            e.Handled = true;
            return;
        }

        base.OnTextInput(e);
    }

    internal bool TryHandleTransientShortcutInput(string? text)
    {
        if (TextDocument.CurrentSnapshot.Length != 0 || CaretIndex != 0 || SelectionLength != 0)
        {
            return false;
        }

        return text switch
        {
            "?" when _onOpenHelp is not null => InvokeTransientShortcut(_onOpenHelp),
            "/" when _onOpenCommandPalette is not null => InvokeTransientShortcut(_onOpenCommandPalette),
            _ => false,
        };
    }

    private static bool InvokeTransientShortcut(Action action)
    {
        action();
        return true;
    }
}
