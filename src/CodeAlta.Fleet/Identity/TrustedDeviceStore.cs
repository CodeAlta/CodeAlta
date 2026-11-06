using System.Text.Json;
using CodeAlta.Catalog;

namespace CodeAlta.Fleet.Identity;

/// <summary>
/// The devices paired with this instance, kept in its fleet folder (<see cref="CatalogOptions.FleetRoot"/>). A revoked
/// device stays in the list, so its key stays refused; pairing it again makes it trusted again.
/// </summary>
/// <remarks>Thread safe. Every change is written at once, by replacing the file.</remarks>
public sealed class TrustedDeviceStore : IDeviceTrustPolicy
{
    /// <summary>The name of the file in the fleet folder.</summary>
    public const string FileName = "trusted-devices.json";

    private const int FileVersion = 1;
    private const int MaxNameLength = 64;

    private readonly Lock _gate = new();
    private readonly TimeProvider _timeProvider;
    private readonly List<TrustedDevice> _devices;

    /// <summary>Opens the trusted devices of an instance.</summary>
    /// <param name="options">The catalog options of the instance.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidDataException">The file exists but cannot be read.</exception>
    public TrustedDeviceStore(CatalogOptions options)
        : this((options ?? throw new ArgumentNullException(nameof(options))).FleetRoot, TimeProvider.System)
    {
    }

    /// <summary>Opens the trusted devices kept in a fleet folder.</summary>
    /// <param name="fleetRoot">The fleet folder.</param>
    /// <param name="timeProvider">The clock of pairing and revocation times.</param>
    /// <exception cref="ArgumentException"><paramref name="fleetRoot"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="timeProvider"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidDataException">The file exists but cannot be read.</exception>
    public TrustedDeviceStore(string fleetRoot, TimeProvider timeProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fleetRoot);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
        FilePath = Path.Combine(Path.GetFullPath(fleetRoot), FileName);
        _devices = Load(FilePath);
    }

    /// <summary>Gets the path of the file.</summary>
    public string FilePath { get; }

    /// <summary>Lists the trusted devices, in the order they were paired.</summary>
    /// <returns>The devices that are not revoked.</returns>
    public IReadOnlyList<TrustedDevice> List() => List(includeRevoked: false);

    /// <summary>Lists the paired devices, in the order they were paired.</summary>
    /// <param name="includeRevoked">Whether to list the revoked devices too.</param>
    /// <returns>The devices.</returns>
    public IReadOnlyList<TrustedDevice> List(bool includeRevoked)
    {
        lock (_gate)
        {
            return _devices.Where(device => includeRevoked || !device.IsRevoked).ToArray();
        }
    }

    /// <summary>
    /// Adds a paired device, or updates the name and permissions of a device paired again (which makes a revoked device
    /// trusted again).
    /// </summary>
    /// <param name="name">The name of the device.</param>
    /// <param name="fingerprint">The fingerprint of its key; its device id derives from it.</param>
    /// <param name="permissions">What it may do here.</param>
    /// <returns>The trusted device.</returns>
    /// <exception cref="ArgumentException">The name is empty, too long or has control characters, or the fingerprint is the default value.</exception>
    /// <exception cref="IOException">The file cannot be written.</exception>
    public TrustedDevice Add(string name, DeviceFingerprint fingerprint, DevicePermissions permissions)
    {
        var validName = ValidateName(name);
        if (fingerprint.IsEmpty)
        {
            throw new ArgumentException("A fingerprint is required.", nameof(fingerprint));
        }

        var device = new TrustedDevice(DeviceIdentity.DeviceIdOf(fingerprint), validName, fingerprint, permissions, _timeProvider.GetUtcNow(), null);
        lock (_gate)
        {
            var index = _devices.FindIndex(existing => existing.Fingerprint == fingerprint);
            var updated = new List<TrustedDevice>(_devices);
            if (index >= 0)
            {
                updated[index] = device;
            }
            else
            {
                updated.Add(device);
            }

            Save(updated);
            return device;
        }
    }

    /// <summary>Revokes a device: its key is refused from now on.</summary>
    /// <param name="deviceId">The device id.</param>
    /// <returns>Whether a trusted device was revoked; false when it is unknown or already revoked.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="deviceId"/> is <see langword="null"/>.</exception>
    /// <exception cref="IOException">The file cannot be written.</exception>
    public bool Revoke(string deviceId)
    {
        ArgumentNullException.ThrowIfNull(deviceId);
        lock (_gate)
        {
            var index = _devices.FindIndex(device => string.Equals(device.DeviceId, deviceId, StringComparison.Ordinal));
            if (index < 0 || _devices[index].IsRevoked)
            {
                return false;
            }

            var updated = new List<TrustedDevice>(_devices);
            updated[index] = updated[index] with { RevokedAt = _timeProvider.GetUtcNow() };
            Save(updated);
            return true;
        }
    }

    /// <summary>
    /// Forgets a device as if it never paired: what takes back a trust given for a pairing that did not happen. A device to
    /// refuse from now on is revoked instead (<see cref="Revoke"/>), so that its key stays refused.
    /// </summary>
    /// <param name="deviceId">The device id.</param>
    /// <returns>Whether a device was forgotten.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="deviceId"/> is <see langword="null"/>.</exception>
    /// <exception cref="IOException">The file cannot be written.</exception>
    public bool Remove(string deviceId)
    {
        ArgumentNullException.ThrowIfNull(deviceId);
        lock (_gate)
        {
            var index = _devices.FindIndex(device => string.Equals(device.DeviceId, deviceId, StringComparison.Ordinal));
            if (index < 0) return false;
            var updated = new List<TrustedDevice>(_devices);
            updated.RemoveAt(index);
            Save(updated);
            return true;
        }
    }

    /// <summary>Restores a device as it was, after a trust given for a pairing that did not happen replaced it.</summary>
    /// <param name="device">The device as it was.</param>
    /// <exception cref="ArgumentNullException"><paramref name="device"/> is <see langword="null"/>.</exception>
    /// <exception cref="IOException">The file cannot be written.</exception>
    public void Restore(TrustedDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        lock (_gate)
        {
            var index = _devices.FindIndex(existing => existing.Fingerprint == device.Fingerprint);
            var updated = new List<TrustedDevice>(_devices);
            if (index >= 0) updated[index] = device;
            else updated.Add(device);
            Save(updated);
        }
    }

    /// <summary>Changes what a trusted device may do.</summary>
    /// <param name="deviceId">The device id.</param>
    /// <param name="permissions">What it may do from now on.</param>
    /// <returns>The device as it is now, or null when it is unknown or revoked.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="deviceId"/> is <see langword="null"/>.</exception>
    /// <exception cref="IOException">The file cannot be written.</exception>
    public TrustedDevice? SetPermissions(string deviceId, DevicePermissions permissions)
    {
        ArgumentNullException.ThrowIfNull(deviceId);
        lock (_gate)
        {
            var index = _devices.FindIndex(device => string.Equals(device.DeviceId, deviceId, StringComparison.Ordinal));
            if (index < 0 || _devices[index].IsRevoked) return null;
            if (_devices[index].Permissions == permissions) return _devices[index];
            var updated = new List<TrustedDevice>(_devices);
            updated[index] = updated[index] with { Permissions = permissions };
            Save(updated);
            return updated[index];
        }
    }

    /// <summary>
    /// Finds the devices a person names: by device id, by name (ignoring case), or by the start of a device id (at least
    /// four characters). An exact device id or name wins over a prefix.
    /// </summary>
    /// <param name="text">The device id, the name or the start of the device id.</param>
    /// <param name="includeRevoked">Whether revoked devices may match.</param>
    /// <returns>The matching devices: one when the text names a device, several when it is ambiguous, none when unknown.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    public IReadOnlyList<TrustedDevice> Match(string text, bool includeRevoked)
    {
        ArgumentNullException.ThrowIfNull(text);
        var wanted = text.Trim();
        if (wanted.Length == 0) return [];
        var devices = List(includeRevoked);
        var exact = devices.Where(device => string.Equals(device.DeviceId, wanted, StringComparison.OrdinalIgnoreCase)
            || string.Equals(device.Name, wanted, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (exact.Length > 0 || wanted.Length < 4) return exact;
        return devices.Where(device => device.DeviceId.StartsWith(wanted, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    /// <inheritdoc />
    public TrustedDevice? FindTrusted(DeviceFingerprint fingerprint)
    {
        lock (_gate)
        {
            return _devices.Find(device => device.Fingerprint == fingerprint && !device.IsRevoked);
        }
    }

    /// <inheritdoc />
    public bool IsRevoked(DeviceFingerprint fingerprint)
    {
        lock (_gate)
        {
            return _devices.Exists(device => device.Fingerprint == fingerprint && device.IsRevoked);
        }
    }

    /// <summary>Checks the name of a device and trims it: 1 to 64 characters, no control character.</summary>
    /// <param name="name">The name.</param>
    /// <returns>The trimmed name.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    /// <exception cref="ArgumentException">The name is empty, too long or has a control character.</exception>
    public static string NormalizeName(string name) => ValidateName(name);

    internal static string ValidateName(string name, string parameterName = "name")
    {
        ArgumentNullException.ThrowIfNull(name, parameterName);
        var trimmed = name.Trim();
        if (trimmed.Length is 0 or > MaxNameLength || trimmed.Any(char.IsControl))
        {
            throw new ArgumentException($"A device name has 1 to {MaxNameLength} characters and no control character.", parameterName);
        }

        return trimmed;
    }

    // The list in memory changes only once the file is written, so a failed write leaves both as they were.
    private void Save(List<TrustedDevice> devices)
    {
        var file = new TrustedDevicesFile
        {
            Version = FileVersion,
            Devices = devices.Select(device => new TrustedDeviceEntry
            {
                DeviceId = device.DeviceId,
                Name = device.Name,
                Fingerprint = device.Fingerprint.ToString(),
                Permissions = device.Permissions,
                PairedAt = device.PairedAt,
                RevokedAt = device.RevokedAt,
            }).ToList(),
        };
        FleetFiles.WriteAtomically(FilePath, JsonSerializer.SerializeToUtf8Bytes(file, FleetIdentityJsonContext.Default.TrustedDevicesFile));
        _devices.Clear();
        _devices.AddRange(devices);
    }

    private static List<TrustedDevice> Load(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        TrustedDevicesFile? file;
        try
        {
            file = JsonSerializer.Deserialize(File.ReadAllBytes(path), FleetIdentityJsonContext.Default.TrustedDevicesFile);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"The trusted devices file '{path}' is not valid JSON.", exception);
        }

        if (file is not { Version: FileVersion })
        {
            throw new InvalidDataException($"The trusted devices file '{path}' has an unknown version.");
        }

        var devices = new List<TrustedDevice>(file.Devices.Count);
        foreach (var entry in file.Devices)
        {
            // A damaged entry is never dropped silently: it might be the revocation of a lost device.
            if (!DeviceFingerprint.TryParse(entry.Fingerprint, out var fingerprint)
                || DeviceIdentity.DeviceIdOf(fingerprint) is var deviceId && !string.Equals(entry.DeviceId, deviceId, StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(entry.Name))
            {
                throw new InvalidDataException($"The trusted devices file '{path}' has a damaged entry.");
            }

            devices.Add(new TrustedDevice(deviceId, entry.Name, fingerprint, entry.Permissions, entry.PairedAt, entry.RevokedAt));
        }

        return devices;
    }
}
