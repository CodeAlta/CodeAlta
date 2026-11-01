using System.Buffers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodeAlta.Plugins.Abstractions;

/// <summary>The kind of the value of a setting of a plugin.</summary>
public enum PluginSettingKind
{
    /// <summary>On or off.</summary>
    Boolean,

    /// <summary>One value among <see cref="PluginSettingContribution.Choices"/>.</summary>
    Choice,

    /// <summary>A whole number, within <see cref="PluginSettingContribution.Minimum"/> and <see cref="PluginSettingContribution.Maximum"/> when they are set.</summary>
    Integer,

    /// <summary>A number, within <see cref="PluginSettingContribution.Minimum"/> and <see cref="PluginSettingContribution.Maximum"/> when they are set.</summary>
    Number,

    /// <summary>A text on one line.</summary>
    Text,

    /// <summary>The path of a file or a folder.</summary>
    Path,

    /// <summary>A list of texts.</summary>
    List,
}

/// <summary>What agents may do with a setting of a plugin, through <c>alta settings</c>.</summary>
public enum PluginSettingAccess
{
    /// <summary>Read it only: the user changes it, in Settings. The default.</summary>
    Read,

    /// <summary>Read it, and change it when the user asks.</summary>
    ReadWrite,
}

/// <summary>Where the value of a setting of a plugin is kept.</summary>
public enum PluginSettingScope
{
    /// <summary>In the configuration of the user, <c>~/.alta/config.toml</c>, under <c>[plugins.&lt;id&gt;]</c>.</summary>
    User,

    /// <summary>
    /// In the configuration of each project, <c>.alta/config.toml</c>, under <c>[plugins.&lt;id&gt;]</c>; the only scope of a
    /// plugin of a project.
    /// </summary>
    Project,
}

/// <summary>One choice of a setting of kind <see cref="PluginSettingKind.Choice"/>.</summary>
/// <param name="Value">The value, as the configuration keeps it.</param>
/// <param name="Label">What Settings shows for it; null to show the value.</param>
public sealed record PluginSettingChoice(string Value, string? Label = null);

/// <summary>The limits of the settings of a plugin.</summary>
public static class PluginSettingLimits
{
    /// <summary>The most settings a plugin declares.</summary>
    public const int Settings = 64;

    /// <summary>The longest label, group or choice label.</summary>
    public const int Label = 120;

    /// <summary>The longest description.</summary>
    public const int Description = 600;

    /// <summary>The most choices or keywords of a setting.</summary>
    public const int Choices = 64;

    /// <summary>The keys of the table of a plugin that are not settings.</summary>
    public static IReadOnlyList<string> ReservedKeys { get; } = ["enabled", "required", "spaces"];
}

/// <summary>
/// A setting of a plugin: a value the user changes in Settings, which the search of Settings, the search of the window,
/// <c>alta settings</c> and <c>alta search</c> find. The host keeps the value in <c>config.toml</c> under
/// <c>[plugins.&lt;id&gt;]</c>; the plugin reads it with <see cref="IPluginSettingsService"/> when it uses it.
/// </summary>
/// <remarks>Create one with the factories of <see cref="Setting"/>.</remarks>
public sealed partial record PluginSettingContribution
{
    /// <summary>Gets the key: 1 to 64 lower-case letters, digits and underscores, the first a letter; the key in the table of the plugin.</summary>
    public required string Key { get; init; }

    /// <summary>Gets the name of the setting, as Settings shows it.</summary>
    public required string Label { get; init; }

    /// <summary>Gets what the setting does, for people and agents; null for none.</summary>
    public string? Description { get; init; }

    /// <summary>Gets the heading the setting is under on the page of the plugin; null for none.</summary>
    public string? Group { get; init; }

    /// <summary>Gets the kind of the value.</summary>
    public required PluginSettingKind Kind { get; init; }

    /// <summary>Gets the value until the user changes it, of the kind of the setting; null for none.</summary>
    public JsonElement? Default { get; init; }

    /// <summary>Gets the choices of a <see cref="PluginSettingKind.Choice"/>.</summary>
    public IReadOnlyList<PluginSettingChoice> Choices { get; init; } = [];

    /// <summary>Gets the least value of a number; null for none.</summary>
    public double? Minimum { get; init; }

    /// <summary>Gets the greatest value of a number; null for none.</summary>
    public double? Maximum { get; init; }

    /// <summary>Gets English words that find the setting beside its label and its description.</summary>
    public IReadOnlyList<string> Keywords { get; init; } = [];

    /// <summary>Gets where the value is kept: for the user (the default) or for each project.</summary>
    public PluginSettingScope Scope { get; init; } = PluginSettingScope.User;

    /// <summary>
    /// Gets what agents may do with it: <see cref="PluginSettingAccess.Read"/> unless the plugin says otherwise. A setting
    /// that protects the user, opens access or deletes data stays <see cref="PluginSettingAccess.Read"/>.
    /// </summary>
    public PluginSettingAccess Agents { get; init; } = PluginSettingAccess.Read;

    /// <summary>Gets whether a change applies at the next start of CodeAlta only; Settings says so.</summary>
    public bool AppliesOnRestart { get; init; }

    /// <summary>Gets the order of the setting on the page of the plugin: lower comes first.</summary>
    public int Order { get; init; }

    /// <summary>Says why the setting cannot be used, or null when it can.</summary>
    /// <returns>The problem, in a sentence, or null.</returns>
    public string? Validate()
    {
        if (string.IsNullOrEmpty(Key) || !KeyPattern().IsMatch(Key)) return "has a key that is not 1 to 64 lower-case letters, digits and underscores starting with a letter";
        if (PluginSettingLimits.ReservedKeys.Contains(Key)) return $"uses '{Key}', a key the table of a plugin keeps for itself";
        if (string.IsNullOrWhiteSpace(Label) || Label.Length > PluginSettingLimits.Label) return $"needs a label of 1 to {PluginSettingLimits.Label} characters";
        if (Description is { Length: > PluginSettingLimits.Description }) return $"has a description longer than {PluginSettingLimits.Description} characters";
        if (Group is { Length: > PluginSettingLimits.Label }) return $"has a group longer than {PluginSettingLimits.Label} characters";
        if (Choices.Count > PluginSettingLimits.Choices || Keywords.Count > PluginSettingLimits.Choices) return $"has more than {PluginSettingLimits.Choices} choices or keywords";
        if (Kind == PluginSettingKind.Choice && Choices.Count == 0) return "is a choice without choices";
        if (Choices.Any(static choice => string.IsNullOrWhiteSpace(choice.Value) || choice.Value.Length > PluginSettingLimits.Label || choice.Label is { Length: > PluginSettingLimits.Label }))
            return "has a choice without a value, or one too long";
        if (Minimum is { } minimum && Maximum is { } maximum && minimum > maximum) return "has a minimum above its maximum";
        if (Default is { } value && !Fits(value)) return "has a default that is not of its kind or not within its bounds";
        return null;
    }

    /// <summary>Whether a value is of the kind of the setting, and within its bounds or among its choices.</summary>
    /// <param name="value">The value.</param>
    /// <returns>True when it fits.</returns>
    public bool Fits(JsonElement value) => Kind switch
    {
        PluginSettingKind.Boolean => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        PluginSettingKind.Choice => value.ValueKind == JsonValueKind.String && Choices.Any(choice => choice.Value == value.GetString()),
        PluginSettingKind.Integer => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var whole) && InBounds(whole),
        PluginSettingKind.Number => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number) && InBounds(number),
        PluginSettingKind.Text or PluginSettingKind.Path => value.ValueKind == JsonValueKind.String,
        PluginSettingKind.List => value.ValueKind == JsonValueKind.Array && value.EnumerateArray().All(static item => item.ValueKind == JsonValueKind.String),
        _ => false,
    };

    private bool InBounds(double value) => (Minimum is not { } minimum || value >= minimum) && (Maximum is not { } maximum || value <= maximum);

    [GeneratedRegex("^[a-z][a-z0-9_]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();
}

/// <summary>Creates the settings a plugin returns from <see cref="PluginBase.GetSettings"/>.</summary>
public static class Setting
{
    /// <summary>A setting that is on or off.</summary>
    /// <param name="key">The key, as <see cref="PluginSettingContribution.Key"/> says it.</param>
    /// <param name="label">The name of the setting.</param>
    /// <param name="description">What it does; null for none.</param>
    /// <param name="defaultValue">The value until the user changes it.</param>
    /// <returns>The setting.</returns>
    public static PluginSettingContribution Boolean(string key, string label, string? description, bool defaultValue)
        => new() { Key = key, Label = label, Description = description, Kind = PluginSettingKind.Boolean, Default = Json(writer => writer.WriteBooleanValue(defaultValue)) };

    /// <summary>A setting that is one value among choices.</summary>
    /// <param name="key">The key, as <see cref="PluginSettingContribution.Key"/> says it.</param>
    /// <param name="label">The name of the setting.</param>
    /// <param name="description">What it does; null for none.</param>
    /// <param name="choices">The values it takes, which Settings shows as they are.</param>
    /// <param name="defaultValue">The value until the user changes it: one of the choices.</param>
    /// <returns>The setting.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="choices"/> is null.</exception>
    public static PluginSettingContribution Choice(string key, string label, string? description, IReadOnlyList<string> choices, string defaultValue)
    {
        ArgumentNullException.ThrowIfNull(choices);
        return new()
        {
            Key = key, Label = label, Description = description, Kind = PluginSettingKind.Choice, Choices = [.. choices.Select(static value => new PluginSettingChoice(value))],
            Default = Json(writer => writer.WriteStringValue(defaultValue)),
        };
    }

    /// <summary>A setting that is a whole number.</summary>
    /// <param name="key">The key, as <see cref="PluginSettingContribution.Key"/> says it.</param>
    /// <param name="label">The name of the setting.</param>
    /// <param name="description">What it does; null for none.</param>
    /// <param name="defaultValue">The value until the user changes it.</param>
    /// <param name="minimum">The least value; null for none.</param>
    /// <param name="maximum">The greatest value; null for none.</param>
    /// <returns>The setting.</returns>
    public static PluginSettingContribution Integer(string key, string label, string? description, long defaultValue, long? minimum = null, long? maximum = null)
        => new()
        {
            Key = key, Label = label, Description = description, Kind = PluginSettingKind.Integer, Minimum = minimum, Maximum = maximum,
            Default = Json(writer => writer.WriteNumberValue(defaultValue)),
        };

    /// <summary>A setting that is a number.</summary>
    /// <param name="key">The key, as <see cref="PluginSettingContribution.Key"/> says it.</param>
    /// <param name="label">The name of the setting.</param>
    /// <param name="description">What it does; null for none.</param>
    /// <param name="defaultValue">The value until the user changes it.</param>
    /// <param name="minimum">The least value; null for none.</param>
    /// <param name="maximum">The greatest value; null for none.</param>
    /// <returns>The setting.</returns>
    public static PluginSettingContribution Number(string key, string label, string? description, double defaultValue, double? minimum = null, double? maximum = null)
        => new()
        {
            Key = key, Label = label, Description = description, Kind = PluginSettingKind.Number, Minimum = minimum, Maximum = maximum,
            Default = Json(writer => writer.WriteNumberValue(defaultValue)),
        };

    /// <summary>A setting that is a text on one line.</summary>
    /// <param name="key">The key, as <see cref="PluginSettingContribution.Key"/> says it.</param>
    /// <param name="label">The name of the setting.</param>
    /// <param name="description">What it does; null for none.</param>
    /// <param name="defaultValue">The value until the user changes it.</param>
    /// <returns>The setting.</returns>
    public static PluginSettingContribution Text(string key, string label, string? description, string defaultValue = "")
        => new() { Key = key, Label = label, Description = description, Kind = PluginSettingKind.Text, Default = Json(writer => writer.WriteStringValue(defaultValue)) };

    /// <summary>A setting that is the path of a file or a folder.</summary>
    /// <param name="key">The key, as <see cref="PluginSettingContribution.Key"/> says it.</param>
    /// <param name="label">The name of the setting.</param>
    /// <param name="description">What it does; null for none.</param>
    /// <param name="defaultValue">The value until the user changes it.</param>
    /// <returns>The setting.</returns>
    public static PluginSettingContribution Path(string key, string label, string? description, string defaultValue = "")
        => new() { Key = key, Label = label, Description = description, Kind = PluginSettingKind.Path, Default = Json(writer => writer.WriteStringValue(defaultValue)) };

    /// <summary>A setting that is a list of texts.</summary>
    /// <param name="key">The key, as <see cref="PluginSettingContribution.Key"/> says it.</param>
    /// <param name="label">The name of the setting.</param>
    /// <param name="description">What it does; null for none.</param>
    /// <param name="defaultValue">The value until the user changes it; null for an empty list.</param>
    /// <returns>The setting.</returns>
    public static PluginSettingContribution List(string key, string label, string? description, IReadOnlyList<string>? defaultValue = null)
        => new()
        {
            Key = key, Label = label, Description = description, Kind = PluginSettingKind.List,
            Default = Json(writer =>
            {
                writer.WriteStartArray();
                foreach (var value in defaultValue ?? []) writer.WriteStringValue(value);
                writer.WriteEndArray();
            }),
        };

    private static JsonElement Json(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>(32);
        using (var writer = new Utf8JsonWriter(buffer)) write(writer);
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }
}

/// <summary>Tells a plugin that one of its settings changed.</summary>
/// <param name="key">The key of the setting.</param>
/// <param name="projectId">The project whose value changed, for a setting of each project; null for the user's.</param>
/// <param name="origin">Who changed it: <c>window</c>, <c>agent</c>, <c>plugin</c>, <c>mcp</c> or <c>other</c>.</param>
public sealed class PluginSettingChangedEventArgs(string key, string? projectId, string origin) : EventArgs
{
    /// <summary>Gets the key of the setting.</summary>
    public string Key { get; } = key;

    /// <summary>Gets the project whose value changed, or null for the user's.</summary>
    public string? ProjectId { get; } = projectId;

    /// <summary>Gets who changed it.</summary>
    public string Origin { get; } = origin;
}

/// <summary>
/// The values of the settings a plugin declares (<see cref="PluginBase.GetSettings"/>). A value the user never changed is
/// the default of the setting. Read a value when it is used: a change made in Settings or by an agent raises
/// <see cref="Changed"/>, and a hand edit of the file is read at the next read.
/// </summary>
public interface IPluginSettingsService
{
    /// <summary>Raised after a setting of the plugin changed through Settings or <c>alta settings</c>.</summary>
    event EventHandler<PluginSettingChangedEventArgs>? Changed;

    /// <summary>Gets the value of a setting.</summary>
    /// <param name="key">The key of the setting.</param>
    /// <param name="projectId">The project, for a setting of each project; null for the user's.</param>
    /// <returns>The value, its default when the user did not change it; null for a key the plugin does not declare, or a host without settings.</returns>
    JsonElement? GetValue(string key, string? projectId = null);

    /// <summary>Gets the value of a setting that is a text, a path or a choice.</summary>
    /// <param name="key">The key of the setting.</param>
    /// <param name="projectId">The project, for a setting of each project; null for the user's.</param>
    /// <returns>The text, or null.</returns>
    string? GetString(string key, string? projectId = null) => GetValue(key, projectId) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    /// <summary>Gets the value of a setting that is on or off.</summary>
    /// <param name="key">The key of the setting.</param>
    /// <param name="projectId">The project, for a setting of each project; null for the user's.</param>
    /// <returns>The value, or null.</returns>
    bool? GetBoolean(string key, string? projectId = null) => GetValue(key, projectId) is { ValueKind: JsonValueKind.True or JsonValueKind.False } value ? value.GetBoolean() : null;

    /// <summary>Gets the value of a setting that is a whole number.</summary>
    /// <param name="key">The key of the setting.</param>
    /// <param name="projectId">The project, for a setting of each project; null for the user's.</param>
    /// <returns>The value, or null.</returns>
    long? GetInt64(string key, string? projectId = null) => GetValue(key, projectId) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt64(out var number) ? number : null;

    /// <summary>Gets the value of a setting that is a number.</summary>
    /// <param name="key">The key of the setting.</param>
    /// <param name="projectId">The project, for a setting of each project; null for the user's.</param>
    /// <returns>The value, or null.</returns>
    double? GetDouble(string key, string? projectId = null) => GetValue(key, projectId) is { ValueKind: JsonValueKind.Number } value ? value.GetDouble() : null;

    /// <summary>Gets the value of a setting that is a list of texts.</summary>
    /// <param name="key">The key of the setting.</param>
    /// <param name="projectId">The project, for a setting of each project; null for the user's.</param>
    /// <returns>The texts, or null.</returns>
    IReadOnlyList<string>? GetList(string key, string? projectId = null)
        => GetValue(key, projectId) is { ValueKind: JsonValueKind.Array } value ? [.. value.EnumerateArray().Where(static item => item.ValueKind == JsonValueKind.String).Select(static item => item.GetString()!)] : null;
}

/// <summary>The settings of a host without settings: no value, no change.</summary>
public sealed class NoopPluginSettingsService : IPluginSettingsService
{
    private NoopPluginSettingsService()
    {
    }

    /// <summary>Gets the shared instance.</summary>
    public static NoopPluginSettingsService Instance { get; } = new();

    /// <inheritdoc />
    public event EventHandler<PluginSettingChangedEventArgs>? Changed { add { } remove { } }

    /// <inheritdoc />
    public JsonElement? GetValue(string key, string? projectId = null) => null;
}
