using System.Text.Json;
using System.Text.Json.Serialization;
using CodeAlta.Fleet.Identity;

namespace CodeAlta.Fleet.Server;

/// <summary>
/// The limits the owner gives a machine (<c>doc/fleet/node.md</c>, limits and good citizenship), kept in the fleet folder of its
/// profile (<c>fleet/limits.json</c>): how many sessions work there at once, the others waiting for a seat
/// (<see cref="NodeApi.FleetSessionSeats"/>). Read again before each decision, so that a change applies to the next run.
/// </summary>
public static class FleetMachineLimits
{
    /// <summary>The name of the file of the limits in the fleet folder of a profile.</summary>
    public const string FileName = "limits.json";

    /// <summary>The highest limit of sessions at work at once.</summary>
    public const int MaxSessions = 256;

    /// <summary>Reads how many sessions may work at once on the machine of a profile.</summary>
    /// <param name="fleetRoot">The fleet folder of the profile.</param>
    /// <returns>The limit; null for none (no file, or a file that names none).</returns>
    /// <exception cref="ArgumentException">The folder is empty.</exception>
    /// <exception cref="InvalidDataException">The file does not read, or names a limit out of range: the caller keeps what it knew.</exception>
    public static int? ReadSessions(string fleetRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fleetRoot);
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(Path.Combine(fleetRoot, FileName));
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException($"The limits of the machine could not be read: {exception.Message}", exception);
        }

        FleetMachineLimitsDocument? document;
        try
        {
            document = JsonSerializer.Deserialize(bytes, FleetMachineLimitsJson.Default.FleetMachineLimitsDocument);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"The limits of the machine do not read: {exception.Message}", exception);
        }

        return document?.Sessions switch
        {
            null => null,
            >= 1 and <= MaxSessions and var sessions => sessions,
            var other => throw new InvalidDataException($"The limit of sessions at work, {other}, is not between 1 and {MaxSessions}."),
        };
    }

    /// <summary>Writes how many sessions may work at once on the machine of a profile.</summary>
    /// <param name="fleetRoot">The fleet folder of the profile.</param>
    /// <param name="sessions">The limit; null for none.</param>
    /// <exception cref="ArgumentException">The folder is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The limit is not between 1 and <see cref="MaxSessions"/>.</exception>
    /// <exception cref="IOException">The file cannot be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The file cannot be written.</exception>
    public static void WriteSessions(string fleetRoot, int? sessions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fleetRoot);
        if (sessions is < 1 or > MaxSessions) throw new ArgumentOutOfRangeException(nameof(sessions), sessions, $"A machine runs between 1 and {MaxSessions} sessions at once.");
        Directory.CreateDirectory(fleetRoot);
        FleetFiles.WriteAtomically(Path.Combine(fleetRoot, FileName),
            JsonSerializer.SerializeToUtf8Bytes(new FleetMachineLimitsDocument { Sessions = sessions }, FleetMachineLimitsJson.Default.FleetMachineLimitsDocument));
    }
}

/// <summary>The file of the limits of a machine.</summary>
internal sealed class FleetMachineLimitsDocument
{
    /// <summary>Gets or sets how many sessions work at once; null for no limit.</summary>
    [JsonPropertyName("sessions")]
    public int? Sessions { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(FleetMachineLimitsDocument))]
internal sealed partial class FleetMachineLimitsJson : JsonSerializerContext;
