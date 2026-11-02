using System.Text.Json.Serialization;

namespace CodeAlta.Fleet.Identity;

/// <summary>The device key file of an instance.</summary>
internal sealed class DeviceKeyFile
{
    public int Version { get; set; }

    public string? Algorithm { get; set; }

    public string? Protection { get; set; }

    public string? PrivateKey { get; set; }

    public string? PublicKey { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>The trusted devices file of an instance.</summary>
internal sealed class TrustedDevicesFile
{
    public int Version { get; set; }

    public List<TrustedDeviceEntry> Devices { get; set; } = [];
}

/// <summary>One paired device in <see cref="TrustedDevicesFile"/>.</summary>
internal sealed class TrustedDeviceEntry
{
    public string? DeviceId { get; set; }

    public string? Name { get; set; }

    public string? Fingerprint { get; set; }

    public DevicePermissions Permissions { get; set; }

    public DateTimeOffset PairedAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(DeviceKeyFile))]
[JsonSerializable(typeof(TrustedDevicesFile))]
internal sealed partial class FleetIdentityJsonContext : JsonSerializerContext;
