using CodeAlta.Desktop.Fleet;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Fleet.Server;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

// Sign-in (doc/fleet/identity.md): the methods of a server, Join an organization through the system browser (or, for
// GitHub, with a code the person enters at GitHub, which the page shows: two calls, no channel), the methods linked to
// this computer's person, and the settings an owner changes. No token reaches the page.
internal sealed partial class FleetServerService
{
    /// <summary>The ways a person may sign in to a server now.</summary>
    [NeoRpcMethod("signInMethods")]
    public async Task<FleetSignInMethodsResult> SignInMethodsAsync(FleetSignInMethodsRequest request, CancellationToken cancellationToken)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused) return new(refused, null, [], false, null);
        if (!FleetServerAddress.TryParse(request!.Url, out var address, out var pin)) return new("invalid_url", null, [], false, null);
        try
        {
            var methods = await _servers!.GetSignInMethodsAsync(address, pin, cancellationToken).ConfigureAwait(false);
            return new("ok", methods.Organization, [.. methods.Methods.Select(static method => new FleetSignInMethodView(method.Id, method.DisplayName, method.BreakGlass))],
                methods.SsoRequired, null);
        }
        catch (Exception exception) when (Failure(exception) is { } failure)
        {
            return new(failure.Status, null, [], false, failure.Message);
        }
    }

    /// <summary>
    /// Starts a sign-in that uses a code (GitHub), to join a server (<c>url</c>) or to link the account from this computer
    /// (<c>linkId</c>): the code the person enters at the provider, and the handle that <c>joinWithSignIn</c> or
    /// <c>linkSignIn</c> continues with and <c>openSignInPage</c> opens the page of.
    /// </summary>
    [NeoRpcMethod("startSignInCode")]
    public async Task<FleetSignInCodeResult> StartSignInCodeAsync(FleetSignInCodeRequest request, CancellationToken cancellationToken)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused) return new(refused, null, null);
        if (!FleetSignInMethods.UsesDeviceCode(request!.Method)) return new("invalid_request", null, null);
        try
        {
            FleetSignInCode code;
            if (request.LinkId is not null)
            {
                if (!FleetServerLinkStore.IsValidId(request.LinkId) || request.Purpose is not (null or FleetSignInPurposes.Link or FleetSignInPurposes.Confirm))
                    return new("invalid_request", null, null);
                code = request.Purpose == FleetSignInPurposes.Confirm
                    ? await _servers!.StartConfirmCodeAsync(request.LinkId, request.Method, cancellationToken).ConfigureAwait(false)
                    : await _servers!.StartLinkCodeAsync(request.LinkId, request.Method, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                if (!FleetServerAddress.TryParse(request.Url, out var address, out var pin)) return new("invalid_url", null, null);
                code = await _servers!.StartJoinCodeAsync(address, request.Method, pin, cancellationToken).ConfigureAwait(false);
            }

            return new("ok", new FleetSignInCodeView(code.Handle, code.UserCode, code.Page.AbsoluteUri, Time(code.ExpiresAt)), null);
        }
        catch (DesktopSignInException exception) { return new(exception.Code, null, exception.Message); }
        catch (InvalidOperationException exception) { return new("already_linked", null, exception.Message); }
        catch (Exception exception) when (Failure(exception) is { } failure) { return new(failure.Status, null, failure.Message); }
    }

    /// <summary>Opens, in the browser of the system, the page where the person enters the code of a sign-in that waits; no other address.</summary>
    [NeoRpcMethod("openSignInPage")]
    public FleetServerActionResult OpenSignInPage(FleetSignInPageRequest request)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused) return new(refused, null);
        return _servers!.OpenSignInPage(request!.Handle) ? new("ok", null) : new("sign_in.browser", null);
    }

    /// <summary>
    /// Joins a server by signing in through the system browser with one of its methods, or, for GitHub, by waiting until
    /// the person entered the code of <c>startSignInCode</c>: this computer then waits, pending, for a device of the
    /// person, their recovery key or the owner.
    /// </summary>
    [NeoRpcMethod("joinWithSignIn")]
    public async Task<FleetSignInJoinResult> JoinWithSignInAsync(FleetSignInJoinRequest request, CancellationToken cancellationToken)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused) return new(refused, null, null, false, null);
        if (!FleetServerAddress.TryParse(request!.Url, out var address, out var pin)) return new("invalid_url", null, null, false, null);
        if (!FleetSignInMethods.IsKnown(request.Method)) return new("invalid_request", null, null, false, null);
        try
        {
            var (link, person) = await _servers!.JoinWithSignInAsync(address, request.Method, request.Handle, pin, cancellationToken).ConfigureAwait(false);
            ChangedLocally?.Invoke();
            return new("ok", Result(link).Server, person.DisplayName, person.HasDevices, null);
        }
        catch (DesktopSignInException exception) { return new(exception.Code, null, null, false, exception.Message); }
        catch (InvalidOperationException exception) { return new("already_linked", null, null, false, exception.Message); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return new("sign_in.cancelled", null, null, false, null); }
        catch (Exception exception) when (Failure(exception) is { } failure) { return new(failure.Status, null, null, false, failure.Message); }
    }

    /// <summary>
    /// Links a sign-in method to the person of this computer at a server, or registers a passkey, through the system
    /// browser; or, for GitHub, waits until the person entered the code of <c>startSignInCode</c>.
    /// </summary>
    [NeoRpcMethod("linkSignIn")]
    public async Task<FleetServerActionResult> LinkSignInAsync(FleetSignInLinkRequest request, CancellationToken cancellationToken)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused) return new(refused, null);
        if (!FleetServerLinkStore.IsValidId(request!.LinkId) || !FleetSignInMethods.IsKnown(request.Method)) return new("invalid_request", null);
        try
        {
            await _servers!.LinkSignInAsync(request.LinkId, request.Method, request.Handle, cancellationToken).ConfigureAwait(false);
            return new("ok", null);
        }
        catch (DesktopSignInException exception) { return new(exception.Code, exception.Message); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return new("sign_in.cancelled", null); }
        catch (Exception exception) when (Failure(exception) is { } failure) { return new(failure.Status, failure.Message); }
    }

    /// <summary>
    /// How people sign in to the organization of a server: the methods linked to this computer's person and, for an owner,
    /// the methods of the organization, its company sign-in and whether it is required; never a secret.
    /// </summary>
    [NeoRpcMethod("signInSettings")]
    public async Task<FleetSignInSettingsResult> SignInSettingsAsync(FleetServerLinkRequest request, CancellationToken cancellationToken)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused) return new(refused, null, [], null);
        if (!FleetServerLinkStore.IsValidId(request!.LinkId)) return new("invalid_request", null, [], null);
        try
        {
            var logins = await _servers!.ListSignInLoginsAsync(request.LinkId, cancellationToken).ConfigureAwait(false);
            FleetSignInSettingsView? settings = null;
            FleetSignInApprovalView[] approvals = [];
            try
            {
                settings = SettingsView(await _servers.GetSignInSettingsAsync(request.LinkId, cancellationToken).ConfigureAwait(false));
                // The changes of the company sign-in another computer of this person asks this one to approve.
                approvals = [.. (await _servers.ListSignInApprovalsAsync(request.LinkId, cancellationToken).ConfigureAwait(false)).Select(ApprovalView)];
            }
            catch (FleetServerException exception) when (exception.Code == FleetErrorCodes.Forbidden)
            {
                // A member sees the methods of their person only.
            }

            return new("ok", settings, [.. logins.Select(static login => new FleetSignInLoginView(login.Method, login.Name, Time(login.CreatedAt),
                login.LastUsedAt is { } used ? Time(used) : null))], null, approvals);
        }
        catch (Exception exception) when (Failure(exception) is { } failure)
        {
            return new(failure.Status, null, [], failure.Message);
        }
    }

    /// <summary>Changes how people sign in to the organization of a server; an owner's computer only.</summary>
    [NeoRpcMethod("setSignInSettings")]
    public async Task<FleetSignInSettingsResult> SetSignInSettingsAsync(FleetSignInSettingsRequest request, CancellationToken cancellationToken)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused) return new(refused, null, [], null);
        if (!FleetServerLinkStore.IsValidId(request!.LinkId) || request.Enabled is not { Length: <= 8 } enabled || enabled.Any(static method => !FleetSignInMethods.IsKnown(method))
            || request.Issuer is { Length: > MaximumText } || request.ClientId is { Length: > 512 } || request.ClientSecret is { Length: > MaximumText }
            || request.DisplayName is { Length: > 128 } || request.GroupsClaim is { Length: > 64 } || request.AllowedDomains is { Length: > 50 }
            || request.AllowedTenants is { Length: > 50 } || request.AllowedDomains?.Any(static value => value is null or { Length: > 253 }) == true
            || request.AllowedTenants?.Any(static value => value is null or { Length: > 64 }) == true
            || request.ConfirmWith is not null && !FleetSignInMethods.IsKnown(request.ConfirmWith) && request.ConfirmWith != ConfirmOnDevice
            || request.GroupRoles is { Length: > 200 } || request.GroupRoles?.Any(static role => role is null || role.Group is not { Length: > 0 and <= 256 } || role.Role is not { Length: <= 16 }) == true)
            return new("invalid_request", null, [], null);
        var company = string.IsNullOrWhiteSpace(request.Issuer) && string.IsNullOrWhiteSpace(request.ClientId)
            ? null
            : new CompanySignInSettings(request.Issuer?.Trim() ?? string.Empty, request.ClientId?.Trim() ?? string.Empty, request.ClientSecret,
                string.IsNullOrWhiteSpace(request.DisplayName) ? null : request.DisplayName.Trim(), string.IsNullOrWhiteSpace(request.GroupsClaim) ? null : request.GroupsClaim.Trim(),
                request.GroupRoles is null ? null : [.. request.GroupRoles.Select(static role => new GroupRoleDto(role.Group.Trim(), role.Role))],
                request.AllowedDomains ?? [], request.AllowedTenants ?? []);
        try
        {
            // A change of the company sign-in asks the person to sign in again first, or another of their computers to approve
            // exactly this change: the page names the way once the server asked.
            var change = new SetSignInSettingsRequest(request.Version, enabled, company, request.RequireSso);
            if (request.ConfirmWith == ConfirmOnDevice)
                change = change with { ApprovalId = await _servers!.ApproveOnAnotherDeviceAsync(request.LinkId, change, cancellationToken).ConfigureAwait(false) };
            else if (request.ConfirmWith is { } method)
                change = change with { Confirmation = await _servers!.ConfirmSignInAsync(request.LinkId, method, request.Handle, cancellationToken).ConfigureAwait(false) };
            var settings = await _servers!.SetSignInSettingsAsync(request.LinkId, change, cancellationToken).ConfigureAwait(false);
            return new("ok", SettingsView(settings), [], null);
        }
        catch (DesktopSignInException exception) { return new(exception.Code, null, [], exception.Message); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return new("sign_in.cancelled", null, [], null); }
        catch (Exception exception) when (Failure(exception) is { } failure)
        {
            return new(failure.Status, null, [], failure.Message);
        }
    }

    /// <summary>
    /// Approves, from this computer, a change of the company sign-in another computer of this person asks for: this computer
    /// checks the change against its digest and signs it.
    /// </summary>
    [NeoRpcMethod("approveSignInChange")]
    public async Task<FleetServerActionResult> ApproveSignInChangeAsync(FleetSignInApprovalRequest request, CancellationToken cancellationToken)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused) return new(refused, null);
        if (!FleetServerLinkStore.IsValidId(request!.LinkId) || request.ApprovalId is not { Length: > 0 and <= 128 }) return new("invalid_request", null);
        try
        {
            await _servers!.ApproveSignInChangeAsync(request.LinkId, request.ApprovalId, cancellationToken).ConfigureAwait(false);
            return new("ok", null);
        }
        catch (InvalidOperationException exception) { return new("sign_in.approval_mismatch", exception.Message); }
        catch (Exception exception) when (Failure(exception) is { } failure) { return new(failure.Status, failure.Message); }
    }

    // The way of confirming a change of the company sign-in by an approval on another computer of the person.
    private const string ConfirmOnDevice = "device";

    // A change to approve as the page shows it: what it sets, never the client secret (only whether it changes).
    private static FleetSignInApprovalView ApprovalView(SignInApprovalDto approval)
        => new(approval.Id, approval.RequesterName ?? approval.RequesterDeviceId, approval.Change.Issuer, approval.Change.ClientId, approval.Change.ClientSecretSha256 is not null,
            [.. approval.Change.AllowedDomains ?? []], [.. approval.Change.AllowedTenants ?? []], approval.Change.RequireSso, [.. approval.Change.EnabledMethods],
            Time(approval.ExpiresAt));

    /// <summary>The sign-in settings as the page shows them: never the client secret, only whether one is set.</summary>
    /// <param name="settings">The settings as the server gives them.</param>
    /// <returns>The view.</returns>
    internal static FleetSignInSettingsView SettingsView(SignInSettingsDto settings)
        => new([.. settings.Methods.Select(static method => new FleetSignInMethodSettingView(method.Id, method.DisplayName, method.Enabled, method.Available))],
            settings.Company?.Issuer, settings.Company?.ClientId, settings.Company?.HasClientSecret ?? false, settings.Company?.DisplayName, settings.Company?.GroupsClaim ?? "groups",
            [.. (settings.Company?.GroupRoles ?? []).Select(static role => new FleetSignInGroupRoleView(role.Group, role.Role))], settings.RequireSso, (int)Math.Min(settings.Version, int.MaxValue), settings.UpdatedAt is { } updated ? Time(updated) : null,
            [.. settings.Company?.AllowedDomains ?? []], [.. settings.Company?.AllowedTenants ?? []]);
}

/// <summary>A request for the sign-in methods of a server.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="Url">The address of the server.</param>
internal sealed record FleetSignInMethodsRequest(string ExpectedEpoch, string Url);

/// <summary>A sign-in method a server offers.</summary>
/// <param name="Id">The method: <c>company</c>, <c>passkey</c>, <c>microsoft</c>, <c>google</c> or <c>github</c>.</param>
/// <param name="Name">Its name, such as the name of the company's provider.</param>
/// <param name="BreakGlass">Whether only the break-glass owner may use it, while company sign-in is required.</param>
internal sealed record FleetSignInMethodView(string Id, string Name, bool BreakGlass);

/// <summary>The sign-in methods of a server.</summary>
/// <param name="Status"><c>ok</c>, <c>invalid_url</c>, <c>unreachable</c>, the code of the server's refusal, <c>stale_epoch</c> or <c>unavailable</c>.</param>
/// <param name="Organization">The name of the organization, once set up.</param>
/// <param name="Methods">The methods offered.</param>
/// <param name="SsoRequired">Whether the organization requires its company sign-in.</param>
/// <param name="Message">What the failure said.</param>
internal sealed record FleetSignInMethodsResult(string Status, string? Organization, FleetSignInMethodView[] Methods, bool SsoRequired, string? Message);

/// <summary>A server to join by signing in.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="Url">The address of the server.</param>
/// <param name="Method">The method.</param>
/// <param name="Handle">The handle of <c>startSignInCode</c>, for GitHub; null otherwise.</param>
internal sealed record FleetSignInJoinRequest(string ExpectedEpoch, string Url, string Method, string? Handle = null);

/// <summary>The outcome of a join by sign-in.</summary>
/// <param name="Status">
/// <c>ok</c>, <c>invalid_url</c>, <c>invalid_request</c>, <c>already_linked</c>, <c>unreachable</c>, <c>sign_in.cancelled</c>,
/// <c>sign_in.browser</c>, <c>sign_in.timeout</c>, the code of the server's refusal (<c>sign_in.*</c>), <c>stale_epoch</c> or <c>unavailable</c>.
/// </param>
/// <param name="Server">The server, once linked (pending).</param>
/// <param name="Person">The name of the person who signed in.</param>
/// <param name="HasDevices">Whether the person has another device, which approves this one; otherwise the owner does.</param>
/// <param name="Message">What the failure said.</param>
internal sealed record FleetSignInJoinResult(string Status, FleetServerView? Server, string? Person, bool HasDevices, string? Message);

/// <summary>A sign-in that uses a code (GitHub) to start: to join a server, or to link the account from this computer.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="Method">The method: <c>github</c>.</param>
/// <param name="Url">The address of the server to join; null for a link.</param>
/// <param name="LinkId">The link whose person links the account; null to join.</param>
/// <param name="Purpose"><c>confirm</c> for a sign-in that confirms a change of the settings; null to link (with a link) or join.</param>
internal sealed record FleetSignInCodeRequest(string ExpectedEpoch, string Method, string? Url, string? LinkId, string? Purpose = null);

/// <summary>The code a person enters at the provider to finish a sign-in (GitHub).</summary>
/// <param name="Handle">What <c>joinWithSignIn</c>, <c>linkSignIn</c> and <c>openSignInPage</c> name it by.</param>
/// <param name="UserCode">The code.</param>
/// <param name="Url">The page where it is entered.</param>
/// <param name="ExpiresAt">When it stops working.</param>
internal sealed record FleetSignInCodeView(string Handle, string UserCode, string Url, string ExpiresAt);

/// <summary>The code of a sign-in that started.</summary>
/// <param name="Status"><c>ok</c>, <c>invalid_url</c>, <c>invalid_request</c>, <c>already_linked</c>, <c>unreachable</c>, <c>sign_in.busy</c>, the code of the server's refusal, <c>stale_epoch</c> or <c>unavailable</c>.</param>
/// <param name="Code">The code, once started.</param>
/// <param name="Message">What the failure said.</param>
internal sealed record FleetSignInCodeResult(string Status, FleetSignInCodeView? Code, string? Message);

/// <summary>The page of a code to open in the browser.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="Handle">The handle of the code.</param>
internal sealed record FleetSignInPageRequest(string ExpectedEpoch, string Handle);

/// <summary>A method to link to this computer's person at a server.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="LinkId">The id of the link.</param>
/// <param name="Method">The method; <c>passkey</c> registers a passkey.</param>
/// <param name="Handle">The handle of <c>startSignInCode</c>, for GitHub; null otherwise.</param>
internal sealed record FleetSignInLinkRequest(string ExpectedEpoch, string LinkId, string Method, string? Handle = null);

/// <summary>A method of the organization as an owner sees it.</summary>
/// <param name="Id">The method.</param>
/// <param name="Name">Its name.</param>
/// <param name="Enabled">Whether it is enabled.</param>
/// <param name="Available">Whether the server can use it.</param>
internal sealed record FleetSignInMethodSettingView(string Id, string Name, bool Enabled, bool Available);

/// <summary>How people sign in to an organization, for its owner.</summary>
/// <param name="Methods">Every method.</param>
/// <param name="Issuer">The issuer of the company's provider, or null.</param>
/// <param name="ClientId">Its client id, or null.</param>
/// <param name="HasClientSecret">Whether a client secret is set.</param>
/// <param name="DisplayName">Its name for people, or null.</param>
/// <param name="GroupsClaim">The claim of the groups.</param>
/// <param name="GroupRoles">The groups that give a role at each company sign-in.</param>
/// <param name="RequireSso">Whether company sign-in is required.</param>
/// <param name="Version">The version, which a change names.</param>
/// <param name="UpdatedAt">When they were last set, or null.</param>
/// <param name="AllowedDomains">The email domains company sign-in lets in.</param>
/// <param name="AllowedTenants">The tenant ids company sign-in lets in.</param>
internal sealed record FleetSignInSettingsView(FleetSignInMethodSettingView[] Methods, string? Issuer, string? ClientId, bool HasClientSecret, string? DisplayName, string GroupsClaim,
    FleetSignInGroupRoleView[] GroupRoles, bool RequireSso, int Version, string? UpdatedAt, string[] AllowedDomains, string[] AllowedTenants);

/// <summary>A group of the company's provider and the role its members get at each company sign-in.</summary>
/// <param name="Group">The id of the group in the groups claim.</param>
/// <param name="Role">The role: <c>admin</c>, <c>member</c>, <c>auditor</c> or <c>guest</c>.</param>
internal sealed record FleetSignInGroupRoleView(string Group, string Role);

/// <summary>A method linked to this computer's person.</summary>
/// <param name="Method">The method.</param>
/// <param name="Name">What it shows: an email, a login, a passkey.</param>
/// <param name="CreatedAt">When it was linked.</param>
/// <param name="LastUsedAt">When it last signed in, or null.</param>
internal sealed record FleetSignInLoginView(string Method, string Name, string CreatedAt, string? LastUsedAt);

/// <summary>How people sign in to an organization.</summary>
/// <param name="Status"><c>ok</c>, the code of the server's refusal, <c>unreachable</c>, <c>not_found</c>, <c>invalid_request</c>, <c>stale_epoch</c> or <c>unavailable</c>.</param>
/// <param name="Settings">The settings of the organization, for an owner; null for a member.</param>
/// <param name="Logins">The methods linked to this computer's person.</param>
/// <param name="Message">What the failure said.</param>
/// <param name="Approvals">For an owner, the changes of the company sign-in other computers of theirs ask this one to approve.</param>
internal sealed record FleetSignInSettingsResult(string Status, FleetSignInSettingsView? Settings, FleetSignInLoginView[] Logins, string? Message,
    FleetSignInApprovalView[]? Approvals = null);

/// <summary>A change of the company sign-in another computer of this person asks this one to approve.</summary>
/// <param name="Id">The approval.</param>
/// <param name="RequestedBy">The computer that asks.</param>
/// <param name="Issuer">The issuer it sets, or null to remove the company sign-in.</param>
/// <param name="ClientId">The client id it sets.</param>
/// <param name="SecretChanges">Whether it sets or removes the client secret.</param>
/// <param name="AllowedDomains">The email domains it lets in.</param>
/// <param name="AllowedTenants">The tenant ids it lets in.</param>
/// <param name="RequireSso">Whether it requires company sign-in.</param>
/// <param name="Enabled">The methods it enables.</param>
/// <param name="ExpiresAt">When it stops waiting.</param>
internal sealed record FleetSignInApprovalView(string Id, string RequestedBy, string? Issuer, string? ClientId, bool SecretChanges, string[] AllowedDomains, string[] AllowedTenants,
    bool RequireSso, string[] Enabled, string ExpiresAt);

/// <summary>A change to approve from this computer.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="LinkId">The link of the server.</param>
/// <param name="ApprovalId">The approval.</param>
internal sealed record FleetSignInApprovalRequest(string ExpectedEpoch, string LinkId, string ApprovalId);

/// <summary>A change of how people sign in to an organization.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="LinkId">The id of the link.</param>
/// <param name="Version">The version the page read.</param>
/// <param name="Enabled">The methods enabled.</param>
/// <param name="Issuer">The issuer of the company's provider; empty with the client id for none.</param>
/// <param name="ClientId">Its client id.</param>
/// <param name="ClientSecret">Its client secret; null keeps the one set, empty removes it.</param>
/// <param name="DisplayName">Its name for people.</param>
/// <param name="GroupsClaim">The claim of the groups; null for <c>groups</c>.</param>
/// <param name="RequireSso">Whether company sign-in is required.</param>
/// <param name="GroupRoles">The groups that give a role at each company sign-in; null keeps the ones set.</param>
/// <param name="AllowedDomains">The email domains company sign-in lets in.</param>
/// <param name="AllowedTenants">The tenant ids company sign-in lets in.</param>
/// <param name="ConfirmWith">
/// The method the person signs in again with to confirm a change of the company sign-in (the server answered
/// <c>sign_in.confirmation_required</c>); null for none.
/// </param>
/// <param name="Handle">The handle of <c>startSignInCode</c> with the purpose <c>confirm</c>, for GitHub; null otherwise.</param>
internal sealed record FleetSignInSettingsRequest(string ExpectedEpoch, string LinkId, int Version, string[] Enabled, string? Issuer, string? ClientId, string? ClientSecret,
    string? DisplayName, string? GroupsClaim, bool RequireSso, FleetSignInGroupRoleView[]? GroupRoles = null, string[]? AllowedDomains = null, string[]? AllowedTenants = null,
    string? ConfirmWith = null, string? Handle = null);
