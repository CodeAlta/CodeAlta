using System.Text;

namespace CodeAlta.Plugins.Abstractions;

/// <summary>Logical modifier flags, independent of frontend modifier ordinals.</summary>
[Flags]
public enum PluginKeyModifiers
{
    /// <summary>No modifiers.</summary>
    None = 0,
    /// <summary>Control.</summary>
    Ctrl = 1,
    /// <summary>Alt.</summary>
    Alt = 2,
    /// <summary>Shift.</summary>
    Shift = 4,
    /// <summary>Meta; not an alias for a platform's primary accelerator.</summary>
    Meta = 8,
}

/// <summary>Stable named logical keys, distinct from Unicode character identities.</summary>
public enum PluginKey
{
    /// <summary>No named key; invalid unless a gesture has a character identity.</summary>
    None = 0,
    /// <summary>Enter.</summary>
    Enter = 1,
    /// <summary>Escape.</summary>
    Escape = 2,
    /// <summary>Backspace.</summary>
    Backspace = 3,
    /// <summary>Tab.</summary>
    Tab = 4,
    /// <summary>Space, distinct from the space character.</summary>
    Space = 5,
    /// <summary>Up arrow.</summary>
    Up = 6,
    /// <summary>Down arrow.</summary>
    Down = 7,
    /// <summary>Left arrow.</summary>
    Left = 8,
    /// <summary>Right arrow.</summary>
    Right = 9,
    /// <summary>Home.</summary>
    Home = 10,
    /// <summary>End.</summary>
    End = 11,
    /// <summary>Page up.</summary>
    PageUp = 12,
    /// <summary>Page down.</summary>
    PageDown = 13,
    /// <summary>Insert.</summary>
    Insert = 14,
    /// <summary>Delete.</summary>
    Delete = 15,
    /// <summary>Function key 1.</summary>
    F1 = 16,
    /// <summary>Function key 2.</summary>
    F2 = 17,
    /// <summary>Function key 3.</summary>
    F3 = 18,
    /// <summary>Function key 4.</summary>
    F4 = 19,
    /// <summary>Function key 5.</summary>
    F5 = 20,
    /// <summary>Function key 6.</summary>
    F6 = 21,
    /// <summary>Function key 7.</summary>
    F7 = 22,
    /// <summary>Function key 8.</summary>
    F8 = 23,
    /// <summary>Function key 9.</summary>
    F9 = 24,
    /// <summary>Function key 10.</summary>
    F10 = 25,
    /// <summary>Function key 11.</summary>
    F11 = 26,
    /// <summary>Function key 12.</summary>
    F12 = 27,
}

/// <summary>An immutable named key or Unicode scalar with logical modifiers.</summary>
/// <remarks>
/// Only Unicode letters are normalized to invariant uppercase. Modifiers remain exact; no Tab/Control+I,
/// Enter/Control+M, physical-key, numpad, left/right, or platform accelerator aliases are inferred.
/// The default value is invalid. Frontends may be unable to represent otherwise valid scalar gestures.
/// </remarks>
public readonly record struct PluginKeyGesture
{
    /// <summary>Creates a named-key gesture.</summary>
    /// <param name="key">A defined named key other than <see cref="PluginKey.None"/>.</param>
    /// <param name="modifiers">Logical modifier flags.</param>
    /// <exception cref="ArgumentOutOfRangeException">The key is None or undefined, or modifiers contain unknown bits.</exception>
    public PluginKeyGesture(PluginKey key, PluginKeyModifiers modifiers = PluginKeyModifiers.None)
    {
        if ((int)key is < (int)PluginKey.Enter or > (int)PluginKey.F12)
        {
            throw new ArgumentOutOfRangeException(nameof(key), key, "A defined named key is required.");
        }
        ValidateModifiers(modifiers);
        Key = key;
        Character = null;
        Modifiers = modifiers;
    }

    /// <summary>Creates a BMP character gesture, normalizing only letters to invariant uppercase.</summary>
    /// <param name="character">A non-control, non-surrogate character.</param>
    /// <param name="modifiers">Logical modifier flags.</param>
    /// <exception cref="ArgumentOutOfRangeException">The character is a surrogate or control, or modifiers contain unknown bits.</exception>
    public PluginKeyGesture(char character, PluginKeyModifiers modifiers = PluginKeyModifiers.None)
        : this(CreateRune(character), modifiers)
    {
    }

    /// <summary>Creates a Unicode scalar gesture, normalizing only letters to invariant uppercase.</summary>
    /// <param name="character">A non-control Unicode scalar, including supplementary scalars.</param>
    /// <param name="modifiers">Logical modifier flags.</param>
    /// <exception cref="ArgumentOutOfRangeException">The scalar is a control (including default Rune/NUL), or modifiers contain unknown bits.</exception>
    public PluginKeyGesture(Rune character, PluginKeyModifiers modifiers = PluginKeyModifiers.None)
    {
        if (Rune.IsControl(character))
        {
            throw new ArgumentOutOfRangeException(nameof(character), character, "Control characters are not logical key identities.");
        }
        ValidateModifiers(modifiers);
        Key = PluginKey.None;
        Character = Rune.IsLetter(character) ? Rune.ToUpperInvariant(character) : character;
        Modifiers = modifiers;
    }

    /// <summary>Gets the named key, or None for a character gesture or the invalid default value.</summary>
    public PluginKey Key { get; }

    /// <summary>Gets the normalized scalar, or null for a named key or the invalid default value.</summary>
    public Rune? Character { get; }

    /// <summary>Gets the exact logical modifiers.</summary>
    public PluginKeyModifiers Modifiers { get; }

    /// <summary>Gets whether this value was constructed with a valid key or scalar identity.</summary>
    public bool IsValid => Key != PluginKey.None || Character is not null;

    /// <summary>Formats a presentation label including Meta; returns empty text for the invalid default.</summary>
    /// <returns>A label, never a parser format or structural identity.</returns>
    public override string ToString()
    {
        if (!IsValid) return string.Empty;
        var builder = new StringBuilder();
        if ((Modifiers & PluginKeyModifiers.Ctrl) != 0) builder.Append("Ctrl+");
        if ((Modifiers & PluginKeyModifiers.Alt) != 0) builder.Append("Alt+");
        if ((Modifiers & PluginKeyModifiers.Shift) != 0) builder.Append("Shift+");
        if ((Modifiers & PluginKeyModifiers.Meta) != 0) builder.Append("Meta+");
        builder.Append(Character is { } character ? character.ToString() : Key.ToString());
        return builder.ToString();
    }

    private static Rune CreateRune(char character)
    {
        if (!Rune.TryCreate(character, out var rune))
        {
            throw new ArgumentOutOfRangeException(nameof(character), character, "A surrogate is not a Unicode scalar.");
        }
        return rune;
    }

    private static void ValidateModifiers(PluginKeyModifiers modifiers)
    {
        if ((modifiers & ~(PluginKeyModifiers.Ctrl | PluginKeyModifiers.Alt | PluginKeyModifiers.Shift | PluginKeyModifiers.Meta)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(modifiers), modifiers, "Unknown logical modifier bits.");
        }
    }
}

/// <summary>An immutable binding of one through four ordered logical strokes.</summary>
/// <remarks>One stroke is a gesture; two through four strokes form a sequence. Null on a command means unbound.</remarks>
public sealed class PluginKeyBinding
{
    private readonly PluginKeyGesture[] _gestures;

    /// <summary>Creates a binding by defensively copying validated strokes.</summary>
    /// <param name="gestures">One through four valid gestures, in order.</param>
    /// <exception cref="ArgumentNullException">The gestures array is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The array has fewer than one or more than four strokes.</exception>
    /// <exception cref="ArgumentException">A stroke is the invalid default gesture.</exception>
    public PluginKeyBinding(params PluginKeyGesture[] gestures)
    {
        ArgumentNullException.ThrowIfNull(gestures);
        if (gestures.Length is < 1 or > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(gestures), "A binding requires one through four strokes.");
        }
        _gestures = gestures.ToArray();
        foreach (var gesture in _gestures)
        {
            if (!gesture.IsValid)
            {
                throw new ArgumentException("Every stroke must have a valid key or scalar identity.", nameof(gestures));
            }
        }
    }

    /// <summary>Gets the number of ordered strokes.</summary>
    public int Count => _gestures.Length;

    /// <summary>Gets a read-only view of the defensively copied strokes.</summary>
    public ReadOnlySpan<PluginKeyGesture> Gestures => _gestures;

    /// <summary>Formats stroke labels separated by spaces, for presentation only.</summary>
    /// <returns>A label that is neither parsed nor used as binding identity.</returns>
    public override string ToString()
    {
        var builder = new StringBuilder();
        foreach (var gesture in _gestures)
        {
            if (builder.Length > 0) builder.Append(' ');
            builder.Append(gesture.ToString());
        }
        return builder.ToString();
    }
}
