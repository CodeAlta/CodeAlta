namespace CodeAlta.Fleet.NodeApi;

/// <summary>
/// A provider configured on a node, as the device that manages it sees it (<c>control.providers.list</c>): whether it is
/// ready to run sessions, and why not. Never its key: only the name of the secret of the machine that holds it.
/// </summary>
/// <param name="Key">Its key in the configuration of the node.</param>
/// <param name="Name">Its name.</param>
/// <param name="Type">Its type (<c>openai-responses</c>, <c>anthropic</c>, <c>copilot</c>...).</param>
/// <param name="Enabled">Whether it is enabled.</param>
/// <param name="State">
/// <see cref="FleetProviderStates.Ready"/>, <see cref="FleetProviderStates.Preparing"/>, <see cref="FleetProviderStates.Failed"/>,
/// <see cref="FleetProviderStates.NotReady"/> (left out when the node started it) or <see cref="FleetProviderStates.Disabled"/>.
/// </param>
/// <param name="Reason">
/// Why it is not ready, as a code: <see cref="FleetProviderReasons.KeyMissing"/>, <see cref="FleetProviderReasons.SecretMissing"/>,
/// <see cref="FleetProviderReasons.SignInNeeded"/>, <see cref="FleetProviderReasons.StartFailed"/>; null when it is ready.
/// </param>
/// <param name="Detail">What the node knows more of it, in words (the secret it waits for, the error of its start); null for nothing.</param>
/// <param name="Credential">How it gets its credential: <c>key</c>, <c>sign_in</c>, <c>cli</c> (its own login on the machine) or <c>none</c>.</param>
/// <param name="KeySecret">The name of the secret of the machine that holds its key; null for none.</param>
/// <param name="SignIn">Whether it signs in with a device code that the desktop shows (<c>device</c> mode); false otherwise.</param>
/// <param name="IsDefault">Whether a session that names no provider runs on it.</param>
/// <param name="Model">Its default model; null for the one of its type.</param>
/// <param name="ApiUrl">Its endpoint, when the configuration names one.</param>
public sealed record FleetProviderStatus(string Key, string Name, string Type, bool Enabled, string State, string? Reason, string? Detail, string Credential,
    string? KeySecret, bool SignIn, bool IsDefault, string? Model, string? ApiUrl)
{
    /// <summary>Gets the most requests it takes at once (<c>max_concurrent_requests</c>); null for no limit, or the default of its type. An older node says none.</summary>
    public int? MaxConcurrentRequests { get; init; }

    /// <summary>Gets the program it runs on the machine (Claude Code, the Codex app-server, an ACP agent), as the node installs and updates it; null for a provider that runs none.</summary>
    public FleetProviderProgram? Program { get; init; }

    /// <summary>Gets the SVG of its icon, as its plugin gives it (the desktop makes it safe before it shows it); null for the icon of its type.</summary>
    public string? IconSvg { get; init; }
}

/// <summary>The program a provider runs on the machine, as the node installs and updates it.</summary>
/// <param name="State">
/// <c>installed</c> (by the node), <c>found</c> (installed on the machine by someone else, which the node leaves as it is), <c>missing</c> (the node
/// installs it), <c>runner</c> (a package runner such as <c>npx</c> fetches it at its first start) or <c>unsupported</c> (nothing runs here).
/// </param>
/// <param name="Version">The version the node installs or installed; null when it is not known.</param>
/// <param name="Update">The version the node can update it to; null when it is up to date or the node does not update it.</param>
/// <param name="Detail">A sentence more: where it is, or why it cannot run here; null for nothing.</param>
public sealed record FleetProviderProgram(string State, string? Version, string? Update, string? Detail);

/// <summary>The providers a node offers to add (<c>control.providers.offers</c>).</summary>
/// <param name="Refresh">Whether to fetch the catalogs of the plugins again (the ACP registry) rather than read the copy kept.</param>
public sealed record FleetProviderOffersParams(bool Refresh = false);

/// <summary>
/// A provider a node can add: a type of its own (Claude Code, an API, a subscription), or an entry of the catalog of a plugin
/// (Codex CLI, an agent of the ACP registry).
/// </summary>
/// <param name="Id">Its identifier among the offers: the type, or <c>&lt;type&gt;:&lt;entry&gt;</c> for an entry of a catalog.</param>
/// <param name="Type">The type of the provider it adds.</param>
/// <param name="Name">Its name.</param>
/// <param name="Group"><c>agent</c> (a program that runs on the machine), <c>subscription</c> (an account signed in), <c>key</c> (an API key) or <c>local</c> (a model server).</param>
/// <param name="Credential">How its provider gets its credential: <c>key</c>, <c>sign_in</c>, <c>cli</c> (the login of its program) or <c>none</c>.</param>
public sealed record FleetProviderOffer(string Id, string Type, string Name, string Group, string Credential)
{
    /// <summary>Gets a sentence that says what it is; null for none.</summary>
    public string? Description { get; init; }

    /// <summary>Gets the SVG of its icon, as its catalog gives it (the desktop makes it safe); null for the icon of its type.</summary>
    public string? IconSvg { get; init; }

    /// <summary>Gets the program it runs and whether the node installs it; null for a provider that runs none.</summary>
    public FleetProviderProgram? Program { get; init; }

    /// <summary>Gets why it cannot be added on this machine; null when it can.</summary>
    public string? Unavailable { get; init; }

    /// <summary>Gets the title of the catalog it comes from (<c>ACP registry</c>); null for a type of the node.</summary>
    public string? Catalog { get; init; }

    /// <summary>Gets the key of a provider of the machine that runs it already; null for none.</summary>
    public string? ConfiguredKey { get; init; }

    /// <summary>Gets whether its provider signs in with a device code the desktop shows.</summary>
    public bool SignIn { get; init; }
}

/// <summary>The providers a node offers to add.</summary>
/// <param name="Offers">The offers, its own types first, then the entries of the catalogs of its plugins.</param>
/// <param name="Error">Why a catalog could not be read, when its entries are missing or old; null when all were read.</param>
public sealed record FleetProviderOfferList(IReadOnlyList<FleetProviderOffer> Offers, string? Error);

/// <summary>A provider to add from an offer (<c>control.providers.add</c>): the program it runs is installed first when it is missing.</summary>
/// <param name="OfferId">The offer.</param>
/// <param name="Key">Its key; null for the key the offer suggests, or the first free one after it.</param>
/// <param name="MakeDefault">Whether sessions that name no provider run on it.</param>
public sealed record FleetProviderAddParams(string OfferId, string? Key = null, bool MakeDefault = false);

/// <summary>Where an addition or an update of a provider of a node stands.</summary>
/// <param name="Id">The operation.</param>
/// <param name="Status"><c>running</c>, <c>completed</c> or <c>failed</c>.</param>
/// <param name="Message">What it does, or why it failed, as a sentence; null for nothing.</param>
/// <param name="Received">The bytes received of a download; null when none runs.</param>
/// <param name="Total">The bytes of the download; null when not known.</param>
/// <param name="Key">The provider added or updated, once it is known.</param>
public sealed record FleetProviderOperationState(string Id, string Status, string? Message, long? Received, long? Total, string? Key);

/// <summary>A wait for an addition or an update of a provider of a node.</summary>
/// <param name="Id">The operation.</param>
/// <param name="WaitSeconds">How long the node waits for it to end before it answers <c>running</c>: 0 to 25.</param>
public sealed record FleetProviderOperationWaitParams(string Id, int WaitSeconds = 20);

/// <summary>A type of provider a node can be given from the desktop, and how it gets its credential.</summary>
/// <param name="Type">The type.</param>
/// <param name="Name">The name it takes by default.</param>
/// <param name="Credential"><c>key</c> or <c>sign_in</c>.</param>
/// <param name="SignIn">Whether it signs in with a device code the desktop shows.</param>
public sealed record FleetProviderType(string Type, string Name, string Credential, bool SignIn);

/// <summary>The providers of a node, and the types it can be given.</summary>
/// <param name="Providers">Its providers, in the order of its configuration.</param>
/// <param name="Types">The types a provider added from the desktop can have.</param>
/// <param name="SecretProtection">How the store of the secrets of the machine protects them (<c>dpapi</c>, <c>keychain</c>, <c>libsecret</c>, <c>file</c>).</param>
public sealed record FleetProviderList(IReadOnlyList<FleetProviderStatus> Providers, IReadOnlyList<FleetProviderType> Types, string? SecretProtection)
{
    /// <summary>Gets why the configuration file of the node does not read since it last changed (its providers run as before); null when it reads.</summary>
    public string? ConfigurationError { get; init; }
}

/// <summary>
/// A provider of a node to add or change (<c>control.providers.set</c>). Its key, when given, goes to the secrets store of
/// the machine, and the configuration names that secret: it is never written in clear in the configuration.
/// </summary>
/// <param name="Key">Its key in the configuration: letters, digits, <c>-</c> and <c>_</c>.</param>
/// <param name="Type">Its type; null keeps the type of a provider that exists.</param>
/// <param name="DisplayName">Its name; null for the one of its type.</param>
/// <param name="Model">Its default model; null for the one of its type.</param>
/// <param name="ApiUrl">Its endpoint; null for the one of its type.</param>
/// <param name="ApiKey">Its key, typed by the person; null keeps the key it has.</param>
/// <param name="Enabled">Whether it is enabled.</param>
/// <param name="MakeDefault">Whether sessions that name no provider run on it.</param>
public sealed record FleetProviderSetParams(string Key, string? Type, string? DisplayName, string? Model, string? ApiUrl, string? ApiKey, bool Enabled = true,
    bool MakeDefault = false)
{
    /// <summary>Gets the model server a <c>llama-cpp</c> provider uses (<c>local/&lt;server&gt;</c> for one of the machine); null keeps it, empty clears it.</summary>
    public string? Server { get; init; }

    /// <summary>
    /// Gets the most requests the provider takes at once (<c>max_concurrent_requests</c>): null keeps the limit it has, 0 removes it.
    /// An older node ignores it.
    /// </summary>
    public int? MaxConcurrentRequests { get; init; }
}

/// <summary>A provider of a node, by its key.</summary>
/// <param name="Key">Its key.</param>
public sealed record FleetProviderRef(string Key);

/// <summary>What the person opens to sign a provider of a node in: a page and the code to enter there.</summary>
/// <param name="Key">The provider.</param>
/// <param name="Url">The page.</param>
/// <param name="UserCode">The code to enter there.</param>
/// <param name="ExpiresAt">When the code expires; null when the provider does not say.</param>
public sealed record FleetProviderSignInPrompt(string Key, string Url, string? UserCode, DateTimeOffset? ExpiresAt);

/// <summary>Where a sign-in of a provider of a node stands (<c>control.providers.signin.wait</c>).</summary>
/// <param name="Key">The provider.</param>
/// <param name="Status"><c>waiting</c>, <c>completed</c>, <c>failed</c> or <c>none</c> (no sign-in runs).</param>
/// <param name="Account">The account signed in, once it is.</param>
/// <param name="Code">Why it failed: <c>timeout</c>, <c>canceled</c>, <c>login_failed</c>.</param>
public sealed record FleetProviderSignInState(string Key, string Status, string? Account, string? Code);

/// <summary>A wait for a sign-in of a provider of a node.</summary>
/// <param name="Key">The provider.</param>
/// <param name="WaitSeconds">How long the node waits for it to end before it answers <c>waiting</c>: 0 to 25.</param>
public sealed record FleetProviderSignInWaitParams(string Key, int WaitSeconds = 20);

/// <summary>The states of a provider of a node.</summary>
public static class FleetProviderStates
{
    /// <summary>It runs sessions.</summary>
    public const string Ready = "ready";

    /// <summary>The node prepares it: it lists its models.</summary>
    public const string Preparing = "preparing";

    /// <summary>Its start or its sign-in failed.</summary>
    public const string Failed = "failed";

    /// <summary>The node left it out when it started it: a credential is missing.</summary>
    public const string NotReady = "not_ready";

    /// <summary>It is turned off.</summary>
    public const string Disabled = "disabled";
}

/// <summary>Why a provider of a node is not ready.</summary>
public static class FleetProviderReasons
{
    /// <summary>It has no key: none written, no secret named, its variable not set.</summary>
    public const string KeyMissing = "key_missing";

    /// <summary>Its configuration names a secret of the machine that is not set.</summary>
    public const string SecretMissing = "secret_missing";

    /// <summary>It signs in with an account, and is not signed in on the machine.</summary>
    public const string SignInNeeded = "sign_in_needed";

    /// <summary>It was started and failed (its sign-in expired, its endpoint refused it...).</summary>
    public const string StartFailed = "start_failed";

    /// <summary>The node left it out: its configuration cannot start it (a value it refuses, a type it does not know).</summary>
    public const string Skipped = "skipped";

    /// <summary>Its configuration can start it, and the node has not applied it yet: it does in a moment.</summary>
    public const string Pending = "pending";
}

/// <summary>
/// The providers of a node for the device that manages it: their readiness, a provider added or changed with its key sent
/// over the end-to-end channel to the secrets store of the machine, and the device-code sign-in of the providers that have
/// one, run on the node. The node application implements it; a host without it leaves
/// <see cref="FleetNodeServiceOptions.Providers"/> null.
/// </summary>
public interface IFleetNodeProviders
{
    /// <summary>Reads the providers of the node and how each stands.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The providers.</returns>
    Task<FleetProviderList> ListAsync(CancellationToken cancellationToken);

    /// <summary>Adds or changes a provider, keeps its key in the secrets store of the machine, and starts the providers again.</summary>
    /// <param name="parameters">The provider.</param>
    /// <param name="setBy">Who sets it, as the store of the secrets records it.</param>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns>The provider as it stands now.</returns>
    /// <exception cref="ArgumentException">A value is not valid.</exception>
    Task<FleetProviderStatus> SetAsync(FleetProviderSetParams parameters, string setBy, CancellationToken cancellationToken);

    /// <summary>Starts the device-code sign-in of a provider on the node, and gives what the person opens.</summary>
    /// <param name="key">The provider.</param>
    /// <param name="cancellationToken">Cancels the wait for the code; the sign-in goes on.</param>
    /// <returns>The page and the code.</returns>
    /// <exception cref="ArgumentException">No provider has that key, or it does not sign in with a device code.</exception>
    Task<FleetProviderSignInPrompt> StartSignInAsync(string key, CancellationToken cancellationToken);

    /// <summary>Waits a while for the sign-in of a provider to end.</summary>
    /// <param name="key">The provider.</param>
    /// <param name="wait">How long to wait before answering that it waits.</param>
    /// <param name="cancellationToken">Cancels the wait; the sign-in goes on.</param>
    /// <returns>Where it stands.</returns>
    Task<FleetProviderSignInState> WaitSignInAsync(string key, TimeSpan wait, CancellationToken cancellationToken);

    /// <summary>Lists the providers the node can add, and the program each runs there.</summary>
    /// <param name="refresh">Whether to fetch the catalogs of the plugins again.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The offers.</returns>
    Task<FleetProviderOfferList> ListOffersAsync(bool refresh, CancellationToken cancellationToken)
        => Task.FromResult(new FleetProviderOfferList([], null));

    /// <summary>Starts adding a provider from an offer: installs the program it runs when it is missing, then writes and starts the provider.</summary>
    /// <param name="parameters">The offer and the key.</param>
    /// <param name="setBy">Who adds it, as the configuration records it.</param>
    /// <param name="cancellationToken">Cancels the start; the operation goes on.</param>
    /// <returns>Where it stands.</returns>
    /// <exception cref="ArgumentException">No offer has that identifier, or it cannot be added here.</exception>
    Task<FleetProviderOperationState> StartAddAsync(FleetProviderAddParams parameters, string setBy, CancellationToken cancellationToken)
        => throw new NotSupportedException("This node adds no provider from an offer.");

    /// <summary>Starts updating the program a provider runs to the version the node offers now.</summary>
    /// <param name="key">The provider.</param>
    /// <param name="cancellationToken">Cancels the start; the operation goes on.</param>
    /// <returns>Where it stands.</returns>
    /// <exception cref="KeyNotFoundException">No provider has that key.</exception>
    /// <exception cref="ArgumentException">Its program has no update.</exception>
    Task<FleetProviderOperationState> StartUpdateAsync(string key, CancellationToken cancellationToken)
        => throw new NotSupportedException("This node updates no provider.");

    /// <summary>Waits a while for an addition or an update to end.</summary>
    /// <param name="id">The operation.</param>
    /// <param name="wait">How long to wait before answering that it runs.</param>
    /// <param name="cancellationToken">Cancels the wait; the operation goes on.</param>
    /// <returns>Where it stands; <c>failed</c> for an operation the node does not know.</returns>
    Task<FleetProviderOperationState> WaitOperationAsync(string id, TimeSpan wait, CancellationToken cancellationToken)
        => Task.FromResult(new FleetProviderOperationState(id, "failed", "The machine knows no such operation.", null, null, null));
}
