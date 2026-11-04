using System.Text.Json;
using System.Text.Json.Serialization;
using CodeAlta.Fleet.Identity;

namespace CodeAlta.Fleet.Server;

/// <summary>The states of the link of an instance to a server, as its device stands there.</summary>
public static class FleetServerLinkStates
{
    /// <summary>The device registered with an enrollment token and waits for a controller device to approve it.</summary>
    public const string Pending = "pending";

    /// <summary>The device is in the device list: its control connection is accepted.</summary>
    public const string Approved = "approved";

    /// <summary>The device was revoked: the server refuses it for good.</summary>
    public const string Revoked = "revoked";

    /// <summary>Gets whether a text is a known state.</summary>
    /// <param name="state">The text.</param>
    /// <returns>Whether it is one of the states.</returns>
    public static bool IsKnown(string? state) => state is Pending or Approved or Revoked;
}

/// <summary>
/// The link of this instance to a CodeAlta Server (<c>doc/fleet/identity.md</c>, spaces and servers): where the server
/// is, its organization, the device this instance is there and how it stands. The spaces that publish to it name its
/// address in their own settings.
/// </summary>
/// <param name="Id">The id of the link: the folder of its outbox under <c>fleet/servers/</c>.</param>
/// <param name="Url">The address of the server (<see cref="FleetServerAddress.Format(Uri)"/>).</param>
/// <param name="OrganizationId">The id of the organization of the server, once known.</param>
/// <param name="OrganizationName">The name of the organization, once known.</param>
/// <param name="DeviceId">The id of this device at the server.</param>
/// <param name="Kind">The class of this device there: <c>controller</c> (the owner's desktop) or <c>node</c>.</param>
/// <param name="State">How the device stands (<see cref="FleetServerLinkStates"/>).</param>
/// <param name="CreatedAt">When the link was made.</param>
/// <param name="UpdatedAt">When it last changed.</param>
/// <param name="CertificatePin">The pin of the server's own certificate (<c>altaserver run --tls self</c>), which every connection to it checks; null for one that validates as usual.</param>
public sealed record FleetServerLink(
    string Id,
    string Url,
    string? OrganizationId,
    string? OrganizationName,
    string DeviceId,
    string Kind,
    string State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? CertificatePin = null)
{
    /// <summary>Gets the address with its pin, as it is given to a person or another machine.</summary>
    [JsonIgnore]
    public string ShareableUrl => FleetServerAddress.Format(Address, CertificatePin);

    /// <summary>Gets the address of the server.</summary>
    [JsonIgnore]
    public Uri Address => FleetServerAddress.TryParse(Url, out var address) ? address : throw new InvalidDataException($"The server link '{Id}' has no valid address.");

    /// <summary>Gets whether this device may sign others in there: a controller device that was approved.</summary>
    [JsonIgnore]
    public bool IsController => Kind == Protocol.FleetDeviceKinds.Controller && State == FleetServerLinkStates.Approved;

    /// <summary>Gets whether two addresses name the same server.</summary>
    /// <param name="url">The other address.</param>
    /// <returns>Whether they are the same once normalized.</returns>
    public bool IsServer(string? url)
        => FleetServerAddress.TryParse(url, out var other) && string.Equals(FleetServerAddress.Format(other), Url, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The servers an instance is linked to, in <c>fleet/servers.json</c> of its state root (beside its device key), and the
/// folder of each link's outbox. One instance alone writes its state root, so writes are atomic renames, without a
/// cross-process gate.
/// </summary>
public sealed class FleetServerLinkStore
{
    /// <summary>The name of the file of the links in the fleet folder.</summary>
    public const string FileName = "servers.json";

    /// <summary>The name of the folder of the outboxes in the fleet folder.</summary>
    public const string FolderName = "servers";

    private const int FileVersion = 1;

    private readonly string _path;
    private readonly Lock _gate = new();

    /// <summary>Initializes a new instance of the <see cref="FleetServerLinkStore"/> class.</summary>
    /// <param name="fleetRoot">The fleet folder of the state root.</param>
    /// <exception cref="ArgumentException"><paramref name="fleetRoot"/> is empty.</exception>
    public FleetServerLinkStore(string fleetRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fleetRoot);
        FleetRoot = Path.GetFullPath(fleetRoot);
        _path = Path.Combine(FleetRoot, FileName);
    }

    /// <summary>Gets the fleet folder.</summary>
    public string FleetRoot { get; }

    /// <summary>Gets the file of the links.</summary>
    public string FilePath => _path;

    /// <summary>Lists the links, in the order they were made.</summary>
    /// <returns>The links; empty when there is no file.</returns>
    /// <exception cref="InvalidDataException">The file exists but does not read.</exception>
    public IReadOnlyList<FleetServerLink> List()
    {
        lock (_gate) return Read();
    }

    /// <summary>Finds the link to a server by its address.</summary>
    /// <param name="url">The address of the server.</param>
    /// <returns>The link, or <see langword="null"/>.</returns>
    /// <exception cref="InvalidDataException">The file exists but does not read.</exception>
    public FleetServerLink? Find(string? url) => List().FirstOrDefault(link => link.IsServer(url));

    /// <summary>Adds a link, or replaces the one with its id.</summary>
    /// <param name="link">The link.</param>
    /// <exception cref="ArgumentNullException"><paramref name="link"/> is null.</exception>
    /// <exception cref="ArgumentException">The link has no valid id, address or state, or another link names its server.</exception>
    /// <exception cref="IOException">The file cannot be written.</exception>
    public void Save(FleetServerLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        if (!IsValidId(link.Id)) throw new ArgumentException($"'{link.Id}' is not the id of a server link.", nameof(link));
        if (!FleetServerAddress.TryParse(link.Url, out _)) throw new ArgumentException($"'{link.Url}' is not the address of a server.", nameof(link));
        if (!FleetServerLinkStates.IsKnown(link.State)) throw new ArgumentException($"'{link.State}' is not the state of a server link.", nameof(link));
        lock (_gate)
        {
            var links = Read().ToList();
            if (links.Any(other => other.Id != link.Id && other.IsServer(link.Url))) throw new ArgumentException($"This instance is linked to {link.Url} already.", nameof(link));
            var index = links.FindIndex(other => other.Id == link.Id);
            if (index >= 0) links[index] = link;
            else links.Add(link);
            Write(links);
        }
    }

    /// <summary>Changes a link, when it exists.</summary>
    /// <param name="id">The id of the link.</param>
    /// <param name="change">The change.</param>
    /// <returns>The link after the change, or <see langword="null"/> when there is none.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="change"/> is null.</exception>
    /// <exception cref="IOException">The file cannot be written.</exception>
    public FleetServerLink? Update(string id, Func<FleetServerLink, FleetServerLink> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_gate)
        {
            var links = Read().ToList();
            var index = links.FindIndex(link => link.Id == id);
            if (index < 0) return null;
            var changed = change(links[index]) with { Id = id };
            if (changed == links[index]) return changed;
            links[index] = changed;
            Write(links);
            return changed;
        }
    }

    /// <summary>Removes a link and its outbox.</summary>
    /// <param name="id">The id of the link.</param>
    /// <returns>Whether a link was removed.</returns>
    /// <exception cref="IOException">The file cannot be written.</exception>
    public bool Remove(string id)
    {
        lock (_gate)
        {
            var links = Read().ToList();
            if (links.RemoveAll(link => link.Id == id) == 0) return false;
            Write(links);
        }

        var folder = LinkFolder(id);
        if (Directory.Exists(folder))
        {
            try { Directory.Delete(folder, recursive: true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { /* Left for a later removal: nothing reads it. */ }
        }

        return true;
    }

    /// <summary>Gets the folder of a link's outbox.</summary>
    /// <param name="id">The id of the link.</param>
    /// <returns>The folder.</returns>
    /// <exception cref="ArgumentException"><paramref name="id"/> is not the id of a link.</exception>
    public string LinkFolder(string id)
    {
        if (!IsValidId(id)) throw new ArgumentException($"'{id}' is not the id of a server link.", nameof(id));
        return Path.Combine(FleetRoot, FolderName, id);
    }

    /// <summary>Makes the id of a new link.</summary>
    /// <returns>An id of 12 lower-case letters and digits.</returns>
    public static string NewId() => Guid.NewGuid().ToString("N")[..12];

    /// <summary>Gets whether a text can be the id of a link: 1 to 32 lower-case letters and digits.</summary>
    /// <param name="id">The text.</param>
    /// <returns>Whether it is an id.</returns>
    public static bool IsValidId(string? id) => id is { Length: > 0 and <= 32 } && id.All(static c => c is >= 'a' and <= 'z' or >= '0' and <= '9');

    private List<FleetServerLink> Read()
    {
        byte[] bytes;
        try { bytes = File.ReadAllBytes(_path); }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException) { return []; }

        FleetServerLinksFile? file;
        try { file = JsonSerializer.Deserialize(bytes, FleetServerJsonFiles.Default.FleetServerLinksFile); }
        catch (JsonException exception) { throw new InvalidDataException($"The server links file '{_path}' is not valid JSON.", exception); }

        if (file is not { Version: FileVersion }) throw new InvalidDataException($"The server links file '{_path}' has an unknown version.");
        return [.. (file.Servers ?? []).Where(static link => link is not null && IsValidId(link.Id) && FleetServerAddress.TryParse(link.Url, out _) && FleetServerLinkStates.IsKnown(link.State))];
    }

    private void Write(List<FleetServerLink> links)
        => FleetFiles.WriteAtomically(_path, JsonSerializer.SerializeToUtf8Bytes(new FleetServerLinksFile { Version = FileVersion, Servers = links },
            FleetServerJsonFiles.Default.FleetServerLinksFile));
}

/// <summary>The file of the server links.</summary>
internal sealed class FleetServerLinksFile
{
    public int Version { get; set; }

    public List<FleetServerLink>? Servers { get; set; }
}

/// <summary>What the outbox of a link keeps between runs: the sequences of its streams and the versions of the facts of its sessions.</summary>
internal sealed class FleetOutboxFile
{
    public int Version { get; set; }

    public long FactsSequence { get; set; }

    public long FactsAcked { get; set; }

    public long InventorySequence { get; set; }

    public long InventoryAcked { get; set; }

    public string? InventoryHash { get; set; }

    public FleetOutboxBatch? InFlight { get; set; }

    public Dictionary<string, FleetOutboxSession> Sessions { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>A batch of facts sent and not acknowledged yet.</summary>
internal sealed class FleetOutboxBatch
{
    public long Sequence { get; set; }

    public Dictionary<string, long> Versions { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>What the outbox knows of one session.</summary>
internal sealed class FleetOutboxSession
{
    public long Epoch { get; set; }

    public long Version { get; set; }

    public long AckedVersion { get; set; }

    public string? Hash { get; set; }

    /// <summary>Whether the session is no longer published and its facts wait to be withdrawn from the server.</summary>
    public bool Withdraw { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(FleetServerLinksFile))]
[JsonSerializable(typeof(FleetOutboxFile))]
internal sealed partial class FleetServerJsonFiles : JsonSerializerContext;
