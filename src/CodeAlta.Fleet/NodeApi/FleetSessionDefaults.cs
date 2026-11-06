using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeAlta.Fleet.Identity;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Fleet.NodeApi;

/// <summary>
/// What the sessions of a node run with when nothing else chooses it (<c>doc/fleet/node.md</c>, "Safety"): the
/// permission mode of a session started there without one of its own, kept in the fleet folder of its profile
/// (<c>fleet/session-defaults.json</c>). A machine is installed to run agents, so its sessions bypass permissions until
/// its owner, or a device that manages it, asks for them to ask first.
/// </summary>
public static class FleetSessionDefaults
{
    /// <summary>The name of the file in the fleet folder of a profile.</summary>
    public const string FileName = "session-defaults.json";

    /// <summary>The permission mode of a machine whose owner chose none: its sessions do not ask.</summary>
    public const string DefaultPermissionMode = SessionPermissionModes.Bypass;

    /// <summary>
    /// Gets the mode a text names: <see cref="SessionPermissionModes.Bypass"/> (<c>bypass</c>) or <see cref="SessionPermissionModes.Ask"/>
    /// (<c>ask</c>), the two modes a machine runs its sessions in by default.
    /// </summary>
    /// <param name="text">The text, whatever its case.</param>
    /// <param name="mode">The mode.</param>
    /// <returns>Whether the text names one.</returns>
    public static bool TryParsePermissionMode(string? text, [NotNullWhen(true)] out string? mode)
    {
        mode = text?.Trim().ToLowerInvariant() switch
        {
            "bypass" or "bypasspermissions" => SessionPermissionModes.Bypass,
            "ask" or "default" => SessionPermissionModes.Ask,
            _ => null,
        };
        return mode is not null;
    }

    /// <summary>Reads what the sessions of the machine of a profile run with.</summary>
    /// <param name="fleetRoot">The fleet folder of the profile.</param>
    /// <returns>The defaults; those of a machine whose owner chose none when the file is missing or does not read.</returns>
    /// <exception cref="ArgumentException"><paramref name="fleetRoot"/> is empty.</exception>
    public static FleetSessionDefaultsInfo Read(string fleetRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fleetRoot);
        try
        {
            var read = JsonSerializer.Deserialize(File.ReadAllBytes(Path.Combine(fleetRoot, FileName)), FleetSessionDefaultsJson.Default.FleetSessionDefaultsInfo);
            return new FleetSessionDefaultsInfo(TryParsePermissionMode(read?.PermissionMode, out var mode) ? mode : DefaultPermissionMode);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException or JsonException or IOException or UnauthorizedAccessException)
        {
            return new FleetSessionDefaultsInfo(DefaultPermissionMode);
        }
    }

    /// <summary>Gets whether the sessions of the machine of a profile ask before commands and file changes when they have no mode of their own.</summary>
    /// <param name="fleetRoot">The fleet folder of the profile.</param>
    /// <returns>Whether they ask.</returns>
    /// <exception cref="ArgumentException"><paramref name="fleetRoot"/> is empty.</exception>
    public static bool AsksByDefault(string fleetRoot) => Read(fleetRoot).PermissionMode != SessionPermissionModes.Bypass;

    /// <summary>Writes what the sessions of the machine of a profile run with.</summary>
    /// <param name="fleetRoot">The fleet folder of the profile.</param>
    /// <param name="defaults">The defaults.</param>
    /// <returns>The defaults as kept.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="defaults"/> is null.</exception>
    /// <exception cref="ArgumentException">The folder is empty, or the mode is not one a machine runs its sessions in by default.</exception>
    /// <exception cref="IOException">The file cannot be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The file cannot be written.</exception>
    public static FleetSessionDefaultsInfo Write(string fleetRoot, FleetSessionDefaultsInfo defaults)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fleetRoot);
        ArgumentNullException.ThrowIfNull(defaults);
        if (!TryParsePermissionMode(defaults.PermissionMode, out var mode))
            throw new ArgumentException($"'{defaults.PermissionMode}' is not a mode a machine runs its sessions in: {SessionPermissionModes.Bypass} or {SessionPermissionModes.Ask}.", nameof(defaults));
        var kept = new FleetSessionDefaultsInfo(mode);
        Directory.CreateDirectory(fleetRoot);
        FleetFiles.WriteAtomically(Path.Combine(fleetRoot, FileName), JsonSerializer.SerializeToUtf8Bytes(kept, FleetSessionDefaultsJson.Default.FleetSessionDefaultsInfo));
        return kept;
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, WriteIndented = true)]
[JsonSerializable(typeof(FleetSessionDefaultsInfo))]
internal sealed partial class FleetSessionDefaultsJson : JsonSerializerContext;
