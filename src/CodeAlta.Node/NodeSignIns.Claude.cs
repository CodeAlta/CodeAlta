using System.Diagnostics.CodeAnalysis;
using System.Text;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Secrets;
using CodeAlta.Fleet.NodeApi;

namespace CodeAlta.Node;

// Claude Code (doc/fleet/node.md, credentials on a node; #414): the terms of Claude Code allow a person to sign their own unmodified
// Claude Code in with their own subscription, and forbid a third party to collect, store, relay or intermediate a Claude credential
// or session token. So CodeAlta takes no part in that sign-in: the person signs Claude Code in on the machine itself, with
// Anthropic's own flow (claude auth login in a terminal there, which altanode providers sign-in starts), and Claude Code keeps its
// login where it keeps it. The node only says whether the provider is signed in, as the probe of Claude Code found it, and how to
// sign it in. A token an older version kept as a secret of the machine still works until it is signed out here, which removes it.
internal sealed partial class NodeSignIns
{
    /// <summary>The variable of the environment of a Claude Code provider an older version set to a token of the account.</summary>
    internal const string ClaudeTokenVariable = "CLAUDE_CODE_OAUTH_TOKEN";

    private const string ClaudeType = "claude-code";

    // The category of the probe of a Claude Code that is not signed in (ClaudeCodeModelProviderRuntime).
    private const string ClaudeSignedOutCategory = "claude-code-signed-out";

    /// <summary>The name of the secret an older version kept the token of a Claude Code provider in.</summary>
    /// <param name="key">The key of the provider.</param>
    /// <returns>The name, such as <c>CLAUDE_CODE_OAUTH_TOKEN_CLAUDE_1A2B3C4D</c>: the key as letters, then 128 bits of a hash of the key, so that two keys never share one.</returns>
    internal static string ClaudeSecretName(string key) => ProviderSecretName(ClaudeTokenVariable, key);

    /// <summary>The name of the secret that keeps the value of a variable of the environment of a provider, given from the desktop.</summary>
    /// <param name="variable">The variable, a name of at most 64 characters.</param>
    /// <param name="key">The key of the provider.</param>
    /// <returns>The variable, the key as letters, then 128 bits of a hash of the key: at most 128 characters, and two keys never share one.</returns>
    internal static string ProviderSecretName(string variable, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (!IsVariableName(variable)) throw new ArgumentException("A variable is a name of at most 64 letters, digits and _.", nameof(variable));
        var name = new StringBuilder(variable).Append('_');
        var letters = Math.Min(64, 94 - variable.Length);
        foreach (var character in key.Length > letters ? key[..letters] : key) name.Append(char.IsAsciiLetterOrDigit(character) ? char.ToUpperInvariant(character) : '_');
        // Provider keys are one key whatever their case.
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(key.ToLowerInvariant()));
        return name.Append('_').Append(Convert.ToHexString(hash, 0, 16)).ToString();
    }

    /// <summary>Whether a text is the name of a variable a provider can be given from the desktop: a name of a secret of at most 64 characters.</summary>
    /// <param name="name">The text.</param>
    /// <returns>Whether it is.</returns>
    internal static bool IsVariableName([NotNullWhen(true)] string? name) => name is { Length: <= 64 } && LocalSecretStore.IsName(name);

    /// <summary>
    /// How a person signs a Claude Code provider of a node in: with Anthropic's own flow, on the machine, as the account the node runs
    /// as, so that Claude Code keeps its login where the node runs it.
    /// </summary>
    /// <param name="key">The key of the provider.</param>
    /// <param name="user">The account the node runs as.</param>
    /// <returns>The sentence.</returns>
    internal static string ClaudeSignInSteps(string key, string user)
        => $"Claude Code signs in on the machine itself, with Anthropic's own sign-in: in a terminal there as {user} (over SSH too), run "
            + $"altanode providers sign-in {key}, then check it again here. CodeAlta never sees your Claude login.";

    // The Claude Code providers of the node: signed in or not as the probe of Claude Code found it, with how to sign it in on the
    // machine; none signs in from another computer. One an older version signed in with a token it kept says so, and its Sign out
    // removes that token.
    private IEnumerable<FleetMachineSignInItem> ClaudeItems()
    {
        IReadOnlyList<SecretInfo> secrets = [];
        try { secrets = new LocalSecretStore(Root).ListSecrets(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or SecretStoreUnavailableException) { }
        var states = host.ModelProviderInitializationService.CurrentStates;
        foreach (var definition in Store.LoadGlobalProviderDefinitions(includeDisabled: true).Where(static value => value.ProviderType == ClaudeType).Take(8))
        {
            var name = ClaudeSecretName(definition.ProviderKey);
            var kept = definition.Env?.GetValueOrDefault(ClaudeTokenVariable) == $"${{secret:{name}}}"
                ? secrets.FirstOrDefault(value => string.Equals(value.Name, name, StringComparison.OrdinalIgnoreCase)) : null;
            var title = definition.DisplayName ?? definition.ProviderKey;
            if (kept is not null)
            {
                yield return new FleetMachineSignInItem(FleetMachineSignInKinds.Provider, definition.ProviderKey, null, title, FleetMachineSignInStates.SignedIn, null,
                    kept.SetAt.AddYears(1), [],
                    "Signed in with a token an older version of CodeAlta kept for it. Sign out removes that token; then sign Claude Code in on the machine.");
                continue;
            }

            var state = states.FirstOrDefault(value => string.Equals(value.ProviderId.Value, definition.ProviderKey, StringComparison.OrdinalIgnoreCase));
            var signedIn = state?.Availability == Agent.ModelProviderAvailability.Ready ? FleetMachineSignInStates.SignedIn
                : state?.Availability == Agent.ModelProviderAvailability.Failed && state.ErrorCategory == ClaudeSignedOutCategory ? FleetMachineSignInStates.SignedOut
                : FleetMachineSignInStates.Unknown;
            yield return new FleetMachineSignInItem(FleetMachineSignInKinds.Provider, definition.ProviderKey, null, title, signedIn, null, null, [],
                signedIn == FleetMachineSignInStates.SignedIn ? null : ClaudeSignInSteps(definition.ProviderKey, Environment.UserName));
        }
    }

    // The Claude Code provider of this key; null when the key is not one.
    private CodeAltaProviderDocument? ClaudeProvider(string key)
        => Store.LoadGlobalProviderDefinitions(includeDisabled: true).FirstOrDefault(value => value.ProviderType == ClaudeType && string.Equals(value.ProviderKey, key, StringComparison.OrdinalIgnoreCase));

    // Signs a Claude Code provider out of the token an older version kept for it: the token leaves the secrets and the provider. The
    // login Claude Code keeps itself stays: claude auth logout on the machine ends it.
    private void SignOutClaude(CodeAltaProviderDocument definition)
    {
        providers.SetEnvironment(definition.ProviderKey, ClaudeTokenVariable, null);
        new LocalSecretStore(Root).DeleteSecret(ClaudeSecretName(definition.ProviderKey));
    }
}
