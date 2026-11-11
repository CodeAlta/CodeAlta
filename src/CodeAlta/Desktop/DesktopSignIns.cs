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
    internal static Task<LoginFillGrant> FillAsync(CredentialBroker? broker, Func<string, AgentPermissionRequest, CancellationToken, Task<AgentPermissionDecision>> ask,
        string sessionId, string origin, string? signInId, CancellationToken cancellationToken)
        => FillAsync(broker, ask, sessionId, origin, signInId, TimeProvider.System, cancellationToken);

    /// <summary>Answers the sign-in a page of a session asks for, with the clock that says when its one-time code is read again.</summary>
    /// <param name="broker">The credential broker of the host, or null when it has none.</param>
    /// <param name="ask">Asks the user of a session on its card; no permission mode answers this request.</param>
    /// <param name="sessionId">The session.</param>
    /// <param name="origin">The origin of the top frame of the tab, read from the browser.</param>
    /// <param name="signInId">The sign-in the agent chose among several, or null.</param>
    /// <param name="time">The clock.</param>
    /// <param name="cancellationToken">Ends the wait.</param>
    /// <returns>The answer, with the values when the user agreed.</returns>
    internal static async Task<LoginFillGrant> FillAsync(CredentialBroker? broker, Func<string, AgentPermissionRequest, CancellationToken, Task<AgentPermissionDecision>> ask,
        string sessionId, string origin, string? signInId, TimeProvider time, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(time);
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
                    // The site is allowed for the sign-in the card showed, not for one the user changed meanwhile.
                    if (!broker.SignIns.TryGet(signIn.Id, out var now) || !SignIns.IsSameDefinition(now, signIn)) return Changed(signIn);
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
            // The sign-in the card showed is the one read, and only while it is still saved as it was, for this site.
            using var values = await broker.ReadSignInAsync(signIn, site, cancellationToken).ConfigureAwait(false);
            var code = values.Totp is { } totp ? new OneTimeCode(broker, signIn, site, totp, values.TotpExpiresAt, time) : null;
            return LoginFillGrant.GrantedReadingCode(signIn.Label, values.Username, values.Password, code is null ? null : code.ReadAsync);
        }
        catch (KeyNotFoundException)
        {
            return LoginFillGrant.NoSignIn();
        }
        catch (InvalidOperationException exception) when (exception is not ObjectDisposedException)
        {
            return Changed(signIn);
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

    private static LoginFillGrant Changed(SignIn signIn)
        => LoginFillGrant.NoSignIn($"The user changed the sign-in {signIn.Label} while being asked, or it is no longer for this site: nothing was typed. Call fill_login again.");

    /// <summary>
    /// The one-time code of a fill: the one read with the password while it has some life left, read again from the vault when
    /// its field is reached later, on a slow page or a later step, for the sign-in the user approved.
    /// </summary>
    internal sealed class OneTimeCode
    {
        /// <summary>The least life a code is typed with, from its field to the server.</summary>
        internal static readonly TimeSpan MinimumLife = TimeSpan.FromSeconds(5);

        /// <summary>How long a code whose end the vault does not say is typed after it was read.</summary>
        internal static readonly TimeSpan UnknownLife = TimeSpan.FromSeconds(10);

        private readonly CredentialBroker _broker;
        private readonly SignIn _signIn;
        private readonly string _site;
        private readonly TimeProvider _time;
        private string _code;
        private DateTimeOffset? _expiresAt;
        private DateTimeOffset _readAt;

        internal OneTimeCode(CredentialBroker broker, SignIn signIn, string site, string code, DateTimeOffset? expiresAt, TimeProvider time)
        {
            _broker = broker;
            _signIn = signIn;
            _site = site;
            _code = code;
            _expiresAt = expiresAt;
            _time = time;
            _readAt = time.GetUtcNow();
        }

        /// <summary>Gives the code to type now, masked, or null when it cannot be read again.</summary>
        internal async Task<string?> ReadAsync(CancellationToken cancellationToken)
        {
            if (IsFresh()) return _code;
            try
            {
                for (var attempt = 0; ; attempt++)
                {
                    var read = await _broker.ReadSignInCodeAsync(_signIn, _site, cancellationToken).ConfigureAwait(false);
                    (_code, _expiresAt, _readAt) = (read.Value, read.ExpiresAt, _time.GetUtcNow());
                    if (IsFresh() || attempt > 0 || _expiresAt is not { } end) return _code;
                    // A code about to end: the next one comes when it does.
                    var wait = end - _time.GetUtcNow();
                    if (wait > TimeSpan.Zero) await Task.Delay(wait + TimeSpan.FromMilliseconds(250), _time, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is CredentialProviderException or KeyNotFoundException or ArgumentException
                                                  || (exception is InvalidOperationException && exception is not ObjectDisposedException))
            {
                // The vault is locked now, or the user changed the sign-in: nothing is typed.
                return null;
            }
        }

        private bool IsFresh()
        {
            var now = _time.GetUtcNow();
            return _expiresAt is { } end ? end - now >= MinimumLife : now - _readAt < UnknownLife;
        }
    }
}
