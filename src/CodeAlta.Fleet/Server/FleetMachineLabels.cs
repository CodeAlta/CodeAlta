using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeAlta.Fleet.Identity;

namespace CodeAlta.Fleet.Server;

/// <summary>
/// The labels the owner gives a machine (<c>doc/fleet/node.md</c>, what a node publishes: <c>always-on</c>, <c>gpu</c>,
/// <c>prod</c>...), kept in the fleet folder of its profile (<c>fleet/labels.json</c>, a JSON array of names). The machine
/// publishes them in its inventory, and the rules of an environment of the fleet read them on the machine itself.
/// </summary>
public static class FleetMachineLabels
{
    /// <summary>The name of the file of the labels in the fleet folder of a profile.</summary>
    public const string FileName = "labels.json";

    /// <summary>The most labels of a machine.</summary>
    public const int MaxLabels = 32;

    /// <summary>The longest label.</summary>
    public const int MaxLength = 64;

    /// <summary>Gets whether a text can be a label: letters, digits, <c>-</c>, <c>_</c>, <c>.</c> and <c>:</c>, at most <see cref="MaxLength"/>.</summary>
    /// <param name="label">The text.</param>
    /// <returns>Whether it can.</returns>
    public static bool IsLabel([NotNullWhen(true)] string? label)
        => label is { Length: > 0 and <= MaxLength } && label.All(static ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.' or ':');

    /// <summary>Gives labels as a machine keeps them: trimmed, each once whatever its case, in the order given.</summary>
    /// <param name="labels">The labels.</param>
    /// <returns>The labels.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="labels"/> is null.</exception>
    /// <exception cref="ArgumentException">A label is not one (<see cref="IsLabel"/>), or there are more than <see cref="MaxLabels"/>.</exception>
    public static IReadOnlyList<string> Normalize(IEnumerable<string> labels)
    {
        ArgumentNullException.ThrowIfNull(labels);
        var kept = new List<string>();
        foreach (var label in labels)
        {
            var trimmed = label?.Trim();
            if (!IsLabel(trimmed)) throw new ArgumentException($"'{label}' is not a tag: letters, digits, -, _, . and :, at most {MaxLength}.", nameof(labels));
            if (!kept.Contains(trimmed, StringComparer.OrdinalIgnoreCase)) kept.Add(trimmed);
        }

        if (kept.Count > MaxLabels) throw new ArgumentException($"A machine has at most {MaxLabels} tags.", nameof(labels));
        return kept;
    }

    /// <summary>Reads the labels of the machine of a profile.</summary>
    /// <param name="fleetRoot">The fleet folder of the profile.</param>
    /// <returns>The labels; none when the file is missing or does not read.</returns>
    /// <exception cref="ArgumentException"><paramref name="fleetRoot"/> is empty.</exception>
    public static IReadOnlyList<string> Read(string fleetRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fleetRoot);
        try
        {
            var read = JsonSerializer.Deserialize(File.ReadAllBytes(Path.Combine(fleetRoot, FileName)), FleetMachineLabelsJson.Default.ListString) ?? [];
            // A label written by hand that is not one is passed over, the others kept.
            return [.. read.Select(static label => label?.Trim()).Where(IsLabel).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxLabels)!];
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException or JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Writes the labels of the machine of a profile, replacing those it had.</summary>
    /// <param name="fleetRoot">The fleet folder of the profile.</param>
    /// <param name="labels">The labels.</param>
    /// <returns>The labels as kept (<see cref="Normalize"/>).</returns>
    /// <exception cref="ArgumentException">The folder is empty, or a label is not one.</exception>
    /// <exception cref="IOException">The file cannot be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The file cannot be written.</exception>
    public static IReadOnlyList<string> Write(string fleetRoot, IEnumerable<string> labels)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fleetRoot);
        var kept = Normalize(labels);
        Directory.CreateDirectory(fleetRoot);
        FleetFiles.WriteAtomically(Path.Combine(fleetRoot, FileName), JsonSerializer.SerializeToUtf8Bytes([.. kept], FleetMachineLabelsJson.Default.ListString));
        return kept;
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(List<string>))]
internal sealed partial class FleetMachineLabelsJson : JsonSerializerContext;
