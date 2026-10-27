using System.Collections.Immutable;
using XenoAtom.Logging;

namespace CodeAlta.Catalog.Logging;

/// <summary>
/// What the application writes to its log, from the global <c>[logging]</c> table: the least severe level written for
/// every feature, and the level of some features over it, so that one feature can be followed closely (Debug for the
/// issue trackers) without the noise of the others.
/// </summary>
/// <param name="Level">The level of a feature <paramref name="Features"/> does not name.</param>
/// <param name="Features">The level of some features, by the id of the feature (<see cref="LogFeatures"/>).</param>
public sealed record LoggingSettings(LogLevel Level, ImmutableDictionary<string, LogLevel> Features)
{
    /// <summary>The levels a user chooses from, the most verbose first.</summary>
    public static ImmutableArray<LogLevel> Levels { get; } = [LogLevel.Trace, LogLevel.Debug, LogLevel.Info, LogLevel.Warn, LogLevel.Error];

    /// <summary>Gets the settings when the configuration says nothing: every feature at <see cref="LogLevel.Info"/>.</summary>
    public static LoggingSettings Default { get; } = new(LogLevel.Info, ImmutableDictionary<string, LogLevel>.Empty);

    /// <summary>Gets the level of a feature.</summary>
    /// <param name="featureId">The id of the feature.</param>
    /// <returns>Its own level, else <see cref="Level"/>.</returns>
    public LogLevel LevelOf(string featureId) => Features.TryGetValue(featureId, out var level) ? level : Level;

    /// <summary>
    /// Reads the global logging table. A level that is not one of <see cref="Levels"/>, or a feature that is not one of
    /// <see cref="LogFeatures.All"/>, is ignored: the log must work whatever the file says.
    /// </summary>
    /// <param name="document">The table; null for none.</param>
    /// <returns>The settings.</returns>
    public static LoggingSettings Read(CodeAltaLoggingSettingsDocument? document)
    {
        if (document is null) return Default;
        var level = TryParseLevel(document.Level, out var parsed) ? parsed : Default.Level;
        var features = ImmutableDictionary.CreateBuilder<string, LogLevel>(StringComparer.Ordinal);
        foreach (var (id, text) in document.Features ?? [])
        {
            if (LogFeatures.Find(id) is not null && TryParseLevel(text, out var featureLevel)) features[id] = featureLevel;
        }

        return new(level, features.ToImmutable());
    }

    /// <summary>Gets the table that writes these settings, without what is the default; null when everything is.</summary>
    /// <returns>The table, or <see langword="null"/> when nothing differs from <see cref="Default"/>.</returns>
    /// <exception cref="ArgumentException">A level is not one of <see cref="Levels"/>, or a feature is unknown.</exception>
    public CodeAltaLoggingSettingsDocument? ToDocument()
    {
        if (GetError() is { } error) throw new ArgumentException(error);
        Dictionary<string, string>? features = null;
        // In the order of the features, so that the file reads as the settings show.
        foreach (var feature in LogFeatures.All)
        {
            if (Features.TryGetValue(feature.Id, out var level) && level != Level) (features ??= new(StringComparer.Ordinal))[feature.Id] = LevelName(level);
        }

        return Level == Default.Level && features is null ? null : new() { Level = Level == Default.Level ? null : LevelName(Level), Features = features };
    }

    /// <summary>Gets why these settings cannot be written, or null when they can.</summary>
    /// <returns>The error, or <see langword="null"/>.</returns>
    public string? GetError()
    {
        if (!Levels.Contains(Level)) return $"The log level must be one of {string.Join(", ", Levels.Select(LevelName))}.";
        foreach (var (id, level) in Features)
        {
            if (LogFeatures.Find(id) is null) return $"'{id}' is not a feature of the log.";
            if (!Levels.Contains(level)) return $"The log level of {id} must be one of {string.Join(", ", Levels.Select(LevelName))}.";
        }

        return null;
    }

    /// <summary>
    /// Sets the level of the loggers of every feature in a logging configuration. The caller applies the changes
    /// (<see cref="LogManagerConfig.ApplyChanges"/>) once it has set its own.
    /// </summary>
    /// <param name="config">The configuration of the log.</param>
    /// <exception cref="ArgumentNullException"><paramref name="config"/> is null.</exception>
    /// <remarks>
    /// Every prefix of every feature is set, so that each feature has its own level whatever the one of
    /// <see cref="LogFeatures.System"/>. The loggers of no prefix, outside of CodeAlta, keep the root level, at least
    /// <see cref="LogLevel.Warn"/>: the libraries are not followed closely with the application.
    /// </remarks>
    public void ApplyTo(LogManagerConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        foreach (var feature in LogFeatures.All)
        {
            var level = LevelOf(feature.Id);
            foreach (var prefix in feature.Prefixes) config.SetLogLevel(prefix, level);
        }

        var system = LevelOf(LogFeatures.System);
        config.RootLogger.MinimumLevel = system > LogLevel.Warn ? system : LogLevel.Warn;
    }

    /// <summary>Gets the name of a level in the configuration: <c>trace</c>, <c>debug</c>, <c>info</c>, <c>warn</c> or <c>error</c>.</summary>
    /// <param name="level">The level.</param>
    /// <returns>The name.</returns>
    public static string LevelName(LogLevel level) => level switch
    {
        LogLevel.Trace => "trace",
        LogLevel.Debug => "debug",
        LogLevel.Info => "info",
        LogLevel.Warn => "warn",
        LogLevel.Error => "error",
        LogLevel.Fatal => "fatal",
        LogLevel.All => "all",
        _ => "none",
    };

    /// <summary>Reads the name of a level, in any case; <c>warning</c> is read as <c>warn</c>.</summary>
    /// <param name="text">The name.</param>
    /// <param name="level">The level, when the name is one of <see cref="Levels"/>.</param>
    /// <returns>True when the name is the one of a level a user chooses from.</returns>
    public static bool TryParseLevel(string? text, out LogLevel level)
    {
        level = default;
        switch (text?.Trim().ToLowerInvariant())
        {
            case "trace": level = LogLevel.Trace; return true;
            case "debug": level = LogLevel.Debug; return true;
            case "info": level = LogLevel.Info; return true;
            case "warn" or "warning": level = LogLevel.Warn; return true;
            case "error": level = LogLevel.Error; return true;
            default: return false;
        }
    }
}
