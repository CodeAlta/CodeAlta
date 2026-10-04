using System.Collections.Immutable;
using System.Runtime.ExceptionServices;
using CodeAlta.Agent.Copilot;
using CodeAlta.Agent.OpenAI.Codex;
using CodeAlta.Agent.Xai;
using CodeAlta.Catalog;

namespace CodeAlta.Hosting;

/// <summary>What the user must open, and for a device flow enter, to continue an account sign-in.</summary>
/// <param name="Uri">The authorization or verification address.</param>
/// <param name="UserCode">The code to enter at <paramref name="Uri"/> for a device flow; null for a browser flow.</param>
/// <param name="ExpiresAt">When a device authorization expires; null for a browser flow.</param>
public sealed record ProviderLoginPrompt(Uri Uri, string? UserCode, DateTimeOffset? ExpiresAt);

/// <summary>Non-secret account sign-in state of one configured provider.</summary>
/// <param name="SignedIn">Whether a credential of the provider is stored.</param>
/// <param name="Account">The Codex account label or the Copilot enterprise domain when known; otherwise null.</param>
/// <param name="Detail">A short neutral fact: the API host, or "No plan usage permission" for Codex.</param>
/// <param name="ExpiresAt">The expiry of the stored access token when the provider reports one.</param>
/// <remarks>Contains no tokens. <paramref name="Account"/> may be personal data and must not be logged indiscriminately.</remarks>
public sealed record ProviderLoginStatus(bool SignedIn, string? Account, string? Detail, DateTimeOffset? ExpiresAt)
{
    /// <summary>
    /// Gets whether the stored credential can run model turns. It is false for a signed-out provider and for a
    /// ChatGPT registration that declined plan usage: like the TUI, callers do not treat that sign-in as a
    /// success that enables the provider.
    /// </summary>
    public bool Usable { get; init; } = SignedIn;
}

/// <summary>
/// Account sign-in, sign-out and stored sign-in state of a configured Codex, Copilot or xAI provider behind
/// provider-neutral types, so a frontend does not name the provider packages.
/// </summary>
/// <remarks>
/// The caller owns root selection, presentation and any browser launch: no browser is opened here. Provider
/// types match by exact ordinal comparison, as the provider-specific compositions do, so definitions must be
/// type-completed (see <see cref="CodeAltaConfigStore.LoadGlobalProviderDefinitions"/>). Provider, storage and
/// callback exceptions propagate unchanged.
/// </remarks>
public static class ConfiguredProviderLogin
{
    /// <summary>The detail reported for a ChatGPT registration that declined plan usage.</summary>
    public const string NoPlanUsageDetail = "No plan usage permission";

    private const string Browser = "browser";
    private const string Device = "device";
    private static readonly ImmutableArray<string> CodexModes = [Browser];
    private static readonly ImmutableArray<string> CopilotModes = [Device];
    private static readonly ImmutableArray<string> XaiModes = [Browser, Device];

    /// <summary>Returns whether a provider type signs in with an account instead of an API key.</summary>
    /// <param name="providerType">The canonical provider type.</param>
    /// <returns>True for <c>codex</c>, <c>copilot</c> and <c>xai</c>.</returns>
    public static bool SupportsLogin(string? providerType) => providerType is "codex" or "copilot" or "xai";

    /// <summary>Returns the sign-in modes of a provider type, preferred mode first.</summary>
    /// <param name="providerType">The canonical provider type.</param>
    /// <returns><c>browser</c> and/or <c>device</c>; empty for a type without account sign-in.</returns>
    public static IReadOnlyList<string> GetLoginModes(string? providerType) => providerType switch
    {
        "codex" => CodexModes,
        "copilot" => CopilotModes,
        "xai" => XaiModes,
        _ => [],
    };

    /// <summary>Signs in to the account of a configured provider and stores its credential.</summary>
    /// <param name="definition">The type-completed provider definition.</param>
    /// <param name="stateRootPath">The global state root that holds provider credentials.</param>
    /// <param name="mode">One of <see cref="GetLoginModes"/> for the provider type.</param>
    /// <param name="onPrompt">
    /// Receives what the user must open. Copilot and xAI await it before waiting for authorization. Codex reports
    /// synchronously, so a prompt that does not complete at once runs beside the sign-in: its failure cancels the
    /// sign-in and is rethrown, and it is joined before this method returns.
    /// </param>
    /// <param name="cancellationToken">Cancels the sign-in and is forwarded to <paramref name="onPrompt"/>.</param>
    /// <returns>
    /// The signed-in state. A ChatGPT registration without plan usage is signed in with
    /// <see cref="NoPlanUsageDetail"/> and <see cref="ProviderLoginStatus.Usable"/> false.
    /// </returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">The root is blank, or the provider type or mode has no sign-in.</exception>
    /// <exception cref="InvalidOperationException">Authorization or token exchange fails.</exception>
    /// <exception cref="IOException">Provider state cannot be read or written.</exception>
    /// <exception cref="TimeoutException">The authorization expires before completion.</exception>
    /// <exception cref="OperationCanceledException">The operation is canceled.</exception>
    public static async Task<ProviderLoginStatus> LoginAsync(CodeAltaProviderDocument definition, string stateRootPath, string mode,
        Func<ProviderLoginPrompt, CancellationToken, ValueTask> onPrompt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRootPath);
        ArgumentNullException.ThrowIfNull(mode);
        ArgumentNullException.ThrowIfNull(onPrompt);
        string Root() => stateRootPath;
        switch (definition.ProviderType, mode)
        {
            case ("codex", Browser):
                return await LoginCodexAsync(onPrompt, (report, token) => ConfiguredCodexAuthentication.LoginWithBrowserAsync(
                    definition, Root, InvalidProvider, report, static _ => { }, token), cancellationToken).ConfigureAwait(false);
            case ("copilot", Device):
                return FromCopilot(await ConfiguredCopilotAuthentication.LoginWithDeviceCodeAsync(definition, Root, InvalidProvider,
                    (code, token) => onPrompt(new(code.VerificationUri, code.UserCode, code.ExpiresAt), token), cancellationToken).ConfigureAwait(false));
            case ("xai", Browser):
                return FromXai(await ConfiguredXaiAuthentication.LoginWithBrowserAsync(definition, Root, InvalidProvider,
                    (authorization, token) => onPrompt(new(authorization.AuthorizeUri, null, null), token), cancellationToken).ConfigureAwait(false));
            case ("xai", Device):
                return FromXai(await ConfiguredXaiAuthentication.LoginWithDeviceCodeAsync(definition, Root, InvalidProvider,
                    (code, token) => onPrompt(new(code.VerificationUri, code.UserCode, code.ExpiresAt), token), cancellationToken).ConfigureAwait(false));
            default:
                throw SupportsLogin(definition.ProviderType)
                    ? new ArgumentException($"Sign-in mode '{mode}' is not supported by provider type '{definition.ProviderType}'.", nameof(mode))
                    : Unsupported(definition);
        }
    }

    /// <summary>Reads the stored sign-in state of a configured provider without network authentication.</summary>
    /// <param name="definition">The type-completed provider definition.</param>
    /// <param name="stateRootPath">The global state root that holds provider credentials.</param>
    /// <param name="cancellationToken">Forwarded to credential storage.</param>
    /// <returns>
    /// The stored state. Codex is signed in only while its registration holds tokens (sign-out keeps the
    /// registration). Copilot and xAI report the provider's cached status: Copilot's follows its short-lived
    /// token, so it can read as signed out until the provider next refreshes it.
    /// </returns>
    /// <exception cref="ArgumentNullException">The definition is null.</exception>
    /// <exception cref="ArgumentException">The root is blank, or the provider type has no sign-in.</exception>
    /// <exception cref="IOException">Provider state cannot be read.</exception>
    /// <exception cref="OperationCanceledException">The operation is canceled.</exception>
    public static async Task<ProviderLoginStatus> GetStatusAsync(CodeAltaProviderDocument definition, string stateRootPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRootPath);
        string Root() => stateRootPath;
        return definition.ProviderType switch
        {
            "codex" => FromCodex(await new FileOpenAICodexSubscriptionCredentialStore(stateRootPath)
                .LoadAsync(definition.ProviderKey, cancellationToken).ConfigureAwait(false)),
            "copilot" => await ConfiguredCopilotAuthentication.GetCredentialStatusAsync(definition, Root, InvalidProvider, cancellationToken)
                .ConfigureAwait(false) is { } copilot ? FromCopilot(copilot) : SignedOut,
            "xai" => await ConfiguredXaiAuthentication.GetCredentialStatusAsync(definition, Root, InvalidProvider, cancellationToken)
                .ConfigureAwait(false) is { } xai ? FromXai(xai) : SignedOut,
            _ => throw Unsupported(definition),
        };
    }

    /// <summary>Signs a configured provider out of its account.</summary>
    /// <param name="definition">The type-completed provider definition.</param>
    /// <param name="stateRootPath">The global state root that holds provider credentials.</param>
    /// <param name="cancellationToken">Forwarded to the provider.</param>
    /// <returns>
    /// True when the provider was signed in (see <see cref="GetStatusAsync"/>) and its credential was removed.
    /// Codex also attempts remote revocation and keeps its registration; Copilot and xAI delete local credentials only.
    /// </returns>
    /// <exception cref="ArgumentNullException">The definition is null.</exception>
    /// <exception cref="ArgumentException">The root is blank, or the provider type has no sign-in.</exception>
    /// <exception cref="IOException">Provider state cannot be read or written.</exception>
    /// <exception cref="OperationCanceledException">The operation is canceled.</exception>
    public static async Task<bool> SignOutAsync(CodeAltaProviderDocument definition, string stateRootPath, CancellationToken cancellationToken)
    {
        var signedIn = (await GetStatusAsync(definition, stateRootPath, cancellationToken).ConfigureAwait(false)).SignedIn;
        string Root() => stateRootPath;
        switch (definition.ProviderType)
        {
            case "codex":
                await ConfiguredCodexAuthentication.SignOutAsync(definition, Root, InvalidProvider, cancellationToken).ConfigureAwait(false);
                break;
            case "copilot":
                await ConfiguredCopilotAuthentication.DeleteCredentialAsync(definition, Root, InvalidProvider, cancellationToken).ConfigureAwait(false);
                break;
            default:
                await ConfiguredXaiAuthentication.DeleteCredentialAsync(definition, Root, InvalidProvider, cancellationToken).ConfigureAwait(false);
                break;
        }

        return signedIn;
    }

    /// <summary>Bridges the synchronous Codex authorization report to an asynchronous prompt.</summary>
    internal static async Task<ProviderLoginStatus> LoginCodexAsync(Func<ProviderLoginPrompt, CancellationToken, ValueTask> onPrompt,
        Func<Action<Uri>, CancellationToken, Task<CodexAccountMetadata>> login, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var prompt = Task.FromResult<ExceptionDispatchInfo?>(null);
        try
        {
            CodexAccountMetadata metadata;
            try
            {
                metadata = await login(uri =>
                {
                    var pending = onPrompt(new(uri, null, null), linked.Token);
                    // A finished prompt reports its failure to the provider at once; a running one must not block the report.
                    if (pending.IsCompleted) pending.GetAwaiter().GetResult();
                    else prompt = ObserveAsync(pending.AsTask(), linked);
                }, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Not the caller's cancellation: when a failed prompt canceled the sign-in, that failure is the cause.
                linked.Cancel();
                (await prompt.WaitAsync(cancellationToken).ConfigureAwait(false))?.Throw();
                throw;
            }

            // The sign-in can finish first; its prompt is still joined so that a late failure is not lost.
            (await prompt.WaitAsync(cancellationToken).ConfigureAwait(false))?.Throw();
            return FromCodex(metadata);
        }
        finally
        {
            linked.Cancel(); // Stops a prompt that outlived a failed sign-in.
        }
    }

    private static async Task<ExceptionDispatchInfo?> ObserveAsync(Task prompt, CancellationTokenSource linked)
    {
        try
        {
            await prompt.ConfigureAwait(false);
            return null;
        }
        catch (Exception exception)
        {
            var failure = ExceptionDispatchInfo.Capture(exception);
            try { linked.Cancel(); }
            catch (ObjectDisposedException) { /* The sign-in already ended and reports the failure itself. */ }
            return failure;
        }
    }

    internal static ProviderLoginStatus FromCodex(CodexAccountMetadata metadata)
        => new(true, Text(metadata.AccountLabel), metadata.HasPlanUsagePermission ? null : NoPlanUsageDetail, null)
        {
            Usable = metadata.HasPlanUsagePermission,
        };

    // Sign-out keeps the registration and clears its tokens, like the check before every authenticated request.
    internal static ProviderLoginStatus FromCodex(OpenAICodexSubscriptionCredential? credential)
        => OpenAICodexSubscriptionLoginManager.IsRegistration(credential) && !string.IsNullOrWhiteSpace(credential.AccessToken)
            ? FromCodex(ConfiguredCodexAuthentication.Project(credential))
            : SignedOut;

    internal static ProviderLoginStatus FromCopilot(CopilotDirectLoginResult result)
        => new(true, Text(result.EnterpriseDomain), result.BaseUri.Host, result.ExpiresAt);

    internal static ProviderLoginStatus FromXai(XaiDirectLoginResult result)
        => new(true, null, result.BaseUri.Host, result.ExpiresAt);

    private static ProviderLoginStatus SignedOut => new(false, null, null, null);

    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string InvalidProvider() => "The provider type does not support this account sign-in.";

    private static ArgumentException Unsupported(CodeAltaProviderDocument definition)
        => new($"Provider type '{definition.ProviderType}' has no account sign-in.", nameof(definition));
}
