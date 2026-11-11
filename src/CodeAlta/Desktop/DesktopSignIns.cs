using CodeAlta.Agent;
using CodeAlta.Browser.Tools;
using CodeAlta.Credentials;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Desktop;

/// <summary>
/// The sign-ins of websites for the browser of a session (<c>fill_login</c>): the sign-ins the user shared for the origin of
/// the tab, the card of the session that asks the user unless the user allowed that site already, and the values read from
/// the vault, masked, for the fill only.
/// </summary>
internal static class DesktopSignIns
{
    /// <summary>Answers the sign-in a page of a session asks for.</summary>
    /// <param name="broker">The credential broker of the host, or null when it has none.</param>
    /// <param name="ask">Asks the user of a session on its card; no permission mode answers this request.</param>
    /// <param name="sessionId">The session.</param>
    /// <param name="origin">The origin of the top frame of the tab, read from the browser.</param>
    /// <param name="signInId">The sign-in the agent chose among several, or null.</param>
    /// <param name="cancellationToken">Ends the wait.</param>
    /// <returns>The answer, with the values when the user agreed.</returns>
    internal static async Task<LoginFillGrant> FillAsync(CredentialBroker? broker, Func<string, AgentPermissionRequest, CancellationToken, Task<AgentPermissionDecision>> ask,
        string sessionId, string origin, string? signInId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ask);
        if (broker is null) return LoginFillGrant.Unsupported("This CodeAlta keeps no sign-ins of websites.");
        if (SignIns.OriginOf(origin) is not { } site) return LoginFillGrant.NoSignIn();
        IReadOnlyList<SignIn> found;
        try
        {
            found = broker.SignIns.FindByOrigin(site);
        }
        catch (InvalidDataException)
        {
            return LoginFillGrant.Unsupported("The file of the sign-ins of this computer is damaged: ask the user to look at Settings > Secrets of CodeAlta.");
        }

        if (found.Count == 0) return LoginFillGrant.NoSignIn();
        SignIn signIn;
        if (signInId is not null)
        {
            if (found.FirstOrDefault(candidate => candidate.Id == signInId) is not { } chosen)
            {
                return LoginFillGrant.NoSignIn($"No sign-in '{signInId}' of the user is for {site}. Call fill_login without a signInId to list them.");
            }

            signIn = chosen;
        }
        else if (found.Count > 1)
        {
            return LoginFillGrant.Choose([.. found.Select(static candidate => new LoginFillChoice(candidate.Id, candidate.Label))]);
        }
        else
        {
            signIn = found[0];
        }

        var vault = broker.GetProvider(signIn.Provider)?.DisplayName ?? signIn.Provider;
        if (!broker.SignIns.IsGranted(signIn.Id, site))
        {
            var request = new AgentCredentialPermissionRequest(new ModelProviderId("browser"), sessionId, DateTimeOffset.UtcNow, null, $"sign-in-{Guid.NewGuid():N}",
                AgentCredentialPermissionRequest.SignInAction, site, $"With {signIn.Label} of {vault}");
            var decision = await ask(sessionId, request, cancellationToken).ConfigureAwait(false);
            switch (decision.Kind)
            {
                case AgentPermissionDecisionKind.AllowAlways:
                    broker.SignIns.Grant(signIn.Id, site);
                    break;
                case AgentPermissionDecisionKind.AllowOnce or AgentPermissionDecisionKind.AllowForSession:
                    break;
                default:
                    return LoginFillGrant.Denied(decision.Source == AgentPermissionDecisionSource.User
                        ? null
                        : $"The sign-in was not allowed: {decision.Reason ?? "nobody could be asked"}.");
            }
        }

        try
        {
            using var values = await broker.ReadSignInAsync(signIn.Id, cancellationToken).ConfigureAwait(false);
            return LoginFillGrant.Granted(signIn.Label, values.Username, values.Password, values.Totp);
        }
        catch (KeyNotFoundException)
        {
            return LoginFillGrant.NoSignIn();
        }
        catch (CredentialProviderException exception) when (exception.Failure is CredentialFailure.Locked or CredentialFailure.SignInNeeded)
        {
            return LoginFillGrant.Locked($"The vault {vault} of the sign-in {signIn.Label} is locked. Ask the user to unlock it in Settings > Secrets of CodeAlta, then call fill_login again.");
        }
        catch (CredentialProviderException exception)
        {
            // The message of a provider names no value.
            return LoginFillGrant.Unsupported($"The sign-in {signIn.Label} cannot be read from {vault}: {exception.Message}");
        }
    }
}
