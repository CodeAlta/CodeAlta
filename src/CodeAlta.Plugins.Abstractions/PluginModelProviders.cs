using System.Globalization;
using CodeAlta.Agent;
using CodeAlta.Agent.Harness;
using XenoAtom.Logging;

namespace CodeAlta.Plugins.Abstractions;

/// <summary>The kind of a setting of a provider type a plugin contributes, which the form of the provider edits.</summary>
public enum PluginProviderSettingKind
{
    /// <summary>One line of text.</summary>
    Text,
    /// <summary>The path of a file or a folder.</summary>
    Path,
    /// <summary>A list of texts, such as command-line arguments.</summary>
    List,
    /// <summary>Names and values, such as environment variables.</summary>
    Map,
    /// <summary>On or off.</summary>
    Boolean,
}

/// <summary>
/// A setting of a provider type a plugin contributes: a key of <c>[providers.&lt;key&gt;]</c> that the form of the
/// provider edits beside the settings every provider has (<c>display_name</c>, <c>model</c>, <c>enabled</c>, ...).
/// </summary>
/// <param name="Key">The key in the configuration, such as <c>command</c>: lowercase letters, digits and <c>_</c>.</param>
/// <param name="Label">The label of the field.</param>
/// <param name="Kind">The kind of the value.</param>
public sealed record PluginProviderSetting(string Key, string Label, PluginProviderSettingKind Kind)
{
    /// <summary>Gets a short text shown under the field, or <see langword="null" />.</summary>
    public string? Description { get; init; }

    /// <summary>Gets the text shown in an empty field, or <see langword="null" />.</summary>
    public string? Placeholder { get; init; }

    /// <summary>Gets whether the field is shown only when the user opens the advanced settings.</summary>
    public bool Advanced { get; init; }

    /// <summary>Gets whether the form shows the field read-only: the plugin writes it (a version it installed).</summary>
    public bool ReadOnly { get; init; }
}

/// <summary>
/// The settings of one provider of a type a plugin contributes, as the configuration gives them.
/// </summary>
public sealed class PluginProviderSettings
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PluginProviderSettings" /> class.
    /// </summary>
    /// <param name="providerKey">The key of the provider (<c>[providers.&lt;key&gt;]</c>).</param>
    /// <param name="providerType">The type of the provider (<c>type</c>).</param>
    /// <param name="values">The values of its section, by key: texts, numbers, booleans, lists and tables.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="providerKey" /> or <paramref name="providerType" /> is empty.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="values" /> is <see langword="null" />.</exception>
    public PluginProviderSettings(string providerKey, string providerType, IReadOnlyDictionary<string, object?> values)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerType);
        ArgumentNullException.ThrowIfNull(values);
        ProviderKey = providerKey;
        ProviderType = providerType;
        Values = values;
    }

    /// <summary>Gets the key of the provider.</summary>
    public string ProviderKey { get; }

    /// <summary>Gets the type of the provider.</summary>
    public string ProviderType { get; }

    /// <summary>Gets the values of the section of the provider, by key.</summary>
    public IReadOnlyDictionary<string, object?> Values { get; }

    /// <summary>Gets whether the provider is the default one of the configuration (<c>chat.default_provider</c>).</summary>
    public bool IsDefault { get; init; }

    /// <summary>Gets the name shown for the provider: <c>display_name</c>, else its key.</summary>
    public string DisplayName => GetString("display_name") ?? ProviderKey;

    /// <summary>Gets whether the provider is turned on (<c>enabled</c>, on when absent).</summary>
    public bool IsEnabled => GetBoolean("enabled") ?? true;

    /// <summary>Gets the model of a new session (<c>single_model_id</c>, else <c>model</c>), or <see langword="null" />.</summary>
    public string? DefaultModelId => GetString("single_model_id") ?? GetString("model");

    /// <summary>Gets the reasoning effort of a new session (<c>reasoning_effort</c>), or <see langword="null" />.</summary>
    public AgentReasoningEffort? DefaultReasoningEffort
        => GetString("reasoning_effort")?.ToLowerInvariant() switch
        {
            "none" => AgentReasoningEffort.None,
            "minimal" => AgentReasoningEffort.Minimal,
            "low" => AgentReasoningEffort.Low,
            "medium" => AgentReasoningEffort.Medium,
            "high" => AgentReasoningEffort.High,
            "xhigh" => AgentReasoningEffort.XHigh,
            "max" => AgentReasoningEffort.Max,
            _ => null,
        };

    /// <summary>Gets the permission mode of a new session (<c>permission_mode</c>), or <see langword="null" />.</summary>
    public string? PermissionMode => GetString("permission_mode");

    /// <summary>Returns a text value, trimmed, or <see langword="null" /> when it is absent, empty or not a text.</summary>
    /// <param name="key">The key.</param>
    public string? GetString(string key)
        => Values.TryGetValue(key, out var value) && value is string text && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;

    /// <summary>Returns a boolean value, or <see langword="null" /> when it is absent or not a boolean.</summary>
    /// <param name="key">The key.</param>
    public bool? GetBoolean(string key)
        => Values.TryGetValue(key, out var value) && value is bool flag ? flag : null;

    /// <summary>Returns a list of texts, or an empty list when it is absent; the items that are not texts are left out.</summary>
    /// <param name="key">The key.</param>
    public IReadOnlyList<string> GetList(string key)
        => Values.TryGetValue(key, out var value) && value is IEnumerable<object?> items
            ? [.. items.OfType<string>()]
            : [];

    /// <summary>Returns names and texts, or an empty map when it is absent; values that are not texts are written as texts.</summary>
    /// <param name="key">The key.</param>
    public IReadOnlyDictionary<string, string> GetMap(string key)
    {
        if (!Values.TryGetValue(key, out var value) || value is not IReadOnlyDictionary<string, object?> table)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, item) in table)
        {
            if (item is null or IReadOnlyDictionary<string, object?> || (item is not string && item is IEnumerable<object?>))
            {
                continue;
            }

            map[name] = Convert.ToString(item, CultureInfo.InvariantCulture) ?? string.Empty;
        }

        return map;
    }
}

/// <summary>What a plugin is given to create the runtime of one of its providers.</summary>
public sealed class PluginModelProviderContext
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PluginModelProviderContext" /> class.
    /// </summary>
    /// <param name="settings">The settings of the provider.</param>
    /// <param name="descriptor">The descriptor the host registered the provider with.</param>
    /// <param name="stateRootPath">The state folder of the application (<c>~/.alta</c>).</param>
    /// <param name="toolEndpoints">Serves the tools of a session to a harness that connects to an MCP server by itself.</param>
    /// <exception cref="ArgumentNullException">Thrown when an argument is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="stateRootPath" /> is empty.</exception>
    public PluginModelProviderContext(
        PluginProviderSettings settings,
        ModelProviderDescriptor descriptor,
        string stateRootPath,
        IHarnessToolEndpoints toolEndpoints)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRootPath);
        ArgumentNullException.ThrowIfNull(toolEndpoints);
        Settings = settings;
        Descriptor = descriptor;
        StateRootPath = stateRootPath;
        ToolEndpoints = toolEndpoints;
    }

    /// <summary>Gets the settings of the provider.</summary>
    public PluginProviderSettings Settings { get; }

    /// <summary>Gets the descriptor the host registered the provider with.</summary>
    public ModelProviderDescriptor Descriptor { get; }

    /// <summary>Gets the state folder of the application (<c>~/.alta</c>); the cache folder is <c>cache</c> under it.</summary>
    public string StateRootPath { get; }

    /// <summary>Gets the service that serves the tools of a session as an MCP endpoint.</summary>
    public IHarnessToolEndpoints ToolEndpoints { get; }

    /// <summary>Gets the logger of the provider, or <see langword="null" />.</summary>
    public Logger? Logger { get; init; }
}

/// <summary>A way to sign in to the account a provider uses.</summary>
/// <param name="Id">The identifier of the method, given back to <see cref="IPluginProviderAccount.SignInAsync" />.</param>
/// <param name="Label">The label of its button.</param>
/// <param name="Description">A sentence that says what it does, or <see langword="null" />.</param>
public sealed record PluginProviderSignInMethod(string Id, string Label, string? Description)
{
    /// <summary>
    /// Gets the command the user runs in a terminal to sign in, when the method is one: the window opens a terminal
    /// on it instead of calling <see cref="IPluginProviderAccount.SignInAsync" />; <see langword="null" /> otherwise.
    /// </summary>
    public IReadOnlyList<string>? TerminalCommand { get; init; }

    /// <summary>Gets the environment variables the terminal command runs with, or <see langword="null" />.</summary>
    public IReadOnlyDictionary<string, string>? TerminalEnvironment { get; init; }
}

/// <summary>The sign-in of the account a provider of a plugin uses.</summary>
public interface IPluginProviderAccount
{
    /// <summary>Lists the ways to sign in to the account of a provider.</summary>
    /// <param name="settings">The settings of the provider.</param>
    /// <param name="cancellationToken">A token to cancel the reading.</param>
    /// <returns>The methods; empty when the provider needs no sign-in or tells none.</returns>
    Task<IReadOnlyList<PluginProviderSignInMethod>> GetSignInMethodsAsync(PluginProviderSettings settings, CancellationToken cancellationToken);

    /// <summary>Signs in with a method that is not a terminal command.</summary>
    /// <param name="settings">The settings of the provider.</param>
    /// <param name="methodId">The identifier of the method.</param>
    /// <param name="cancellationToken">A token to cancel the sign-in.</param>
    /// <returns>A task that ends once the sign-in ended.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the sign-in fails; its message is shown.</exception>
    Task SignInAsync(PluginProviderSettings settings, string methodId, CancellationToken cancellationToken);
}

/// <summary>The kind of what an entry of a catalog of providers installs.</summary>
public enum PluginProviderCatalogInstallKind
{
    /// <summary>Nothing to install: the entry runs through a tool already on the computer.</summary>
    None,
    /// <summary>An executable downloaded by the plugin.</summary>
    Download,
    /// <summary>A package a package runner (<c>npx</c>, <c>uvx</c>) fetches on its first start.</summary>
    PackageRunner,
}

/// <summary>An entry of a catalog of providers: something a user adds as a provider in one step.</summary>
/// <param name="Id">The identifier of the entry in its catalog.</param>
/// <param name="Name">The name shown.</param>
/// <param name="Version">The version it installs, or <see langword="null" />.</param>
public sealed record PluginProviderCatalogEntry(string Id, string Name, string? Version)
{
    /// <summary>Gets a sentence that says what it is, or <see langword="null" />.</summary>
    public string? Description { get; init; }

    /// <summary>Gets the SVG of its icon, as the catalog gives it (the host makes it safe before it shows it), or <see langword="null" />.</summary>
    public string? IconSvg { get; init; }

    /// <summary>Gets the address of its web page, or <see langword="null" />.</summary>
    public string? Website { get; init; }

    /// <summary>Gets who makes it, or <see langword="null" />.</summary>
    public string? Authors { get; init; }

    /// <summary>Gets what it installs.</summary>
    public PluginProviderCatalogInstallKind InstallKind { get; init; }

    /// <summary>Gets a short text that says how it runs, such as <c>npx</c> or <c>binary</c>, or <see langword="null" />.</summary>
    public string? Distribution { get; init; }

    /// <summary>Gets why it cannot be added on this computer (a missing tool, an unsupported platform), or <see langword="null" /> when it can.</summary>
    public string? Unavailable { get; init; }
}

/// <summary>The entries of a catalog of providers.</summary>
/// <param name="Entries">The entries.</param>
/// <param name="FetchedAt">When the catalog was fetched, or <see langword="null" /> when it never was.</param>
public sealed record PluginProviderCatalogSnapshot(IReadOnlyList<PluginProviderCatalogEntry> Entries, DateTimeOffset? FetchedAt)
{
    /// <summary>Gets why the last fetch failed, when the entries are the ones kept from an earlier one, or <see langword="null" />.</summary>
    public string? Error { get; init; }
}

/// <summary>The progress of the installation of an entry of a catalog.</summary>
/// <param name="Message">What is being done.</param>
/// <param name="Received">The bytes received so far, or <see langword="null" />.</param>
/// <param name="Total">The bytes to receive, or <see langword="null" /> when not known.</param>
public sealed record PluginProviderInstallProgress(string Message, long? Received = null, long? Total = null);

/// <summary>What the host writes for an entry of a catalog that was installed: the section of a new provider.</summary>
/// <param name="SuggestedKey">The key the provider is given, or the first free one after it (<c>-2</c>, <c>-3</c>...).</param>
/// <param name="DisplayName">The name of the provider.</param>
/// <param name="Values">The settings of its type to write, by key.</param>
public sealed record PluginProviderInstallResult(string SuggestedKey, string DisplayName, IReadOnlyDictionary<string, object?> Values)
{
    /// <summary>Gets the brand icon or Lucide icon the provider is shown with, or <see langword="null" />.</summary>
    public string? Icon { get; init; }
}

/// <summary>A catalog of providers a plugin offers: the Add provider dialog of the window lists it.</summary>
public interface IPluginProviderCatalog
{
    /// <summary>Gets the title of the catalog, such as "ACP registry".</summary>
    string Title { get; }

    /// <summary>Lists the entries of the catalog.</summary>
    /// <param name="refresh">Whether to fetch the catalog again rather than read the copy kept.</param>
    /// <param name="cancellationToken">A token to cancel the reading.</param>
    /// <returns>The entries.</returns>
    Task<PluginProviderCatalogSnapshot> ListAsync(bool refresh, CancellationToken cancellationToken);

    /// <summary>Installs an entry and returns the settings of its provider.</summary>
    /// <param name="entryId">The identifier of the entry.</param>
    /// <param name="progress">Receives the progress.</param>
    /// <param name="cancellationToken">A token to cancel the installation.</param>
    /// <returns>What the host writes.</returns>
    /// <exception cref="InvalidOperationException">Thrown when it cannot be installed; its message is shown.</exception>
    Task<PluginProviderInstallResult> InstallAsync(string entryId, IProgress<PluginProviderInstallProgress> progress, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the version an installed provider can be updated to, or <see langword="null" /> when it is up to date
    /// or was not installed from the catalog.
    /// </summary>
    /// <param name="settings">The settings of the provider.</param>
    /// <param name="cancellationToken">A token to cancel the reading.</param>
    Task<string?> GetUpdateAsync(PluginProviderSettings settings, CancellationToken cancellationToken);

    /// <summary>Updates an installed provider, and returns the settings that change.</summary>
    /// <param name="settings">The settings of the provider.</param>
    /// <param name="progress">Receives the progress.</param>
    /// <param name="cancellationToken">A token to cancel the update.</param>
    /// <returns>The values to write over the ones of the provider.</returns>
    /// <exception cref="InvalidOperationException">Thrown when it cannot be updated; its message is shown.</exception>
    Task<IReadOnlyDictionary<string, object?>> UpdateAsync(PluginProviderSettings settings, IProgress<PluginProviderInstallProgress> progress, CancellationToken cancellationToken);
}

/// <summary>Describes what one provider of a plugin runs, for its form: a line such as "npx @github/copilot@1.0.103 --acp".</summary>
/// <param name="Summary">The line shown.</param>
/// <param name="Detail">A sentence that says more, or <see langword="null" />.</param>
public sealed record PluginProviderDescription(string Summary, string? Detail = null);

/// <summary>
/// A provider type a plugin contributes: CodeAlta registers each provider of the configuration with this
/// <c>type</c> through the plugin, and shows it like any other provider.
/// </summary>
/// <remarks>
/// <para>
/// Return it from <see cref="PluginBase.GetModelProviders"/>. While the plugin runs, the providers of its type are
/// registered, listed in the model pickers and edited in Settings &gt; Providers with the settings it declares; when it
/// stops, they are removed, their runtimes disposed (which ends what they started), and their sessions see them as
/// unavailable until it runs again. Their sections stay in the configuration file.
/// </para>
/// <para>
/// The runtime is created lazily, at the first use of the provider, and is the plugin's: it owns what it starts and
/// releases it when disposed. A plugin that runs its own agent loop implements the harness seams of
/// <c>CodeAlta.Agent.Runtime</c> (<see cref="Agent.Runtime.IAgentProviderToolHost"/>, ...) on its turn executor.
/// </para>
/// </remarks>
public sealed record PluginModelProviderContribution
{
    /// <summary>Gets the type: the <c>type</c> of <c>[providers.&lt;key&gt;]</c>, lowercase, which no built-in provider has.</summary>
    public required string ProviderType { get; init; }

    /// <summary>Gets the name of the type, such as "ACP agent".</summary>
    public required string DisplayName { get; init; }

    /// <summary>Gets the settings of the type the form edits.</summary>
    public IReadOnlyList<PluginProviderSetting> Settings { get; init; } = [];

    /// <summary>Gets the permission modes a session of a provider of this type can be in; empty for none.</summary>
    public IReadOnlyList<string> PermissionModes { get; init; } = [];

    /// <summary>Gets the short tag shown with a provider of this type in Settings, such as <c>ACP</c>, or <see langword="null" />.</summary>
    public string? Tag { get; init; }

    /// <summary>Creates the runtime of a provider.</summary>
    public required Func<PluginModelProviderContext, IAgentModelProviderRuntime> CreateRuntime { get; init; }

    /// <summary>Gets what checks the settings of a provider: the problems, each in a sentence; null to check nothing.</summary>
    public Func<PluginProviderSettings, IReadOnlyList<string>>? Validate { get; init; }

    /// <summary>Gets what describes what a provider runs, for its form, or <see langword="null" />.</summary>
    public Func<PluginProviderSettings, CancellationToken, Task<PluginProviderDescription?>>? Describe { get; init; }

    /// <summary>Gets the sign-in of the account of a provider, or <see langword="null" /> when there is none.</summary>
    public IPluginProviderAccount? Account { get; init; }

    /// <summary>Gets the catalog of providers the Add provider dialog lists, or <see langword="null" />.</summary>
    public IPluginProviderCatalog? Catalog { get; init; }

    /// <summary>Gets a message that says why the contribution is not valid, or <see langword="null" /> when it is.</summary>
    /// <returns>The reason, in a sentence.</returns>
    public string? CheckValid()
    {
        if (string.IsNullOrWhiteSpace(ProviderType) || ProviderType.Any(static character => !(char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character == '-')))
        {
            return "has a provider type that is not lowercase letters, digits and '-'";
        }

        if (string.IsNullOrWhiteSpace(DisplayName))
        {
            return "has no display name";
        }

        foreach (var setting in Settings)
        {
            if (string.IsNullOrWhiteSpace(setting.Key) || setting.Key.Any(static character => !(char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character == '_')))
            {
                return $"has a setting key '{setting.Key}' that is not lowercase letters, digits and '_'";
            }
        }

        return null;
    }
}
