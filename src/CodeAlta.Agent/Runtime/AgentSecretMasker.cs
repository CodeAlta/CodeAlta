using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace CodeAlta.Agent.Runtime;

/// <summary>
/// Replaces the values of the secrets of the machine by <see cref="Mask"/> wherever they would be written or shown: in
/// the outputs of tools, the events of sessions (and so their journals and what the user sees), the logs and the
/// telemetry. It reads the values from the store when they change, and never keeps them anywhere else than in its memory.
/// </summary>
/// <remarks>
/// A value shorter than <see cref="MinimumLength"/> is not masked: masking it would hide ordinary words. A text that
/// comes in parts (the deltas of a stream) is masked through <see cref="CreateStream"/>, which holds back the end of a
/// part that could begin a secret until the next part tells. A value it masked once stays masked for as long as it lives,
/// also once the store no longer gives it (a rotation, a deletion, a value taken away, the bounded and dated values the store keeps
/// for masking): a process or a stream of this host that took it may still write it.
/// </remarks>
public sealed class AgentSecretMasker
{
    /// <summary>The text that stands for a secret.</summary>
    public const string Mask = "***";

    /// <summary>The length below which a value is not masked.</summary>
    public const int MinimumLength = 4;

    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1);
    private static readonly JavaScriptEncoder[] Encoders = [JavaScriptEncoder.Default, JavaScriptEncoder.UnsafeRelaxedJsonEscaping];

    private readonly Func<IReadOnlyCollection<string>> _values;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private Patterns _patterns = Patterns.Empty;
    private long _readAt = long.MinValue;
    // Every value read from the store since this masker was made, under the gate: the work of this process may still hold one.
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    /// <summary>Initializes a new instance of the <see cref="AgentSecretMasker"/> class.</summary>
    /// <param name="values">Gives the values of the secrets; read again at most once a second.</param>
    /// <exception cref="ArgumentNullException"><paramref name="values"/> is <see langword="null"/>.</exception>
    public AgentSecretMasker(Func<IReadOnlyCollection<string>> values)
        : this(values, TimeProvider.System)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="AgentSecretMasker"/> class.</summary>
    /// <param name="values">Gives the values of the secrets.</param>
    /// <param name="time">The clock that says when to read them again.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public AgentSecretMasker(Func<IReadOnlyCollection<string>> values, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(time);
        _values = values;
        _time = time;
    }

    /// <summary>Reads the values again at the next use, after a secret was set, rotated or deleted.</summary>
    public void Refresh()
    {
        lock (_gate) _readAt = long.MinValue;
    }

    /// <summary>Gets whether there is no secret to mask now.</summary>
    public bool IsEmpty => Current().Plain.Length == 0;

    /// <summary>
    /// Creates the masking of one text that comes in parts, such as the deltas of a content: what a part gives is masked
    /// and can be shown at once, and its end that could begin a secret is held back until a next part, or the end of the
    /// text (<see cref="AgentSecretTextStream.Flush"/>), says whether it does.
    /// </summary>
    /// <returns>The masking of the text, which belongs to its caller.</returns>
    public AgentSecretTextStream CreateStream() => new(this);

    /// <summary>
    /// Replaces each secret of an exception by <see cref="Mask"/> before it goes where the masking of the logs does not
    /// reach (an export of telemetry): its message, its stack and its inner exceptions, which its text holds.
    /// </summary>
    /// <param name="exception">The exception, or <see langword="null"/>.</param>
    /// <returns>The exception, the same instance when it holds no secret; else an <see cref="AgentMaskedException"/> that tells the same, masked.</returns>
    [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(exception))]
    public Exception? MaskException(Exception? exception)
    {
        if (exception is null) return null;
        string text;
        try
        {
            text = exception.ToString();
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            // An exception whose text cannot be made is told by its type and its message alone.
            text = $"{exception.GetType().FullName}: {exception.Message}";
        }

        if (!Contains(text) && !Contains(exception.Message)) return exception;
        return new AgentMaskedException(exception.GetType().FullName ?? exception.GetType().Name, MaskText(exception.Message), MaskText(text),
            MaskException(exception.InnerException));
    }

    /// <summary>Tells whether a text holds a secret.</summary>
    /// <param name="text">The text.</param>
    /// <returns><see langword="true"/> when it holds one.</returns>
    public bool Contains(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (var value in Current().Plain)
        {
            if (text.Contains(value, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    /// <summary>Replaces each secret of a text by <see cref="Mask"/>.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The text, the same instance when it holds no secret.</returns>
    [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(text))]
    public string? MaskText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var masked = text;
        foreach (var value in Current().Plain)
        {
            if (masked.Contains(value, StringComparison.Ordinal)) masked = masked.Replace(value, Mask, StringComparison.Ordinal);
        }

        return masked;
    }

    /// <summary>Replaces each secret of a text by <see cref="Mask"/> where it is, as a formatted line of a log.</summary>
    /// <param name="text">The text, changed in place.</param>
    /// <returns>The length of the text now: a mask is never longer than the secret it replaces.</returns>
    public int MaskInPlace(Span<char> text)
    {
        var length = text.Length;
        foreach (var value in Current().Plain)
        {
            int found;
            var from = 0;
            while ((found = text[from..length].IndexOf(value.AsSpan(), StringComparison.Ordinal)) >= 0)
            {
                var at = from + found;
                Mask.AsSpan().CopyTo(text[at..]);
                text[(at + value.Length)..length].CopyTo(text[(at + Mask.Length)..]);
                length -= value.Length - Mask.Length;
                from = at + Mask.Length;
            }
        }

        return length;
    }

    /// <summary>
    /// Replaces each secret inside the strings of a JSON text by <see cref="Mask"/>, as it is escaped there: the names,
    /// numbers and punctuation of the JSON are left as they are, so that the text stays valid JSON.
    /// </summary>
    /// <param name="json">The JSON text.</param>
    /// <returns>The text, the same instance when it holds no secret.</returns>
    public string MaskJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var patterns = Current();
        if (patterns.Json.Length == 0) return json;
        StringBuilder? builder = null;
        var copied = 0;
        var inString = false;
        var isName = false;
        // Whether each open container is an object, and whether its next string is the name of a property.
        var objects = new List<bool>();
        var expectName = false;
        for (var index = 0; index < json.Length; index++)
        {
            var ch = json[index];
            if (!inString)
            {
                switch (ch)
                {
                    case '"':
                        inString = true;
                        isName = expectName;
                        break;
                    case '{':
                        objects.Add(true);
                        expectName = true;
                        break;
                    case '[':
                        objects.Add(false);
                        expectName = false;
                        break;
                    case '}' or ']':
                        if (objects.Count > 0) objects.RemoveAt(objects.Count - 1);
                        expectName = false;
                        break;
                    case ':':
                        expectName = false;
                        break;
                    case ',':
                        expectName = objects.Count > 0 && objects[^1];
                        break;
                }

                continue;
            }

            if (ch == '"')
            {
                inString = false;
                continue;
            }

            // The name of a property is no value: it is left as it is, its escapes skipped.
            if (!isName && Match(json, index, patterns.Json) is { } length)
            {
                (builder ??= new StringBuilder(json.Length)).Append(json, copied, index - copied).Append(Mask);
                index += length - 1;
                copied = index + 1;
                continue;
            }

            // An escape that begins no secret is skipped whole, its quote included.
            if (ch == '\\') index++;
        }

        return builder is null ? json : builder.Append(json, copied, json.Length - copied).ToString();
    }

    /// <summary>Replaces each secret of a tool result by <see cref="Mask"/>: its text, its error, what a host shows and its structured value.</summary>
    /// <param name="result">The result.</param>
    /// <returns>The result, the same instance when it holds no secret.</returns>
    public AgentToolResult MaskResult(AgentToolResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (Current().Plain.Length == 0) return result;
        var changed = false;
        var items = new AgentToolResultItem[result.Items.Count];
        for (var index = 0; index < items.Length; index++)
        {
            items[index] = result.Items[index] is AgentToolResultItem.Text text && Contains(text.Value) ? new AgentToolResultItem.Text(MaskText(text.Value)) : result.Items[index];
            changed |= !ReferenceEquals(items[index], result.Items[index]);
        }

        var error = MaskText(result.Error);
        var display = MaskText(result.Display);
        var structured = MaskElement(result.Structured);
        var details = MaskElement(result.Details);
        changed |= !ReferenceEquals(error, result.Error) || !ReferenceEquals(display, result.Display)
            || !Equals(structured, result.Structured) || !Equals(details, result.Details);
        return changed ? result with { Items = items, Error = error, Display = display, Structured = structured, Details = details } : result;
    }

    /// <summary>Replaces each secret of events by <see cref="Mask"/>.</summary>
    /// <param name="events">The events.</param>
    /// <returns>The events, the same list when none holds a secret.</returns>
    public IReadOnlyList<AgentEvent> MaskEvents(IReadOnlyList<AgentEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0 || Current().Plain.Length == 0) return events;
        AgentEvent[]? masked = null;
        for (var index = 0; index < events.Count; index++)
        {
            var @event = events[index];
            var json = @event.ToJson();
            var maskedJson = MaskJson(json);
            if (ReferenceEquals(json, maskedJson)) continue;
            masked ??= [.. events];
            masked[index] = JsonSerializer.Deserialize(maskedJson, AgentJsonSerializerContext.Default.AgentEvent) ?? @event;
        }

        return masked ?? events;
    }

    // Masks a part of a text that comes in parts: each secret it holds whole is masked, and its end that could begin a
    // secret longer than what is left (never as long as the longest secret) is held back, unless the part is the last.
    internal string MaskPart(string text, bool last, out string held)
    {
        held = string.Empty;
        var patterns = Current();
        if (text.Length == 0 || patterns.Plain.Length == 0) return text;
        StringBuilder? builder = null;
        var copied = 0;
        var index = 0;
        while (index < text.Length)
        {
            var next = text.AsSpan(index).IndexOfAny(patterns.FirstCharacters);
            if (next < 0) break;
            index += next;
            var rest = text.AsSpan(index);
            int? found = null;
            var open = false;
            // The longest first: a secret that the rest may still become wins over a shorter one it already holds.
            foreach (var value in patterns.Plain)
            {
                if (rest.Length < value.Length)
                {
                    if (!last && value.AsSpan().StartsWith(rest, StringComparison.Ordinal))
                    {
                        open = true;
                        break;
                    }
                }
                else if (rest.StartsWith(value, StringComparison.Ordinal))
                {
                    found = value.Length;
                    break;
                }
            }

            if (open)
            {
                held = text[index..];
                return builder is null ? text[..index] : builder.Append(text, copied, index - copied).ToString();
            }

            if (found is { } length)
            {
                (builder ??= new StringBuilder(text.Length)).Append(text, copied, index - copied).Append(Mask);
                index += length;
                copied = index;
                continue;
            }

            index++;
        }

        return builder is null ? text : builder.Append(text, copied, text.Length - copied).ToString();
    }

    private JsonElement? MaskElement(JsonElement? element)
    {
        if (element is not { } value) return element;
        var json = value.GetRawText();
        var masked = MaskJson(json);
        return ReferenceEquals(json, masked) ? element : JsonDocument.Parse(masked).RootElement.Clone();
    }

    private static int? Match(string text, int index, string[] patterns)
    {
        foreach (var pattern in patterns)
        {
            if (string.CompareOrdinal(text, index, pattern, 0, pattern.Length) == 0) return pattern.Length;
        }

        return null;
    }

    private Patterns Current()
    {
        lock (_gate)
        {
            var now = _time.GetTimestamp();
            if (_readAt != long.MinValue && _time.GetElapsedTime(_readAt, now) < RefreshInterval) return _patterns;
            _readAt = now;
            IReadOnlyCollection<string> values;
            try
            {
                values = _values();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                // The store is being written or damaged: the values read last are masked meanwhile.
                return _patterns;
            }

            var count = _seen.Count;
            foreach (var value in values)
            {
                if (value is { Length: >= MinimumLength }) _seen.Add(value);
            }

            return count == _seen.Count && _patterns.Plain.Length == count ? _patterns : _patterns = Patterns.Of(_seen);
        }
    }

    // The values to mask, the longest first so that a secret that holds another is masked whole; and their forms as
    // the JSON of the events writes them.
    private sealed class Patterns(string[] plain, string[] json)
    {
        public static Patterns Empty { get; } = new([], []);

        public string[] Plain { get; } = plain;

        public string[] Json { get; } = json;

        // The characters a secret starts with: a text is searched for them, not read character by character.
        public System.Buffers.SearchValues<char> FirstCharacters { get; } = System.Buffers.SearchValues.Create([.. plain.Select(static value => value[0]).Distinct()]);

        public static Patterns Of(IReadOnlyCollection<string> values)
        {
            var plain = values.Where(static value => value is { Length: >= MinimumLength }).Distinct(StringComparer.Ordinal)
                .OrderByDescending(static value => value.Length).ToArray();
            if (plain.Length == 0) return Empty;
            var json = plain.SelectMany(static value => Encoders.Select(encoder => JsonEncodedText.Encode(value, encoder).Value).Append(value))
                .Distinct(StringComparer.Ordinal).OrderByDescending(static value => value.Length).ToArray();
            return new Patterns(plain, json);
        }
    }
}
