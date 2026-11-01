using System.Text.Json;

namespace CodeAlta.LiveTool;

/// <summary>
/// The settings of CodeAlta as one catalog, for the <c>alta settings</c> commands: what each setting is, its value, a
/// change of it, and the page of Settings that shows it. A host without a window registers no such service, and the
/// commands do not exist there.
/// </summary>
/// <remarks>
/// The settings are the user's. A caller other than the window reads them all, changes only those whose
/// <see cref="AltaSettingDescriptor.Agents"/> is <see cref="AltaSettingAccess.ReadWrite"/>, and the window shows every
/// such change with a way to take it back.
/// </remarks>
public interface IAltaSettings
{
    /// <summary>Lists the settings: the built-in ones in the order of the pages of Settings, then those of the plugins.</summary>
    /// <returns>The descriptors.</returns>
    IReadOnlyList<AltaSettingDescriptor> List();

    /// <summary>Reads the value of a setting.</summary>
    /// <param name="id">The id of the setting.</param>
    /// <param name="projectId">The project whose value is read, for a setting a project can have; null for the user's.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The value and where it comes from, or why it cannot be read.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="id"/> is null.</exception>
    ValueTask<AltaSettingRead> GetAsync(string id, string? projectId, CancellationToken cancellationToken);

    /// <summary>Changes the value of a setting, or gives it back its default.</summary>
    /// <param name="id">The id of the setting.</param>
    /// <param name="value">The value, of the kind of the setting; null for its default.</param>
    /// <param name="projectId">The project whose value is changed, for a setting a project can have; null for the user's.</param>
    /// <param name="origin">Who changes it: anyone but the window changes only what agents may change.</param>
    /// <param name="cancellationToken">Cancels the change before it is written.</param>
    /// <returns>The value now and the one before, or why nothing changed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="id"/> or <paramref name="origin"/> is null.</exception>
    ValueTask<AltaSettingWrite> SetAsync(string id, JsonElement? value, string? projectId, AltaSettingOrigin origin, CancellationToken cancellationToken);

    /// <summary>Opens Settings in the window, on a page, and makes a setting of it glow.</summary>
    /// <param name="page">The page: an id of the catalog, <c>spaces/&lt;id&gt;/&lt;section&gt;</c> or <c>plugin/&lt;key&gt;/&lt;canvas&gt;</c>; null for the page of <paramref name="id"/>.</param>
    /// <param name="id">The setting that glows; null for the page alone.</param>
    /// <param name="callerSessionId">The session that asks, or null.</param>
    /// <returns>True when a window received the request.</returns>
    bool Open(string? page, string? id, string? callerSessionId);
}

/// <summary>The kinds of a setting.</summary>
public static class AltaSettingKinds
{
    /// <summary>On or off.</summary>
    public const string Boolean = "boolean";

    /// <summary>One value among choices.</summary>
    public const string Choice = "choice";

    /// <summary>A whole number, within bounds when the setting has them.</summary>
    public const string Integer = "integer";

    /// <summary>A number, within bounds when the setting has them.</summary>
    public const string Number = "number";

    /// <summary>A text.</summary>
    public const string Text = "text";

    /// <summary>A path of a file or a folder.</summary>
    public const string Path = "path";

    /// <summary>A list of texts.</summary>
    public const string List = "list";

    /// <summary>A value with a structure of its own: it is read, and changed on its page.</summary>
    public const string Complex = "complex";

    /// <summary>Items with commands of their own (<see cref="AltaSettingDescriptor.ManagedBy"/>): listed and opened.</summary>
    public const string Collection = "collection";

    /// <summary>Gets whether a kind has one value that <c>alta settings set</c> can change.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>True for a scalar kind or a list.</returns>
    public static bool IsScalar(string kind) => kind is Boolean or Choice or Integer or Number or Text or Path or List;
}

/// <summary>What callers other than the window may do with a setting.</summary>
public static class AltaSettingAccess
{
    /// <summary>Read and change it.</summary>
    public const string ReadWrite = "readWrite";

    /// <summary>Read it only: the setting protects the user, widens what agents may do, exposes the computer or deletes data.</summary>
    public const string Read = "read";
}

/// <summary>The statuses of a read or a change of a setting.</summary>
public static class AltaSettingStatus
{
    /// <summary>Done.</summary>
    public const string Ok = "ok";

    /// <summary>No setting has this id.</summary>
    public const string NotFound = "not_found";

    /// <summary>The value does not fit the setting; the message says why.</summary>
    public const string Invalid = "invalid";

    /// <summary>Only the user changes this setting, in the window.</summary>
    public const string ReadOnly = "read_only";

    /// <summary>The setting is changed on its page only: a change of it asks the user something first.</summary>
    public const string PageOnly = "page_only";

    /// <summary>The setting is a list of items that commands of their own change.</summary>
    public const string Managed = "managed";

    /// <summary>The setting has a structure of its own: it is changed on its page.</summary>
    public const string Complex = "complex";

    /// <summary>The setting cannot be read or changed here now: no window for a setting of the window, a stopped plugin, another platform.</summary>
    public const string Unavailable = "unavailable";

    /// <summary>The store of the setting refused the change; the message says why.</summary>
    public const string Failed = "failed";
}

/// <summary>Who changes a setting.</summary>
public static class AltaSettingOrigins
{
    /// <summary>The user, in the window.</summary>
    public const string Window = "window";

    /// <summary>An agent session.</summary>
    public const string Agent = "agent";

    /// <summary>A plugin.</summary>
    public const string Plugin = "plugin";

    /// <summary>Another application, through the MCP server.</summary>
    public const string Mcp = "mcp";

    /// <summary>The host itself, or another caller.</summary>
    public const string Other = "other";
}

/// <summary>One choice of a setting.</summary>
/// <param name="Value">The value, as the setting stores it.</param>
/// <param name="Label">What the window shows for it, in English; null to show the value.</param>
public sealed record AltaSettingChoice(string Value, string? Label);

/// <summary>What a setting is: where it is shown, what it holds, and who may change it.</summary>
/// <param name="Id">The id: the key path of a setting of <c>config.toml</c>, else named after its page (<c>general.keep_awake</c>); <c>plugins.&lt;id&gt;.&lt;key&gt;</c> for a plugin.</param>
/// <param name="Label">The name of the setting, in English, as its page shows it.</param>
/// <param name="Help">What it does, in English, or null.</param>
/// <param name="Page">The page of Settings that shows it.</param>
/// <param name="PagePath">The group and the name of that page, in English: <c>Agent &amp; models › Sessions</c>.</param>
/// <param name="Section">The part of the page that shows it, in English, or null.</param>
/// <param name="Kind">One of <see cref="AltaSettingKinds"/>.</param>
/// <param name="Choices">The choices of a choice; empty when they change (their binding gives them with the value).</param>
/// <param name="Minimum">The least value of a number, or null.</param>
/// <param name="Maximum">The greatest value of a number, or null.</param>
/// <param name="Default">The value until the user changes it, or null when it has none.</param>
/// <param name="Agents">One of <see cref="AltaSettingAccess"/>.</param>
/// <param name="Keywords">English words that find it beside its name.</param>
public sealed record AltaSettingDescriptor(string Id, string Label, string? Help, string Page, string PagePath, string? Section, string Kind,
    IReadOnlyList<AltaSettingChoice> Choices, double? Minimum, double? Maximum, JsonElement? Default, string Agents, IReadOnlyList<string> Keywords)
{
    /// <summary>Gets who keeps the value: <c>host</c>, <c>window</c> (the page of the window) or <c>plugin</c>.</summary>
    public string Owner { get; init; } = "host";

    /// <summary>Gets whether anyone changes it outside its page; false when a change asks the user something first (a download, a consent).</summary>
    public bool Writable { get; init; } = true;

    /// <summary>Gets the commands that change a collection, such as <c>alta mcp server</c>, or null.</summary>
    public string? ManagedBy { get; init; }

    /// <summary>Gets when a change applies: <c>now</c>, <c>nextPrompt</c> or <c>nextStart</c>.</summary>
    public string Applies { get; init; } = "now";

    /// <summary>Gets the scopes a value can have: <c>user</c>, and <c>project</c> for a setting a project can carry.</summary>
    public IReadOnlyList<string> Scopes { get; init; } = ["user"];

    /// <summary>Gets the runtime key of the plugin of the setting, or null for a built-in one.</summary>
    public string? Plugin { get; init; }

    /// <summary>Gets the display name of the plugin of the setting, or null.</summary>
    public string? PluginName { get; init; }

    /// <summary>
    /// Gets the project of a plugin of a project, whose settings are all that project's and reached only from its spaces;
    /// null for a built-in setting or a plugin of the user.
    /// </summary>
    public string? Project { get; init; }

    /// <summary>Gets whether the setting is there in this host and on this platform.</summary>
    public bool Available { get; init; } = true;
}

/// <summary>A value of a setting, as it was read.</summary>
/// <param name="Status">One of <see cref="AltaSettingStatus"/>.</param>
/// <param name="Value">The value, or null when it was not read.</param>
/// <param name="Source">Where it comes from: <c>default</c>, <c>user</c>, <c>project</c> or <c>window</c>; null when it was not read.</param>
/// <param name="Message">Why it was not read, or null.</param>
public sealed record AltaSettingRead(string Status, JsonElement? Value, string? Source, string? Message)
{
    /// <summary>Gets the choices of a choice whose choices change, or null.</summary>
    public IReadOnlyList<AltaSettingChoice>? Choices { get; init; }
}

/// <summary>How a change of a setting ended.</summary>
/// <param name="Status">One of <see cref="AltaSettingStatus"/>.</param>
/// <param name="Value">The value now, or null when nothing changed.</param>
/// <param name="Previous">The value before, or null.</param>
/// <param name="Message">Why nothing changed, or null.</param>
public sealed record AltaSettingWrite(string Status, JsonElement? Value, JsonElement? Previous, string? Message);

/// <summary>Who changes a setting, so that the window says it to the user.</summary>
/// <param name="Kind">One of <see cref="AltaSettingOrigins"/>.</param>
/// <param name="SessionId">The session that changes it, or null.</param>
/// <param name="Name">The plugin that changes it, or null.</param>
public sealed record AltaSettingOrigin(string Kind, string? SessionId, string? Name)
{
    /// <summary>Gets the origin of a change the user makes in the window.</summary>
    public static AltaSettingOrigin Window { get; } = new(AltaSettingOrigins.Window, null, null);

    /// <summary>Gets whether the change comes from the user in the window.</summary>
    public bool IsWindow => Kind == AltaSettingOrigins.Window;
}
