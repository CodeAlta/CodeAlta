using System.Text.Json;
using System.Text.Json.Serialization;
using CodeAlta.Catalog;
using CodeAlta.Core.IO;
using CodeAlta.Fleet.Identity;

namespace CodeAlta.Fleet.Client;

/// <summary>A machine this device paired with: how to reach it and which key it must present.</summary>
/// <param name="Name">The name of the machine, from its pairing code.</param>
/// <param name="DeviceId">Its device id.</param>
/// <param name="Fingerprint">The fingerprint of its key: a channel to it is refused when it presents another one.</param>
/// <param name="Addresses">Its addresses, as <c>host:port</c>, the one that worked last first.</param>
/// <param name="PairedAt">When this device paired with it.</param>
/// <param name="Permissions">What this device may do on it, as it said when they paired.</param>
public sealed record PairedMachine(string Name, string DeviceId, DeviceFingerprint Fingerprint, IReadOnlyList<string> Addresses, DateTimeOffset PairedAt,
    IReadOnlyList<string> Permissions);

/// <summary>
/// The machines a profile paired with, in <c>paired-machines.json</c> of its fleet folder. Every read takes the file as it
/// is now, and every change is made under a lock file and written atomically, so that several processes of one profile
/// (CodeAlta Desktop, <c>altanode</c> commands) can use it at once.
/// </summary>
public sealed class PairedMachineStore
{
    /// <summary>The name of the file in the fleet folder of a profile.</summary>
    public const string FileName = "paired-machines.json";

    private const int FileVersion = 1;
    private static readonly TimeSpan LockWait = TimeSpan.FromSeconds(10);

    /// <summary>Initializes a new instance of the <see cref="PairedMachineStore"/> class for a profile.</summary>
    /// <param name="options">The catalog of the profile, whose fleet folder holds the file.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public PairedMachineStore(CatalogOptions options)
        : this((options ?? throw new ArgumentNullException(nameof(options))).FleetRoot)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="PairedMachineStore"/> class.</summary>
    /// <param name="fleetRoot">The fleet folder of the profile.</param>
    /// <exception cref="ArgumentException"><paramref name="fleetRoot"/> is null or blank.</exception>
    public PairedMachineStore(string fleetRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fleetRoot);
        FilePath = Path.Combine(Path.GetFullPath(fleetRoot), FileName);
    }

    /// <summary>Gets the path of the file.</summary>
    public string FilePath { get; }

    /// <summary>Lists the paired machines, as the file holds them now.</summary>
    /// <returns>The machines, in the order they were paired.</returns>
    /// <exception cref="InvalidDataException">The file is damaged.</exception>
    public IReadOnlyList<PairedMachine> List() => Load(FilePath);

    /// <summary>Finds a machine by its device id, or by its name when exactly one machine has it.</summary>
    /// <param name="nameOrId">The device id or the name.</param>
    /// <returns>The machine, or null when none or several match.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="nameOrId"/> is null.</exception>
    /// <exception cref="InvalidDataException">The file is damaged.</exception>
    public PairedMachine? Find(string nameOrId)
    {
        ArgumentNullException.ThrowIfNull(nameOrId);
        var machines = List();
        if (machines.FirstOrDefault(machine => string.Equals(machine.DeviceId, nameOrId, StringComparison.Ordinal)) is { } byId) return byId;
        var named = machines.Where(machine => string.Equals(machine.Name, nameOrId, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        return named.Length == 1 ? named[0] : null;
    }

    /// <summary>Adds a machine, or replaces the one with its device id.</summary>
    /// <param name="machine">The machine.</param>
    /// <exception cref="ArgumentNullException"><paramref name="machine"/> is null.</exception>
    /// <exception cref="InvalidDataException">The file is damaged.</exception>
    /// <exception cref="TimeoutException">Another process held the lock of the file too long.</exception>
    public void Save(PairedMachine machine)
    {
        ArgumentNullException.ThrowIfNull(machine);
        if (machine.Fingerprint.IsEmpty || !string.Equals(machine.DeviceId, DeviceIdentity.DeviceIdOf(machine.Fingerprint), StringComparison.Ordinal))
            throw new ArgumentException("The device id of a machine is the one of its fingerprint.", nameof(machine));
        Change(machines =>
        {
            var index = machines.FindIndex(existing => string.Equals(existing.DeviceId, machine.DeviceId, StringComparison.Ordinal));
            if (index >= 0) machines[index] = machine;
            else machines.Add(machine);
            return true;
        });
    }

    /// <summary>Removes a machine by its device id.</summary>
    /// <param name="deviceId">The device id.</param>
    /// <returns>Whether a machine was removed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="deviceId"/> is null.</exception>
    /// <exception cref="InvalidDataException">The file is damaged.</exception>
    /// <exception cref="TimeoutException">Another process held the lock of the file too long.</exception>
    public bool Remove(string deviceId)
    {
        ArgumentNullException.ThrowIfNull(deviceId);
        return Change(machines => machines.RemoveAll(machine => string.Equals(machine.DeviceId, deviceId, StringComparison.Ordinal)) > 0);
    }

    // Read, change and write under the lock file, so that two processes never write one over the other.
    private bool Change(Func<List<PairedMachine>, bool> change)
    {
        var directory = Path.GetDirectoryName(FilePath)!;
        FleetFiles.EnsurePrivateDirectory(directory);
        using var held = AcquireLock(FilePath + ".lock");
        var machines = Load(FilePath).ToList();
        if (!change(machines)) return false;
        var file = new PairedMachinesFile
        {
            Version = FileVersion,
            Machines = [.. machines.Select(static machine => new PairedMachineEntry
            {
                Name = machine.Name, DeviceId = machine.DeviceId, Fingerprint = machine.Fingerprint.ToString(),
                Addresses = [.. machine.Addresses], PairedAt = machine.PairedAt, Permissions = [.. machine.Permissions],
            })],
        };
        FleetFiles.WriteAtomically(FilePath, JsonSerializer.SerializeToUtf8Bytes(file, PairedMachinesJsonContext.Default.PairedMachinesFile));
        return true;
    }

    private static FileStream AcquireLock(string path)
    {
        try
        {
            return LockFile.Acquire(path, LockWait);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException($"The paired machines file '{path}' stayed locked by another process.", exception);
        }
    }

    private static IReadOnlyList<PairedMachine> Load(string path)
    {
        // A change of another process may replace the file while it is read: it is never missing meanwhile.
        if (SafeFile.ReadAllBytes(path) is not { } bytes) return [];

        PairedMachinesFile? file;
        try
        {
            file = JsonSerializer.Deserialize(bytes, PairedMachinesJsonContext.Default.PairedMachinesFile);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"The paired machines file '{path}' is not valid JSON.", exception);
        }

        if (file is not { Version: FileVersion }) throw new InvalidDataException($"The paired machines file '{path}' has an unknown version.");
        var machines = new List<PairedMachine>(file.Machines.Count);
        foreach (var entry in file.Machines)
        {
            if (!DeviceFingerprint.TryParse(entry.Fingerprint, out var fingerprint)
                || !string.Equals(entry.DeviceId, DeviceIdentity.DeviceIdOf(fingerprint), StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(entry.Name))
                throw new InvalidDataException($"The paired machines file '{path}' has a damaged entry.");
            machines.Add(new PairedMachine(entry.Name, entry.DeviceId!, fingerprint, [.. entry.Addresses.Where(PairingPayload.IsValidAddress)],
                entry.PairedAt, [.. entry.Permissions]));
        }

        return machines;
    }
}

internal sealed class PairedMachinesFile
{
    public int Version { get; set; }

    public List<PairedMachineEntry> Machines { get; set; } = [];
}

internal sealed class PairedMachineEntry
{
    public string? Name { get; set; }

    public string? DeviceId { get; set; }

    public string? Fingerprint { get; set; }

    public List<string> Addresses { get; set; } = [];

    public DateTimeOffset PairedAt { get; set; }

    public List<string> Permissions { get; set; } = [];
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PairedMachinesFile))]
internal sealed partial class PairedMachinesJsonContext : JsonSerializerContext;
