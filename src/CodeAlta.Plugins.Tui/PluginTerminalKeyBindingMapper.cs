using CodeAlta.Plugins.Abstractions;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI.Input;

namespace CodeAlta.Plugins.Tui;

/// <summary>Pure, optional conversion of logical plugin bindings to terminal values.</summary>
public static class PluginTerminalKeyBindingMapper
{
    /// <summary>Maps an entire binding without acquiring services or executing a command.</summary>
    /// <param name="binding">The validated logical binding; callers handle absent bindings separately.</param>
    /// <param name="gesture">The mapped single stroke, otherwise null.</param>
    /// <param name="sequence">The mapped two-through-four-stroke sequence, otherwise null.</param>
    /// <returns>True for representable values; false with both outputs null for an unsupported scalar.</returns>
    /// <exception cref="ArgumentNullException">The binding is null.</exception>
    /// <remarks>
    /// Logical Control+A through Control+Z use terminal control-character encoding while retaining all modifiers.
    /// Supplementary scalars cannot fit the native character field. No stroke is dropped and no partial binding is returned.
    /// Success describes value representability, not keyboard, protocol or OS delivery. Meta is preserved even though
    /// the current native hint formatter omits Meta and may show a leading '+' for Meta-only gestures.
    /// This mapper does not alter frontend help or native dispatch.
    /// </remarks>
    public static bool TryMap(
        PluginKeyBinding binding,
        out KeyGesture? gesture,
        out KeySequence? sequence)
    {
        ArgumentNullException.ThrowIfNull(binding);
        gesture = null;
        sequence = null;
        var mapped = new KeyGesture[binding.Count];
        for (var i = 0; i < binding.Count; i++)
        {
            var stroke = binding.Gestures[i];
            var modifiers = TerminalModifiers.None;
            if ((stroke.Modifiers & PluginKeyModifiers.Ctrl) != 0) modifiers |= TerminalModifiers.Ctrl;
            if ((stroke.Modifiers & PluginKeyModifiers.Alt) != 0) modifiers |= TerminalModifiers.Alt;
            if ((stroke.Modifiers & PluginKeyModifiers.Shift) != 0) modifiers |= TerminalModifiers.Shift;
            if ((stroke.Modifiers & PluginKeyModifiers.Meta) != 0) modifiers |= TerminalModifiers.Meta;
            if (stroke.Character is { } character)
            {
                if (!character.IsBmp) return false;
                var value = (char)character.Value;
                if ((stroke.Modifiers & PluginKeyModifiers.Ctrl) != 0 && value is >= 'A' and <= 'Z')
                {
                    value = (char)(value - 'A' + 1);
                }
                mapped[i] = new KeyGesture(value, modifiers);
            }
            else
            {
                var key = stroke.Key switch
                {
                    PluginKey.Enter => TerminalKey.Enter,
                    PluginKey.Escape => TerminalKey.Escape,
                    PluginKey.Backspace => TerminalKey.Backspace,
                    PluginKey.Tab => TerminalKey.Tab,
                    PluginKey.Space => TerminalKey.Space,
                    PluginKey.Up => TerminalKey.Up,
                    PluginKey.Down => TerminalKey.Down,
                    PluginKey.Left => TerminalKey.Left,
                    PluginKey.Right => TerminalKey.Right,
                    PluginKey.Home => TerminalKey.Home,
                    PluginKey.End => TerminalKey.End,
                    PluginKey.PageUp => TerminalKey.PageUp,
                    PluginKey.PageDown => TerminalKey.PageDown,
                    PluginKey.Insert => TerminalKey.Insert,
                    PluginKey.Delete => TerminalKey.Delete,
                    PluginKey.F1 => TerminalKey.F1,
                    PluginKey.F2 => TerminalKey.F2,
                    PluginKey.F3 => TerminalKey.F3,
                    PluginKey.F4 => TerminalKey.F4,
                    PluginKey.F5 => TerminalKey.F5,
                    PluginKey.F6 => TerminalKey.F6,
                    PluginKey.F7 => TerminalKey.F7,
                    PluginKey.F8 => TerminalKey.F8,
                    PluginKey.F9 => TerminalKey.F9,
                    PluginKey.F10 => TerminalKey.F10,
                    PluginKey.F11 => TerminalKey.F11,
                    PluginKey.F12 => TerminalKey.F12,
                    _ => TerminalKey.Unknown,
                };
                if (key == TerminalKey.Unknown) return false;
                mapped[i] = new KeyGesture(key, modifiers);
            }
        }
        if (mapped.Length == 1) gesture = mapped[0];
        else sequence = new KeySequence(mapped);
        return true;
    }
}
