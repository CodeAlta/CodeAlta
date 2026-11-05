using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeAlta.Catalog.Secrets;

/// <summary>The newest version of an environment a machine took from an organization: a server never makes it take an older one again.</summary>
/// <param name="Number">The version of its definition.</param>
/// <param name="Removed">Whether that version took the environment away.</param>
/// <param name="Digest">The digest of the signed definition of that version: another definition of the same version is a conflict.</param>
public sealed record FleetEnvironmentCheckpoint(long Number, bool Removed, string Digest);

/// <summary>
/// The newest versions a machine took from one organization (<c>doc/fleet/secrets.md</c>, environments protect themselves):
/// of each environment and of each value, kept across restarts, so that a server that answers an older document,
/// correctly signed, makes the machine loosen no rule, bring back no environment taken away and take no superseded value.
/// </summary>
/// <param name="Environments">The environments, by their id at the server.</param>
/// <param name="Secrets">The versions of the values, by their id at the server.</param>
public sealed record FleetCheckpoint(IReadOnlyDictionary<string, FleetEnvironmentCheckpoint> Environments, IReadOnlyDictionary<string, int> Secrets)
{
    /// <summary>Gets the checkpoint of an organization this machine took nothing from yet.</summary>
    public static FleetCheckpoint Empty { get; } = new(new Dictionary<string, FleetEnvironmentCheckpoint>(), new Dictionary<string, int>());
}

// The checkpoints of the organizations a machine takes values from: a file of their own beside the store, not secret, read
// and written under its lock as the store is. They are kept when a link is gone or the machine revoked: a server of the
// same organization never brings an older version back.
public sealed partial class LocalSecretStore
{
    private const string CheckpointFileName = "fleet-checkpoints.json";
    /// <summary>The most environments, and the most values, the checkpoint of one organization records.</summary>
    public const int MaximumCheckpointEntries = 4096;

    private string CheckpointPath => Path.Combine(FolderPath, CheckpointFileName);

    /// <summary>Reads the newest versions this machine took from an organization.</summary>
    /// <param name="organizationId">The organization.</param>
    /// <returns>The checkpoint; <see cref="FleetCheckpoint.Empty"/> when it took nothing yet.</returns>
    /// <exception cref="ArgumentException"><paramref name="organizationId"/> is empty.</exception>
    /// <exception cref="InvalidDataException">The file of the checkpoints is damaged.</exception>
    public FleetCheckpoint ReadFleetCheckpoint(string organizationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(organizationId);
        lock (_gate)
        {
            var document = LoadCheckpoints(CheckpointPath);
            return document.Organizations.TryGetValue(organizationId, out var organization) ? Checkpoint(organization) : FleetCheckpoint.Empty;
        }
    }

    /// <summary>
    /// Records the versions this machine took from an organization now: each one newer than the one recorded replaces it;
    /// one older is never recorded. An environment or a value the checkpoint does not hold yet, once it holds
    /// <see cref="MaximumCheckpointEntries"/> of them, is not recorded: it is given back, and must not be taken.
    /// </summary>
    /// <param name="organizationId">The organization.</param>
    /// <param name="taken">The versions taken.</param>
    /// <returns>The versions not recorded, which the machine must not take; empty when every one is recorded.</returns>
    /// <exception cref="ArgumentException"><paramref name="organizationId"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="taken"/> is null.</exception>
    /// <exception cref="InvalidDataException">The file of the checkpoints is damaged.</exception>
    /// <exception cref="IOException">The file of the checkpoints could not be written: nothing may be taken.</exception>
    public FleetCheckpoint AdvanceFleetCheckpoint(string organizationId, FleetCheckpoint taken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(organizationId);
        ArgumentNullException.ThrowIfNull(taken);
        lock (_gate)
        {
            using var held = ConfigFileLock.Acquire(CheckpointPath);
            var document = LoadCheckpoints(CheckpointPath);
            if (!document.Organizations.TryGetValue(organizationId, out var organization)) document.Organizations[organizationId] = organization = new CheckpointOrganizationDocument();
            var changed = false;
            var unrecordedEnvironments = new Dictionary<string, FleetEnvironmentCheckpoint>(StringComparer.Ordinal);
            var unrecordedSecrets = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var (id, environment) in taken.Environments)
            {
                if (organization.Environments.TryGetValue(id, out var known) && known.Number >= environment.Number) continue;
                if (!organization.Environments.ContainsKey(id) && organization.Environments.Count >= MaximumCheckpointEntries)
                {
                    unrecordedEnvironments[id] = environment;
                    continue;
                }

                organization.Environments[id] = new CheckpointEnvironmentDocument { Number = environment.Number, Removed = environment.Removed, Digest = environment.Digest };
                changed = true;
            }

            foreach (var (id, version) in taken.Secrets)
            {
                if (organization.Secrets.TryGetValue(id, out var known) && known >= version) continue;
                if (!organization.Secrets.ContainsKey(id) && organization.Secrets.Count >= MaximumCheckpointEntries)
                {
                    unrecordedSecrets[id] = version;
                    continue;
                }

                organization.Secrets[id] = version;
                changed = true;
            }

            if (changed)
            {
                var temporary = CheckpointPath + ".tmp";
                WritePrivateFile(temporary, JsonSerializer.Serialize(document, CheckpointJsonContext.Default.CheckpointDocument));
                File.Move(temporary, CheckpointPath, overwrite: true);
            }

            return unrecordedEnvironments.Count == 0 && unrecordedSecrets.Count == 0 ? FleetCheckpoint.Empty : new FleetCheckpoint(unrecordedEnvironments, unrecordedSecrets);
        }
    }

    private static FleetCheckpoint Checkpoint(CheckpointOrganizationDocument organization)
        => new(organization.Environments.Where(static entry => entry.Value is { Digest: not null }).ToDictionary(static entry => entry.Key,
                static entry => new FleetEnvironmentCheckpoint(entry.Value.Number, entry.Value.Removed, entry.Value.Digest!), StringComparer.Ordinal),
            new Dictionary<string, int>(organization.Secrets, StringComparer.Ordinal));

    private static CheckpointDocument LoadCheckpoints(string path)
    {
        try
        {
            var document = JsonSerializer.Deserialize(File.ReadAllBytes(path), CheckpointJsonContext.Default.CheckpointDocument) ?? new CheckpointDocument();
            document.Organizations = new(document.Organizations.Where(static entry => entry.Value is not null), StringComparer.Ordinal);
            return document;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return new CheckpointDocument();
        }
        catch (JsonException exception)
        {
            // Never read as empty: that would take any older version again.
            throw new InvalidDataException($"The checkpoints of the fleet '{path}' are damaged: {exception.Message}", exception);
        }
    }
}

internal sealed class CheckpointDocument
{
    [JsonPropertyName("format")]
    public int Format { get; set; } = 1;

    [JsonPropertyName("organizations")]
    public Dictionary<string, CheckpointOrganizationDocument> Organizations { get; set; } = new(StringComparer.Ordinal);
}

internal sealed class CheckpointOrganizationDocument
{
    [JsonPropertyName("environments")]
    public Dictionary<string, CheckpointEnvironmentDocument> Environments { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("secrets")]
    public Dictionary<string, int> Secrets { get; set; } = new(StringComparer.Ordinal);
}

internal sealed class CheckpointEnvironmentDocument
{
    [JsonPropertyName("number")]
    public long Number { get; set; }

    [JsonPropertyName("removed")]
    public bool Removed { get; set; }

    [JsonPropertyName("digest")]
    public string? Digest { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CheckpointDocument))]
internal sealed partial class CheckpointJsonContext : JsonSerializerContext;
