using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeAlta.Pseudonymization;

/// <summary>
/// What each text of a session was sent as, by the hash of the text: a conversation is sent again at each request, and
/// each of its parts goes out as it went the first time. The provider then reads what it cached, and a reasoning it
/// signed comes back to it word for word. Its methods may be called from several threads.
/// </summary>
public sealed class PseudonymCache
{
    /// <summary>The entries a cache keeps at most: the oldest go first.</summary>
    public const int Capacity = 20_000;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, string> _sent = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private int _version;

    /// <summary>Gets a number that changes each time the cache changes.</summary>
    public int Version
    {
        get
        {
            lock (_gate) return _version;
        }
    }

    /// <summary>Gets what a text was sent as.</summary>
    /// <param name="text">The text, with its real values.</param>
    /// <param name="sent">What was sent.</param>
    /// <returns><see langword="true"/> when the text was sent before.</returns>
    public bool TryGet(string text, out string sent)
    {
        var key = Key(text);
        lock (_gate)
        {
            if (_sent.TryGetValue(key, out var found))
            {
                sent = found;
                return true;
            }
        }

        sent = string.Empty;
        return false;
    }

    /// <summary>Records what a text was sent as, or came back as from the provider.</summary>
    /// <param name="text">The text, with its real values.</param>
    /// <param name="sent">What the provider reads.</param>
    public void Set(string text, string sent)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(sent);
        var key = Key(text);
        lock (_gate)
        {
            if (_sent.TryGetValue(key, out var known) && string.Equals(known, sent, StringComparison.Ordinal)) return;
            if (!_sent.ContainsKey(key)) _order.Enqueue(key);
            _sent[key] = sent;
            while (_order.Count > Capacity) _sent.Remove(_order.Dequeue());
            _version++;
        }
    }

    /// <summary>Writes the cache.</summary>
    /// <returns>Its JSON.</returns>
    public byte[] ToJson()
    {
        lock (_gate)
        {
            var document = new PseudonymCacheDocument { Entries = [.. _order.Select(key => new PseudonymCacheEntry(key, _sent[key]))] };
            return JsonSerializer.SerializeToUtf8Bytes(document, PseudonymJsonContext.Default.PseudonymCacheDocument);
        }
    }

    /// <summary>Reads a cache written by <see cref="ToJson"/>.</summary>
    /// <param name="json">Its JSON.</param>
    /// <returns>The cache.</returns>
    /// <exception cref="JsonException">The JSON is not a cache.</exception>
    public static PseudonymCache FromJson(ReadOnlySpan<byte> json)
    {
        var document = JsonSerializer.Deserialize(json, PseudonymJsonContext.Default.PseudonymCacheDocument) ?? throw new JsonException("The cache is empty.");
        var cache = new PseudonymCache();
        foreach (var entry in document.Entries ?? [])
        {
            if (string.IsNullOrEmpty(entry.Key) || entry.Sent is null || cache._sent.ContainsKey(entry.Key)) continue;
            cache._order.Enqueue(entry.Key);
            cache._sent[entry.Key] = entry.Sent;
        }

        return cache;
    }

    private static string Key(string text)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(text), hash);
        return Convert.ToHexString(hash);
    }
}

/// <summary>What <see cref="PseudonymCache.ToJson"/> writes.</summary>
internal sealed class PseudonymCacheDocument
{
    [JsonPropertyName("entries")]
    public List<PseudonymCacheEntry>? Entries { get; set; }
}

/// <summary>One entry of the cache: the hash of a text and what it was sent as.</summary>
/// <param name="Key">The hash.</param>
/// <param name="Sent">What was sent.</param>
internal sealed record PseudonymCacheEntry(string Key, string Sent);
