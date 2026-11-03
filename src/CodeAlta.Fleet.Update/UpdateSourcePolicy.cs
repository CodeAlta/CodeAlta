using System.Text.Json;

namespace CodeAlta.Fleet.Update;

/// <summary>Who asks for an update: the owner on the machine, or the fleet through its server.</summary>
public enum UpdateOrigin
{
    /// <summary>The owner of the machine, on it (<c>altanode update</c>, <c>altaserver update</c>).</summary>
    Local,

    /// <summary>The fleet (the coordinated update a controller device signs), through the server.</summary>
    Remote,
}

/// <summary>The local configuration of the updates of a machine (<c>update.json</c> beside the folder of versions).</summary>
public sealed class UpdateSourceSettings
{
    /// <summary>Gets or sets the feed an update reads when none is given; null for nuget.org.</summary>
    public string? Source { get; set; }

    /// <summary>
    /// Gets or sets the only feeds an update may read, whoever asks; null when the owner restricts nothing (the owner on
    /// the machine may then name any feed, the fleet only <see cref="Source"/> or nuget.org).
    /// </summary>
    public List<string>? AllowedSources { get; set; }
}

/// <summary>
/// Which feeds an update may read on a machine. The owner restricts them in <c>update.json</c>
/// (<see cref="UpdateSourceSettings"/>); without a list, the owner on the machine may name any feed, and the fleet only
/// the configured feed or nuget.org, so that a server cannot point a machine at a feed its owner never chose.
/// </summary>
public sealed class UpdateSourcePolicy
{
    /// <summary>The name of the file of the settings.</summary>
    public const string FileName = "update.json";

    private UpdateSourcePolicy(UpdateSourceSettings settings, string? path)
    {
        Settings = settings;
        Path = path;
    }

    /// <summary>Gets the settings.</summary>
    public UpdateSourceSettings Settings { get; }

    /// <summary>Gets the file the settings were read from; null for settings given in code.</summary>
    public string? Path { get; }

    /// <summary>Creates a policy from settings.</summary>
    /// <param name="settings">The settings.</param>
    /// <returns>The policy.</returns>
    public static UpdateSourcePolicy From(UpdateSourceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new UpdateSourcePolicy(settings, null);
    }

    /// <summary>Reads the policy of a machine; a missing file restricts nothing.</summary>
    /// <param name="path">The file (<see cref="FileName"/>).</param>
    /// <returns>The policy.</returns>
    /// <exception cref="UpdateException">The file exists but is not valid: an update does not guess what the owner meant.</exception>
    public static UpdateSourcePolicy Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            var settings = JsonSerializer.Deserialize(File.ReadAllBytes(path), UpdateJsonContext.Default.UpdateSourceSettings) ?? new UpdateSourceSettings();
            return new UpdateSourcePolicy(settings, path);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return new UpdateSourcePolicy(new UpdateSourceSettings(), path);
        }
        catch (JsonException exception)
        {
            throw new UpdateException(UpdateErrorCodes.SourceNotAllowed, $"{path} is not valid JSON ({exception.Message}): fix it, or remove it to allow any feed.", exception);
        }
    }

    /// <summary>Resolves the feed of an update and checks that it is allowed.</summary>
    /// <param name="requested">The feed asked for; null for the configured one, or nuget.org.</param>
    /// <param name="origin">Who asks.</param>
    /// <returns>The normalized feed.</returns>
    /// <exception cref="UpdateException">The feed is not valid, or not allowed on this machine.</exception>
    public string Resolve(string? requested, UpdateOrigin origin)
    {
        var configured = Settings.Source is { Length: > 0 } source ? UpdateFeeds.Normalize(source) : UpdateFeeds.NuGetOrg;
        var resolved = requested is null ? configured : UpdateFeeds.Normalize(requested);
        if (Settings.AllowedSources is { } allowed)
        {
            if (UpdateFeeds.SameSource(resolved, configured) && Settings.Source is not null) return resolved;
            if (allowed.Any(entry => UpdateFeeds.SameSource(UpdateFeeds.Normalize(entry), resolved))) return resolved;
            throw new UpdateException(UpdateErrorCodes.SourceNotAllowed,
                $"The feed {resolved} is not allowed on this machine ({Path ?? FileName} allows {string.Join(", ", allowed)}).");
        }

        if (origin == UpdateOrigin.Local || UpdateFeeds.SameSource(resolved, configured) || UpdateFeeds.SameSource(resolved, UpdateFeeds.NuGetOrg)) return resolved;
        throw new UpdateException(UpdateErrorCodes.SourceNotAllowed,
            $"The fleet may update this machine from {configured} only: the owner allows other feeds in {Path ?? FileName} (allowedSources).");
    }
}
