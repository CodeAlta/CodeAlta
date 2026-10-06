using System.Globalization;

namespace CodeAlta.Desktop.Terminals;

/// <summary>What the keyboard of a terminal sends for a key that is named rather than pressed.</summary>
internal static class TerminalKeys
{
    /// <summary>
    /// Gives what a key sends: <c>enter</c>, <c>tab</c>, <c>escape</c>, <c>space</c>, <c>backspace</c>, <c>delete</c>,
    /// <c>insert</c>, the arrows <c>up</c> <c>down</c> <c>left</c> <c>right</c>, <c>home</c>, <c>end</c>, <c>pageup</c>,
    /// <c>pagedown</c>, <c>f1</c> to <c>f12</c>, or one character; each with <c>ctrl+</c>, <c>alt+</c> and
    /// <c>shift+</c> before it, as in <c>ctrl+c</c> or <c>shift+tab</c>.
    /// </summary>
    /// <param name="name">The name of the key; letter case does not matter.</param>
    /// <param name="application">Whether the program asked for the arrow keys of full-screen programs.</param>
    /// <returns>What the key sends, or null when the name is not the one of a key.</returns>
    public static string? Encode(string? name, bool application)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var text = name.Trim();
        bool control = false, alt = false, shift = false;
        // What follows the last plus sign is the key, which can be the plus sign itself.
        while (text.IndexOf('+', StringComparison.Ordinal) is var plus and > 0 && plus < text.Length - 1)
        {
            switch (text[..plus].Trim().ToLowerInvariant())
            {
                case "ctrl" or "control" when !control: control = true; break;
                case "alt" or "option" or "meta" when !alt: alt = true; break;
                case "shift" when !shift: shift = true; break;
                default: return null;
            }
            text = text[(plus + 1)..].TrimStart();
        }
        var modifier = 1 + (shift ? 1 : 0) + (alt ? 2 : 0) + (control ? 4 : 0);
        var key = text.ToLowerInvariant();
        switch (key)
        {
            case "enter" or "return": return Prefixed("\r");
            case "tab": return shift ? Prefixed("\u001b[Z") : Prefixed("\t");
            case "escape" or "esc": return Prefixed("\u001b");
            case "space": return Prefixed(control ? "\0" : " ");
            case "backspace": return Prefixed(control ? "\b" : "\u007f");
            case "up": return Letter('A');
            case "down": return Letter('B');
            case "right": return Letter('C');
            case "left": return Letter('D');
            case "home": return Letter('H');
            case "end": return Letter('F');
            case "insert" or "ins": return Tilde(2);
            case "delete" or "del": return Tilde(3);
            case "pageup" or "pgup": return Tilde(5);
            case "pagedown" or "pgdn": return Tilde(6);
        }
        if (key.Length is 2 or 3 && key[0] == 'f' && int.TryParse(key.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number is >= 1 and <= 12)
        {
            if (number <= 4) return modifier == 1 ? "\u001bO" + "PQRS"[number - 1] : string.Create(CultureInfo.InvariantCulture, $"\u001b[1;{modifier}{"PQRS"[number - 1]}");
            return Tilde(number switch { 5 => 15, 6 => 17, 7 => 18, 8 => 19, 9 => 20, 10 => 21, 11 => 23, _ => 24 });
        }

        // One character: itself, the control character it stands for, or the letter in capitals.
        if (text.Length != 1 || char.IsControl(text[0]) || char.IsSurrogate(text[0])) return null;
        var character = text[0];
        if (control)
        {
            if (char.ToUpperInvariant(character) is >= '@' and <= '_' and var upper) character = (char)(upper & 0x1f);
            else if (character == '?') character = '\u007f';
            else return null;
        }
        else if (shift) character = char.ToUpperInvariant(character);
        return Prefixed(character.ToString());

        // A key with Alt is the key after an escape character.
        string Prefixed(string sent) => alt ? "\u001b" + sent : sent;

        string Letter(char letter)
            => modifier == 1 ? (application ? "\u001bO" : "\u001b[") + letter : string.Create(CultureInfo.InvariantCulture, $"\u001b[1;{modifier}{letter}");

        string Tilde(int code)
            => modifier == 1 ? string.Create(CultureInfo.InvariantCulture, $"\u001b[{code}~") : string.Create(CultureInfo.InvariantCulture, $"\u001b[{code};{modifier}~");
    }
}
