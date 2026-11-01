using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using CodeAlta.Desktop.Settings;
using CodeAlta.LiveTool;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// The settings as one catalog, for the search of Settings and the global search of the window: what exists, the value
/// of a setting, a change of it, and the events of the registry (a page to show, a change made by an agent, a change of
/// a setting the page keeps, a change of the catalog). The page of the main window also reports here the values of the
/// settings it keeps.
/// </summary>
/// <remarks>Values cross the bridge as JSON texts: a setting is a boolean, a number, a text, a list or an object.</remarks>
[NeoRpcService("settings", Version = 1)]
internal sealed class SettingsService
{
    private readonly DesktopSettings? _settings;
    private readonly string? _epoch;

    /// <summary>Creates an unavailable service for launches without an owned host.</summary>
    internal SettingsService()
    {
    }

    /// <summary>Creates the service for an owned host.</summary>
    /// <param name="settings">The registry of the settings.</param>
    /// <param name="epoch">The host epoch that requests must name.</param>
    /// <exception cref="ArgumentNullException"><paramref name="settings"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="epoch"/> is blank.</exception>
    internal SettingsService(DesktopSettings settings, string epoch)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(epoch);
        (_settings, _epoch) = (settings, epoch);
    }

    /// <summary>Lists the settings: the built-in ones with their availability here, and those of the plugins.</summary>
    [NeoRpcMethod("catalog")]
    public SettingsCatalogResponse Catalog(SettingsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, []);
        return new("ok", [.. _settings!.List().Select(Describe)]);
    }

    /// <summary>Reads the value of a setting.</summary>
    [NeoRpcMethod("get")]
    public async Task<SettingsValueResponse> GetAsync(SettingsGetRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, null, null, null, null);
        if (request.Id is not { Length: > 0 and <= 256 } id) return new("invalid_request", null, null, null, null);
        var read = await _settings!.GetAsync(id, request.ProjectId, cancellationToken).ConfigureAwait(false);
        return new(read.Status, read.Value is { } value ? value.GetRawText() : null, read.Source, read.Message, read.Choices?.Select(Choice).ToArray());
    }

    /// <summary>Changes a setting, as the user does in the window; a value of null gives it back its default.</summary>
    [NeoRpcMethod("set")]
    public async Task<SettingsSetResponse> SetAsync(SettingsSetRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, null, null, null);
        if (request.Id is not { Length: > 0 and <= 256 } id || request.Value is { Length: > 64 * 1024 }) return new("invalid_request", null, null, null);
        JsonElement? value;
        try { value = request.Value is null ? null : SettingValues.Parse(request.Value); }
        catch (JsonException) { return new("invalid_request", null, null, "The value is not JSON."); }
        var write = await _settings!.SetAsync(id, value, request.ProjectId, AltaSettingOrigin.Window, cancellationToken).ConfigureAwait(false);
        return new(write.Status, write.Value?.GetRawText(), write.Previous?.GetRawText(), write.Message);
    }

    /// <summary>Takes the values of the settings the page keeps, and the answer to a change of one it was asked for.</summary>
    [NeoRpcMethod("reportWindow")]
    public SettingsReportResponse ReportWindow(SettingsReportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused);
        if (request.Values is not { Count: <= 512 } values) return new("invalid_request");
        var taken = new List<(string, JsonElement, IReadOnlyList<AltaSettingChoice>?)>(values.Count);
        foreach (var item in values)
        {
            if (item?.Id is not { Length: > 0 and <= 256 } || item.Value is not { Length: <= 64 * 1024 }) return new("invalid_request");
            try
            {
                taken.Add((item.Id, SettingValues.Parse(item.Value),
                    item.Choices is { Count: <= 512 } choices ? [.. choices.Select(static choice => new AltaSettingChoice(choice.Value, choice.Label))] : null));
            }
            catch (JsonException)
            {
                return new("invalid_request");
            }
        }

        _settings!.ReportWindow(taken, request.RequestId, request.Error);
        return new("ok");
    }

    /// <summary>The events of the settings for this page, until the page goes away.</summary>
    [NeoRpcMethod("watch")]
    public NeoRpcChannel<SettingsEvent> Watch(SettingsRequest request, NeoRpcContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new(WatchAsync(request, context.ViewLabel, cancellationToken), DesktopJsonContext.Default.SettingsEvent);
    }

    internal async IAsyncEnumerable<SettingsEvent> WatchAsync(SettingsRequest request, string? label, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (_settings is null || !string.Equals(request.ExpectedEpoch, _epoch, StringComparison.Ordinal)) yield break;
        // A page that does not read keeps the newest events.
        var events = Channel.CreateBounded<SettingsEvent>(new BoundedChannelOptions(32) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        using var registration = _settings.Watch(label, value => events.Writer.TryWrite(value));
        await foreach (var value in events.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false)) yield return value;
    }

    private string? Refuse(string? expectedEpoch)
        => _settings is null || _epoch is null ? "unavailable" : string.Equals(expectedEpoch, _epoch, StringComparison.Ordinal) ? null : "stale_epoch";

    private static SettingsChoice Choice(AltaSettingChoice choice) => new(choice.Value, choice.Label);

    // The bounds of a number cross the bridge as texts: a bound of a plugin may be a fraction.
    private static string? Bound(double? value) => value?.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

    private static SettingsDescriptor Describe(AltaSettingDescriptor setting) => new(setting.Id, setting.Label, setting.Help, setting.Page, setting.Section, setting.Kind,
        [.. setting.Choices.Select(Choice)], Bound(setting.Minimum), Bound(setting.Maximum), setting.Default?.GetRawText(), setting.Owner, setting.Agents, setting.Writable,
        setting.ManagedBy, setting.Applies, [.. setting.Scopes], [.. setting.Keywords], setting.Plugin, setting.PluginName, setting.Available, setting.Project);
}

/// <summary>Asks for what needs no more than the epoch.</summary>
/// <param name="ExpectedEpoch">The host epoch the page believes it is talking to.</param>
internal sealed record SettingsRequest(string ExpectedEpoch);

/// <summary>One choice of a setting.</summary>
/// <param name="Value">The value.</param>
/// <param name="Label">What is shown for it, in English; null to show the value.</param>
internal sealed record SettingsChoice(string Value, string? Label);

/// <summary>
/// What a setting is (see <see cref="AltaSettingDescriptor"/>): its default as a JSON text, its bounds as numbers written
/// as texts, and the project of a plugin of a project.
/// </summary>
internal sealed record SettingsDescriptor(string Id, string Label, string? Help, string Page, string? Section, string Kind, IReadOnlyList<SettingsChoice> Choices,
    string? Minimum, string? Maximum, string? Default, string Owner, string Agents, bool Writable, string? ManagedBy, string Applies, IReadOnlyList<string> Scopes,
    IReadOnlyList<string> Keywords, string? Plugin, string? PluginName, bool Available, string? Project);

/// <summary>The settings, or why they are not listed.</summary>
internal sealed record SettingsCatalogResponse(string Status, IReadOnlyList<SettingsDescriptor> Settings);

/// <summary>Asks for the value of a setting.</summary>
/// <param name="ExpectedEpoch">The host epoch the page believes it is talking to.</param>
/// <param name="Id">The id of the setting.</param>
/// <param name="ProjectId">The project, for a setting a project can have; null for the user's.</param>
internal sealed record SettingsGetRequest(string ExpectedEpoch, string? Id, string? ProjectId);

/// <summary>A value of a setting as a JSON text, where it comes from, and the choices of a choice whose choices change.</summary>
internal sealed record SettingsValueResponse(string Status, string? Value, string? Source, string? Message, IReadOnlyList<SettingsChoice>? Choices);

/// <summary>Asks for a change of a setting.</summary>
/// <param name="ExpectedEpoch">The host epoch the page believes it is talking to.</param>
/// <param name="Id">The id of the setting.</param>
/// <param name="Value">The value as a JSON text; null for the default.</param>
/// <param name="ProjectId">The project, for a setting a project can have; null for the user's.</param>
internal sealed record SettingsSetRequest(string ExpectedEpoch, string? Id, string? Value, string? ProjectId);

/// <summary>How a change ended: the value now and the one before, as JSON texts.</summary>
internal sealed record SettingsSetResponse(string Status, string? Value, string? Previous, string? Message);

/// <summary>A value of a setting the page keeps.</summary>
/// <param name="Id">The id of the setting.</param>
/// <param name="Value">The value as a JSON text.</param>
/// <param name="Choices">The choices now, for a choice whose choices change; null otherwise.</param>
internal sealed record SettingsWindowValue(string Id, string Value, IReadOnlyList<SettingsChoice>? Choices);

/// <summary>The values the page keeps, and the answer to a change it was asked for.</summary>
/// <param name="ExpectedEpoch">The host epoch the page believes it is talking to.</param>
/// <param name="Values">The values: all of them when the page starts, then those that changed.</param>
/// <param name="RequestId">The change this answers; null for none.</param>
/// <param name="Error">Why the page refused that change; null when it made it.</param>
internal sealed record SettingsReportRequest(string ExpectedEpoch, IReadOnlyList<SettingsWindowValue> Values, string? RequestId, string? Error);

/// <summary>Whether the report was taken.</summary>
internal sealed record SettingsReportResponse(string Status);

/// <summary>An event of the settings for a page.</summary>
/// <param name="Kind">
/// <c>show</c> (open Settings on <paramref name="Page"/> and make <paramref name="Id"/> glow), <c>changed</c> (someone but the
/// window changed <paramref name="Id"/>), <c>write</c> (change a setting the page keeps, then answer with
/// <paramref name="RequestId"/>), or <c>catalog</c> (the settings that exist changed).
/// </param>
/// <param name="Page">The page to show.</param>
/// <param name="Id">The setting.</param>
/// <param name="Value">The value, as a JSON text.</param>
/// <param name="Previous">The value before a change, as a JSON text.</param>
/// <param name="Origin">Who changed it: <c>agent</c>, <c>plugin</c>, <c>mcp</c> or <c>other</c>.</param>
/// <param name="SessionId">The session that changed it.</param>
/// <param name="Who">The plugin that changed it.</param>
/// <param name="ProjectId">The project whose value changed.</param>
/// <param name="RequestId">The change a <c>write</c> asks for.</param>
/// <param name="Expires">
/// When a <c>write</c> expires, in milliseconds since 1970 (UTC), as a text: the page makes the change before, else answers
/// that it did not, since the host has stopped waiting for it.
/// </param>
internal sealed record SettingsEvent(string Kind, string? Page = null, string? Id = null, string? Value = null, string? Previous = null, string? Origin = null,
    string? SessionId = null, string? Who = null, string? ProjectId = null, string? RequestId = null, string? Expires = null);
