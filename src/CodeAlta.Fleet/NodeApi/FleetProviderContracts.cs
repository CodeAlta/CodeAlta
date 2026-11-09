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
}

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
public sealed record FleetProviderList(IReadOnlyList<FleetProviderStatus> Providers, IReadOnlyList<FleetProviderType> Types, string? SecretProtection);

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

    /// <summary>The node left it out for another reason, which its log says.</summary>
    public const string Skipped = "skipped";
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
}
