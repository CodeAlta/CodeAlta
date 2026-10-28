using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Logging;
using NeoAstra.Rpc;
using XenoAtom.Logging;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// The log viewer of Settings: the messages captured since the application started, each with the feature of its
/// logger, and the level of every feature, which the user changes there and which is saved in <c>[logging]</c>.
/// </summary>
[NeoRpcService("applicationLogs", Version = 1)]
internal sealed class ApplicationLogsService
{
    internal const int MaximumResponseBytes = 256 * 1024;
    internal const int MaximumResponseRows = 400;
    private readonly DesktopLogCapture? _capture;
    private readonly CodeAltaConfigStore? _store;
    private readonly string? _epoch;
    private readonly Lock _gate = new();

    /// <summary>Creates the service of a window that reads the log and cannot change its levels.</summary>
    /// <param name="capture">The captured messages; null when the log is not the window's.</param>
    internal ApplicationLogsService(DesktopLogCapture? capture) => _capture = capture;

    /// <summary>Creates the service of an owned host, which saves the levels in the configuration of the user.</summary>
    /// <param name="capture">The captured messages; null when the log is not the window's.</param>
    /// <param name="store">The configuration of the user.</param>
    /// <param name="epoch">The host epoch that a change must name.</param>
    internal ApplicationLogsService(DesktopLogCapture? capture, CodeAltaConfigStore store, string epoch)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(epoch);
        (_capture, _store, _epoch) = (capture, store, epoch);
    }

    [NeoRpcMethod("read")]
    public ApplicationLogsResponse Read(ApplicationLogsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_capture is null) return new("unavailable", [], "0", 0);
        try
        {
            var (snapshot, omitted, boundary, grant) = _capture.IssueSnapshot();
            var start = Math.Max(0, snapshot.Length - MaximumResponseRows);
            var rows = snapshot[start..].ToList();
            ApplicationLogsResponse response;
            do
            {
                response = new("ok", rows, omitted.ToString(CultureInfo.InvariantCulture), snapshot.Length - rows.Count)
                { CaptureId = _capture.CaptureId, Boundary = boundary, Grant = grant };
                if (JsonSerializer.SerializeToUtf8Bytes(response, DesktopJsonContext.Default.ApplicationLogsResponse).Length <= MaximumResponseBytes)
                    return response;
                rows.RemoveRange(0, Math.Max(1, rows.Count / 8)); // Keep the newest rows; report the omitted prefix.
            } while (rows.Count > 0);
            return new("ok", [], omitted.ToString(CultureInfo.InvariantCulture), snapshot.Length)
            { CaptureId = _capture.CaptureId, Boundary = boundary, Grant = grant };
        }
        catch (Exception) { return new("read_failed", [], "0", 0); }
    }

    [NeoRpcMethod("clear")]
    public ApplicationLogsClearResponse Clear(ApplicationLogsClearRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_capture is null) return new("unavailable", null, "", 0, "0");
        try
        {
            var (status, removed, covered) = _capture.Clear(request.CaptureId, request.Boundary, request.Grant, request.Confirmation);
            return status == "cleared"
                ? new(status, _capture.CaptureId, request.Boundary, removed, covered.ToString(CultureInfo.InvariantCulture))
                : new(status, null, "", 0, "0");
        }
        catch (Exception) { return new("clear_failed", null, "", 0, "0"); }
    }

    /// <summary>Reads the features of the log and the level of each one.</summary>
    [NeoRpcMethod("levels")]
    public ApplicationLogLevelsResponse Levels(ApplicationLogLevelsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _capture?.Levels is { } levels ? Describe("ok", levels.Current) : Unavailable();
    }

    /// <summary>
    /// Saves the level of every feature in the configuration of the user (<c>[logging]</c>), and the log takes them at
    /// once. A feature the request does not name takes the level of every feature.
    /// </summary>
    [NeoRpcMethod("saveLevels")]
    public ApplicationLogLevelsResponse SaveLevels(ApplicationLogLevelsSaveRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_capture?.Levels is not { } levels || _store is null) return Unavailable();
        if (!string.Equals(request.ExpectedEpoch, _epoch, StringComparison.Ordinal)) return Describe("stale_epoch", levels.Current);
        if (!LoggingSettings.TryParseLevel(request.Level, out var level) || !LoggingSettings.Levels.Contains(level) || request.Features is not { Count: <= 64 } asked)
            return Describe("invalid_request", levels.Current);
        var features = ImmutableDictionary.CreateBuilder<string, XenoAtom.Logging.LogLevel>(StringComparer.Ordinal);
        foreach (var feature in asked)
        {
            if (feature is null || LogFeatures.Find(feature.Id) is null) return Describe("invalid_request", levels.Current);
            if (feature.Level is null) continue; // The level of every feature.
            if (!LoggingSettings.TryParseLevel(feature.Level, out var featureLevel)) return Describe("invalid_request", levels.Current);
            features[feature.Id] = featureLevel;
        }

        var settings = new LoggingSettings(level, features.ToImmutable());
        lock (_gate)
        {
            try
            {
                _store.SaveGlobalLoggingSettings(settings);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or FormatException or ArgumentException)
            {
                Log?.Warn(exception, "The log levels could not be saved in the configuration.");
                return Describe("write_failed", levels.Current);
            }

            levels.Apply(settings);
        }

        Log?.Info($"Log levels: {Summary(settings)}.");
        return Describe("ok", settings);
    }

    /// <summary>
    /// Reads the levels in the configuration of the user again and gives them to the log, after the file was saved by
    /// another editor of Settings.
    /// </summary>
    internal void Reload()
    {
        if (_capture?.Levels is not { } levels || _store is null) return;
        lock (_gate)
        {
            var settings = _store.LoadGlobalLoggingSettings();
            if (settings.Level == levels.Current.Level && settings.Features.Count == levels.Current.Features.Count
                && settings.Features.All(pair => levels.Current.Features.TryGetValue(pair.Key, out var level) && level == pair.Value)) return;
            levels.Apply(settings);
            Log?.Info($"Log levels: {Summary(settings)}.");
        }
    }

    // XenoAtom.Logging throws for a message with values before the log starts, as in a test: nothing is logged then.
    private static Logger? Log => LogManager.IsInitialized ? LogManager.GetLogger("CodeAlta.Desktop") : null;

    private ApplicationLogLevelsResponse Describe(string status, LoggingSettings settings)
        => new(status, LoggingSettings.LevelName(settings.Level),
            [.. LogFeatures.All.Select(feature => new ApplicationLogFeature(feature.Id, feature.Name,
                settings.Features.TryGetValue(feature.Id, out var level) ? LoggingSettings.LevelName(level) : null))],
            [.. LoggingSettings.Levels.Select(LoggingSettings.LevelName)])
        { Editable = _store is not null };

    private static ApplicationLogLevelsResponse Unavailable()
        => new("unavailable", LoggingSettings.LevelName(LoggingSettings.Default.Level),
            [.. LogFeatures.All.Select(static feature => new ApplicationLogFeature(feature.Id, feature.Name, null))],
            [.. LoggingSettings.Levels.Select(LoggingSettings.LevelName)]);

    private static string Summary(LoggingSettings settings)
        => string.Join(", ", [LoggingSettings.LevelName(settings.Level), .. LogFeatures.All.Where(feature => settings.Features.ContainsKey(feature.Id))
            .Select(feature => $"{feature.Id} {LoggingSettings.LevelName(settings.LevelOf(feature.Id))}")]);
}

internal sealed record ApplicationLogsRequest;
internal sealed record ApplicationLogsResponse(string Status, IReadOnlyList<DesktopLogLine> Rows, string CaptureOmitted, int ReadOmitted)
{
    public string? CaptureId { get; init; }
    public string Boundary { get; init; } = "0";
    public string Grant { get; init; } = "";
}
internal sealed record ApplicationLogsClearRequest(string CaptureId, string Boundary, string Grant, string Confirmation);
internal sealed record ApplicationLogsClearResponse(string Status, string? CaptureId, string Boundary, int ClearedRows, string CoveredOmitted);

/// <summary>Asks for the features of the log and their levels.</summary>
internal sealed record ApplicationLogLevelsRequest;

/// <summary>One feature of the log.</summary>
/// <param name="Id">The id of the feature, as the rows of the log name it.</param>
/// <param name="Name">The English name of the feature.</param>
/// <param name="Level">The level of the feature; null when it takes the level of every feature.</param>
internal sealed record ApplicationLogFeature(string Id, string Name, string? Level);

/// <summary>The features of the log and their levels.</summary>
/// <param name="Status"><c>ok</c>, <c>unavailable</c>, <c>stale_epoch</c>, <c>invalid_request</c> or <c>write_failed</c>.</param>
/// <param name="Level">The level of a feature that has none of its own.</param>
/// <param name="Features">Every feature, in the order they are shown.</param>
/// <param name="Levels">The levels a user chooses from, the most verbose first.</param>
internal sealed record ApplicationLogLevelsResponse(string Status, string Level, IReadOnlyList<ApplicationLogFeature> Features, IReadOnlyList<string> Levels)
{
    /// <summary>Gets whether the levels can be changed: the window has the host and its configuration.</summary>
    public bool Editable { get; init; }
}

/// <summary>The level of one feature in a change of the levels.</summary>
/// <param name="Id">The id of the feature.</param>
/// <param name="Level">Its level; null for the level of every feature.</param>
internal sealed record ApplicationLogFeatureLevel(string Id, string? Level);

/// <summary>Changes the levels of the log.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="Level">The level of a feature that has none of its own.</param>
/// <param name="Features">The features that have a level of their own.</param>
internal sealed record ApplicationLogLevelsSaveRequest(string? ExpectedEpoch, string Level, IReadOnlyList<ApplicationLogFeatureLevel>? Features);
