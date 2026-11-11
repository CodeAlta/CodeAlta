using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeAlta.Core.IO;

namespace CodeAlta.Catalog.Secrets;

/// <summary>How long the confirmation of a secret that asks before use lasts.</summary>
public static class SecretAskDurations
{
    /// <summary>Asked at each use.</summary>
    public const string EachUse = "each";

    /// <summary>Kept 15 minutes, the default.</summary>
    public const string FifteenMinutes = "15m";

    /// <summary>Kept one hour.</summary>
    public const string OneHour = "1h";

    /// <summary>Kept until CodeAlta closes.</summary>
    public const string UntilClosed = "session";

    /// <summary>Gets whether a text is one of the durations.</summary>
    /// <param name="value">The text.</param>
    /// <returns>True for a duration.</returns>
    public static bool IsDuration([NotNullWhen(true)] string? value) => value is EachUse or FifteenMinutes or OneHour or UntilClosed;

    /// <summary>Gets how long a confirmation lasts: zero for each use, null until CodeAlta closes.</summary>
    /// <param name="value">One of the durations.</param>
    /// <returns>The time.</returns>
    public static TimeSpan? TimeOf(string value) => value switch
    {
        EachUse => TimeSpan.Zero,
        OneHour => TimeSpan.FromHours(1),
        UntilClosed => null,
        _ => TimeSpan.FromMinutes(15),
    };
}

/// <summary>A secret whose value is kept in a vault: the credential provider that reads it and what that provider reads.</summary>
/// <param name="Name">The name of the secret, as the configuration refers to it (<c>${secret:NAME}</c>).</param>
/// <param name="Provider">The id of the credential provider (<c>[credentials.&lt;id&gt;]</c>).</param>
/// <param name="Reference">What the provider reads, such as <c>op://Private/Anthropic/credential</c>: never shown to agents.</param>
/// <param name="Label">What the user picked, as the list of the secrets shows it, such as <c>Private › Anthropic › credential</c>.</param>
/// <param name="Version">1 when bound, one more at each change.</param>
/// <param name="SetBy">Who bound it.</param>
/// <param name="SetAt">When.</param>
public sealed record SecretBinding(string Name, string Provider, string Reference, string Label, int Version, string SetBy, DateTimeOffset SetAt);

/// <summary>
/// The secrets of this machine whose values are kept in vaults, and the secrets that ask before use, in
/// <c>~/.alta/secrets/bindings.json</c> beside the store. Only the user's own store maps a name to a vault item: no
/// configuration file can choose what is read from a vault. The file holds no value.
/// </summary>
/// <remarks>
/// It is written as the store is (its lock, a private file, a replacement without gap), and keeps the members it does not
/// know, so that another version of CodeAlta on the same profile loses nothing of it. Each read follows the file.
/// </remarks>
public sealed class SecretBindings
{
    /// <summary>The name of the file in the folder of the store.</summary>
    public const string FileName = "bindings.json";

    /// <summary>The longest reference or label.</summary>
    public const int MaximumTextLength = 1024;

    private const string BindingsMember = "bindings";
    private const string AskMember = "ask_before_use";

    private readonly Lock _gate = new();
    private (long Stamp, JsonObject Document)? _read;

    /// <summary>Initializes a new instance of the <see cref="SecretBindings"/> class for the folder of a store.</summary>
    /// <param name="folder">The folder of the store, <c>~/.alta/secrets</c>.</param>
    /// <exception cref="ArgumentException"><paramref name="folder"/> is empty.</exception>
    public SecretBindings(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        FilePath = Path.Combine(folder, FileName);
    }

    /// <summary>Gets the path of the file.</summary>
    public string FilePath { get; }

    /// <summary>Gets a number that changes each time the file changes.</summary>
    public long ChangeStamp
    {
        get
        {
            var info = new FileInfo(FilePath);
            return info.Exists ? info.LastWriteTimeUtc.Ticks ^ (info.Length << 1) : 0;
        }
    }

    /// <summary>Lists the bindings.</summary>
    /// <returns>The bindings, in the order of their names.</returns>
    /// <exception cref="InvalidDataException">The file is damaged.</exception>
    public IReadOnlyList<SecretBinding> List()
    {
        var document = Read();
        if (document[BindingsMember] is not JsonObject bindings) return [];
        var list = new List<SecretBinding>(bindings.Count);
        foreach (var (name, node) in bindings)
        {
            if (LocalSecretStore.IsName(name) && ToBinding(name, node) is { } binding) list.Add(binding);
        }

        return [.. list.OrderBy(static binding => binding.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Gets the binding of a name.</summary>
    /// <param name="name">The name of the secret.</param>
    /// <param name="binding">The binding, when the name has one.</param>
    /// <returns>True when the secret is kept in a vault.</returns>
    public bool TryGet(string name, [NotNullWhen(true)] out SecretBinding? binding)
    {
        ArgumentNullException.ThrowIfNull(name);
        binding = null;
        Read();
        foreach (var listed in List())
        {
            if (string.Equals(listed.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                binding = listed;
                return true;
            }
        }

        return false;
    }

    /// <summary>Binds a name to an item of a vault, or changes its binding.</summary>
    /// <param name="name">The name of the secret.</param>
    /// <param name="provider">The id of the credential provider.</param>
    /// <param name="reference">What the provider reads.</param>
    /// <param name="label">What the list shows.</param>
    /// <param name="setBy">Who binds it.</param>
    /// <returns>The binding now.</returns>
    /// <exception cref="ArgumentException">A value is not valid.</exception>
    /// <exception cref="InvalidOperationException">The store holds the most secrets already.</exception>
    public SecretBinding Bind(string name, string provider, string reference, string label, string setBy)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(label);
        ArgumentNullException.ThrowIfNull(setBy);
        if (!LocalSecretStore.IsName(name)) throw new ArgumentException($"'{name}' is not a valid name: letters, digits and _, not starting with a digit.", nameof(name));
        if (!IsProviderId(provider)) throw new ArgumentException($"'{provider}' is not the id of a credential provider: lowercase letters, digits, - and _.", nameof(provider));
        if (string.IsNullOrWhiteSpace(reference) || reference.Length > MaximumTextLength) throw new ArgumentException("The reference is empty or too long.", nameof(reference));
        if (label.Length > MaximumTextLength) throw new ArgumentException("The label is too long.", nameof(label));
        SecretBinding? result = null;
        Change(document =>
        {
            var bindings = document[BindingsMember] as JsonObject ?? (JsonObject)(document[BindingsMember] = new JsonObject());
            var existing = Find(bindings, name);
            var version = 1;
            if (existing is { } key && bindings[key] is JsonObject old)
            {
                version = (old["version"]?.GetValueKind() == JsonValueKind.Number ? old["version"]!.GetValue<int>() : 0) + 1;
                bindings.Remove(key);
            }
            else if (bindings.Count >= LocalSecretStore.MaximumEntries)
            {
                throw new InvalidOperationException($"This machine holds at most {LocalSecretStore.MaximumEntries} secrets kept in vaults.");
            }

            var at = DateTimeOffset.UtcNow;
            bindings[name] = new JsonObject
            {
                ["provider"] = provider,
                ["ref"] = reference.Trim(),
                ["label"] = label.Trim(),
                ["version"] = version,
                ["set_by"] = setBy,
                ["set_at"] = at.ToString("O", CultureInfo.InvariantCulture),
            };
            result = new SecretBinding(name, provider, reference.Trim(), label.Trim(), version, setBy, at);
            return true;
        });
        return result!;
    }

    /// <summary>Removes the binding of a name.</summary>
    /// <param name="name">The name of the secret.</param>
    /// <returns>True when it had one.</returns>
    public bool Unbind(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Change(document => document[BindingsMember] is JsonObject bindings && Find(bindings, name) is { } key && bindings.Remove(key));
    }

    /// <summary>Removes the bindings of a credential provider, which is removed.</summary>
    /// <param name="provider">The id of the provider.</param>
    /// <returns>The names that were bound to it.</returns>
    public IReadOnlyList<string> UnbindProvider(string provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        List<string> removed = [];
        Change(document =>
        {
            if (document[BindingsMember] is not JsonObject bindings) return false;
            foreach (var (name, node) in bindings.ToArray())
            {
                if (node is JsonObject entry && entry["provider"]?.GetValueKind() == JsonValueKind.String && string.Equals(entry["provider"]!.GetValue<string>(), provider, StringComparison.Ordinal))
                {
                    bindings.Remove(name);
                    removed.Add(name);
                }
            }

            return removed.Count > 0;
        });
        return removed;
    }

    /// <summary>Gets how long the confirmation of a secret that asks before use lasts.</summary>
    /// <param name="name">The name of the secret.</param>
    /// <returns>One of <see cref="SecretAskDurations"/>; null when it does not ask.</returns>
    public string? AskBeforeUse(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Read()[AskMember] is JsonObject asks && Find(asks, name) is { } key && asks[key]?.GetValueKind() == JsonValueKind.String
            && asks[key]!.GetValue<string>() is var value && SecretAskDurations.IsDuration(value) ? value : null;
    }

    /// <summary>Gets the secrets that ask before use, with how long a confirmation lasts.</summary>
    /// <returns>The durations by name.</returns>
    public IReadOnlyDictionary<string, string> ListAskBeforeUse()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (Read()[AskMember] is not JsonObject asks) return result;
        foreach (var (name, node) in asks)
        {
            if (LocalSecretStore.IsName(name) && node?.GetValueKind() == JsonValueKind.String && node.GetValue<string>() is var value && SecretAskDurations.IsDuration(value)) result[name] = value;
        }

        return result;
    }

    /// <summary>Makes a secret ask before use, or not.</summary>
    /// <param name="name">The name of the secret.</param>
    /// <param name="duration">One of <see cref="SecretAskDurations"/>; null to stop asking.</param>
    /// <returns>True when something changed.</returns>
    /// <exception cref="ArgumentException">The name or the duration is not valid.</exception>
    public bool SetAskBeforeUse(string name, string? duration)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!LocalSecretStore.IsName(name)) throw new ArgumentException($"'{name}' is not a valid name.", nameof(name));
        if (duration is not null && !SecretAskDurations.IsDuration(duration)) throw new ArgumentException($"'{duration}' is not a duration: each, 15m, 1h or session.", nameof(duration));
        return Change(document =>
        {
            var asks = document[AskMember] as JsonObject;
            var key = asks is null ? null : Find(asks, name);
            if (duration is null)
            {
                return asks is not null && key is not null && asks.Remove(key);
            }

            asks ??= (JsonObject)(document[AskMember] = new JsonObject());
            if (key is not null && asks[key]?.GetValueKind() == JsonValueKind.String && asks[key]!.GetValue<string>() == duration) return false;
            if (key is not null) asks.Remove(key);
            asks[name] = duration;
            return true;
        });
    }

    /// <summary>Gets a raw member of the file that another part of CodeAlta keeps there (the sign-ins of websites), as a copy.</summary>
    /// <param name="member">The name of the member.</param>
    /// <returns>A copy of the member, or null.</returns>
    public JsonNode? ReadMember(string member)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(member);
        return Read()[member]?.DeepClone();
    }

    /// <summary>Changes a raw member of the file under its lock.</summary>
    /// <param name="member">The name of the member.</param>
    /// <param name="change">Gets the member (null when missing) and returns its new value (null removes it), or the same node when nothing changes.</param>
    /// <returns>True when something changed.</returns>
    public bool ChangeMember(string member, Func<JsonNode?, JsonNode?> change)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(member);
        ArgumentNullException.ThrowIfNull(change);
        return Change(document =>
        {
            var current = document[member];
            var before = current?.ToJsonString();
            var next = change(current?.DeepClone());
            if (string.Equals(next?.ToJsonString(), before, StringComparison.Ordinal)) return false;
            document.Remove(member);
            if (next is not null) document[member] = next;
            return true;
        });
    }

    /// <summary>Tells whether a text can be the id of a credential provider: lowercase letters, digits, <c>-</c> and <c>_</c>, at most 64.</summary>
    /// <param name="id">The text.</param>
    /// <returns>True for an id.</returns>
    public static bool IsProviderId([NotNullWhen(true)] string? id)
        => id is { Length: > 0 and <= 64 } && char.IsAsciiLetterLower(id[0]) && id.All(static ch => char.IsAsciiLetterLower(ch) || char.IsAsciiDigit(ch) || ch is '-' or '_');

    private static string? Find(JsonObject bindings, string name)
    {
        foreach (var (key, _) in bindings)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) return key;
        }

        return null;
    }

    private static SecretBinding? ToBinding(string name, JsonNode? node)
    {
        if (node is not JsonObject entry) return null;
        string? Text(string member) => entry[member]?.GetValueKind() == JsonValueKind.String ? entry[member]!.GetValue<string>() : null;
        if (Text("provider") is not { } provider || !IsProviderId(provider) || Text("ref") is not { Length: > 0 } reference) return null;
        var version = entry["version"]?.GetValueKind() == JsonValueKind.Number ? entry["version"]!.GetValue<int>() : 1;
        var at = DateTimeOffset.TryParse(Text("set_at"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) ? parsed : DateTimeOffset.MinValue;
        return new SecretBinding(name, provider, reference, Text("label") ?? reference, version, Text("set_by") ?? string.Empty, at);
    }

    private JsonObject Read()
    {
        lock (_gate)
        {
            var stamp = ChangeStamp;
            if (_read is { } known && known.Stamp == stamp) return known.Document;
            var document = Load();
            _read = (stamp, document);
            return document;
        }
    }

    private JsonObject Load()
    {
        try
        {
            if (SafeFile.ReadAllBytes(FilePath) is not { Length: > 0 } bytes) return [];
            return JsonNode.Parse(bytes) as JsonObject ?? throw new InvalidDataException($"The file of the secrets kept in vaults '{FilePath}' is not a JSON object.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"The file of the secrets kept in vaults '{FilePath}' is damaged: {exception.Message}", exception);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return [];
        }
    }

    private static string Write(JsonObject document)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            document.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private bool Change(Func<JsonObject, bool> change)
    {
        lock (_gate)
        {
            using var held = ConfigFileLock.Acquire(FilePath);
            var document = Load();
            if (!change(document)) return false;
            document["format"] = 1;
            var temporary = FilePath + ".tmp";
            LocalSecretStore.WritePrivateFile(temporary, Write(document));
            StagedFileReplacement.MoveOverWithoutGap(temporary, FilePath);
            _read = null;
            return true;
        }
    }
}
