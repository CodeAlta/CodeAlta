using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CodeAlta.Pseudonymization;

/// <summary>One pseudonym of a session and the value it stands for.</summary>
/// <param name="Token">The pseudonym, such as <c>[PERSON_001]</c>.</param>
/// <param name="Entity">The kind of the value.</param>
/// <param name="Value">The value.</param>
public sealed record PseudonymEntry(string Token, string Entity, string Value)
{
    /// <summary>Gets how many times it was sent.</summary>
    public int Count { get; init; }
}

/// <summary>
/// The pseudonyms of one session: each value of a kind has one pseudonym, numbered in the order it was first seen, and a
/// pseudonym never stands for another value afterwards, so that what a provider caches stays valid. It never leaves the
/// machine; its owner stores it encrypted. Its methods may be called from several threads.
/// </summary>
public sealed partial class PseudonymTable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(string Entity, string Value), string> _byValue = [];
    private readonly Dictionary<string, PseudonymEntry> _byToken = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _counters = new(StringComparer.Ordinal);
    private int _version;
    // The values, the longest first, made again only when a value comes in: each text of a request looks for all of them.
    private IReadOnlyList<(string Entity, string Value)>? _knownValues;

    /// <summary>Gets a number that changes each time the table changes.</summary>
    public int Version
    {
        get
        {
            lock (_gate) return _version;
        }
    }

    /// <summary>Gets how many pseudonyms the table holds.</summary>
    public int Count
    {
        get
        {
            lock (_gate) return _byToken.Count;
        }
    }

    /// <summary>Gets the pseudonyms, in the order they were made.</summary>
    public IReadOnlyList<PseudonymEntry> Entries
    {
        get
        {
            lock (_gate) return [.. _byToken.Values];
        }
    }

    /// <summary>Gets the pseudonym of a value, made when it has none.</summary>
    /// <param name="entity">The kind of the value.</param>
    /// <param name="value">The value.</param>
    /// <returns>The pseudonym.</returns>
    /// <exception cref="ArgumentException">An argument is blank.</exception>
    public string TokenFor(string entity, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entity);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        entity = PiiEntities.CanonicalName(entity);
        lock (_gate)
        {
            if (_byValue.TryGetValue((entity, value), out var known))
            {
                var entry = _byToken[known];
                _byToken[known] = entry with { Count = entry.Count + 1 };
                _version++;
                return known;
            }

            var number = _counters.TryGetValue(entity, out var last) ? last + 1 : 1;
            _counters[entity] = number;
            var token = Format(entity, number);
            _byValue[(entity, value)] = token;
            _byToken[token] = new PseudonymEntry(token, entity, value) { Count = 1 };
            _knownValues = null;
            _version++;
            return token;
        }
    }

    /// <summary>Gets the value a pseudonym stands for.</summary>
    /// <param name="token">The pseudonym, in its exact form.</param>
    /// <param name="value">The value.</param>
    /// <returns><see langword="true"/> when the table has it.</returns>
    public bool TryGetValue(string token, out string value)
    {
        lock (_gate)
        {
            if (_byToken.TryGetValue(token, out var entry))
            {
                value = entry.Value;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    /// <summary>Gets the values the table has, longest first: they are hidden wherever they are found again.</summary>
    /// <returns>Each value with its kind.</returns>
    public IReadOnlyList<(string Entity, string Value)> KnownValues()
    {
        lock (_gate)
        {
            return _knownValues ??= [.. _byValue.Keys.OrderByDescending(static key => key.Value.Length)];
        }
    }

    /// <summary>Gets the pseudonym of a kind and a number: <c>[PERSON_001]</c>.</summary>
    /// <param name="entity">The kind.</param>
    /// <param name="number">The number, from 1.</param>
    /// <returns>The pseudonym.</returns>
    public static string Format(string entity, int number)
        => string.Create(CultureInfo.InvariantCulture, $"[{entity}_{number:000}]");

    /// <summary>
    /// Finds the pseudonym a text means at a place, also when a model changed it a little: the case, the brackets, a dash
    /// or a space for the underscore, the zeros, or one letter of its kind. Only a pseudonym of this table counts, and only
    /// one: a form two pseudonyms could be is left as it is.
    /// </summary>
    /// <param name="candidate">What looks like a pseudonym.</param>
    /// <returns>The pseudonym of the table; <see langword="null"/> when none, or more than one, fits.</returns>
    public string? Canonical(string candidate)
    {
        var match = TolerantToken().Match(candidate);
        if (!match.Success || match.Length != candidate.Length) return null;
        var bracketed = candidate.StartsWith('[') && candidate.EndsWith(']');
        // Without brackets, a space is ordinary text ("person 1"): only a dash or an underscore counts.
        if (!bracketed && match.Groups["sep"].Value == " ") return null;
        var label = RemoveAccents(match.Groups["label"].Value).ToUpperInvariant();
        if (!int.TryParse(match.Groups["number"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)) return null;
        lock (_gate)
        {
            var exact = Format(label, number);
            if (_byToken.ContainsKey(exact)) return exact;
            string? found = null;
            foreach (var knownLabel in _counters.Keys)
            {
                if (!IsOneEditAway(label, knownLabel)) continue;
                var token = Format(knownLabel, number);
                if (!_byToken.ContainsKey(token)) continue;
                if (found is not null) return null;
                found = token;
            }

            return found;
        }
    }

    /// <summary>Writes the table.</summary>
    /// <returns>Its JSON.</returns>
    public byte[] ToJson()
    {
        lock (_gate)
        {
            var document = new PseudonymTableDocument
            {
                Entries = [.. _byToken.Values],
                Counters = new Dictionary<string, int>(_counters, StringComparer.Ordinal),
            };
            return JsonSerializer.SerializeToUtf8Bytes(document, PseudonymJsonContext.Default.PseudonymTableDocument);
        }
    }

    /// <summary>Reads a table written by <see cref="ToJson"/>.</summary>
    /// <param name="json">Its JSON.</param>
    /// <returns>The table.</returns>
    /// <exception cref="JsonException">The JSON is not a table.</exception>
    public static PseudonymTable FromJson(ReadOnlySpan<byte> json)
    {
        var document = JsonSerializer.Deserialize(json, PseudonymJsonContext.Default.PseudonymTableDocument) ?? throw new JsonException("The table is empty.");
        var table = new PseudonymTable();
        foreach (var entry in document.Entries ?? [])
        {
            if (string.IsNullOrEmpty(entry.Token) || string.IsNullOrEmpty(entry.Entity) || string.IsNullOrEmpty(entry.Value)) throw new JsonException("An entry of the table is incomplete.");
            table._byToken[entry.Token] = entry;
            // Match a known value by its current kind, but keep the exact token already sent to the provider.
            table._byValue.TryAdd((PiiEntities.CanonicalName(entry.Entity), entry.Value), entry.Token);
        }

        foreach (var (label, number) in document.Counters ?? [])
        {
            table.KeepCounterAtLeast(label, number);
        }

        // A counter is never behind a pseudonym the table has: a new value never takes the number of another.
        foreach (var entry in table._byToken.Values)
        {
            var match = StrictToken().Match(entry.Token);
            var number = int.Parse(match.Groups["number"].Value, CultureInfo.InvariantCulture);
            table.KeepCounterAtLeast(match.Groups["label"].Value, number);
        }

        return table;
    }

    // Keep the old label for tolerant restoration, and continue numbering under the new label instead of reusing a number.
    private void KeepCounterAtLeast(string label, int number)
    {
        if (_counters.GetValueOrDefault(label) < number) _counters[label] = number;
        var entity = PiiEntities.CanonicalName(label);
        if (entity != label && _counters.GetValueOrDefault(entity) < number) _counters[entity] = number;
    }

    /// <summary>Matches a pseudonym in its exact form.</summary>
    [GeneratedRegex(@"\[(?<label>[A-Z][A-Z_]*?)_(?<number>\d{3,})\]", RegexOptions.CultureInvariant)]
    internal static partial Regex StrictToken();

    /// <summary>Matches what a model may have made of a pseudonym.</summary>
    [GeneratedRegex(@"\[?(?<label>[A-Za-zÀ-ÖØ-öø-ÿ][A-Za-zÀ-ÖØ-öø-ÿ_]{1,24}?)(?<sep>[ _-])0*(?<number>\d{1,4})\]?", RegexOptions.CultureInvariant)]
    internal static partial Regex TolerantToken();

    private static string RemoveAccents(string text)
    {
        var decomposed = text.Normalize(System.Text.NormalizationForm.FormD);
        return string.Concat(decomposed.Where(static character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark));
    }

    private static bool IsOneEditAway(string left, string right)
    {
        if (left == right) return true;
        if (Math.Abs(left.Length - right.Length) > 1) return false;
        if (left.Length == right.Length) return left.Zip(right).Count(static pair => pair.First != pair.Second) == 1;
        var (shorter, longer) = left.Length < right.Length ? (left, right) : (right, left);
        int i = 0, j = 0, edits = 0;
        while (i < shorter.Length && j < longer.Length)
        {
            if (shorter[i] == longer[j])
            {
                i++;
                j++;
                continue;
            }

            if (++edits > 1) return false;
            j++;
        }

        return edits + (longer.Length - j) <= 1;
    }
}

/// <summary>What <see cref="PseudonymTable.ToJson"/> writes.</summary>
internal sealed class PseudonymTableDocument
{
    [JsonPropertyName("entries")]
    public List<PseudonymEntry>? Entries { get; set; }

    [JsonPropertyName("counters")]
    public Dictionary<string, int>? Counters { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PseudonymTableDocument))]
[JsonSerializable(typeof(PseudonymCacheDocument))]
internal sealed partial class PseudonymJsonContext : JsonSerializerContext;
