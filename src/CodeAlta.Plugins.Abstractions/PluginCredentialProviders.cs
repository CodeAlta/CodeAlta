using System.Globalization;
using XenoAtom.Logging;

namespace CodeAlta.Plugins.Abstractions;

/// <summary>Whether a credential provider can give values now.</summary>
public enum CredentialProviderState
{
    /// <summary>It reads values without asking anybody.</summary>
    Ready,

    /// <summary>Its vault must be unlocked first (<see cref="ICredentialProvider.UnlockAsync"/>).</summary>
    Locked,

    /// <summary>The program of its vault has no account: the user signs in with the vault's own command.</summary>
    SignInNeeded,

    /// <summary>The program of its vault is not found on this computer.</summary>
    NotInstalled,

    /// <summary>It cannot answer, for another reason its message says.</summary>
    Unavailable,
}

/// <summary>How to install the program a credential provider needs, on this computer.</summary>
/// <param name="Command">The command of the package manager of the system, such as <c>winget install 1password-cli</c>, run in a terminal the user sees; null when there is none.</param>
/// <param name="Page">The page that explains how to install it; null for none.</param>
public sealed record CredentialProviderInstall(IReadOnlyList<string>? Command, Uri? Page);

/// <summary>What a credential provider says of itself.</summary>
/// <param name="State">Whether it can give values now.</param>
/// <param name="Message">What the user reads beside it, in a sentence; null for nothing more.</param>
public sealed record CredentialProviderStatus(CredentialProviderState State, string? Message)
{
    /// <summary>Gets the account it reads, as the user knows it (an address, a user name); null when it does not say.</summary>
    public string? Account { get; init; }

    /// <summary>Gets the version of the program of its vault; null when unknown.</summary>
    public string? Version { get; init; }

    /// <summary>Gets the full path of the program it runs; null when it runs none, or none was found.</summary>
    public string? ProgramPath { get; init; }

    /// <summary>Gets the command the user runs in a terminal to sign in, for <see cref="CredentialProviderState.SignInNeeded"/>; null for none.</summary>
    public IReadOnlyList<string>? SignInCommand { get; init; }

    /// <summary>Gets how to install its program, for <see cref="CredentialProviderState.NotInstalled"/>; null for no help.</summary>
    public CredentialProviderInstall? Install { get; init; }

    /// <summary>Creates the status of a provider that reads values without asking anybody.</summary>
    /// <param name="message">What the user reads beside it; null for nothing.</param>
    /// <returns>The status.</returns>
    public static CredentialProviderStatus Ready(string? message = null) => new(CredentialProviderState.Ready, message);

    /// <summary>Creates the status of a provider whose vault must be unlocked.</summary>
    /// <param name="message">What the user reads beside it; null for nothing.</param>
    /// <returns>The status.</returns>
    public static CredentialProviderStatus Locked(string? message = null) => new(CredentialProviderState.Locked, message);

    /// <summary>Creates the status of a provider that cannot answer.</summary>
    /// <param name="message">Why, in a sentence.</param>
    /// <returns>The status.</returns>
    public static CredentialProviderStatus Unavailable(string message) => new(CredentialProviderState.Unavailable, message);
}

/// <summary>Why a credential provider could not read a value.</summary>
public enum CredentialFailure
{
    /// <summary>The vault is locked: it is read once unlocked.</summary>
    Locked,

    /// <summary>The program of the vault has no account.</summary>
    SignInNeeded,

    /// <summary>The program of the vault is not installed.</summary>
    NotInstalled,

    /// <summary>The item or the field is not in the vault.</summary>
    NotFound,

    /// <summary>Anything else; the message says what.</summary>
    Failed,
}

/// <summary>Thrown by a credential provider that cannot read a value or list a vault. Its message is shown: it names no value.</summary>
public sealed class CredentialProviderException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="CredentialProviderException"/> class.</summary>
    /// <param name="failure">Why it failed.</param>
    /// <param name="message">What failed, in a sentence; never a value.</param>
    public CredentialProviderException(CredentialFailure failure, string message)
        : base(message)
    {
        Failure = failure;
    }

    /// <summary>Initializes a new instance of the <see cref="CredentialProviderException"/> class.</summary>
    /// <param name="failure">Why it failed.</param>
    /// <param name="message">What failed, in a sentence; never a value.</param>
    /// <param name="innerException">The cause.</param>
    public CredentialProviderException(CredentialFailure failure, string message, Exception? innerException)
        : base(message, innerException)
    {
        Failure = failure;
    }

    /// <summary>Gets why it failed.</summary>
    public CredentialFailure Failure { get; }
}

/// <summary>A value a credential provider read.</summary>
/// <param name="Value">The value. The host masks it before anything else gets it.</param>
public sealed record CredentialValue(string Value)
{
    /// <summary>Gets when the value stops being valid (a one-time code); null for a value that does not expire.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <inheritdoc />
    public override string ToString() => "***";
}

/// <summary>What the host gives a credential provider that unlocks its vault.</summary>
public sealed class CredentialUnlockContext
{
    /// <summary>Gets the password the user typed in the window for a provider that asks one (<see cref="PluginCredentialProviderContribution.UnlockAsksPassword"/>); null for none.</summary>
    public string? Password { get; init; }

    /// <summary>Gets whether a user is in front of the window, who can answer the vault's own prompt.</summary>
    public bool UserPresent { get; init; } = true;
}

/// <summary>What the picker of Settings opens in a vault.</summary>
/// <param name="Parent">The id of the entry to open (a vault, an item); null for the top of the vault.</param>
public sealed record CredentialBrowseRequest(string? Parent);

/// <summary>What an entry of a vault is, in the picker.</summary>
public enum CredentialBrowseEntryKind
{
    /// <summary>Something that holds other entries: a vault, a folder, an item.</summary>
    Folder,

    /// <summary>A field whose value can be a secret: picking it binds the secret.</summary>
    Field,
}

/// <summary>An entry of a vault, as the picker shows it: a name, never a value.</summary>
/// <param name="Id">What the provider opens it by (<see cref="CredentialBrowseRequest.Parent"/>).</param>
/// <param name="Label">Its name.</param>
/// <param name="Kind">What it is.</param>
public sealed record CredentialBrowseEntry(string Id, string Label, CredentialBrowseEntryKind Kind)
{
    /// <summary>Gets what the provider reads for a field, such as <c>op://vault/item/field</c>; null for a folder.</summary>
    public string? Reference { get; init; }

    /// <summary>Gets a short text beside it (the type of a field, the number of items); null for none.</summary>
    public string? Detail { get; init; }
}

/// <summary>The entries of a vault the picker shows.</summary>
/// <param name="Entries">The entries, in the order shown.</param>
public sealed record CredentialBrowsePage(IReadOnlyList<CredentialBrowseEntry> Entries)
{
    /// <summary>Gets the label of what was opened, such as <c>Private › Anthropic</c>; null at the top.</summary>
    public string? Path { get; init; }
}

/// <summary>
/// Reads values from one vault: 1Password, Bitwarden, a command, or what a plugin brings. The host owns everything around it:
/// the names of the secrets bound to its items, the memory of the values and how long they stay, the masking, the
/// confirmations of the secrets that ask before use, and what the window shows. A provider only says whether it can read, and
/// reads. It never writes into a vault.
/// </summary>
/// <remarks>
/// The host calls a provider from the thread pool, one call at a time per provider, with a time limit. A provider that runs
/// a program starts it by its full path with an array of arguments, never through a shell, and never gives a password or a
/// token in an argument.
/// </remarks>
public interface ICredentialProvider
{
    /// <summary>Says whether it can answer now. It never shows a prompt.</summary>
    /// <param name="cancellationToken">A token to cancel the check.</param>
    /// <returns>The status.</returns>
    ValueTask<CredentialProviderStatus> GetStatusAsync(CancellationToken cancellationToken);

    /// <summary>Unlocks its vault while the user is present: the vault shows its own prompt, or takes the password the user typed.</summary>
    /// <param name="context">What the host gives.</param>
    /// <param name="cancellationToken">A token to cancel the unlock.</param>
    /// <returns>The status after it.</returns>
    ValueTask<CredentialProviderStatus> UnlockAsync(CredentialUnlockContext context, CancellationToken cancellationToken);

    /// <summary>Reads one value.</summary>
    /// <param name="reference">What to read, as the picker gave it.</param>
    /// <param name="interactive">Whether the vault may show its own prompt; false never does, and fails with <see cref="CredentialFailure.Locked"/> instead.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The value.</returns>
    /// <exception cref="CredentialProviderException">It cannot be read; the failure says why.</exception>
    ValueTask<CredentialValue> ReadAsync(string reference, bool interactive, CancellationToken cancellationToken);

    /// <summary>Lists entries of the vault for the picker of Settings: names, never values.</summary>
    /// <param name="request">What to open.</param>
    /// <param name="cancellationToken">A token to cancel the listing.</param>
    /// <returns>The entries.</returns>
    /// <exception cref="CredentialProviderException">The vault cannot be listed; the failure says why.</exception>
    /// <exception cref="NotSupportedException">The provider lists nothing: references are typed.</exception>
    ValueTask<CredentialBrowsePage> BrowseAsync(CredentialBrowseRequest request, CancellationToken cancellationToken)
        => throw new NotSupportedException("This credential provider lists nothing: type the reference.");

    /// <summary>Forgets what it holds in memory to read its vault: a session key, a password.</summary>
    void Lock()
    {
    }
}

/// <summary>What a setting of a credential provider holds, which decides how its form draws it.</summary>
public enum PluginCredentialProviderSettingKind
{
    /// <summary>One line of text.</summary>
    Text,

    /// <summary>The path of a file or a program.</summary>
    Path,

    /// <summary>A <c>${secret:NAME}</c> reference to a secret of this computer (a token of the vault), never the secret itself. The host gives the provider its value.</summary>
    Secret,

    /// <summary>A list of texts, such as the arguments of a command.</summary>
    List,

    /// <summary>On or off.</summary>
    Boolean,
}

/// <summary>A setting of a type of credential provider: a key of <c>[credentials.&lt;id&gt;]</c> its form edits.</summary>
/// <param name="Key">The key: lowercase letters, digits and <c>_</c>.</param>
/// <param name="Label">The label of the field.</param>
/// <param name="Kind">What it holds.</param>
public sealed record PluginCredentialProviderSetting(string Key, string Label, PluginCredentialProviderSettingKind Kind)
{
    /// <summary>Gets a short text under the field; null for none.</summary>
    public string? Description { get; init; }

    /// <summary>Gets the text of the empty field; null for none.</summary>
    public string? Placeholder { get; init; }

    /// <summary>Gets whether the provider needs a value of it.</summary>
    public bool Required { get; init; }
}

/// <summary>What the host gives a type to create one of its providers.</summary>
public sealed class PluginCredentialProviderContext
{
    /// <summary>Initializes a new instance of the <see cref="PluginCredentialProviderContext"/> class.</summary>
    /// <param name="id">The id of the provider (<c>[credentials.&lt;id&gt;]</c>).</param>
    /// <param name="settings">Its settings, by key: texts, booleans and lists of texts; a <see cref="PluginCredentialProviderSettingKind.Secret"/> setting holds the value of its secret.</param>
    /// <exception cref="ArgumentException"><paramref name="id"/> is blank.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="settings"/> is null.</exception>
    public PluginCredentialProviderContext(string id, IReadOnlyDictionary<string, object?> settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(settings);
        Id = id;
        Settings = settings;
    }

    /// <summary>Gets the id of the provider.</summary>
    public string Id { get; }

    /// <summary>Gets the settings of the provider, by key.</summary>
    public IReadOnlyDictionary<string, object?> Settings { get; }

    /// <summary>Gets the logger of the provider; null for none. It never gets a value.</summary>
    public Logger? Logger { get; init; }

    /// <summary>
    /// Gets the masking of the host, for what the provider holds that is not a value it returns (a session key, a token): the host
    /// masks it from then on. The values a provider returns are masked by the host already.
    /// </summary>
    public Action<string> Mask { get; init; } = static _ => { };

    /// <summary>Gets a text setting, trimmed; null when absent or blank.</summary>
    /// <param name="key">The key.</param>
    /// <returns>The text.</returns>
    public string? GetString(string key)
        => Settings.TryGetValue(key, out var value) && value is string text && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;

    /// <summary>Gets a boolean setting; null when absent.</summary>
    /// <param name="key">The key.</param>
    /// <returns>The flag.</returns>
    public bool? GetBoolean(string key) => Settings.TryGetValue(key, out var value) && value is bool flag ? flag : null;

    /// <summary>Gets a list setting; empty when absent. Items that are not texts are written as texts.</summary>
    /// <param name="key">The key.</param>
    /// <returns>The texts.</returns>
    public IReadOnlyList<string> GetList(string key)
        => Settings.TryGetValue(key, out var value) && value is IEnumerable<object?> items && value is not string
            ? [.. items.Where(static item => item is not null).Select(static item => Convert.ToString(item, CultureInfo.InvariantCulture)!)]
            : [];
}

/// <summary>
/// A type of credential provider a plugin brings: each <c>[credentials.&lt;id&gt;]</c> of the user's configuration whose
/// <c>type</c> is <see cref="Type"/> is created through it while the plugin runs.
/// </summary>
/// <remarks>
/// Return it from <see cref="PluginBase.GetCredentialProviders"/>. Only the built-in plugins and the plugins of the user
/// (<c>~/.alta/plugins</c>) bring credential providers: the plugin of a project folder never takes part in reading secrets.
/// </remarks>
public sealed record PluginCredentialProviderContribution
{
    /// <summary>
    /// Gets the type, as <c>[credentials.&lt;id&gt;]</c> writes it: <c>&lt;prefix&gt;/&lt;name&gt;</c> for a plugin, with a
    /// prefix of its own, such as <c>acme.vault/secrets</c>.
    /// </summary>
    public required string Type { get; init; }

    /// <summary>Gets the name of the type, such as <c>Acme Vault</c>.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Gets what it reads, in a sentence; null for none.</summary>
    public string? Description { get; init; }

    /// <summary>Gets the brand icon or the Lucide icon it is shown with, such as <c>vault</c>; null for a key.</summary>
    public string? Icon { get; init; }

    /// <summary>Gets the SVG of its icon (the host makes it safe before it shows it); null for <see cref="Icon"/>.</summary>
    public string? IconSvg { get; init; }

    /// <summary>Gets the settings its form edits.</summary>
    public IReadOnlyList<PluginCredentialProviderSetting> Settings { get; init; } = [];

    /// <summary>Gets an example of a reference, shown where it is typed, such as <c>team/item#field</c>; null for none.</summary>
    public string? ReferenceHint { get; init; }

    /// <summary>Gets whether its unlock takes a password the user types in the window (a master password), never kept beyond the unlock.</summary>
    public bool UnlockAsksPassword { get; init; }

    /// <summary>Gets the page of the vault, shown in its form; null for none.</summary>
    public Uri? Website { get; init; }

    /// <summary>Creates a provider.</summary>
    public required Func<PluginCredentialProviderContext, ICredentialProvider> Create { get; init; }

    /// <summary>Gets a message that says why the contribution is not valid, or <see langword="null"/> when it is.</summary>
    /// <returns>The reason, in a sentence.</returns>
    public string? Validate()
    {
        if (!IsPluginType(Type)) return "has a type that is not <prefix>/<name>, such as acme.vault/secrets";
        if (string.IsNullOrWhiteSpace(DisplayName) || DisplayName.Length > 80) return "has no display name, or one longer than 80 characters";
        if (Create is null) return "creates no provider";
        if (Settings is null) return "has no list of settings";
        foreach (var setting in Settings)
        {
            if (setting is null || string.IsNullOrWhiteSpace(setting.Key) || setting.Key.Any(static ch => !(char.IsAsciiLetterLower(ch) || char.IsAsciiDigit(ch) || ch == '_'))
                || setting.Key is "type" or "cache" or "unlock_at_start" or "display_name")
                return $"has a setting key '{setting?.Key}' that is not lowercase letters, digits and _, or that the host keeps";
        }

        return Settings.Select(static setting => setting.Key).Distinct(StringComparer.Ordinal).Count() == Settings.Count ? null : "names a setting twice";
    }

    /// <summary>Gets whether a text is the type of a credential provider of a plugin: <c>&lt;prefix&gt;/&lt;name&gt;</c>.</summary>
    /// <param name="text">The text.</param>
    /// <returns>True for such a type.</returns>
    public static bool IsPluginType(string? text) => PluginAutomationTriggerContribution.IsType(text);
}
