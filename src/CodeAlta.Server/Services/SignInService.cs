using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Server.Identity;
using CodeAlta.Server.Identity.SignIn;
using CodeAlta.Server.Storage;
using Microsoft.AspNetCore.Http;

namespace CodeAlta.Server.Services;

/// <summary>
/// The hook that gives people roles from the groups of their company sign-in (<c>doc/fleet/identity.md</c>, company SSO).
/// The groups are stored at every sign-in; <see cref="GroupRoleMapper"/> applies the groups an organization maps.
/// </summary>
public interface ISignInRoleMapper
{
    /// <summary>Gives the role a person gets from their groups.</summary>
    /// <param name="person">The person.</param>
    /// <param name="groups">The groups of their last company sign-in (ids).</param>
    /// <returns>The role, or null to keep theirs.</returns>
    string? MapRole(PersonRecord person, IReadOnlyList<string> groups);
}

/// <summary>The mapping of an organization that maps no group: groups are kept, roles stay as they are.</summary>
public sealed class KeepRolesMapper : ISignInRoleMapper
{
    /// <inheritdoc />
    public string? MapRole(PersonRecord person, IReadOnlyList<string> groups) => null;
}

/// <summary>
/// The role the groups of a company sign-in give (<c>doc/fleet/identity.md</c>, company SSO): the widest role among the
/// groups the person is in, a member when they are in none; an owner keeps their role, which no group gives or takes.
/// </summary>
public sealed class GroupRoleMapper : ISignInRoleMapper
{
    private readonly IReadOnlyDictionary<string, string> _roles;

    /// <summary>Initializes a new instance of the <see cref="GroupRoleMapper"/> class.</summary>
    /// <param name="roles">The groups and their roles.</param>
    /// <exception cref="ArgumentNullException"><paramref name="roles"/> is null.</exception>
    public GroupRoleMapper(IEnumerable<GroupRoleDto> roles)
    {
        ArgumentNullException.ThrowIfNull(roles);
        _roles = roles.Where(static role => role.Role != PersonRoles.Owner).ToDictionary(static role => role.Group, static role => role.Role, StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public string? MapRole(PersonRecord person, IReadOnlyList<string> groups)
    {
        ArgumentNullException.ThrowIfNull(person);
        ArgumentNullException.ThrowIfNull(groups);
        if (person.Role == PersonRoles.Owner) return null;
        var widest = groups.Select(group => _roles.GetValueOrDefault(group)).OfType<string>()
            .OrderBy(static role => IndexOf(role)).FirstOrDefault();
        return widest ?? PersonRoles.Member;
    }

    private static int IndexOf(string role)
    {
        for (var index = 0; index < FleetOrganizationRoles.All.Count; index++)
        {
            if (FleetOrganizationRoles.All[index] == role) return index;
        }

        return int.MaxValue;
    }
}

/// <summary>
/// Sign-in on a server (<c>doc/fleet/identity.md</c>, a new device and linked sign-in methods). A person proves who they
/// are with a method the owner enabled — a passkey, a linked Microsoft, Google or GitHub account, or the company's
/// OpenID Connect provider — through the system browser of the device, which comes back to its loopback address; the
/// server makes the state, the nonce and the PKCE verifier, redeems the code and checks the answer. GitHub has no
/// redirect: the person enters a code at GitHub (the device flow of a GitHub App), and the server, polled by the device,
/// asks GitHub until it answers; the device code and the user token stay on the server, the token only for one read. A
/// sign-in grants nothing by itself: it gives the device that started it a single-use grant to enroll as a pending controller device of
/// that person, which one of their devices, their recovery key or (for a person without a device) the owner then signs
/// into the device list. Device keys stay the authority for every action.
/// </summary>
public sealed class SignInService : IDisposable
{
    // How long a confirmation works: the fresh confirmation of a dangerous action (doc/fleet/identity.md) lasts 10 to 15 minutes.
    private static readonly TimeSpan ConfirmationLifetime = TimeSpan.FromMinutes(10);

    private const int MaxGroupRoles = 200;
    private static readonly string[] DefaultEnabled = [FleetSignInMethods.Passkey, FleetSignInMethods.Microsoft, FleetSignInMethods.Google, FleetSignInMethods.GitHub];

    private readonly ICoordinatorStore _store;
    private readonly ISignInStore _signIn;
    private readonly RequestAuthenticator _authenticator;
    private readonly SignInAttempts _attempts;
    private readonly ExternalSignInClient _external;
    private readonly PasskeyCeremonies _passkeys;
    private readonly SignInOptions _options;
    private readonly TimeProvider _time;

    /// <summary>Initializes a new instance of the <see cref="SignInService"/> class.</summary>
    /// <param name="store">The store of people and devices.</param>
    /// <param name="signIn">The store of sign-in.</param>
    /// <param name="authenticator">Checks the signatures of the requests.</param>
    /// <param name="attempts">The sign-ins in progress and their grants.</param>
    /// <param name="options">The clients of the providers.</param>
    /// <param name="time">The clock.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public SignInService(ICoordinatorStore store, ISignInStore signIn, RequestAuthenticator authenticator, SignInAttempts attempts, SignInOptions options, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(signIn);
        ArgumentNullException.ThrowIfNull(authenticator);
        ArgumentNullException.ThrowIfNull(attempts);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        _store = store;
        _signIn = signIn;
        _authenticator = authenticator;
        _attempts = attempts;
        _options = options;
        _time = time;
        _external = new ExternalSignInClient(options, time);
        _passkeys = new PasskeyCeremonies(signIn, store, time);
    }

    /// <summary>Gets the mapping of groups to roles when the organization maps none; the groups it maps replace it.</summary>
    public ISignInRoleMapper Roles { get; init; } = new KeepRolesMapper();

    /// <summary>Gets the access control that applies the role the groups give, or null where nothing applies it.</summary>
    internal AccessService? Access { get; init; }

    /// <summary>Gets the reverse proxies the server trusts, whose forwarded scheme names the address of its passkey page; null for none.</summary>
    internal Hosting.TrustedProxies? Proxies { get; init; }

    /// <summary>Lists the methods a person may sign in with now; needs no device.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The methods.</returns>
    public async Task<SignInMethodsResponse> GetMethodsAsync(CancellationToken cancellationToken)
    {
        var organization = await _store.GetOrganizationAsync(cancellationToken).ConfigureAwait(false);
        if (organization is null) return new SignInMethodsResponse(null, [], false);
        var settings = await ReadSettingsAsync(organization.Id, cancellationToken).ConfigureAwait(false);
        var methods = Describe(settings.Document).Where(static method => method.Enabled && method.Available)
            .Select(method => settings.Document.RequireSso && method.Id != FleetSignInMethods.Company
                ? method with { BreakGlass = true }
                : method)
            .Where(method => !settings.Document.RequireSso || method.Id is FleetSignInMethods.Company or FleetSignInMethods.Passkey)
            .ToArray();
        return new SignInMethodsResponse(organization.Name, methods, settings.Document.RequireSso);
    }

    /// <summary>Reads the sign-in settings; an owner's controller device only.</summary>
    /// <param name="actor">The calling device.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The settings, never the client secret.</returns>
    /// <exception cref="CoordinatorException">The caller is not an owner's controller device.</exception>
    public async Task<SignInSettingsDto> GetSettingsAsync(DeviceRecord actor, CancellationToken cancellationToken)
    {
        await RequireOwnerAsync(actor, cancellationToken).ConfigureAwait(false);
        return ToDto(await ReadSettingsAsync(actor.OrganizationId, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Changes the sign-in settings; an owner's controller device only, from the version it read.</summary>
    /// <param name="actor">The calling device.</param>
    /// <param name="request">The settings.</param>
    /// <param name="origin">Where it comes from.</param>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns>The settings now.</returns>
    /// <exception cref="CoordinatorException">The caller is not an owner's controller device, the settings do not hold, the company's provider does not answer, or the version moved on.</exception>
    public async Task<SignInSettingsDto> SetSettingsAsync(DeviceRecord actor, SetSignInSettingsRequest? request, RequestOrigin origin, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(origin);
        var owner = await RequireOwnerAsync(actor, cancellationToken).ConfigureAwait(false);
        if (request?.EnabledMethods is null) throw Invalid("The request needs the methods to enable.");
        var held = await ReadSettingsAsync(actor.OrganizationId, cancellationToken).ConfigureAwait(false);
        if (request.Version != held.Version) throw CoordinatorException.Conflict(SignInErrorCodes.SettingsInvalid, "The sign-in settings changed meanwhile: read them again.");
        var enabled = request.EnabledMethods.Distinct(StringComparer.Ordinal).ToArray();
        if (enabled.Any(static method => !FleetSignInMethods.IsKnown(method))) throw Invalid("A method to enable is not a sign-in method.");

        CompanyDocument? company = null;
        var secret = held.Secret;
        if (request.Company is { } given)
        {
            if (!Uri.TryCreate(given.Issuer?.Trim(), UriKind.Absolute, out var issuer) || issuer.Scheme != Uri.UriSchemeHttps && !(_options.AllowLoopbackHttpProviders && issuer.IsLoopback)
                || !string.IsNullOrEmpty(issuer.Query) || !string.IsNullOrEmpty(issuer.Fragment))
                throw Invalid("The issuer of the company's provider must be an https:// address.");
            if (string.IsNullOrWhiteSpace(given.ClientId) || given.ClientId.Length > 512) throw Invalid("The company's provider needs a client id.");
            var claim = string.IsNullOrWhiteSpace(given.GroupsClaim) ? "groups" : given.GroupsClaim.Trim();
            if (claim.Length > 64 || claim.Any(static c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-' or '.' or ':' or '/')))
                throw Invalid("The groups claim is not the name of a claim.");
            var name = string.IsNullOrWhiteSpace(given.DisplayName) ? null : SetupService.RequireName(given.DisplayName, "displayName");
            var groupRoles = given.GroupRoles is null ? held.Document.Company?.GroupRoles : GroupRoles(given.GroupRoles);
            // Neither named keeps the ones held, as for the group roles: a client that does not know them changes nothing of them.
            var keepAllowed = given.AllowedDomains is null && given.AllowedTenants is null && held.Document.Company is not null;
            var domains = keepAllowed ? held.Document.Company!.AllowedDomains ?? [] : ReadAllowed(given.AllowedDomains, IsDomain, "An allowed domain is not a domain name (contoso.com).");
            var tenants = keepAllowed ? held.Document.Company!.AllowedTenants ?? [] : ReadAllowed(given.AllowedTenants, IsTenant, "An allowed tenant is not a tenant id.");
            // An identity provider serves more than one company: the server lets in only the domains and tenants named.
            if (domains.Length == 0 && tenants.Length == 0) throw Invalid("Company sign-in needs the email domains or the tenant ids it lets in.");
            // Entra ID gives emails it did not verify: only its tenant id proves the company.
            if (IsEntraId(issuer) && tenants.Length == 0) throw Invalid("Company sign-in with Entra ID needs the tenant ids it lets in.");
            company = new CompanyDocument(issuer.AbsoluteUri.TrimEnd('/'), given.ClientId.Trim(), name, claim, groupRoles is { Length: > 0 } ? groupRoles : null, domains, tenants);
            if (given.ClientSecret is not null) secret = given.ClientSecret.Length == 0 ? null : given.ClientSecret;
            if (secret is { Length: > 2048 }) throw Invalid("The client secret is too long.");
            // A provider that does not answer is refused now rather than at the first sign-in of a person.
            try
            {
                await _external.ProbeAsync(Provider(FleetSignInMethods.Company, company, secret)!, cancellationToken).ConfigureAwait(false);
            }
            catch (ExternalSignInException exception)
            {
                throw CoordinatorException.BadRequest(exception.Code, exception.Message);
            }
        }
        else
        {
            secret = null;
        }

        if (enabled.Contains(FleetSignInMethods.Company) && company is null) throw Invalid("Company sign-in needs its provider.");
        var breakGlass = request.BreakGlassPersonId ?? (request.RequireSso ? owner.Id : null);
        if (request.RequireSso)
        {
            if (company is null || !enabled.Contains(FleetSignInMethods.Company)) throw Invalid("Requiring company sign-in needs company sign-in configured and enabled.");
            var keeper = breakGlass is null ? null : await _store.GetPersonAsync(breakGlass, cancellationToken).ConfigureAwait(false);
            if (keeper is null || keeper.OrganizationId != actor.OrganizationId || keeper.Role != PersonRoles.Owner)
                throw Invalid("The break-glass person must be an owner of the organization.");
            // Their way in is a passkey: without one, company sign-in required would leave the organization to its provider.
            if ((await _signIn.ListPasskeysAsync(keeper.Id, cancellationToken).ConfigureAwait(false)).Count == 0)
                throw Invalid("The break-glass owner needs a passkey before company sign-in is required: add one in Link….");
            // The break-glass owner keeps the way in that company sign-in replaces: a passkey.
            if (!enabled.Contains(FleetSignInMethods.Passkey)) enabled = [.. enabled, FleetSignInMethods.Passkey];
        }

        var document = new SignInSettingsDocument(enabled, company, request.RequireSso, request.RequireSso ? breakGlass : null);
        // Changing the company sign-in is a dangerous action: the owner signed in again from this device a few minutes ago.
        // The confirmation is taken last, so that a change refused for another reason does not use it up.
        if (ChangesCompanySignIn(held.Document, document, !string.Equals(secret, held.Secret, StringComparison.Ordinal)) && !IsConfirmed(request, actor, owner))
        {
            throw CoordinatorException.Forbidden(SignInErrorCodes.ConfirmationRequired,
                "Changing the company sign-in needs you to sign in again, or to approve it on another of your devices.", "confirm");
        }
        var record = new SignInSettingsRecord(actor.OrganizationId, JsonSerializer.Serialize(document, SignInSettingsJsonContext.Default.SignInSettingsDocument), secret,
            held.Version + 1, _time.GetUtcNow());
        var details = JsonSerializer.Serialize(new SignInSettingsAudit(enabled, company?.Issuer, request.RequireSso, company?.AllowedDomains, company?.AllowedTenants),
            SignInSettingsJsonContext.Default.SignInSettingsAudit);
        switch (await _signIn.SetSignInSettingsAsync(record, document.BreakGlassPersonId, origin.Audit("sign_in.settings.set", actor, actor.OrganizationId, details: details),
                    cancellationToken).ConfigureAwait(false))
        {
            case SignInSettingsWriteOutcome.VersionMoved:
                throw CoordinatorException.Conflict(SignInErrorCodes.SettingsInvalid, "The sign-in settings changed meanwhile: read them again.");
            case SignInSettingsWriteOutcome.BreakGlassWithoutPasskey:
                throw Invalid("The break-glass owner needs a passkey before company sign-in is required: add one in Link….");
        }
        return ToDto(new HeldSettings(document, secret, record.Version, record.UpdatedAt));
    }

    /// <summary>Starts a sign-in: the request is signed by the key it names (a new device), or by an approved controller device for a link.</summary>
    /// <param name="request">The request.</param>
    /// <param name="signed">The signature of the request.</param>
    /// <param name="origin">Where it comes from.</param>
    /// <param name="context">The request, for the address of the server's own sign-in page.</param>
    /// <param name="cancellationToken">Cancels the start.</param>
    /// <returns>The address to open in the browser.</returns>
    /// <exception cref="CoordinatorException">The request does not verify, the method is not enabled, company sign-in is required, or too many sign-ins wait.</exception>
    public async Task<StartSignInResponse> StartAsync(StartSignInRequest? request, SignedRequestData signed, RequestOrigin origin, HttpContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(signed);
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(context);
        if (request is null || !FleetDevicePublicKey.TryParse(request.PublicKey, out var key))
            throw Invalid("The request needs a method, the public key of the device and its loopback redirect.");
        var purpose = request.Purpose ?? FleetSignInPurposes.Enroll;
        if (purpose is not (FleetSignInPurposes.Enroll or FleetSignInPurposes.Link or FleetSignInPurposes.Confirm)) throw Invalid("purpose must be 'enroll', 'link' or 'confirm'.");
        // A device code has no redirect: the device polls instead.
        var deviceFlow = FleetSignInMethods.UsesDeviceCode(request.Method);
        var redirect = deviceFlow ? string.Empty : RequireLoopbackRedirect(request.RedirectUri);
        var organization = await _store.GetOrganizationAsync(cancellationToken).ConfigureAwait(false)
            ?? throw CoordinatorException.NotFound(CoordinatorErrorCodes.NotSetUp, "The server is not set up.");

        string? linking = null;
        var firstWayIn = false;
        if (purpose is FleetSignInPurposes.Link or FleetSignInPurposes.Confirm)
        {
            // A link or a confirmation is made by a person on one of their approved controller devices, never by a device the server does not know.
            var actor = await _authenticator.AuthenticateAsync(signed, cancellationToken).ConfigureAwait(false);
            if (actor.Id != key.DeviceId || actor.Kind != FleetDeviceKinds.Controller || actor.OwnerPersonId is null)
                throw CoordinatorException.Forbidden(FleetErrorCodes.Forbidden, "A sign-in method is linked from an approved controller device of its person.");
            linking = actor.OwnerPersonId;
            if (purpose == FleetSignInPurposes.Link)
                firstWayIn = await RequireLinkAuthorityAsync(actor, request.Method, request.Confirmation, request.ApprovalId, origin, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            _authenticator.VerifyWithKey(signed, key);
        }

        var settings = await ReadSettingsAsync(organization.Id, cancellationToken).ConfigureAwait(false);
        RequireMethod(settings, request.Method, purpose);

        var id = SignInAttempts.NewSecret();
        var state = SignInAttempts.NewSecret();
        var nonce = SignInAttempts.NewSecret();
        var verifier = SignInAttempts.NewSecret();
        var now = _time.GetUtcNow();
        var lifetime = _options.AttemptLifetime;
        DeviceAuthorization? device = null;
        Uri url;
        try
        {
            if (request.Method == FleetSignInMethods.Passkey)
            {
                // Behind a proxy that ends TLS (Tailscale Serve, Caddy), the browser reaches the page at https://, which only a
                // trusted proxy says; the passkey ceremony needs that secure origin.
                var scheme = Proxies is { Any: true } proxies ? proxies.SchemeOf(context.Connection.RemoteIpAddress, context.Request.Headers, context.Request.Scheme)
                    : context.Request.Scheme;
                url = new Uri($"{scheme}://{context.Request.Host}{context.Request.PathBase}/sign-in/passkey#{Uri.EscapeDataString(id)}");
            }
            else if (deviceFlow)
            {
                device = await _external.StartDeviceAuthorizationAsync(Provider(request.Method, settings.Document.Company, settings.Secret)!, cancellationToken).ConfigureAwait(false);
                url = device.VerificationUri;
                if (device.ExpiresIn < lifetime) lifetime = device.ExpiresIn;
            }
            else
            {
                // A confirmation is a fresh sign-in at the provider, never the session the browser keeps there.
                url = await _external.BuildAuthorizationUrlAsync(Provider(request.Method, settings.Document.Company, settings.Secret)!, redirect, state, nonce,
                    SignInAttempts.Challenge(verifier), purpose == FleetSignInPurposes.Confirm, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (ExternalSignInException exception)
        {
            throw new CoordinatorException(StatusCodes.Status502BadGateway, exception.Code, exception.Message);
        }

        var attempt = new SignInAttempt
        {
            Id = id, OrganizationId = organization.Id, Method = request.Method, Purpose = purpose, DeviceId = key.DeviceId, PublicKey = key.Text,
            LinkingPersonId = linking, FirstWayIn = firstWayIn,
            RedirectUri = redirect, State = state, Nonce = nonce, CodeVerifier = verifier,
            ExpiresAt = now + lifetime, StartedAt = now,
            DeviceCode = device?.DeviceCode, PollInterval = device?.Interval ?? 5,
            // GitHub's interval is the time between two requests for a token, the first one included.
            NextPollAt = now + TimeSpan.FromSeconds(device?.Interval ?? 0),
        };
        if (!_attempts.Add(attempt)) throw new CoordinatorException(StatusCodes.Status503ServiceUnavailable, SignInErrorCodes.Busy, "Too many sign-ins wait: try again in a minute.");
        await _store.AppendAuditAsync(new AuditEntry("sign_in.start", AuditEntry.Success, linking, key.DeviceId, request.Method, origin.SourceAddress, origin.RequestId,
            $"{{\"purpose\":\"{purpose}\"}}"), cancellationToken).ConfigureAwait(false);
        return new StartSignInResponse(attempt.Id, url.AbsoluteUri, device is null ? attempt.State : null, attempt.ExpiresAt, device?.UserCode, device?.Interval);
    }

    /// <summary>
    /// Completes a sign-in with the code the browser brought back: signed by the key that started it, once. An enrollment
    /// gives a grant; a link links the method (or the passkey registered) to the person.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="signed">The signature of the request.</param>
    /// <param name="origin">Where it comes from.</param>
    /// <param name="cancellationToken">Cancels the completion.</param>
    /// <returns>The person and the grant.</returns>
    /// <exception cref="CoordinatorException">The attempt is unknown, used or of another device, the state differs, the provider's answer does not verify, the account is linked to nobody, or company sign-in is required.</exception>
    public async Task<CompleteSignInResponse> CompleteAsync(CompleteSignInRequest? request, SignedRequestData signed, RequestOrigin origin, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(signed);
        ArgumentNullException.ThrowIfNull(origin);
        if (request is null || string.IsNullOrEmpty(request.Code) || request.Code.Length > 4096 || string.IsNullOrEmpty(request.State))
            throw Invalid("The request needs the attempt, the code and the state.");
        var pending = _attempts.Find(request.AttemptId);
        if (pending is null || pending.DeviceId != signed.DeviceId || pending.DeviceCode is not null)
            throw await RefuseAsync(origin, signed.DeviceId, request.AttemptId, SignInErrorCodes.AttemptInvalid, "The sign-in is unknown, used or expired: start again.", cancellationToken)
                .ConfigureAwait(false);
        await RequireStartingDeviceAsync(pending, signed, cancellationToken).ConfigureAwait(false);

        // Taken once: a replayed code finds no attempt, whatever the provider would answer.
        var attempt = _attempts.Take(request.AttemptId);
        if (attempt is null)
            throw await RefuseAsync(origin, signed.DeviceId, request.AttemptId, SignInErrorCodes.AttemptInvalid, "The sign-in is used or expired: start again.", cancellationToken)
                .ConfigureAwait(false);
        if (!CryptographicEquals(attempt.State, request.State))
            throw await RefuseAsync(origin, signed.DeviceId, attempt.Method, SignInErrorCodes.StateMismatch, "The browser came back with another sign-in's state.", cancellationToken)
                .ConfigureAwait(false);

        var settings = await ReadSettingsAsync(attempt.OrganizationId, cancellationToken).ConfigureAwait(false);
        try
        {
            RequireMethod(settings, attempt.Method, attempt.Purpose);
        }
        catch (CoordinatorException exception)
        {
            throw await RefuseAsync(origin, signed.DeviceId, attempt.Method, exception.Code, exception.Message, cancellationToken).ConfigureAwait(false);
        }

        PersonRecord person;
        LoginRecord? login = null;
        if (attempt.Method == FleetSignInMethods.Passkey)
        {
            if (attempt.PasskeyCodeHash is null || attempt.PasskeyPersonId is null || !CryptographicEquals(attempt.PasskeyCodeHash, SignInAttempts.Hash(request.Code)))
                throw await RefuseAsync(origin, signed.DeviceId, attempt.Method, SignInErrorCodes.PasskeyInvalid, "The passkey sign-in did not finish.", cancellationToken)
                    .ConfigureAwait(false);
            person = await _store.GetPersonAsync(attempt.PasskeyPersonId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("A passkey without its person.");
            if (attempt.Purpose == FleetSignInPurposes.Link) await AddPendingPasskeyAsync(attempt, person, origin, signed.DeviceId, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            ExternalIdentity identity;
            try
            {
                identity = await _external.RedeemAsync(Provider(attempt.Method, settings.Document.Company, settings.Secret)!, request.Code, attempt.RedirectUri, attempt.CodeVerifier,
                    attempt.Nonce, attempt.Purpose == FleetSignInPurposes.Confirm ? attempt.StartedAt : null, cancellationToken).ConfigureAwait(false);
            }
            catch (ExternalSignInException exception)
            {
                throw await RefuseAsync(origin, signed.DeviceId, attempt.Method, exception.Code, exception.Message, cancellationToken).ConfigureAwait(false);
            }

            (person, login) = await ResolvePersonAsync(attempt, settings, identity, origin, signed.DeviceId, cancellationToken).ConfigureAwait(false);
        }

        return await FinishAsync(attempt, settings, person, login, origin, signed.DeviceId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Asks the provider whether the person entered the code of a sign-in that uses one (GitHub): signed by the key that
    /// started it, at most once per interval of the provider whatever the device sends. Once the person entered it, the
    /// sign-in completes as <see cref="CompleteAsync"/> does, once.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="signed">The signature of the request.</param>
    /// <param name="origin">Where it comes from.</param>
    /// <param name="cancellationToken">Cancels the poll.</param>
    /// <returns>Pending with the interval to wait, or what the sign-in gave.</returns>
    /// <exception cref="CoordinatorException">The attempt is unknown, used, expired or of another device, the person refused or let the code expire, the provider refused, or the account is linked to nobody.</exception>
    public async Task<PollSignInResponse> PollAsync(PollSignInRequest? request, SignedRequestData signed, RequestOrigin origin, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(signed);
        ArgumentNullException.ThrowIfNull(origin);
        if (request is null || string.IsNullOrEmpty(request.AttemptId)) throw Invalid("The request needs the attempt.");
        var pending = _attempts.Find(request.AttemptId);
        if (pending is null || pending.DeviceId != signed.DeviceId || pending.DeviceCode is not { } deviceCode)
            throw await RefuseAsync(origin, signed.DeviceId, null, SignInErrorCodes.AttemptInvalid, "The sign-in is unknown, used or expired: start again.", cancellationToken)
                .ConfigureAwait(false);
        await RequireStartingDeviceAsync(pending, signed, cancellationToken).ConfigureAwait(false);

        // One poll of the provider at a time, and never faster than it asked: a device that polls faster only waits.
        if (!pending.TryBeginPoll(_time.GetUtcNow())) return new PollSignInResponse(true, pending.PollInterval, null);
        HeldSettings settings;
        DevicePollResult result;
        try
        {
            settings = await ReadSettingsAsync(pending.OrganizationId, cancellationToken).ConfigureAwait(false);
            RequireMethod(settings, pending.Method, pending.Purpose);
            result = await _external.PollDeviceAuthorizationAsync(Provider(pending.Method, settings.Document.Company, settings.Secret)!, deviceCode, pending.PollInterval,
                cancellationToken).ConfigureAwait(false);
            pending.PollInterval = result.Interval;
        }
        catch (Exception exception) when (exception is CoordinatorException or ExternalSignInException)
        {
            // Refused, expired or turned off: the attempt is over.
            _attempts.Take(pending.Id);
            var code = exception is CoordinatorException refused ? refused.Code : ((ExternalSignInException)exception).Code;
            throw await RefuseAsync(origin, signed.DeviceId, pending.Method, code, exception.Message, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            pending.EndPoll(_time.GetUtcNow());
        }

        if (result.Pending) return new PollSignInResponse(true, result.Interval, null);
        var attempt = _attempts.Take(pending.Id);
        if (attempt is null)
            throw await RefuseAsync(origin, signed.DeviceId, pending.Method, SignInErrorCodes.AttemptInvalid, "The sign-in expired: start again.", cancellationToken)
                .ConfigureAwait(false);
        var (person, login) = await ResolvePersonAsync(attempt, settings, result.Identity!, origin, signed.DeviceId, cancellationToken).ConfigureAwait(false);
        return new PollSignInResponse(false, result.Interval, await FinishAsync(attempt, settings, person, login, origin, signed.DeviceId, cancellationToken).ConfigureAwait(false));
    }

    // A new way to sign in becomes a factor of its own (a later confirmation, a new device): the device key alone, which a
    // thief may hold, does not add one. Another approved controller device of the person approves this very link; or a
    // person who has a way in proves it again first, from this device; or a person who has none links their first one soon
    // after the device joined, when its key was just proved by the setup code or by another device. Returns whether the
    // link is of the first way in, which its completion checks again.
    private async Task<bool> RequireLinkAuthorityAsync(DeviceRecord actor, string method, string? confirmation, string? approvalId, RequestOrigin origin,
        CancellationToken cancellationToken)
    {
        var person = actor.OwnerPersonId!;
        if (approvalId is not null)
        {
            // An approval of another link (another method, person or device) is left as it is.
            if (_attempts.FindLinkApproval(approvalId) is { } linkApproval && linkApproval.PersonId == person && linkApproval.OrganizationId == actor.OrganizationId
                && linkApproval.Method == method && _attempts.TakeLinkApproval(approvalId, actor.Id) is not null)
                return false;
            throw await RefuseStartAsync(origin, actor, SignInErrorCodes.ConfirmationRequired,
                "This link is not approved on another of your devices: ask for its approval again.", cancellationToken).ConfigureAwait(false);
        }

        if (await HasWayInAsync(person, cancellationToken).ConfigureAwait(false))
        {
            if (_attempts.TakeConfirmation(confirmation, actor.Id) is { } confirmed && confirmed.PersonId == person && confirmed.OrganizationId == actor.OrganizationId)
                return false;
            throw await RefuseStartAsync(origin, actor, SignInErrorCodes.ConfirmationRequired,
                "Adding a way to sign in needs you to sign in again with one you have, from this device.", cancellationToken).ConfigureAwait(false);
        }

        if (actor.ApprovedAt is { } approved && _time.GetUtcNow() - approved <= _options.FirstWayInWindow) return true;
        throw await RefuseStartAsync(origin, actor, SignInErrorCodes.FirstWayInExpired,
            "A first way to sign in is linked soon after this device joined the server: link it from a device of yours that just joined.", cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> HasWayInAsync(string personId, CancellationToken cancellationToken)
        => (await _signIn.ListLoginsAsync(personId, cancellationToken).ConfigureAwait(false)).Count > 0
            || (await _signIn.ListPasskeysAsync(personId, cancellationToken).ConfigureAwait(false)).Count > 0;

    private async Task<CoordinatorException> RefuseStartAsync(RequestOrigin origin, DeviceRecord actor, string code, string message, CancellationToken _)
    {
        await _store.AppendAuditAsync(new AuditEntry("sign_in.start", code, actor.OwnerPersonId, actor.Id, FleetSignInPurposes.Link, origin.SourceAddress, origin.RequestId),
            CancellationToken.None).ConfigureAwait(false);
        return CoordinatorException.Forbidden(code, message);
    }

    // Only the key that started an attempt completes it: a new device is not in the table yet, so its key is the attempt's;
    // and a link completes only from a device that is still an approved controller of its person.
    private async Task RequireStartingDeviceAsync(SignInAttempt pending, SignedRequestData signed, CancellationToken cancellationToken)
    {
        if (!FleetDevicePublicKey.TryParse(pending.PublicKey, out var started)) throw new InvalidOperationException("An attempt without its key.");
        _authenticator.VerifyWithKey(signed, started);
        if (pending.Purpose is FleetSignInPurposes.Link or FleetSignInPurposes.Confirm
            && await _store.GetDeviceAsync(pending.DeviceId, cancellationToken).ConfigureAwait(false) is not { Status: DeviceStatuses.Approved, Kind: FleetDeviceKinds.Controller })
        {
            throw CoordinatorException.Forbidden(FleetErrorCodes.Forbidden, "A sign-in method is linked from an approved controller device of its person.");
        }
    }

    // The passkey the page registered becomes a way in now, by the signed completion of the device that started it, and
    // only if that device is still an approved controller of the acting person, in the same write.
    private async Task AddPendingPasskeyAsync(SignInAttempt attempt, PersonRecord person, RequestOrigin origin, string deviceId, CancellationToken cancellationToken)
    {
        if (attempt.PendingPasskey is not { } passkey || passkey.PersonId != person.Id)
            throw await RefuseAsync(origin, deviceId, attempt.Method, SignInErrorCodes.PasskeyInvalid, "The passkey sign-in did not finish.", cancellationToken).ConfigureAwait(false);
        var outcome = await _signIn.AddPasskeyAsync(passkey, new WayInAddition(attempt.DeviceId, attempt.FirstWayIn),
            new AuditEntry("passkey.add", AuditEntry.Success, person.Id, attempt.DeviceId, person.Id, origin.SourceAddress, origin.RequestId), cancellationToken).ConfigureAwait(false);
        if (outcome != PasskeyAddOutcome.Added) throw await AdditionRefusedAsync(outcome == PasskeyAddOutcome.Exists ? null : outcome == PasskeyAddOutcome.NotFirst, origin, deviceId,
            attempt.Method, person.Id, cancellationToken).ConfigureAwait(false);
    }

    // A way in that was not added: a passkey registered already (null), the device no longer approved (false), or a first
    // way in another one came before (true).
    private async Task<CoordinatorException> AdditionRefusedAsync(bool? notFirst, RequestOrigin origin, string deviceId, string method, string personId,
        CancellationToken cancellationToken) => notFirst switch
    {
        null => await RefuseAsync(origin, deviceId, method, SignInErrorCodes.PasskeyInvalid, "This passkey is registered already.", cancellationToken, personId).ConfigureAwait(false),
        true => await RefuseAsync(origin, deviceId, method, SignInErrorCodes.ConfirmationRequired,
            "Adding a way to sign in needs you to sign in again with one you have, from this device.", cancellationToken, personId).ConfigureAwait(false),
        false => await RefuseAsync(origin, deviceId, method, FleetErrorCodes.Forbidden, "A sign-in method is linked from an approved controller device of its person.",
            cancellationToken, personId).ConfigureAwait(false),
    };

    // What every sign-in that proved a person ends with: the company sign-in rule, the groups, the audit, and the grant of an enrollment.
    private async Task<CompleteSignInResponse> FinishAsync(SignInAttempt attempt, HeldSettings settings, PersonRecord person, LoginRecord? login, RequestOrigin origin,
        string deviceId, CancellationToken cancellationToken)
    {
        // Company sign-in required: no other way in for anyone but the break-glass owner, whatever the method proved.
        if (attempt.Purpose == FleetSignInPurposes.Enroll && settings.Document.RequireSso && attempt.Method != FleetSignInMethods.Company
            && person.Id != settings.Document.BreakGlassPersonId)
        {
            throw await RefuseAsync(origin, deviceId, attempt.Method, SignInErrorCodes.SsoRequired,
                "Your organization requires its company sign-in.", cancellationToken, person.Id).ConfigureAwait(false);
        }

        // The groups of a company sign-in are kept with its login, and give the role the organization maps them to.
        if (login is not null && attempt.Method == FleetSignInMethods.Company && person.IsActive)
        {
            var mapper = settings.Document.Company?.GroupRoles is { Length: > 0 } map ? new GroupRoleMapper(map) : Roles;
            if (mapper.MapRole(person, ReadGroups(login.Groups)) is { } role && role != person.Role && FleetOrganizationRoles.IsKnown(role) && Access is { } access)
            {
                await access.ApplyGroupRoleAsync(person, role, new AuditEntry("person.role", AuditEntry.Success, person.Id, deviceId, person.Id, origin.SourceAddress,
                    origin.RequestId, $"{{\"from\":\"{person.Role}\",\"to\":\"{role}\",\"by\":\"sign-in\"}}"), cancellationToken).ConfigureAwait(false);
            }
        }

        var devices = await _store.ListDevicesAsync(attempt.OrganizationId, cancellationToken).ConfigureAwait(false);
        var hasDevices = devices.Any(other => other.OwnerPersonId == person.Id && other.Kind == FleetDeviceKinds.Controller && other.Status == DeviceStatuses.Approved);
        var answer = new SignInPersonDto(person.Id, person.DisplayName, login?.Email, hasDevices);
        // A confirmation proves the person of the device that asked, signed in again: another person's sign-in confirms nothing.
        if (attempt.Purpose == FleetSignInPurposes.Confirm && person.Id != attempt.LinkingPersonId)
            throw await RefuseAsync(origin, deviceId, attempt.Method, SignInErrorCodes.NotLinked, "This sign-in is not one of yours: confirm with a method linked to you.",
                cancellationToken, person.Id).ConfigureAwait(false);
        await _store.AppendAuditAsync(new AuditEntry("sign_in.complete", AuditEntry.Success, person.Id, deviceId, attempt.Method, origin.SourceAddress, origin.RequestId,
            $"{{\"purpose\":\"{attempt.Purpose}\"}}"), cancellationToken).ConfigureAwait(false);
        if (attempt.Purpose == FleetSignInPurposes.Link) return new CompleteSignInResponse(attempt.Method, answer, null, null, true);
        if (attempt.Purpose == FleetSignInPurposes.Confirm)
        {
            var until = _time.GetUtcNow() + ConfirmationLifetime;
            return new CompleteSignInResponse(attempt.Method, answer, _attempts.Confirm(new SignInGrant(attempt.OrganizationId, person.Id, attempt.DeviceId, attempt.Method, until)),
                until, false);
        }

        var expires = _time.GetUtcNow() + _options.GrantLifetime;
        var grant = _attempts.Grant(new SignInGrant(attempt.OrganizationId, person.Id, attempt.DeviceId, attempt.Method, expires));
        return new CompleteSignInResponse(attempt.Method, answer, grant, expires, false);
    }

    /// <summary>Gives the server's sign-in page the options of its passkey ceremony: registration for a link, sign-in otherwise.</summary>
    /// <param name="request">The attempt.</param>
    /// <param name="context">The request of the page.</param>
    /// <returns>The options.</returns>
    /// <exception cref="CoordinatorException">The attempt is unknown, expired or not a passkey's.</exception>
    public async Task<PasskeyOptionsResponse> PasskeyOptionsAsync(PasskeyOptionsRequest? request, HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var attempt = RequirePasskeyAttempt(request?.AttemptId);
        if (attempt.Purpose == FleetSignInPurposes.Link)
        {
            var person = await _store.GetPersonAsync(attempt.LinkingPersonId!, context.RequestAborted).ConfigureAwait(false)
                ?? throw new InvalidOperationException("A link without its person.");
            var (options, state) = await _passkeys.BeginRegistrationAsync(person, context).ConfigureAwait(false);
            attempt.PasskeyState = state;
            return new PasskeyOptionsResponse("create", options);
        }

        var (request2, state2) = await _passkeys.BeginAssertionAsync(context).ConfigureAwait(false);
        attempt.PasskeyState = state2;
        return new PasskeyOptionsResponse("get", request2);
    }

    /// <summary>Ends the passkey ceremony of the page and gives the address of the device's loopback, with a one-time code and the state.</summary>
    /// <param name="request">The attempt and what the browser's ceremony answered.</param>
    /// <param name="origin">Where it comes from.</param>
    /// <param name="context">The request of the page.</param>
    /// <returns>Where the page sends the browser.</returns>
    /// <exception cref="CoordinatorException">The attempt is unknown, or the passkey does not verify.</exception>
    public async Task<PasskeyFinishResponse> PasskeyFinishAsync(PasskeyFinishRequest? request, RequestOrigin origin, HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(context);
        var attempt = RequirePasskeyAttempt(request?.AttemptId);
        if (attempt.PasskeyState is not { } state || string.IsNullOrEmpty(request!.CredentialJson) || request.CredentialJson.Length > 64 * 1024)
            throw Invalid("The passkey ceremony did not start.");
        attempt.PasskeyState = null;
        try
        {
            if (attempt.Purpose == FleetSignInPurposes.Link)
            {
                var person = await _store.GetPersonAsync(attempt.LinkingPersonId!, context.RequestAborted).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("A link without its person.");
                // Verified here, added only by the signed completion of the device (CompleteAsync): the page proves the
                // authenticator, never that the device may still add a way in.
                attempt.PendingPasskey = await _passkeys.VerifyRegistrationAsync(person, request.CredentialJson, state, context).ConfigureAwait(false);
                attempt.PasskeyPersonId = person.Id;
            }
            else
            {
                attempt.PasskeyPersonId = (await _passkeys.CompleteAssertionAsync(request.CredentialJson, state, context).ConfigureAwait(false)).PersonId;
            }
        }
        catch (ExternalSignInException exception)
        {
            throw await RefuseAsync(origin, attempt.DeviceId, FleetSignInMethods.Passkey, exception.Code, exception.Message, context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JsonException or FormatException or InvalidOperationException or ArgumentException)
        {
            throw await RefuseAsync(origin, attempt.DeviceId, FleetSignInMethods.Passkey, SignInErrorCodes.PasskeyInvalid, "The passkey answer does not read.", context.RequestAborted)
                .ConfigureAwait(false);
        }

        var code = SignInAttempts.NewSecret();
        attempt.PasskeyCodeHash = SignInAttempts.Hash(code);
        var separator = attempt.RedirectUri.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        return new PasskeyFinishResponse($"{attempt.RedirectUri}{separator}code={Uri.EscapeDataString(code)}&state={Uri.EscapeDataString(attempt.State)}");
    }

    /// <summary>Lists the sign-in methods linked to the calling device's person.</summary>
    /// <param name="actor">The calling device.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The linked accounts and passkeys.</returns>
    public Task<ListResponse<SignInLoginDto>> ListLoginsAsync(DeviceRecord actor, CancellationToken cancellationToken) => ListLoginsAsync(actor, null, cancellationToken);

    /// <summary>Lists the sign-in methods linked to a person: the calling device's, or another one's for an owner or an admin.</summary>
    /// <param name="actor">The calling device.</param>
    /// <param name="personId">The person; null for the calling device's.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The linked accounts and passkeys, each with the id that removes it.</returns>
    /// <exception cref="CoordinatorException">The caller may not read another person's, or there is no such person.</exception>
    public async Task<ListResponse<SignInLoginDto>> ListLoginsAsync(DeviceRecord actor, string? personId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (actor.OwnerPersonId is null && personId is null) return new ListResponse<SignInLoginDto>([]);
        return await LoginsOfAsync(await PersonOfLoginsAsync(actor, personId, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes a way a person signs in, a linked account or a passkey: it signs nobody in afterwards. The calling device's
    /// own person, or another one for an owner or an admin. A company account belongs to the organization (removing a
    /// person is deprovisioning, in People and access); the last way in of an owner without a recovery key stays.
    /// </summary>
    /// <param name="actor">The calling device.</param>
    /// <param name="id">The id of the way in (<see cref="SignInLoginDto.Id"/>).</param>
    /// <param name="personId">The person; null for the calling device's.</param>
    /// <param name="origin">Where the request came from.</param>
    /// <param name="cancellationToken">Cancels the removal.</param>
    /// <returns>The ways the person signs in now.</returns>
    /// <exception cref="CoordinatorException">The caller may not; no way in has this id; it is a company account; or it is the last way in of an owner without a recovery key.</exception>
    public async Task<ListResponse<SignInLoginDto>> RemoveLoginAsync(DeviceRecord actor, string id, string? personId, RequestOrigin origin, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(origin);
        if (actor.Kind != FleetDeviceKinds.Controller || actor.OwnerPersonId is null)
            throw CoordinatorException.Forbidden(FleetErrorCodes.Forbidden, "A way to sign in is removed from a controller device of a person.");
        var person = await PersonOfLoginsAsync(actor, personId, cancellationToken).ConfigureAwait(false);
        var login = (await _signIn.ListLoginsAsync(person.Id, cancellationToken).ConfigureAwait(false)).FirstOrDefault(login => LoginId(login) == id);
        var passkey = login is not null ? null
            : (await _signIn.ListPasskeysAsync(person.Id, cancellationToken).ConfigureAwait(false)).FirstOrDefault(passkey => PasskeyId(passkey) == id);
        if (login is null && passkey is null) throw CoordinatorException.NotFound(SignInErrorCodes.LoginNotFound, "No way to sign in of this person has this id.");
        if (login?.Method == FleetSignInMethods.Company)
            throw CoordinatorException.Forbidden(SignInErrorCodes.ManagedByOrganization,
                "A company account is managed by your organization: an admin removes the person in People and access.");

        // An owner keeps a way in that signs them in under the settings now while nothing else lets them back in when every
        // device is lost, and the break-glass owner a passkey while company sign-in is required: checked in the write,
        // against the settings it was judged by, read again when they moved on meanwhile.
        var method = login?.Method ?? FleetSignInMethods.Passkey;
        var audit = origin.Audit("sign_in.login.remove", actor, person.Id, details: $"{{\"method\":\"{method}\"}}");
        var outcome = LoginRemovalOutcome.SettingsChanged;
        for (var tries = 0; tries < 3 && outcome == LoginRemovalOutcome.SettingsChanged; tries++)
        {
            var settings = await ReadSettingsAsync(person.OrganizationId, cancellationToken).ConfigureAwait(false);
            var rule = new WayInRemoval(settings.Version, UsableMethods(settings.Document, person.Id), person.Role == PersonRoles.Owner,
                settings.Document.RequireSso && settings.Document.BreakGlassPersonId == person.Id);
            outcome = login is not null
                ? await _signIn.RemoveLoginAsync(login, rule, audit, cancellationToken).ConfigureAwait(false)
                : await _signIn.RemovePasskeyAsync(passkey!, rule, audit, cancellationToken).ConfigureAwait(false);
        }

        return outcome switch
        {
            LoginRemovalOutcome.Removed => await LoginsOfAsync(person, cancellationToken).ConfigureAwait(false),
            LoginRemovalOutcome.LastWayIn => throw CoordinatorException.Conflict(SignInErrorCodes.LastWayIn,
                "This is the last way this owner signs in now, and they have no recovery key: make the recovery key first, or link another way to sign in."),
            LoginRemovalOutcome.BreakGlassPasskey => throw CoordinatorException.Conflict(SignInErrorCodes.LastWayIn,
                "This is the break-glass owner's last passkey while company sign-in is required: add another passkey first."),
            LoginRemovalOutcome.SettingsChanged => throw CoordinatorException.Conflict(SignInErrorCodes.SettingsInvalid, "The sign-in settings keep changing: try again."),
            _ => throw CoordinatorException.NotFound(SignInErrorCodes.LoginNotFound, "No way to sign in of this person has this id."),
        };
    }

    // The methods that sign a person in on a new device under the settings: the enabled and available ones, and, while
    // company sign-in is required, company sign-in, with the passkeys of the break-glass owner.
    private IReadOnlyCollection<string> UsableMethods(SignInSettingsDocument document, string personId)
        => [.. Describe(document).Where(static method => method.Enabled && method.Available).Select(static method => method.Id)
            .Where(method => !document.RequireSso || method == FleetSignInMethods.Company || method == FleetSignInMethods.Passkey && personId == document.BreakGlassPersonId)];

    // The person whose ways in a call reads or removes: the calling device's own, or another of the organization for a
    // device whose person manages the people (an owner, an admin).
    private async Task<PersonRecord> PersonOfLoginsAsync(DeviceRecord actor, string? personId, CancellationToken cancellationToken)
    {
        if (personId is not null && personId != actor.OwnerPersonId
            && (Access is not { } access || !await access.MayManagePeopleAsync(actor, cancellationToken).ConfigureAwait(false)))
            throw CoordinatorException.Forbidden(FleetErrorCodes.Forbidden, "Only an owner or an admin manages how another person signs in.");
        var person = await _store.GetPersonAsync(personId ?? actor.OwnerPersonId!, cancellationToken).ConfigureAwait(false);
        return person is not null && person.OrganizationId == actor.OrganizationId
            ? person
            : throw CoordinatorException.NotFound(SignInErrorCodes.LoginNotFound, "No person of the organization has this id.");
    }

    private async Task<ListResponse<SignInLoginDto>> LoginsOfAsync(PersonRecord person, CancellationToken cancellationToken)
    {
        var logins = await _signIn.ListLoginsAsync(person.Id, cancellationToken).ConfigureAwait(false);
        var passkeys = await _signIn.ListPasskeysAsync(person.Id, cancellationToken).ConfigureAwait(false);
        return new ListResponse<SignInLoginDto>([
            .. logins.Select(static login => new SignInLoginDto(login.Method, login.Name, login.CreatedAt, login.LastSignInAt, LoginId(login))),
            .. passkeys.Select(static passkey => new SignInLoginDto(FleetSignInMethods.Passkey, passkey.Name, passkey.CreatedAt, passkey.LastUsedAt, PasskeyId(passkey))),
        ]);
    }

    // The ids of the ways in: opaque, so that the list shows neither the issuer and subject of an account nor the id of a credential.
    private static string LoginId(LoginRecord login) => "a_" + ShortHash(login.Issuer + "\n" + login.Subject);

    private static string PasskeyId(PasskeyRecord passkey) => "p_" + ShortHash(passkey.CredentialId);

    private static string ShortHash(string text) => Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..22];

    /// <inheritdoc />
    public void Dispose()
    {
        _external.Dispose();
        _passkeys.Dispose();
    }

    private async Task<(PersonRecord Person, LoginRecord Login)> ResolvePersonAsync(SignInAttempt attempt, HeldSettings settings, ExternalIdentity identity, RequestOrigin origin,
        string? deviceId, CancellationToken cancellationToken)
    {
        // Every company sign-in, of a new person or of one already linked, is of a domain or a tenant the owner named.
        if (attempt.Method == FleetSignInMethods.Company && Admit(settings.Document.Company, identity) is var admission and not Admission.Allowed)
            throw await RefuseAsync(origin, deviceId, attempt.Method, SignInErrorCodes.IdentityNotAllowed,
                admission == Admission.EmailNotConfirmed
                    ? "The provider did not confirm this account's email, so its domain cannot be checked: let in its tenant instead."
                    : "This account is not of the domains or tenants your organization lets in.", cancellationToken).ConfigureAwait(false);
        var now = _time.GetUtcNow();
        var groups = identity.Groups.Count == 0 ? null : JsonSerializer.Serialize(identity.Groups.ToArray(), SignInSettingsJsonContext.Default.StringArray);
        var name = Truncate(identity.Email ?? identity.Name ?? identity.Subject);
        var existing = await _signIn.FindLoginAsync(attempt.OrganizationId, identity.Issuer, identity.Subject, cancellationToken).ConfigureAwait(false);
        if (attempt.Purpose == FleetSignInPurposes.Link)
        {
            var person = await _store.GetPersonAsync(attempt.LinkingPersonId!, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("A link without its person.");
            var login = new LoginRecord(attempt.OrganizationId, person.Id, attempt.Method, identity.Issuer, identity.Subject, identity.Email, name, groups, now, now);
            // The device is still an approved controller of the acting person, and a first way in still the first, in the same write.
            var outcome = await _signIn.LinkLoginAsync(login, new WayInAddition(attempt.DeviceId, attempt.FirstWayIn), new AuditEntry("sign_in.link", AuditEntry.Success, person.Id,
                deviceId, attempt.Method, origin.SourceAddress, origin.RequestId), cancellationToken).ConfigureAwait(false);
            if (outcome is LoginLinkOutcome.DeviceNotApproved or LoginLinkOutcome.NotFirst)
                throw await AdditionRefusedAsync(outcome == LoginLinkOutcome.NotFirst, origin, attempt.DeviceId, attempt.Method, person.Id, cancellationToken).ConfigureAwait(false);
            if (outcome == LoginLinkOutcome.LinkedElsewhere)
                throw await RefuseAsync(origin, deviceId, attempt.Method, SignInErrorCodes.LinkedElsewhere, "This account is linked to another person of the organization.",
                    cancellationToken, person.Id).ConfigureAwait(false);
            if (outcome == LoginLinkOutcome.AlreadyLinked) await _signIn.UpdateLoginAsync(login, cancellationToken).ConfigureAwait(false);
            return (person, login);
        }

        // A confirmation signs in with a method already linked to the person of the device; it links and creates nothing.
        if (attempt.Purpose == FleetSignInPurposes.Confirm && (existing is null || existing.PersonId != attempt.LinkingPersonId))
            throw await RefuseAsync(origin, deviceId, attempt.Method, SignInErrorCodes.NotLinked, "This account is not linked to you: confirm with a method linked to you.",
                cancellationToken).ConfigureAwait(false);

        if (existing is not null)
        {
            var updated = existing with { Email = identity.Email ?? existing.Email, Name = name, Groups = groups ?? (attempt.Method == FleetSignInMethods.Company ? null : existing.Groups),
                LastSignInAt = now };
            await _signIn.UpdateLoginAsync(updated, cancellationToken).ConfigureAwait(false);
            var person = await _store.GetPersonAsync(existing.PersonId, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("A login without its person.");
            return (person, updated);
        }

        // People are created at their first company sign-in; an account of Microsoft, Google or GitHub signs in only once linked.
        if (attempt.Method != FleetSignInMethods.Company)
            throw await RefuseAsync(origin, deviceId, attempt.Method, SignInErrorCodes.NotLinked,
                "No person of this organization linked this account: sign in on one of your devices and link it first.", cancellationToken).ConfigureAwait(false);
        var created = new PersonRecord(SetupService.NewId(), attempt.OrganizationId, Truncate(identity.Name ?? identity.Email ?? "Member"), PersonRoles.Member, now);
        var first = new LoginRecord(attempt.OrganizationId, created.Id, attempt.Method, identity.Issuer, identity.Subject, identity.Email, name, groups, now, now);
        var held = await _signIn.CreatePersonWithLoginAsync(created, first, new AuditEntry("person.create", AuditEntry.Success, created.Id, deviceId, created.Id,
            origin.SourceAddress, origin.RequestId, "{\"by\":\"company\"}"), cancellationToken).ConfigureAwait(false);
        var holder = await _store.GetPersonAsync(held.PersonId, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("A login without its person.");
        return (holder, held);
    }

    private SignInAttempt RequirePasskeyAttempt(string? attemptId)
    {
        var attempt = _attempts.Find(attemptId);
        if (attempt is null || attempt.Method != FleetSignInMethods.Passkey)
            throw CoordinatorException.NotFound(SignInErrorCodes.AttemptInvalid, "The sign-in is unknown or expired: start again from CodeAlta.");
        return attempt;
    }

    private void RequireMethod(HeldSettings settings, string? method, string purpose)
    {
        var described = Describe(settings.Document).FirstOrDefault(candidate => candidate.Id == method);
        if (described is null || !described.Enabled || !described.Available)
            throw CoordinatorException.Forbidden(SignInErrorCodes.MethodDisabled, $"'{method}' is not a sign-in method of this server.");
        // While company sign-in is required, a new device signs in with it, or with the break-glass owner's passkey.
        if (purpose == FleetSignInPurposes.Enroll && settings.Document.RequireSso && method is not (FleetSignInMethods.Company or FleetSignInMethods.Passkey))
            throw CoordinatorException.Forbidden(SignInErrorCodes.SsoRequired, "Your organization requires its company sign-in.");
    }

    private IReadOnlyList<SignInMethodDto> Describe(SignInSettingsDocument document)
        => [.. FleetSignInMethods.All.Select(method => new SignInMethodDto(method, DisplayName(method, document), document.Enabled.Contains(method), IsAvailable(method, document)))];

    private bool IsAvailable(string method, SignInSettingsDocument document) => method switch
    {
        FleetSignInMethods.Passkey => true,
        FleetSignInMethods.Microsoft => !PublicSignInClients.IsPlaceholder(_options.Microsoft.ClientId),
        FleetSignInMethods.Google => !PublicSignInClients.IsPlaceholder(_options.Google.ClientId),
        FleetSignInMethods.GitHub => !PublicSignInClients.IsPlaceholder(_options.GitHub.ClientId),
        FleetSignInMethods.Company => document.Company is not null,
        _ => false,
    };

    private static string DisplayName(string method, SignInSettingsDocument document) => method switch
    {
        FleetSignInMethods.Passkey => "Passkey",
        FleetSignInMethods.Microsoft => "Microsoft",
        FleetSignInMethods.Google => "Google",
        FleetSignInMethods.GitHub => "GitHub",
        _ => document.Company?.DisplayName ?? "Company sign-in",
    };

    private ExternalProvider? Provider(string method, CompanyDocument? company, string? secret) => method switch
    {
        FleetSignInMethods.Microsoft => new ExternalProvider(method, ExternalProviderKind.OpenIdConnect, _options.MicrosoftAuthority, _options.Microsoft, "openid profile email"),
        FleetSignInMethods.Google => new ExternalProvider(method, ExternalProviderKind.OpenIdConnect, _options.GoogleAuthority, _options.Google, "openid profile email"),
        // A GitHub App asks no scope: reading the account needs no permission.
        FleetSignInMethods.GitHub => new ExternalProvider(method, ExternalProviderKind.GitHub, _options.GitHubAddress, _options.GitHub, string.Empty,
            ApiAddress: _options.GitHubApiAddress),
        FleetSignInMethods.Company when company is not null => new ExternalProvider(method, ExternalProviderKind.OpenIdConnect, new Uri(company.Issuer),
            new SignInClient(company.ClientId, secret), "openid profile email", company.GroupsClaim),
        _ => null,
    };

    private async Task<HeldSettings> ReadSettingsAsync(string organizationId, CancellationToken cancellationToken)
    {
        var record = await _signIn.GetSignInSettingsAsync(organizationId, cancellationToken).ConfigureAwait(false);
        if (record is null) return new HeldSettings(new SignInSettingsDocument(DefaultEnabled, null, false, null), null, 0, null);
        var document = JsonSerializer.Deserialize(record.Settings, SignInSettingsJsonContext.Default.SignInSettingsDocument)
            ?? throw new InvalidDataException("The sign-in settings of the store do not read.");
        return new HeldSettings(document, record.CompanyClientSecret, record.Version, record.UpdatedAt);
    }

    private SignInSettingsDto ToDto(HeldSettings settings)
        => new(Describe(settings.Document), settings.Document.Company is { } company
                ? new CompanySignInDto(company.Issuer, company.ClientId, settings.Secret is not null, company.DisplayName, company.GroupsClaim, company.GroupRoles ?? [],
                    company.AllowedDomains ?? [], company.AllowedTenants ?? [])
                : null,
            settings.Document.RequireSso, settings.Document.BreakGlassPersonId, settings.Version, settings.UpdatedAt);

    private async Task<PersonRecord> RequireOwnerAsync(DeviceRecord actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var person = actor.Kind == FleetDeviceKinds.Controller && actor.OwnerPersonId is not null
            ? await _store.GetPersonAsync(actor.OwnerPersonId, cancellationToken).ConfigureAwait(false)
            : null;
        if (person is null || person.Role != PersonRoles.Owner)
            throw CoordinatorException.Forbidden(FleetErrorCodes.Forbidden, "Only a controller device of an owner changes how people sign in.");
        return person;
    }

    private string RequireLoopbackRedirect(string? text)
    {
        // The browser comes back to the device that started the sign-in, never to an address elsewhere.
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp || !uri.IsLoopback || uri.IsDefaultPort || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.UserInfo)
            || uri.Host is not ("127.0.0.1" or "[::1]" or "localhost"))
            throw CoordinatorException.BadRequest(SignInErrorCodes.RedirectInvalid, "redirectUri must be http://127.0.0.1:<port>/<path> on the device.");
        return uri.AbsoluteUri;
    }

    // The refusal is audited even when the request was cancelled meanwhile: a failed sign-in always leaves its trace.
    private async Task<CoordinatorException> RefuseAsync(RequestOrigin origin, string? deviceId, string? target, string code, string message, CancellationToken _,
        string? personId = null)
    {
        await _store.AppendAuditAsync(new AuditEntry("sign_in.complete", code, personId, deviceId, target, origin.SourceAddress, origin.RequestId), CancellationToken.None)
            .ConfigureAwait(false);
        var status = code switch
        {
            SignInErrorCodes.MethodDisabled or SignInErrorCodes.SsoRequired or SignInErrorCodes.NotLinked or SignInErrorCodes.LinkedElsewhere
                or SignInErrorCodes.IdentityNotAllowed or SignInErrorCodes.ConfirmationRequired or SignInErrorCodes.FirstWayInExpired or FleetErrorCodes.Forbidden
                => StatusCodes.Status403Forbidden,
            SignInErrorCodes.ProviderUnavailable => StatusCodes.Status502BadGateway,
            _ => StatusCodes.Status401Unauthorized,
        };
        return new CoordinatorException(status, code, message);
    }

    private static CoordinatorException Invalid(string message) => CoordinatorException.BadRequest(SignInErrorCodes.SettingsInvalid, message);

    // A change of the company sign-in is confirmed by a fresh sign-in of its owner from this device, or by an approval of
    // exactly this change that another controller device of the owner signed; either works once.
    private bool IsConfirmed(SetSignInSettingsRequest request, DeviceRecord actor, PersonRecord owner)
    {
        if (request.ApprovalId is { } id)
        {
            var approval = _attempts.TakeApproval(id, actor.Id);
            return approval is not null && approval.PersonId == owner.Id && approval.OrganizationId == actor.OrganizationId
                && CryptographicEquals(approval.Digest, FleetSignInApprovals.Digest(FleetSignInApprovals.Describe(request with { Confirmation = null, ApprovalId = null })));
        }

        return _attempts.TakeConfirmation(request.Confirmation, actor.Id)?.PersonId == owner.Id;
    }

    /// <summary>
    /// Asks another controller device of the caller's person to approve a change of the company sign-in: the change, its
    /// client secret by its hash only, waits 10 minutes for <see cref="ApproveAsync"/>.
    /// </summary>
    /// <param name="actor">The calling device, which alone applies the change once approved.</param>
    /// <param name="request">The change, as it will be sent to <c>PUT /api/v1/sign-in/settings</c>.</param>
    /// <param name="origin">Where it comes from.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The approval that waits.</returns>
    /// <exception cref="CoordinatorException">The caller is not an owner's controller device, the change does not read, or too many wait.</exception>
    public async Task<SignInApprovalDto> RequestApprovalAsync(DeviceRecord actor, SetSignInSettingsRequest? request, RequestOrigin origin, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(origin);
        var owner = await RequireOwnerAsync(actor, cancellationToken).ConfigureAwait(false);
        if (request?.EnabledMethods is null) throw Invalid("The request needs the change to approve.");
        var change = FleetSignInApprovals.Describe(request with { Confirmation = null, ApprovalId = null });
        var approval = new SignInApprovalRequest
        {
            Id = SignInAttempts.NewSecret(), OrganizationId = actor.OrganizationId, PersonId = owner.Id, RequesterDeviceId = actor.Id, RequesterName = actor.Name, Change = change,
            Digest = FleetSignInApprovals.Digest(change), ExpiresAt = _time.GetUtcNow() + ConfirmationLifetime,
        };
        if (!_attempts.AddApproval(approval)) throw new CoordinatorException(StatusCodes.Status503ServiceUnavailable, SignInErrorCodes.Busy, "Too many changes wait for an approval.");
        await _store.AppendAuditAsync(origin.Audit("sign_in.approval.request", actor, actor.OrganizationId, details: $"{{\"digest\":\"{approval.Digest}\"}}"), cancellationToken)
            .ConfigureAwait(false);
        return ToDto(approval);
    }

    /// <summary>Lists the changes of the company sign-in that wait for an approval of the caller's person.</summary>
    /// <param name="actor">The calling device.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The approvals.</returns>
    /// <exception cref="CoordinatorException">The caller is not an owner's controller device.</exception>
    public async Task<ListResponse<SignInApprovalDto>> ListApprovalsAsync(DeviceRecord actor, CancellationToken cancellationToken)
    {
        var owner = await RequireOwnerAsync(actor, cancellationToken).ConfigureAwait(false);
        return new ListResponse<SignInApprovalDto>([.. _attempts.ListApprovals(owner.Id).Where(approval => approval.OrganizationId == actor.OrganizationId).Select(ToDto)]);
    }

    /// <summary>Reads an approval of the caller's person.</summary>
    /// <param name="actor">The calling device.</param>
    /// <param name="id">The approval.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The approval.</returns>
    /// <exception cref="CoordinatorException">The caller is not an owner's controller device, or no approval of their person has that id.</exception>
    public async Task<SignInApprovalDto> GetApprovalAsync(DeviceRecord actor, string id, CancellationToken cancellationToken)
    {
        var owner = await RequireOwnerAsync(actor, cancellationToken).ConfigureAwait(false);
        return ToDto(RequireApproval(id, owner, actor));
    }

    /// <summary>
    /// Approves a change of the company sign-in with the approval this device signed: another controller device of the person
    /// who asks, which computed the digest of the change from what it was shown.
    /// </summary>
    /// <param name="actor">The approving device.</param>
    /// <param name="id">The approval.</param>
    /// <param name="signed">The signed approval.</param>
    /// <param name="origin">Where it comes from.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The approval, approved.</returns>
    /// <exception cref="CoordinatorException">The caller is not an owner's controller device, is the device that asks, the approval is unknown, or the signed approval does not hold.</exception>
    public async Task<SignInApprovalDto> ApproveAsync(DeviceRecord actor, string id, FleetSignedSignInApproval? signed, RequestOrigin origin, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(origin);
        var owner = await RequireOwnerAsync(actor, cancellationToken).ConfigureAwait(false);
        var approval = RequireApproval(id, owner, actor);
        if (approval.RequesterDeviceId == actor.Id)
            throw CoordinatorException.Forbidden(FleetErrorCodes.Forbidden, "Another of your devices approves the change: the one that asks for it does not.");
        if (signed is null || string.IsNullOrEmpty(signed.Signature) || !FleetSignInApprovals.TryParse(signed.Payload, out var parsed)
            || !FleetDevicePublicKey.TryParse(actor.PublicKey, out var key) || !key.Verify(FleetSignInApprovals.GetSignedBytes(signed.Payload), signed.Signature))
            throw Invalid("The approval is not signed by the calling device.");
        if (parsed.Version != 1 || parsed.OrganizationId != actor.OrganizationId || parsed.ApprovalId != approval.Id || parsed.RequesterDeviceId != approval.RequesterDeviceId
            || parsed.SignerDeviceId != actor.Id || !CryptographicEquals(parsed.Digest, approval.Digest) || (parsed.Time - _time.GetUtcNow()).Duration() > TimeSpan.FromMinutes(5))
            throw Invalid("The approval is not of this change, or not of now.");
        approval.ApprovedBy = actor.Id;
        await _store.AppendAuditAsync(origin.Audit("sign_in.approval.approve", actor, approval.RequesterDeviceId,
            details: $"{{\"digest\":\"{approval.Digest}\",\"signature\":\"{signed.Signature}\"}}"), cancellationToken).ConfigureAwait(false);
        return ToDto(approval);
    }

    /// <summary>
    /// Asks another approved controller device of the caller's person to approve a link of a way to sign in from this
    /// device: the approval waits 10 minutes for <see cref="ApproveLinkAsync"/>, and then works once, for this method.
    /// </summary>
    /// <param name="actor">The calling device, which alone links with the approval.</param>
    /// <param name="request">The method to link.</param>
    /// <param name="origin">Where it comes from.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The approval that waits.</returns>
    /// <exception cref="CoordinatorException">The caller is not a controller device of a person, the method is not offered, or too many wait.</exception>
    public async Task<SignInLinkApprovalDto> RequestLinkApprovalAsync(DeviceRecord actor, RequestLinkApprovalRequest? request, RequestOrigin origin,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(origin);
        var person = await RequirePersonDeviceAsync(actor, cancellationToken).ConfigureAwait(false);
        if (request?.Method is not { } method || !FleetSignInMethods.IsKnown(method)) throw Invalid("The request needs the method to link.");
        RequireMethod(await ReadSettingsAsync(actor.OrganizationId, cancellationToken).ConfigureAwait(false), method, FleetSignInPurposes.Link);
        var approval = new SignInLinkApproval
        {
            Id = SignInAttempts.NewSecret(), OrganizationId = actor.OrganizationId, PersonId = person.Id, RequesterDeviceId = actor.Id, RequesterName = actor.Name, Method = method,
            Digest = FleetSignInApprovals.LinkDigest(actor.OrganizationId, actor.Id, method), ExpiresAt = _time.GetUtcNow() + ConfirmationLifetime,
        };
        if (!_attempts.AddLinkApproval(approval)) throw new CoordinatorException(StatusCodes.Status503ServiceUnavailable, SignInErrorCodes.Busy, "Too many links wait for an approval.");
        await _store.AppendAuditAsync(origin.Audit("sign_in.link_approval.request", actor, person.Id, details: $"{{\"method\":\"{method}\"}}"), cancellationToken)
            .ConfigureAwait(false);
        return ToDto(approval);
    }

    /// <summary>Lists the links that wait for an approval of the caller's person.</summary>
    /// <param name="actor">The calling device.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The approvals.</returns>
    /// <exception cref="CoordinatorException">The caller is not a controller device of a person.</exception>
    public async Task<ListResponse<SignInLinkApprovalDto>> ListLinkApprovalsAsync(DeviceRecord actor, CancellationToken cancellationToken)
    {
        var person = await RequirePersonDeviceAsync(actor, cancellationToken).ConfigureAwait(false);
        return new ListResponse<SignInLinkApprovalDto>([.. _attempts.ListLinkApprovals(person.Id).Where(approval => approval.OrganizationId == actor.OrganizationId).Select(ToDto)]);
    }

    /// <summary>Reads a link approval of the caller's person.</summary>
    /// <param name="actor">The calling device.</param>
    /// <param name="id">The approval.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The approval.</returns>
    /// <exception cref="CoordinatorException">The caller is not a controller device of a person, or no link of their person waits with that id.</exception>
    public async Task<SignInLinkApprovalDto> GetLinkApprovalAsync(DeviceRecord actor, string id, CancellationToken cancellationToken)
    {
        var person = await RequirePersonDeviceAsync(actor, cancellationToken).ConfigureAwait(false);
        return ToDto(RequireLinkApproval(id, person, actor));
    }

    /// <summary>
    /// Approves a link with the approval this device signed: another approved controller device of the person who links,
    /// which computed the digest of the link from what it was shown (the method, the device that links).
    /// </summary>
    /// <param name="actor">The approving device.</param>
    /// <param name="id">The approval.</param>
    /// <param name="signed">The signed approval.</param>
    /// <param name="origin">Where it comes from.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The approval, approved.</returns>
    /// <exception cref="CoordinatorException">The caller is not a controller device of the person, is the device that links, the approval is unknown, or the signed approval does not hold.</exception>
    public async Task<SignInLinkApprovalDto> ApproveLinkAsync(DeviceRecord actor, string id, FleetSignedSignInApproval? signed, RequestOrigin origin,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(origin);
        var person = await RequirePersonDeviceAsync(actor, cancellationToken).ConfigureAwait(false);
        var approval = RequireLinkApproval(id, person, actor);
        if (approval.RequesterDeviceId == actor.Id)
            throw CoordinatorException.Forbidden(FleetErrorCodes.Forbidden, "Another of your devices approves the link: the one that links does not.");
        if (signed is null || string.IsNullOrEmpty(signed.Signature) || !FleetSignInApprovals.TryParse(signed.Payload, out var parsed)
            || !FleetDevicePublicKey.TryParse(actor.PublicKey, out var key) || !key.Verify(FleetSignInApprovals.GetSignedBytes(signed.Payload), signed.Signature))
            throw Invalid("The approval is not signed by the calling device.");
        if (parsed.Version != 1 || parsed.OrganizationId != actor.OrganizationId || parsed.ApprovalId != approval.Id || parsed.RequesterDeviceId != approval.RequesterDeviceId
            || parsed.SignerDeviceId != actor.Id || !CryptographicEquals(parsed.Digest, approval.Digest) || (parsed.Time - _time.GetUtcNow()).Duration() > TimeSpan.FromMinutes(5))
            throw Invalid("The approval is not of this link, or not of now.");
        approval.ApprovedBy = actor.Id;
        await _store.AppendAuditAsync(origin.Audit("sign_in.link_approval.approve", actor, approval.RequesterDeviceId,
            details: $"{{\"method\":\"{approval.Method}\",\"signature\":\"{signed.Signature}\"}}"), cancellationToken).ConfigureAwait(false);
        return ToDto(approval);
    }

    // A link is asked for and approved by approved controller devices of an acting person.
    private async Task<PersonRecord> RequirePersonDeviceAsync(DeviceRecord actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var person = actor.Kind == FleetDeviceKinds.Controller && actor.Status == DeviceStatuses.Approved && actor.OwnerPersonId is not null
            ? await _store.GetPersonAsync(actor.OwnerPersonId, cancellationToken).ConfigureAwait(false)
            : null;
        if (person is null || !person.IsActive || person.OrganizationId != actor.OrganizationId)
            throw CoordinatorException.Forbidden(FleetErrorCodes.Forbidden, "A link is asked for and approved by approved controller devices of its person.");
        return person;
    }

    private SignInLinkApproval RequireLinkApproval(string id, PersonRecord person, DeviceRecord actor)
        => _attempts.FindLinkApproval(id) is { } approval && approval.PersonId == person.Id && approval.OrganizationId == actor.OrganizationId
            ? approval
            : throw CoordinatorException.NotFound(SignInErrorCodes.AttemptInvalid, "No link waits for an approval with this id: it was used, or it expired.");

    private static SignInLinkApprovalDto ToDto(SignInLinkApproval approval)
        => new(approval.Id, approval.ApprovedBy is null ? "pending" : "approved", approval.RequesterDeviceId, approval.RequesterName, approval.Method, approval.Digest,
            approval.ExpiresAt, approval.ApprovedBy);

    private SignInApprovalRequest RequireApproval(string id, PersonRecord owner, DeviceRecord actor)
        => _attempts.FindApproval(id) is { } approval && approval.PersonId == owner.Id && approval.OrganizationId == actor.OrganizationId
            ? approval
            : throw CoordinatorException.NotFound(SignInErrorCodes.AttemptInvalid, "No change waits for an approval with this id: it was used, or it expired.");

    private static SignInApprovalDto ToDto(SignInApprovalRequest approval)
        => new(approval.Id, approval.ApprovedBy is null ? "pending" : "approved", approval.RequesterDeviceId, approval.RequesterName, approval.Change, approval.Digest,
            approval.ExpiresAt, approval.ApprovedBy);

    // What makes a change one of the company sign-in: its provider, its secret, who it lets in, the roles its groups
    // give, whether it is on or required, and the break-glass owner. The other methods are turned on and off without a confirmation.
    private static bool ChangesCompanySignIn(SignInSettingsDocument before, SignInSettingsDocument after, bool secretChanged)
    {
        if (secretChanged || before.RequireSso != after.RequireSso || before.BreakGlassPersonId != after.BreakGlassPersonId
            || before.Enabled.Contains(FleetSignInMethods.Company) != after.Enabled.Contains(FleetSignInMethods.Company)) return true;
        if (before.Company is not { } was || after.Company is not { } now) return before.Company is not null || after.Company is not null;
        return was.Issuer != now.Issuer || was.ClientId != now.ClientId || was.GroupsClaim != now.GroupsClaim || was.DisplayName != now.DisplayName
            || !(was.AllowedDomains ?? []).SequenceEqual(now.AllowedDomains ?? [], StringComparer.Ordinal)
            || !(was.AllowedTenants ?? []).SequenceEqual(now.AllowedTenants ?? [], StringComparer.Ordinal)
            || !(was.GroupRoles ?? []).SequenceEqual(now.GroupRoles ?? []);
    }

    // A tenant named must be the account's; a domain named must be the one of its verified email. Both, when both are named.
    // Settings kept before the owner had to name them let nobody in.
    internal static bool IsAllowed(CompanyDocument? company, ExternalIdentity identity) => Admit(company, identity) == Admission.Allowed;

    // Why an identity is let in or not: a domain counts only from a verified email, so an account of the right tenant whose
    // provider did not confirm its email (Entra ID never does) is refused for that reason, which the refusal then says.
    private static Admission Admit(CompanyDocument? company, ExternalIdentity identity)
    {
        if (company is null) return Admission.NotAllowed;
        var domains = company.AllowedDomains ?? [];
        var tenants = company.AllowedTenants ?? [];
        if (domains.Length == 0 && tenants.Length == 0) return Admission.NotAllowed;
        if (tenants.Length > 0 && (identity.TenantId is not { Length: > 0 } tenant || !tenants.Contains(tenant, StringComparer.OrdinalIgnoreCase))) return Admission.NotAllowed;
        if (domains.Length == 0) return Admission.Allowed;
        if (!identity.EmailVerified || identity.Email is not { } email) return Admission.EmailNotConfirmed;
        var at = email.LastIndexOf('@');
        return at > 0 && at < email.Length - 1 && domains.Contains(email[(at + 1)..], StringComparer.OrdinalIgnoreCase) ? Admission.Allowed : Admission.NotAllowed;
    }

    private enum Admission { Allowed, NotAllowed, EmailNotConfirmed }

    private static string[] ReadAllowed(IReadOnlyList<string>? values, Func<string, bool> valid, string message)
    {
        if (values is null) return [];
        var read = values.Select(static value => value?.Trim().ToLowerInvariant() ?? string.Empty).Where(static value => value.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        if (read.Length > 50 || read.Any(value => !valid(value))) throw Invalid(message);
        return read;
    }

    private static bool IsDomain(string value)
        => value.Length <= 253 && value.Contains('.', StringComparison.Ordinal) && !value.StartsWith('.') && !value.EndsWith('.')
            && value.Split('.').All(static label => label.Length is > 0 and <= 63 && !label.StartsWith('-') && !label.EndsWith('-') && label.All(static c => char.IsAsciiLetterOrDigit(c) || c == '-'));

    private static bool IsTenant(string value) => value.Length <= 64 && value.All(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_');

    private static bool IsEntraId(Uri issuer) => ExternalSignInClient.IsMicrosoftHost(issuer);

    private static bool CryptographicEquals(string left, string right)
        => System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(left), System.Text.Encoding.UTF8.GetBytes(right));

    private static string Truncate(string text) => text.Length <= 128 ? text : text[..128];

    // The groups an owner maps: each once, to a role other than owner, by an id of the claim.
    private static GroupRoleDto[] GroupRoles(IReadOnlyList<GroupRoleDto> given)
    {
        if (given.Count > MaxGroupRoles) throw Invalid($"At most {MaxGroupRoles} groups give roles.");
        var roles = new GroupRoleDto[given.Count];
        for (var index = 0; index < given.Count; index++)
        {
            var group = given[index]?.Group?.Trim();
            if (string.IsNullOrEmpty(group) || group.Length > 256 || group.Any(char.IsControl)) throw Invalid("A group is named by its id in the groups claim.");
            if (given[index].Role is not (PersonRoles.Admin or PersonRoles.Member or FleetOrganizationRoles.Auditor or FleetOrganizationRoles.Guest))
                throw Invalid("A group gives the role admin, member, auditor or guest; never owner.");
            roles[index] = new GroupRoleDto(group, given[index].Role);
        }

        if (roles.Select(static role => role.Group).Distinct(StringComparer.Ordinal).Count() != roles.Length) throw Invalid("A group gives one role.");
        return roles;
    }

    private static IReadOnlyList<string> ReadGroups(string? groups)
        => groups is null ? [] : JsonSerializer.Deserialize(groups, SignInSettingsJsonContext.Default.StringArray) ?? [];

    private sealed record HeldSettings(SignInSettingsDocument Document, string? Secret, long Version, DateTimeOffset? UpdatedAt);
}

/// <summary>The settings of sign-in, as the store keeps them (the client secret apart).</summary>
/// <param name="Enabled">The methods enabled.</param>
/// <param name="Company">The company's provider, or null.</param>
/// <param name="RequireSso">Whether company sign-in is required.</param>
/// <param name="BreakGlassPersonId">The owner who keeps a passkey while it is.</param>
internal sealed record SignInSettingsDocument(string[] Enabled, CompanyDocument? Company, bool RequireSso, string? BreakGlassPersonId);

/// <summary>The company's provider, as the store keeps it.</summary>
/// <param name="Issuer">The issuer.</param>
/// <param name="ClientId">The client id.</param>
/// <param name="DisplayName">Its name for people.</param>
/// <param name="GroupsClaim">The claim of the groups.</param>
/// <param name="GroupRoles">The groups that give a role, or null.</param>
/// <param name="AllowedDomains">The email domains it lets in; null in settings kept before they were named.</param>
/// <param name="AllowedTenants">The tenant ids it lets in; null in settings kept before they were named.</param>
internal sealed record CompanyDocument(string Issuer, string ClientId, string? DisplayName, string GroupsClaim, GroupRoleDto[]? GroupRoles = null, string[]? AllowedDomains = null,
    string[]? AllowedTenants = null);

/// <summary>What the audit log records of a change of the settings, never a secret.</summary>
internal sealed record SignInSettingsAudit(string[] Enabled, string? Issuer, bool RequireSso, string[]? AllowedDomains = null, string[]? AllowedTenants = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SignInSettingsDocument))]
[JsonSerializable(typeof(SignInSettingsAudit))]
[JsonSerializable(typeof(string[]))]
internal sealed partial class SignInSettingsJsonContext : JsonSerializerContext;
