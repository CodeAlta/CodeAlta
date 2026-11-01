using System.Collections.Frozen;

namespace CodeAlta.Browser.Tools;

/// <summary>A key of the US keyboard layout as a keyboard event describes it.</summary>
/// <param name="Key">The <c>key</c> of the event.</param>
/// <param name="Code">The <c>code</c> of the event.</param>
/// <param name="KeyCode">The legacy <c>keyCode</c> of the event.</param>
/// <param name="Location">The <c>location</c> of the event.</param>
/// <param name="Text">The text that the key types, or <see langword="null"/>.</param>
/// <param name="Shift">
/// Whether Shift is held for the text: it is the upper symbol of its key, or a capital letter. Only a typed text says so;
/// a key combination names the keys that are held.
/// </param>
internal readonly record struct BrowserKey(string Key, string Code, int KeyCode, int Location, string? Text, bool Shift = false)
{
    internal bool IsModifier => Key is "Shift" or "Control" or "Alt" or "Meta";

    /// <summary>The bit of a modifier key in the <c>modifiers</c> of CDP: Alt 1, Control 2, Meta 4, Shift 8.</summary>
    internal int ModifierBit => Key switch { "Alt" => 1, "Control" => 2, "Meta" => 4, "Shift" => 8, _ => 0 };
}

/// <summary>
/// Resolves the key names that <c>press_key</c> accepts, which are the key names of Puppeteer: the <c>key</c> and
/// <c>code</c> values of keyboard events on a US layout.
/// </summary>
internal static class BrowserKeys
{
    private static readonly FrozenDictionary<string, BrowserKey> Keys = Create().ToFrozenDictionary(StringComparer.Ordinal);
    private static readonly FrozenDictionary<char, BrowserKey> Characters = CreateCharacters().ToFrozenDictionary();

    /// <summary>Splits a key combination such as <c>Control+Shift+R</c> into its key and the modifiers held with it.</summary>
    /// <exception cref="BrowserToolException">The combination is empty, repeats a key, or names an unknown key.</exception>
    internal static (BrowserKey Key, IReadOnlyList<BrowserKey> Modifiers) ParseCombination(string combination)
    {
        ArgumentNullException.ThrowIfNull(combination);
        var names = new List<string>();
        var current = string.Empty;
        foreach (var character in combination)
        {
            // A plus sign separates keys, except where it is itself the key, as in "Control++".
            if (character == '+' && current.Length != 0)
            {
                names.Add(current);
                current = string.Empty;
            }
            else
            {
                current += character;
            }
        }

        if (current.Length != 0) names.Add(current);
        if (names.Count == 0) throw new BrowserToolException("invalid-key", $"Key {combination} could not be parsed.");
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Count) throw new BrowserToolException("invalid-key", $"Key {combination} contains duplicate keys.");

        var resolved = names.ConvertAll(Resolve);
        var key = resolved[^1];
        var modifiers = resolved.GetRange(0, resolved.Count - 1);
        // Shift selects the upper symbol of a key, as on a keyboard.
        if (key.Text is { Length: 1 } text && char.IsAsciiLetterLower(text[0]) && modifiers.Exists(static modifier => modifier.Key == "Shift"))
        {
            var upper = text.ToUpperInvariant();
            key = key with { Key = upper, Text = upper };
        }
        else if (key.Text is { Length: 1 } letter && char.IsAsciiLetterUpper(letter[0]) && modifiers.Count != 0 && !modifiers.Exists(static modifier => modifier.Key == "Shift"))
        {
            // "Control+A" is the shortcut of the A key, which a keyboard gives as "a" without Shift.
            var lower = letter.ToLowerInvariant();
            key = key with { Key = lower, Text = lower };
        }

        return (key, modifiers);
    }

    /// <summary>Resolves one key name.</summary>
    /// <exception cref="BrowserToolException">The name is not a known key.</exception>
    internal static BrowserKey Resolve(string name)
    {
        if (Keys.TryGetValue(name, out var key)) return key;
        throw new BrowserToolException("invalid-key",
            $"{name} is not a known key. Use a key name such as Enter, Tab, Escape, Backspace, ArrowDown, PageUp, F5, a, A, or 1, " +
            "optionally after the modifiers Control, Shift, Alt, and Meta, as in Control+Shift+R.");
    }

    internal static bool IsKnown(string name) => Keys.ContainsKey(name);

    /// <summary>Finds the key of the main block of the keyboard that types a character, with Shift for the upper symbol of a key.</summary>
    /// <remarks>
    /// The names of <see cref="Resolve"/> take <c>*</c>, <c>+</c>, <c>-</c>, and <c>/</c> for the keys of the numeric keypad, as
    /// Puppeteer does. A text is typed on the main block, where each of the four is on a key as well.
    /// </remarks>
    internal static bool TryResolveCharacter(char character, out BrowserKey key) => Characters.TryGetValue(character, out key);

    private static Dictionary<char, BrowserKey> CreateCharacters()
    {
        // The keys of the main block that type a character, by their code, each with its lower and its upper symbol.
        (string Code, string Symbols)[] symbols =
        [
            ("Backquote", "`~"), ("Digit1", "1!"), ("Digit2", "2@"), ("Digit3", "3#"), ("Digit4", "4$"), ("Digit5", "5%"), ("Digit6", "6^"),
            ("Digit7", "7&"), ("Digit8", "8*"), ("Digit9", "9("), ("Digit0", "0)"), ("Minus", "-_"), ("Equal", "=+"), ("BracketLeft", "[{"),
            ("BracketRight", "]}"), ("Backslash", "\\|"), ("Semicolon", ";:"), ("Quote", "'\""), ("Comma", ",<"), ("Period", ".>"), ("Slash", "/?"),
        ];
        var keys = Create();
        var characters = new Dictionary<char, BrowserKey> { [' '] = keys["Space"] };
        foreach (var (code, pair) in symbols) Add(code, pair[0], pair[1]);
        for (var letter = 'a'; letter <= 'z'; letter++) Add("Key" + char.ToUpperInvariant(letter), letter, char.ToUpperInvariant(letter));
        return characters;

        void Add(string code, char lower, char upper)
        {
            var key = keys[code];
            characters[lower] = key with { Key = lower.ToString(), Text = lower.ToString() };
            characters[upper] = key with { Key = upper.ToString(), Text = upper.ToString(), Shift = true };
        }
    }

    private static Dictionary<string, BrowserKey> Create()
    {
        var keys = new Dictionary<string, BrowserKey>(StringComparer.Ordinal);
        void Add(string name, string key, string code, int keyCode, int location = 0, string? text = null)
            => keys[name] = new BrowserKey(key, code, keyCode, location, text ?? (key.Length == 1 && key[0] >= ' ' ? key : null));

        for (var digit = 0; digit <= 9; digit++)
        {
            var text = digit.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Add(text, text, "Digit" + text, 48 + digit);
            Add("Digit" + text, text, "Digit" + text, 48 + digit);
        }

        const string shiftedDigits = ")!@#$%^&*(";
        for (var digit = 0; digit <= 9; digit++)
        {
            // "*" names the numeric keypad key, as in Puppeteer, and is added below.
            if (shiftedDigits[digit] != '*') Add(shiftedDigits[digit].ToString(), shiftedDigits[digit].ToString(), "Digit" + digit, 48 + digit);
        }

        for (var letter = 'a'; letter <= 'z'; letter++)
        {
            var lower = letter.ToString();
            var upper = lower.ToUpperInvariant();
            var keyCode = 65 + (letter - 'a');
            Add(lower, lower, "Key" + upper, keyCode);
            Add(upper, upper, "Key" + upper, keyCode);
            Add("Key" + upper, lower, "Key" + upper, keyCode);
        }

        for (var function = 1; function <= 24; function++) Add("F" + function, "F" + function, "F" + function, 111 + function);

        Add("Power", "Power", "Power", 0);
        Add("Eject", "Eject", "Eject", 0);
        Add("Abort", "Cancel", "Abort", 3);
        Add("Cancel", "Cancel", "Abort", 3);
        Add("Help", "Help", "Help", 6);
        Add("Backspace", "Backspace", "Backspace", 8);
        Add("Tab", "Tab", "Tab", 9);
        Add("Numpad5", "Clear", "Numpad5", 12, 3);
        Add("Clear", "Clear", "Numpad5", 12, 3);
        Add("NumpadEqual", "Clear", "NumpadEqual", 12, 3);
        Add("NumpadEnter", "Enter", "NumpadEnter", 13, 3, "\r");
        Add("Enter", "Enter", "Enter", 13, 0, "\r");
        Add("\r", "Enter", "Enter", 13, 0, "\r");
        Add("\n", "Enter", "Enter", 13, 0, "\r");
        Add("ShiftLeft", "Shift", "ShiftLeft", 16, 1);
        Add("ShiftRight", "Shift", "ShiftRight", 16, 2);
        Add("Shift", "Shift", "ShiftLeft", 16, 1);
        Add("ControlLeft", "Control", "ControlLeft", 17, 1);
        Add("ControlRight", "Control", "ControlRight", 17, 2);
        Add("Control", "Control", "ControlLeft", 17, 1);
        Add("AltLeft", "Alt", "AltLeft", 18, 1);
        Add("AltRight", "Alt", "AltRight", 18, 2);
        Add("Alt", "Alt", "AltLeft", 18, 1);
        Add("Pause", "Pause", "Pause", 19);
        Add("CapsLock", "CapsLock", "CapsLock", 20);
        Add("Escape", "Escape", "Escape", 27);
        Add("Convert", "Convert", "Convert", 28);
        Add("NonConvert", "NonConvert", "NonConvert", 29);
        Add("Accept", "Accept", "", 30);
        Add("ModeChange", "ModeChange", "", 31);
        Add("Space", " ", "Space", 32);
        Add(" ", " ", "Space", 32);
        Add("Numpad9", "PageUp", "Numpad9", 33, 3);
        Add("PageUp", "PageUp", "PageUp", 33);
        Add("Numpad3", "PageDown", "Numpad3", 34, 3);
        Add("PageDown", "PageDown", "PageDown", 34);
        Add("End", "End", "End", 35);
        Add("Numpad1", "End", "Numpad1", 35, 3);
        Add("Home", "Home", "Home", 36);
        Add("Numpad7", "Home", "Numpad7", 36, 3);
        Add("ArrowLeft", "ArrowLeft", "ArrowLeft", 37);
        Add("Numpad4", "ArrowLeft", "Numpad4", 37, 3);
        Add("Numpad8", "ArrowUp", "Numpad8", 38, 3);
        Add("ArrowUp", "ArrowUp", "ArrowUp", 38);
        Add("ArrowRight", "ArrowRight", "ArrowRight", 39);
        Add("Numpad6", "ArrowRight", "Numpad6", 39, 3);
        Add("Numpad2", "ArrowDown", "Numpad2", 40, 3);
        Add("ArrowDown", "ArrowDown", "ArrowDown", 40);
        Add("Select", "Select", "Select", 41);
        Add("Print", "Print", "", 42);
        Add("Open", "Execute", "Open", 43);
        Add("Execute", "Execute", "Open", 43);
        Add("PrintScreen", "PrintScreen", "PrintScreen", 44);
        Add("Insert", "Insert", "Insert", 45);
        Add("Numpad0", "Insert", "Numpad0", 45, 3);
        Add("Delete", "Delete", "Delete", 46);
        Add("NumpadDecimal", "\0", "NumpadDecimal", 46, 3);
        Add("\0", "\0", "NumpadDecimal", 46, 3);
        Add("MetaLeft", "Meta", "MetaLeft", 91, 1);
        Add("MetaRight", "Meta", "MetaRight", 92, 2);
        Add("Meta", "Meta", "MetaLeft", 91, 1);
        Add("ContextMenu", "ContextMenu", "ContextMenu", 93);
        Add("NumpadMultiply", "*", "NumpadMultiply", 106, 3);
        Add("*", "*", "NumpadMultiply", 106, 3);
        Add("NumpadAdd", "+", "NumpadAdd", 107, 3);
        Add("+", "+", "NumpadAdd", 107, 3);
        Add("NumpadSubtract", "-", "NumpadSubtract", 109, 3);
        Add("-", "-", "NumpadSubtract", 109, 3);
        Add("NumpadDivide", "/", "NumpadDivide", 111, 3);
        Add("/", "/", "NumpadDivide", 111, 3);
        Add("NumLock", "NumLock", "NumLock", 144);
        Add("ScrollLock", "ScrollLock", "ScrollLock", 145);
        Add("AudioVolumeMute", "AudioVolumeMute", "AudioVolumeMute", 173);
        Add("AudioVolumeDown", "AudioVolumeDown", "AudioVolumeDown", 174);
        Add("AudioVolumeUp", "AudioVolumeUp", "AudioVolumeUp", 175);
        Add("MediaTrackNext", "MediaTrackNext", "MediaTrackNext", 176);
        Add("MediaTrackPrevious", "MediaTrackPrevious", "MediaTrackPrevious", 177);
        Add("MediaStop", "MediaStop", "MediaStop", 178);
        Add("MediaPlayPause", "MediaPlayPause", "MediaPlayPause", 179);
        Add("Semicolon", ";", "Semicolon", 186);
        Add(";", ";", "Semicolon", 186);
        Add(":", ":", "Semicolon", 186);
        Add("Equal", "=", "Equal", 187);
        Add("=", "=", "Equal", 187);
        Add("Comma", ",", "Comma", 188);
        Add(",", ",", "Comma", 188);
        Add("<", "<", "Comma", 188);
        Add("Minus", "-", "Minus", 189);
        Add("_", "_", "Minus", 189);
        Add("Period", ".", "Period", 190);
        Add(".", ".", "Period", 190);
        Add(">", ">", "Period", 190);
        Add("Slash", "/", "Slash", 191);
        Add("?", "?", "Slash", 191);
        Add("Backquote", "`", "Backquote", 192);
        Add("`", "`", "Backquote", 192);
        Add("~", "~", "Backquote", 192);
        Add("BracketLeft", "[", "BracketLeft", 219);
        Add("[", "[", "BracketLeft", 219);
        Add("{", "{", "BracketLeft", 219);
        Add("Backslash", "\\", "Backslash", 220);
        Add("\\", "\\", "Backslash", 220);
        Add("|", "|", "Backslash", 220);
        Add("BracketRight", "]", "BracketRight", 221);
        Add("]", "]", "BracketRight", 221);
        Add("}", "}", "BracketRight", 221);
        Add("Quote", "'", "Quote", 222);
        Add("'", "'", "Quote", 222);
        Add("\"", "\"", "Quote", 222);
        Add("AltGraph", "AltGraph", "AltGraph", 225);
        Add("Attn", "Attn", "", 246);
        Add("Props", "CrSel", "Props", 247);
        Add("CrSel", "CrSel", "Props", 247);
        Add("ExSel", "ExSel", "", 248);
        Add("EraseEof", "EraseEof", "", 249);
        Add("Play", "Play", "", 250);
        Add("ZoomOut", "ZoomOut", "", 251);
        Add("SoftLeft", "SoftLeft", "SoftLeft", 0, 4);
        Add("SoftRight", "SoftRight", "SoftRight", 0, 4);
        Add("Camera", "Camera", "Camera", 44, 4);
        Add("Call", "Call", "Call", 0, 4);
        Add("EndCall", "EndCall", "EndCall", 95, 4);
        Add("VolumeDown", "VolumeDown", "VolumeDown", 182, 4);
        Add("VolumeUp", "VolumeUp", "VolumeUp", 183, 4);
        return keys;
    }
}
