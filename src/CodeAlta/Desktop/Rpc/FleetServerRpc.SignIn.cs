using CodeAlta.Desktop.Fleet;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Fleet.Server;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

// Sign-in (doc/fleet/identity.md): the methods of a server, Join an organization through the system browser, the
// methods linked to this computer's person, and the settings an owner changes. No token reaches the page.
internal sealed partial class FleetServerService
{
    /// <summary>The ways a person may sign in to a server now.</summary>
    [NeoRpcMethod("signInMethods")]
    public async Task<FleetSignInMethodsResult> SignInMethodsAsync(FleetSignInMethodsRequest request, CancellationToken cancellationToken)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused) return new(refused, null, [], false, null);
        if (!FleetServerAddress.TryParse(request!.Url, out var address)) return new("invalid_url", null, [], false, null);
        try
        {
            var methods = await _servers!.GetSignInMethodsAsync(address, cancellationToken).ConfigureAwait(false);
            return new("ok", methods.Organization, [.. methods.Methods.Select(static method => new FleetSignInMethodView(method.Id, method.DisplayName, method.BreakGlass))],
                methods.SsoRequired, null);
        }
        catch (Exception exception) when (Failure(exception) is { } failure)
        {
            return new(failure.Status, null, [], false, failure.Message);
        }
    }

    /// <summary>
    /// Joins a server by signing in through the system browser with one of its methods: this computer then waits, pending,
    /// for a device of the person, their recovery key or the owner.
    /// </summary>
    [NeoRpcMethod("joinWithSignIn")]
    public async Task<FleetSignInJoinResult> JoinWithSignInAsync(FleetSignInJoinRequest request, CancellationToken cancellationToken)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused) return new(refused, null, null, false, null);
        if (!FleetServerAddress.TryParse(request!.Url, out var address)) return new("invalid_url", null, null, false, null);
        if (!FleetSignInMethods.IsKnown(request.Method)) return new("invalid_request", null, null, false, null);
        try
        {
            var (link, person) = await _servers!.JoinWithSignInAsync(address, request.Method, cancellationToken).ConfigureAwait(false);
            ChangedLocally?.Invoke();
            return new("ok", Result(link).Server, person.DisplayName, person.HasDevices, null);
        }
        catch (DesktopSignInException exception) { return new(exception.Code, null, null, false, exception.Message); }
        catch (InvalidOperationException exception) { return new("already_linked", null, null, false, exception.Message); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return new("sign_in.cancelled", null, null, false, null); }
        catch (Exception exception) when (Failure(exception) is { } failure) { return new(failure.Status, null, null, false, failure.Message); }
    }

    /// <summary>Links a sign-in method to the person of this computer at a server, or registers a passkey, through the system browser.</summary>
    [NeoRpcMethod("linkSignIn")]
    public async Task<FleetServerActionResult> LinkSignInAsync(FleetSignInLinkRequest request, CancellationToken cancellationToken)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused) return new(refused, null);
        if (!FleetServerLinkStore.IsValidId(request!.LinkId) || !FleetSignInMethods.IsKnown(request.Method)) return new("invalid_request", null);
        try
        {
            await _servers!.LinkSignInAsync(request.LinkId, request.Method, cancellationToken).ConfigureAwait(false);
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
            try
            {
                settings = SettingsView(await _servers.GetSignInSettingsAsync(request.LinkId, cancellationToken).ConfigureAwait(false));
            }
            catch (FleetServerException exception) when (exception.Code == FleetErrorCodes.Forbidden)
            {
                // A member sees the methods of their person only.
            }

            return new("ok", settings, [.. logins.Select(static login => new FleetSignInLoginView(login.Method, login.Name, Time(login.CreatedAt),
                login.LastUsedAt is { } used ? Time(used) : null))], null);
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
            || request.DisplayName is { Length: > 128 } || request.GroupsClaim is { Length: > 64 })
            return new("invalid_request", null, [], null);
        var company = string.IsNullOrWhiteSpace(request.Issuer) && string.IsNullOrWhiteSpace(request.ClientId)
            ? null
            : new CompanySignInSettings(request.Issuer?.Trim() ?? string.Empty, request.ClientId?.Trim() ?? string.Empty, request.ClientSecret,
                string.IsNullOrWhiteSpace(request.DisplayName) ? null : request.DisplayName.Trim(), string.IsNullOrWhiteSpace(request.GroupsClaim) ? null : request.GroupsClaim.Trim());
        try
        {
            var settings = await _servers!.SetSignInSettingsAsync(request.LinkId, new SetSignInSettingsRequest(request.Version, enabled, company, request.RequireSso),
                cancellationToken).ConfigureAwait(false);
            return new("ok", SettingsView(settings), [], null);
        }
        catch (Exception exception) when (Failure(exception) is { } failure)
        {
            return new(failure.Status, null, [], failure.Message);
        }
    }

    /// <summary>The sign-in settings as the page shows them: never the client secret, only whether one is set.</summary>
    /// <param name="settings">The settings as the server gives them.</param>
    /// <returns>The view.</returns>
    internal static FleetSignInSettingsView SettingsView(SignInSettingsDto settings)
        => new([.. settings.Methods.Select(static method => new FleetSignInMethodSettingView(method.Id, method.DisplayName, method.Enabled, method.Available))],
            settings.Company?.Issuer, settings.Company?.ClientId, settings.Company?.HasClientSecret ?? false, settings.Company?.DisplayName, settings.Company?.GroupsClaim ?? "groups",
            settings.RequireSso, (int)Math.Min(settings.Version, int.MaxValue), settings.UpdatedAt is { } updated ? Time(updated) : null);
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
internal sealed record FleetSignInJoinRequest(string ExpectedEpoch, string Url, string Method);

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

/// <summary>A method to link to this computer's person at a server.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="LinkId">The id of the link.</param>
/// <param name="Method">The method; <c>passkey</c> registers a passkey.</param>
internal sealed record FleetSignInLinkRequest(string ExpectedEpoch, string LinkId, string Method);

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
/// <param name="RequireSso">Whether company sign-in is required.</param>
/// <param name="Version">The version, which a change names.</param>
/// <param name="UpdatedAt">When they were last set, or null.</param>
internal sealed record FleetSignInSettingsView(FleetSignInMethodSettingView[] Methods, string? Issuer, string? ClientId, bool HasClientSecret, string? DisplayName, string GroupsClaim,
    bool RequireSso, int Version, string? UpdatedAt);

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
internal sealed record FleetSignInSettingsResult(string Status, FleetSignInSettingsView? Settings, FleetSignInLoginView[] Logins, string? Message);

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
internal sealed record FleetSignInSettingsRequest(string ExpectedEpoch, string LinkId, int Version, string[] Enabled, string? Issuer, string? ClientId, string? ClientSecret,
    string? DisplayName, string? GroupsClaim, bool RequireSso);
