using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CodeAlta.Plugins.Abstractions;

/// <summary>What a value of the settings of a trigger holds.</summary>
public enum PluginTriggerValueKind
{
    /// <summary>A text.</summary>
    Text,

    /// <summary>True or false.</summary>
    Flag,

    /// <summary>A whole number.</summary>
    Number,

    /// <summary>A list of texts.</summary>
    Texts,
}

/// <summary>One value of the settings of a trigger, as the configuration file writes it.</summary>
public sealed record PluginTriggerValue
{
    private PluginTriggerValue(PluginTriggerValueKind kind, string? text, bool flag, long number, IReadOnlyList<string>? texts)
        => (Kind, Text, Flag, Number, Texts) = (kind, text, flag, number, texts ?? []);

    /// <summary>Gets what the value holds.</summary>
    public PluginTriggerValueKind Kind { get; }

    /// <summary>Gets the text of a <see cref="PluginTriggerValueKind.Text"/> value; null otherwise.</summary>
    public string? Text { get; }

    /// <summary>Gets the flag of a <see cref="PluginTriggerValueKind.Flag"/> value.</summary>
    public bool Flag { get; }

    /// <summary>Gets the number of a <see cref="PluginTriggerValueKind.Number"/> value.</summary>
    public long Number { get; }

    /// <summary>Gets the texts of a <see cref="PluginTriggerValueKind.Texts"/> value; empty otherwise.</summary>
    public IReadOnlyList<string> Texts { get; }

    /// <summary>Creates a text value.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The value.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    public static PluginTriggerValue OfText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new(PluginTriggerValueKind.Text, text, false, 0, null);
    }

    /// <summary>Creates a flag value.</summary>
    /// <param name="flag">The flag.</param>
    /// <returns>The value.</returns>
    public static PluginTriggerValue OfFlag(bool flag) => new(PluginTriggerValueKind.Flag, null, flag, 0, null);

    /// <summary>Creates a number value.</summary>
    /// <param name="number">The number.</param>
    /// <returns>The value.</returns>
    public static PluginTriggerValue OfNumber(long number) => new(PluginTriggerValueKind.Number, null, false, number, null);

    /// <summary>Creates a value that is a list of texts.</summary>
    /// <param name="texts">The texts, in their order.</param>
    /// <returns>The value.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="texts"/> or one of its texts is null.</exception>
    public static PluginTriggerValue OfTexts(IEnumerable<string> texts)
    {
        ArgumentNullException.ThrowIfNull(texts);
        var list = texts.ToArray();
        if (Array.Exists(list, static text => text is null)) throw new ArgumentNullException(nameof(texts), "A list of texts holds no null.");
        return new(PluginTriggerValueKind.Texts, null, false, 0, list);
    }

    /// <summary>Gets the value as one line: what a key of the trigger is made of.</summary>
    /// <returns>The text of a text, <c>true</c> or <c>false</c>, the number, or the texts joined with commas.</returns>
    public override string ToString() => Kind switch
    {
        PluginTriggerValueKind.Text => Text!,
        PluginTriggerValueKind.Flag => Flag ? "true" : "false",
        PluginTriggerValueKind.Number => Number.ToString(CultureInfo.InvariantCulture),
        _ => string.Join(',', Texts),
    };

    /// <summary>Gets whether two values hold the same.</summary>
    /// <param name="other">The other value.</param>
    /// <returns>True when they hold the same kind and the same content.</returns>
    public bool Equals(PluginTriggerValue? other)
        => other is not null && Kind == other.Kind && Text == other.Text && Flag == other.Flag && Number == other.Number && Texts.SequenceEqual(other.Texts, StringComparer.Ordinal);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Kind, StringComparer.Ordinal.GetHashCode(ToString()));
}

/// <summary>
/// The settings of a trigger: the keys of its table in the configuration file besides <c>type</c> and <c>on</c>, such as
/// <c>space = "work"</c>. The keys are kept in their ordinal order, so that two triggers that say the same read the same.
/// </summary>
public sealed class PluginTriggerSettings : IEquatable<PluginTriggerSettings>
{
    private readonly KeyValuePair<string, PluginTriggerValue>[] _values;

    /// <summary>Gets the settings that are empty.</summary>
    public static PluginTriggerSettings Empty { get; } = new([]);

    /// <summary>Creates settings.</summary>
    /// <param name="values">The values by key; a key given twice keeps its last value.</param>
    /// <exception cref="ArgumentNullException"><paramref name="values"/>, a key or a value is null.</exception>
    public PluginTriggerSettings(IEnumerable<KeyValuePair<string, PluginTriggerValue>> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var byKey = new SortedDictionary<string, PluginTriggerValue>(StringComparer.Ordinal);
        foreach (var (key, value) in values)
        {
            ArgumentNullException.ThrowIfNull(key, nameof(values));
            ArgumentNullException.ThrowIfNull(value, nameof(values));
            byKey[key] = value;
        }

        _values = [.. byKey];
    }

    /// <summary>Gets how many keys the settings have.</summary>
    public int Count => _values.Length;

    /// <summary>Gets the keys and their values, in the ordinal order of the keys.</summary>
    public IReadOnlyList<KeyValuePair<string, PluginTriggerValue>> Values => _values;

    /// <summary>Gets the value of a key.</summary>
    /// <param name="key">The key.</param>
    /// <returns>The value, or null when the settings do not have the key.</returns>
    public PluginTriggerValue? Get(string key)
    {
        foreach (var (name, value) in _values)
        {
            if (string.Equals(name, key, StringComparison.Ordinal)) return value;
        }

        return null;
    }

    /// <summary>Gets the text of a key.</summary>
    /// <param name="key">The key.</param>
    /// <returns>The text, or null when the key is missing or is not a text.</returns>
    public string? Text(string key) => Get(key) is { Kind: PluginTriggerValueKind.Text } value ? value.Text : null;

    /// <summary>Gets the flag of a key.</summary>
    /// <param name="key">The key.</param>
    /// <param name="fallback">What a missing key, or a key that is not a flag, gives.</param>
    /// <returns>The flag.</returns>
    public bool Flag(string key, bool fallback = false) => Get(key) is { Kind: PluginTriggerValueKind.Flag } value ? value.Flag : fallback;

    /// <summary>Gets the number of a key.</summary>
    /// <param name="key">The key.</param>
    /// <returns>The number, or null when the key is missing or is not a number.</returns>
    public long? Number(string key) => Get(key) is { Kind: PluginTriggerValueKind.Number } value ? value.Number : null;

    /// <summary>Gets the texts of a key: the list it holds, or the one text it holds.</summary>
    /// <param name="key">The key.</param>
    /// <returns>The texts; empty when the key is missing or holds neither.</returns>
    public IReadOnlyList<string> Texts(string key) => Get(key) switch
    {
        { Kind: PluginTriggerValueKind.Texts } value => value.Texts,
        { Kind: PluginTriggerValueKind.Text } value => [value.Text!],
        _ => [],
    };

    /// <summary>Writes the settings as a JSON object.</summary>
    /// <returns>The JSON object, with the keys in their order.</returns>
    public string ToJson()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var (key, value) in _values)
            {
                switch (value.Kind)
                {
                    case PluginTriggerValueKind.Text: writer.WriteString(key, value.Text); break;
                    case PluginTriggerValueKind.Flag: writer.WriteBoolean(key, value.Flag); break;
                    case PluginTriggerValueKind.Number: writer.WriteNumber(key, value.Number); break;
                    default:
                        writer.WriteStartArray(key);
                        foreach (var text in value.Texts) writer.WriteStringValue(text);
                        writer.WriteEndArray();
                        break;
                }
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <inheritdoc />
    public bool Equals(PluginTriggerSettings? other)
        => other is not null && _values.Length == other._values.Length
            && _values.Zip(other._values).All(static pair => pair.First.Key == pair.Second.Key && pair.First.Value.Equals(pair.Second.Value));

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as PluginTriggerSettings);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var (key, value) in _values) hash.Add(HashCode.Combine(StringComparer.Ordinal.GetHashCode(key), value.GetHashCode()));
        return hash.ToHashCode();
    }
}
