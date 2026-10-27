using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeAlta.Catalog;

namespace CodeAlta.Orchestration.Runtime.Scripts;

/// <summary>
/// The values the scripts of a session keep for its later scripts: <c>store(key, value)</c> and <c>load(key)</c>. Each
/// session has one file, <c>codemode/values/&lt;session&gt;.json</c> of the state root, of at most <see cref="MaxSessionBytes"/>,
/// each value at most <see cref="MaxValueBytes"/> of JSON. A host whose state is thrown away (<c>altacli</c>) has none:
/// the values of a run then last as long as the run.
/// </summary>
public sealed class CodeModeValueStore
{
    /// <summary>The most bytes of JSON one value takes.</summary>
    public const int MaxValueBytes = 64 * 1024;

    /// <summary>The most bytes the values of one session take, keys included.</summary>
    public const int MaxSessionBytes = 1024 * 1024;

    /// <summary>The longest key.</summary>
    public const int MaxKeyLength = 200;

    private readonly ConcurrentDictionary<string, Lock> _gates = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates the store of a folder.</summary>
    /// <param name="root">The folder of the files of the sessions.</param>
    /// <exception cref="ArgumentException"><paramref name="root"/> is blank.</exception>
    public CodeModeValueStore(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.GetFullPath(root);
    }

    /// <summary>Gets the folder of the files of the sessions.</summary>
    public string Root { get; }

    /// <summary>Gives the folder of the values of an instance, under its state root.</summary>
    /// <param name="catalog">The roots of the instance.</param>
    /// <returns>The folder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="catalog"/> is null.</exception>
    public static string RootOf(CatalogOptions catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return Path.Combine(catalog.StateRoot, "codemode", "values");
    }

    /// <summary>Reads a value a script of a session stored.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="key">The key.</param>
    /// <returns>The value as JSON, or null when there is none.</returns>
    /// <exception cref="IOException">The file cannot be read.</exception>
    public string? Load(string sessionId, string key)
    {
        lock (Gate(sessionId))
        {
            return Read(sessionId).TryGetValue(key, out var value) ? value : null;
        }
    }

    /// <summary>Stores a value for the later scripts of a session, or removes it.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="key">The key.</param>
    /// <param name="valueJson">The value as JSON, or null to remove it.</param>
    /// <returns>Why it was refused, or null when it was stored.</returns>
    /// <exception cref="IOException">The file cannot be written.</exception>
    public string? Store(string sessionId, string key, string? valueJson)
    {
        lock (Gate(sessionId))
        {
            var values = Read(sessionId);
            if (Check(values, key, valueJson) is { } refusal) return refusal;
            if (valueJson is null) values.Remove(key); else values[key] = valueJson;
            Write(sessionId, values);
            return null;
        }
    }

    /// <summary>Checks a value against the limits, as the values of a session are with it.</summary>
    /// <param name="values">The values of the session, as JSON by key.</param>
    /// <param name="key">The key.</param>
    /// <param name="valueJson">The value as JSON, or null to remove it.</param>
    /// <returns>Why it is refused, or null.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="values"/> is null.</exception>
    public static string? Check(IReadOnlyDictionary<string, string> values, string key, string? valueJson)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (string.IsNullOrEmpty(key) || key.Length > MaxKeyLength || key.Any(char.IsControl))
            return $"store(key, value): the key is a string of 1 to {MaxKeyLength} characters, without control characters.";
        if (valueJson is null) return null;
        var size = Encoding.UTF8.GetByteCount(valueJson);
        if (size > MaxValueBytes)
            return $"store(\"{key}\", value): the value takes {Kilobytes(size)} of JSON, more than the {Kilobytes(MaxValueBytes)} a value may take. Keep a smaller value, or a path to a file.";
        var total = values.Where(pair => pair.Key != key).Sum(static pair => Encoding.UTF8.GetByteCount(pair.Key) + Encoding.UTF8.GetByteCount(pair.Value))
            + Encoding.UTF8.GetByteCount(key) + size;
        return total > MaxSessionBytes
            ? $"store(\"{key}\", value): the values of this session would take {Kilobytes(total)}, more than the {Kilobytes(MaxSessionBytes)} they may take. Remove some with store(key, undefined)."
            : null;
    }

    private Lock Gate(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return _gates.GetOrAdd(sessionId, static _ => new Lock());
    }

    private string PathOf(string sessionId)
    {
        // A session id is a file name: anything else is refused, never a path out of the folder.
        if (sessionId.IndexOfAny(['/', '\\', ':']) >= 0 || sessionId.Contains("..", StringComparison.Ordinal) || sessionId.Length > 128)
            throw new ArgumentException("A session id names a file of the folder of the values.", nameof(sessionId));
        return Path.Combine(Root, sessionId + ".json");
    }

    private Dictionary<string, string> Read(string sessionId)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var path = PathOf(sessionId);
        if (!File.Exists(path)) return values;
        try
        {
            if (JsonNode.Parse(File.ReadAllBytes(path)) is JsonObject root)
            {
                foreach (var (key, value) in root)
                {
                    values[key] = value?.ToJsonString() ?? "null";
                }
            }
        }
        catch (JsonException)
        {
            // A file that is not JSON any more keeps nothing: the next store starts it again.
        }

        return values;
    }

    private void Write(string sessionId, Dictionary<string, string> values)
    {
        var path = PathOf(sessionId);
        Directory.CreateDirectory(Root);
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            foreach (var (key, value) in values.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            {
                writer.WritePropertyName(key);
                writer.WriteRawValue(value);
            }

            writer.WriteEndObject();
        }

        // Written whole, then moved over the previous one: a reader never sees half of it.
        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, buffer.ToArray());
        File.Move(temporary, path, overwrite: true);
    }

    private static string Kilobytes(int bytes) => bytes >= 1024 * 1024 && bytes % (1024 * 1024) == 0 ? $"{bytes / (1024 * 1024)} MB" : $"{(bytes + 1023) / 1024} KB";
}
