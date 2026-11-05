using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeAlta.Fleet.Server;

/// <summary>What a controller device saw of one value it manages: the newest version, the key of its own copy of it, and the machines it withdrew.</summary>
/// <param name="Version">The newest version this device set or saw.</param>
/// <param name="KeyId">The id of the key of that version, as the copy of this device names it; null when unknown.</param>
/// <param name="Withdrawn">The machines this device withdrew the value from, with the version they held then.</param>
public sealed record FleetSecretControllerState(int Version, string? KeyId, IReadOnlyDictionary<string, int> Withdrawn);

/// <summary>
/// What a controller device keeps of the values it manages at one server (<c>doc/fleet/secrets.md</c>, rotation): the newest
/// version of each and the key of its copy, and the machines it withdrew, kept across restarts in the folder of the link. A server
/// that answers an older record (correctly sealed, its holders' grants genuine) makes it seal nothing for a machine it withdrew.
/// </summary>
/// <remarks>One writer: the controller device of the link.</remarks>
public sealed class FleetSecretControllerCheckpoint
{
    /// <summary>The name of the file in the folder of a link.</summary>
    public const string FileName = "secret-checkpoint.json";

    private readonly string _path;
    private readonly Lock _gate = new();

    /// <summary>Initializes a new instance of the <see cref="FleetSecretControllerCheckpoint"/> class.</summary>
    /// <param name="path">The file.</param>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
    public FleetSecretControllerCheckpoint(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    /// <summary>Reads what this device saw of a value.</summary>
    /// <param name="secretId">The value.</param>
    /// <returns>Its state; null when this device saw none of it.</returns>
    /// <exception cref="InvalidDataException">The file does not read: nothing is sealed.</exception>
    public FleetSecretControllerState? Read(string secretId)
    {
        lock (_gate)
        {
            return Load().Secrets.TryGetValue(secretId, out var entry)
                ? new FleetSecretControllerState(entry.Version, entry.KeyId, new Dictionary<string, int>(entry.Withdrawn, StringComparer.Ordinal))
                : null;
        }
    }

    /// <summary>Records a version of a value this device set or saw, and the key of its copy; an older version changes nothing.</summary>
    /// <param name="secretId">The value.</param>
    /// <param name="version">The version.</param>
    /// <param name="keyId">The key of the copy of this device; null when unknown.</param>
    /// <exception cref="IOException">It could not be written.</exception>
    public void Observe(string secretId, int version, string? keyId)
        => Change(secretId, entry =>
        {
            if (version < entry.Version) return false;
            if (version == entry.Version && (keyId is null || entry.KeyId == keyId)) return false;
            entry.Version = version;
            entry.KeyId = keyId;
            return true;
        });

    /// <summary>Records that this device withdrew a value from a machine, which held a version up to the one given.</summary>
    /// <param name="secretId">The value.</param>
    /// <param name="machineId">The machine.</param>
    /// <param name="version">The version it held.</param>
    /// <exception cref="IOException">It could not be written.</exception>
    public void Withdraw(string secretId, string machineId, int version)
        => Change(secretId, entry =>
        {
            entry.Withdrawn[machineId] = Math.Max(version, entry.Withdrawn.GetValueOrDefault(machineId));
            return true;
        });

    /// <summary>Records that this device gave a value to a machine again: it holds the versions given from now on.</summary>
    /// <param name="secretId">The value.</param>
    /// <param name="machineId">The machine.</param>
    /// <exception cref="IOException">It could not be written.</exception>
    public void Regrant(string secretId, string machineId) => Change(secretId, entry => entry.Withdrawn.Remove(machineId));

    private void Change(string secretId, Func<SecretEntry, bool> change)
    {
        lock (_gate)
        {
            var document = Load();
            if (!document.Secrets.TryGetValue(secretId, out var entry)) document.Secrets[secretId] = entry = new SecretEntry();
            if (!change(entry)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            var staging = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(staging, JsonSerializer.SerializeToUtf8Bytes(document, FleetSecretControllerJsonContext.Default.ControllerDocument));
            File.Move(staging, _path, overwrite: true);
        }
    }

    private ControllerDocument Load()
    {
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllBytes(_path), FleetSecretControllerJsonContext.Default.ControllerDocument) ?? new ControllerDocument();
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return new ControllerDocument();
        }
        catch (JsonException exception)
        {
            // Never read as empty: an older record would be taken again.
            throw new InvalidDataException($"What this device keeps of the values it manages ('{_path}') does not read: {exception.Message}", exception);
        }
    }

    internal sealed class ControllerDocument
    {
        [JsonPropertyName("secrets")]
        public Dictionary<string, SecretEntry> Secrets { get; set; } = new(StringComparer.Ordinal);
    }

    internal sealed class SecretEntry
    {
        [JsonPropertyName("version")]
        public int Version { get; set; }

        [JsonPropertyName("key")]
        public string? KeyId { get; set; }

        [JsonPropertyName("withdrawn")]
        public Dictionary<string, int> Withdrawn { get; set; } = new(StringComparer.Ordinal);
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(FleetSecretControllerCheckpoint.ControllerDocument))]
internal sealed partial class FleetSecretControllerJsonContext : JsonSerializerContext;
