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
/// The hook that will give people roles from the groups of their company sign-in (milestone 4,
/// <c>doc/fleet/access-control.md</c>). The groups are stored at every sign-in; today no role follows from them.
/// </summary>
public interface ISignInRoleMapper
{
    /// <summary>Gives the role a person gets from their groups.</summary>
    /// <param name="person">The person.</param>
    /// <param name="groups">The groups of their last company sign-in (ids).</param>
    /// <returns>The role, or null to keep theirs.</returns>
    string? MapRole(PersonRecord person, IReadOnlyList<string> groups);
}

/// <summary>The mapping of milestone 3: groups are kept, roles stay as they are.</summary>
public sealed class KeepRolesMapper : ISignInRoleMapper
{
    /// <inheritdoc />
    public string? MapRole(PersonRecord person, IReadOnlyList<string> groups) => null;
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

    /// <summary>Gets the mapping of groups to roles.</summary>
    public ISignInRoleMapper Roles { get; init; } = new KeepRolesMapper();

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
            var domains = ReadAllowed(given.AllowedDomains, IsDomain, "An allowed domain is not a domain name (contoso.com).");
            var tenants = ReadAllowed(given.AllowedTenants, IsTenant, "An allowed tenant is not a tenant id.");
            // An identity provider serves more than one company: the server lets in only the domains and tenants named.
            if (domains.Length == 0 && tenants.Length == 0) throw Invalid("Company sign-in needs the email domains or the tenant ids it lets in.");
            // Entra ID gives emails it did not verify: only its tenant id proves the company.
            if (IsEntraId(issuer) && tenants.Length == 0) throw Invalid("Company sign-in with Entra ID needs the tenant ids it lets in.");
            company = new CompanyDocument(issuer.AbsoluteUri.TrimEnd('/'), given.ClientId.Trim(), name, claim, domains, tenants);
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
            // The break-glass owner keeps the way in that company sign-in replaces: a passkey.
            if (!enabled.Contains(FleetSignInMethods.Passkey)) enabled = [.. enabled, FleetSignInMethods.Passkey];
        }

        var document = new SignInSettingsDocument(enabled, company, request.RequireSso, request.RequireSso ? breakGlass : null);
        // Changing the company sign-in is a dangerous action: the owner signed in again from this device a few minutes ago.
        // The confirmation is taken last, so that a change refused for another reason does not use it up.
        if (ChangesCompanySignIn(held.Document, document, !string.Equals(secret, held.Secret, StringComparison.Ordinal))
            && _attempts.TakeConfirmation(request.Confirmation, actor.Id)?.PersonId != owner.Id)
        {
            throw CoordinatorException.Forbidden(SignInErrorCodes.ConfirmationRequired,
                "Changing the company sign-in needs you to sign in again: confirm with a passkey or an account linked to you.", "confirm");
        }
        var record = new SignInSettingsRecord(actor.OrganizationId, JsonSerializer.Serialize(document, SignInSettingsJsonContext.Default.SignInSettingsDocument), secret,
            held.Version + 1, _time.GetUtcNow());
        var details = JsonSerializer.Serialize(new SignInSettingsAudit(enabled, company?.Issuer, request.RequireSso, company?.AllowedDomains, company?.AllowedTenants),
            SignInSettingsJsonContext.Default.SignInSettingsAudit);
        if (!await _signIn.SetSignInSettingsAsync(record, origin.Audit("sign_in.settings.set", actor, actor.OrganizationId, details: details), cancellationToken)
                .ConfigureAwait(false))
            throw CoordinatorException.Conflict(SignInErrorCodes.SettingsInvalid, "The sign-in settings changed meanwhile: read them again.");
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
        if (purpose is FleetSignInPurposes.Link or FleetSignInPurposes.Confirm)
        {
            // A link or a confirmation is made by a person on one of their approved controller devices, never by a device the server does not know.
            var actor = await _authenticator.AuthenticateAsync(signed, cancellationToken).ConfigureAwait(false);
            if (actor.Id != key.DeviceId || actor.Kind != FleetDeviceKinds.Controller || actor.OwnerPersonId is null)
                throw CoordinatorException.Forbidden(FleetErrorCodes.Forbidden, "A sign-in method is linked from an approved controller device of its person.");
            linking = actor.OwnerPersonId;
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
                url = new Uri($"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}/sign-in/passkey#{Uri.EscapeDataString(id)}");
            }
            else if (deviceFlow)
            {
                device = await _external.StartDeviceAuthorizationAsync(Provider(request.Method, settings.Document.Company, settings.Secret)!, cancellationToken).ConfigureAwait(false);
                url = device.VerificationUri;
                if (device.ExpiresIn < lifetime) lifetime = device.ExpiresIn;
            }
            else
            {
                url = await _external.BuildAuthorizationUrlAsync(Provider(request.Method, settings.Document.Company, settings.Secret)!, redirect, state, nonce,
                    SignInAttempts.Challenge(verifier), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (ExternalSignInException exception)
        {
            throw new CoordinatorException(StatusCodes.Status502BadGateway, exception.Code, exception.Message);
        }

        var attempt = new SignInAttempt
        {
            Id = id, OrganizationId = organization.Id, Method = request.Method, Purpose = purpose, DeviceId = key.DeviceId, PublicKey = key.Text,
            LinkingPersonId = linking,
            RedirectUri = redirect, State = state, Nonce = nonce, CodeVerifier = verifier,
            ExpiresAt = now + lifetime,
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
        }
        else
        {
            ExternalIdentity identity;
            try
            {
                identity = await _external.RedeemAsync(Provider(attempt.Method, settings.Document.Company, settings.Secret)!, request.Code, attempt.RedirectUri, attempt.CodeVerifier,
                    attempt.Nonce, cancellationToken).ConfigureAwait(false);
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

        // The groups of a company sign-in are kept with its login; milestone 4 applies the role the mapping gives, which keeps roles today.
        if (login is not null) Roles.MapRole(person, ReadGroups(login.Groups));

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
                await _passkeys.CompleteRegistrationAsync(person, request.CredentialJson, state, context,
                    new AuditEntry("passkey.add", AuditEntry.Success, person.Id, attempt.DeviceId, person.Id, origin.SourceAddress, origin.RequestId)).ConfigureAwait(false);
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
    public async Task<ListResponse<SignInLoginDto>> ListLoginsAsync(DeviceRecord actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (actor.OwnerPersonId is null) return new ListResponse<SignInLoginDto>([]);
        var logins = await _signIn.ListLoginsAsync(actor.OwnerPersonId, cancellationToken).ConfigureAwait(false);
        var passkeys = await _signIn.ListPasskeysAsync(actor.OwnerPersonId, cancellationToken).ConfigureAwait(false);
        return new ListResponse<SignInLoginDto>([
            .. logins.Select(static login => new SignInLoginDto(login.Method, login.Name, login.CreatedAt, login.LastSignInAt)),
            .. passkeys.Select(static passkey => new SignInLoginDto(FleetSignInMethods.Passkey, passkey.Name, passkey.CreatedAt, passkey.LastUsedAt)),
        ]);
    }

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
        if (attempt.Method == FleetSignInMethods.Company && !IsAllowed(settings.Document.Company, identity))
            throw await RefuseAsync(origin, deviceId, attempt.Method, SignInErrorCodes.IdentityNotAllowed,
                "This account is not of the domains or tenants your organization lets in.", cancellationToken).ConfigureAwait(false);
        var now = _time.GetUtcNow();
        var groups = identity.Groups.Count == 0 ? null : JsonSerializer.Serialize(identity.Groups.ToArray(), SignInSettingsJsonContext.Default.StringArray);
        var name = Truncate(identity.Email ?? identity.Name ?? identity.Subject);
        var existing = await _signIn.FindLoginAsync(attempt.OrganizationId, identity.Issuer, identity.Subject, cancellationToken).ConfigureAwait(false);
        if (attempt.Purpose == FleetSignInPurposes.Link)
        {
            var person = await _store.GetPersonAsync(attempt.LinkingPersonId!, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("A link without its person.");
            var login = new LoginRecord(attempt.OrganizationId, person.Id, attempt.Method, identity.Issuer, identity.Subject, identity.Email, name, groups, now, now);
            var outcome = await _signIn.LinkLoginAsync(login, new AuditEntry("sign_in.link", AuditEntry.Success, person.Id, deviceId, attempt.Method, origin.SourceAddress,
                origin.RequestId), cancellationToken).ConfigureAwait(false);
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
                ? new CompanySignInDto(company.Issuer, company.ClientId, settings.Secret is not null, company.DisplayName, company.GroupsClaim, company.AllowedDomains ?? [],
                    company.AllowedTenants ?? [])
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
                or SignInErrorCodes.IdentityNotAllowed => StatusCodes.Status403Forbidden,
            SignInErrorCodes.ProviderUnavailable => StatusCodes.Status502BadGateway,
            _ => StatusCodes.Status401Unauthorized,
        };
        return new CoordinatorException(status, code, message);
    }

    private static CoordinatorException Invalid(string message) => CoordinatorException.BadRequest(SignInErrorCodes.SettingsInvalid, message);

    // What makes a change one of the company sign-in: its provider, its secret, who it lets in, whether it is on or
    // required, and the break-glass owner. The other methods are turned on and off without a confirmation.
    private static bool ChangesCompanySignIn(SignInSettingsDocument before, SignInSettingsDocument after, bool secretChanged)
    {
        if (secretChanged || before.RequireSso != after.RequireSso || before.BreakGlassPersonId != after.BreakGlassPersonId
            || before.Enabled.Contains(FleetSignInMethods.Company) != after.Enabled.Contains(FleetSignInMethods.Company)) return true;
        if (before.Company is not { } was || after.Company is not { } now) return before.Company is not null || after.Company is not null;
        return was.Issuer != now.Issuer || was.ClientId != now.ClientId || was.GroupsClaim != now.GroupsClaim || was.DisplayName != now.DisplayName
            || !(was.AllowedDomains ?? []).SequenceEqual(now.AllowedDomains ?? [], StringComparer.Ordinal)
            || !(was.AllowedTenants ?? []).SequenceEqual(now.AllowedTenants ?? [], StringComparer.Ordinal);
    }

    // A tenant named must be the account's; a domain named must be the one of its verified email. Both, when both are named.
    // Settings kept before the owner had to name them let nobody in.
    internal static bool IsAllowed(CompanyDocument? company, ExternalIdentity identity)
    {
        if (company is null) return false;
        var domains = company.AllowedDomains ?? [];
        var tenants = company.AllowedTenants ?? [];
        if (domains.Length == 0 && tenants.Length == 0) return false;
        if (tenants.Length > 0 && (identity.TenantId is not { Length: > 0 } tenant || !tenants.Contains(tenant, StringComparer.OrdinalIgnoreCase))) return false;
        if (domains.Length == 0) return true;
        if (!identity.EmailVerified || identity.Email is not { } email) return false;
        var at = email.LastIndexOf('@');
        return at > 0 && at < email.Length - 1 && domains.Contains(email[(at + 1)..], StringComparer.OrdinalIgnoreCase);
    }

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

    private static bool IsEntraId(Uri issuer) => issuer.Host.Equals("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase)
        || issuer.Host.Equals("login.microsoft.com", StringComparison.OrdinalIgnoreCase) || issuer.Host.Equals("sts.windows.net", StringComparison.OrdinalIgnoreCase);

    private static bool CryptographicEquals(string left, string right)
        => System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(left), System.Text.Encoding.UTF8.GetBytes(right));

    private static string Truncate(string text) => text.Length <= 128 ? text : text[..128];

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
/// <param name="AllowedDomains">The email domains it lets in; null in settings kept before they were named.</param>
/// <param name="AllowedTenants">The tenant ids it lets in; null in settings kept before they were named.</param>
internal sealed record CompanyDocument(string Issuer, string ClientId, string? DisplayName, string GroupsClaim, string[]? AllowedDomains = null, string[]? AllowedTenants = null);

/// <summary>What the audit log records of a change of the settings, never a secret.</summary>
internal sealed record SignInSettingsAudit(string[] Enabled, string? Issuer, bool RequireSso, string[]? AllowedDomains = null, string[]? AllowedTenants = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SignInSettingsDocument))]
[JsonSerializable(typeof(SignInSettingsAudit))]
[JsonSerializable(typeof(string[]))]
internal sealed partial class SignInSettingsJsonContext : JsonSerializerContext;
