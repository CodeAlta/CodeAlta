using System.Text.Json.Serialization;

namespace CodeAlta.Fleet.Protocol.Api;

/// <summary>
/// The ways a person signs in to a server (<c>doc/fleet/identity.md</c>, linked sign-in methods). A sign-in grants
/// nothing by itself: it lets the person enroll a new device of theirs, which a device of theirs, their recovery key or
/// the owner then signs into the device list.
/// </summary>
public static class FleetSignInMethods
{
    /// <summary>A passkey (WebAuthn) registered at the server, through the server's own sign-in page.</summary>
    public const string Passkey = "passkey";

    /// <summary>A Microsoft account, personal or work, through the public client of the CodeAlta project.</summary>
    public const string Microsoft = "microsoft";

    /// <summary>A Google account, through the public client of the CodeAlta project.</summary>
    public const string Google = "google";

    /// <summary>
    /// A GitHub account, through the device flow of the CodeAlta GitHub App: the person enters a code at GitHub, the
    /// server asks GitHub until it answers; no redirect and no client secret.
    /// </summary>
    public const string GitHub = "github";

    /// <summary>The OpenID Connect provider of the company, which the owner configures.</summary>
    public const string Company = "company";

    /// <summary>Gets every method, in the order a sign-in page shows them.</summary>
    public static IReadOnlyList<string> All { get; } = [Company, Passkey, Microsoft, Google, GitHub];

    /// <summary>Gets whether a text is a sign-in method.</summary>
    /// <param name="method">The text.</param>
    /// <returns>Whether it is one of <see cref="All"/>.</returns>
    public static bool IsKnown(string? method) => method is Passkey or Microsoft or Google or GitHub or Company;

    /// <summary>Gets whether a method signs in with a code the person enters at the provider (a device flow), rather than through a redirect.</summary>
    /// <param name="method">The method.</param>
    /// <returns>Whether the device polls the server (<c>POST /api/v1/sign-in/poll</c>) instead of listening on a loopback address.</returns>
    public static bool UsesDeviceCode(string? method) => method is GitHub;
}

/// <summary>Why a sign-in starts.</summary>
public static class FleetSignInPurposes
{
    /// <summary>A person signs in on a new device to enroll it: the request is signed by the key of the new device.</summary>
    public const string Enroll = "enroll";

    /// <summary>A person links a sign-in method (or registers a passkey) from an approved controller device of theirs.</summary>
    public const string Link = "link";
}

/// <summary>A sign-in method as a server offers it.</summary>
/// <param name="Id">The method (<see cref="FleetSignInMethods"/>).</param>
/// <param name="DisplayName">Its name for people, such as the name of the company's provider.</param>
/// <param name="Enabled">Whether the owner enabled it.</param>
/// <param name="Available">Whether the server can use it: a public client id is registered, or the company's provider is configured.</param>
/// <param name="BreakGlass">Whether it stays only for the break-glass owner, while the organization requires company sign-in.</param>
public sealed record SignInMethodDto(string Id, string DisplayName, bool Enabled, bool Available, bool BreakGlass = false);

/// <summary>The answer of <c>GET /api/v1/sign-in/methods</c>, which needs no device.</summary>
/// <param name="Organization">The name of the organization, or null while the server has no owner.</param>
/// <param name="Methods">The methods a person may sign in with now: enabled and available.</param>
/// <param name="SsoRequired">Whether the organization requires its company sign-in.</param>
public sealed record SignInMethodsResponse(string? Organization, IReadOnlyList<SignInMethodDto> Methods, bool SsoRequired);

/// <summary>
/// The body of <c>POST /api/v1/sign-in/start</c>, signed with the key it names: the server makes the state, the nonce
/// and the PKCE verifier of an authorization request whose answer comes back to the loopback address of the device; or,
/// for a method that uses a device code (<see cref="FleetSignInMethods.UsesDeviceCode"/>), asks the provider for the
/// code the person enters, and keeps the provider's device code to itself.
/// </summary>
/// <param name="Method">The method (<see cref="FleetSignInMethods"/>).</param>
/// <param name="PublicKey">The public key of the device that signs in: the new device to enroll, or the approved device that links.</param>
/// <param name="RedirectUri">
/// The loopback address the browser comes back to: <c>http://127.0.0.1:&lt;port&gt;/&lt;path&gt;</c>; null for a method
/// that uses a device code.
/// </param>
/// <param name="Purpose">Why (<see cref="FleetSignInPurposes"/>); <see cref="FleetSignInPurposes.Enroll"/> when absent.</param>
public sealed record StartSignInRequest(string Method, string PublicKey, string? RedirectUri, string? Purpose = null);

/// <summary>A sign-in that started.</summary>
/// <param name="AttemptId">The attempt, which only its device completes.</param>
/// <param name="AuthorizationUrl">The address to open in the system browser: the provider's sign-in, or the page where the person enters <paramref name="UserCode"/>.</param>
/// <param name="State">The state the browser brings back with the code, which the device checks; null for a device code.</param>
/// <param name="ExpiresAt">When the attempt stops working.</param>
/// <param name="UserCode">The code the person enters at <paramref name="AuthorizationUrl"/>, for a method that uses a device code; null otherwise.</param>
/// <param name="PollInterval">How many seconds the device waits between two polls, for a device code; null otherwise.</param>
public sealed record StartSignInResponse(string AttemptId, string AuthorizationUrl, string? State, DateTimeOffset ExpiresAt, string? UserCode = null, int? PollInterval = null);

/// <summary>The body of <c>POST /api/v1/sign-in/complete</c>, signed by the key that started the attempt.</summary>
/// <param name="AttemptId">The attempt.</param>
/// <param name="Code">The code the browser brought back to the loopback address.</param>
/// <param name="State">The state it brought back.</param>
public sealed record CompleteSignInRequest(string AttemptId, string Code, string State);

/// <summary>The person a sign-in proved.</summary>
/// <param name="Id">The person.</param>
/// <param name="DisplayName">Their name.</param>
/// <param name="Email">The email the method gave, or null.</param>
/// <param name="HasDevices">Whether they have an approved controller device, which approves the new one; otherwise the owner does.</param>
public sealed record SignInPersonDto(string Id, string DisplayName, string? Email, bool HasDevices);

/// <summary>What a sign-in gave.</summary>
/// <param name="Method">The method.</param>
/// <param name="Person">The person.</param>
/// <param name="Grant">
/// For an enrollment, the single-use grant that lets the device of the attempt enroll as a controller device of the
/// person (<see cref="EnrollRequest.SignInGrant"/>), valid a few minutes; null for a link.
/// </param>
/// <param name="GrantExpiresAt">When the grant stops working; null for a link.</param>
/// <param name="Linked">Whether a method was linked to the person (or a passkey registered).</param>
public sealed record CompleteSignInResponse(string Method, SignInPersonDto Person, string? Grant, DateTimeOffset? GrantExpiresAt, bool Linked);

/// <summary>
/// The body of <c>POST /api/v1/sign-in/poll</c>, signed by the key that started an attempt with a device code: the server
/// asks the provider whether the person entered the code, at most once per interval.
/// </summary>
/// <param name="AttemptId">The attempt.</param>
public sealed record PollSignInRequest(string AttemptId);

/// <summary>The answer of a poll: still waiting for the person, or what the sign-in gave, once.</summary>
/// <param name="Pending">Whether the person has not entered the code yet.</param>
/// <param name="PollInterval">How many seconds the device waits before the next poll.</param>
/// <param name="Completed">What the sign-in gave, once it is done; null while it waits.</param>
public sealed record PollSignInResponse(bool Pending, int PollInterval, CompleteSignInResponse? Completed);

/// <summary>The body of <c>POST /api/v1/sign-in/passkey/options</c>, sent by the server's sign-in page.</summary>
/// <param name="AttemptId">The attempt the page was opened for.</param>
public sealed record PasskeyOptionsRequest(string AttemptId);

/// <summary>The options of a passkey ceremony, as the browser takes them.</summary>
/// <param name="Ceremony"><c>create</c> to register a passkey, <c>get</c> to sign in with one.</param>
/// <param name="OptionsJson">The JSON of <c>PublicKeyCredentialCreationOptions</c> or <c>PublicKeyCredentialRequestOptions</c>.</param>
public sealed record PasskeyOptionsResponse(string Ceremony, string OptionsJson);

/// <summary>The body of <c>POST /api/v1/sign-in/passkey/finish</c>: what the browser's ceremony answered.</summary>
/// <param name="AttemptId">The attempt.</param>
/// <param name="CredentialJson">The JSON of the credential (<c>PublicKeyCredential.toJSON()</c>).</param>
public sealed record PasskeyFinishRequest(string AttemptId, string CredentialJson);

/// <summary>Where the page sends the browser once the ceremony is done: the loopback address of the device, with a code and the state.</summary>
/// <param name="RedirectUrl">The address.</param>
public sealed record PasskeyFinishResponse(string RedirectUrl);

/// <summary>The company's OpenID Connect provider, as the server shows it (never its secret).</summary>
/// <param name="Issuer">The issuer, whose discovery document the server reads.</param>
/// <param name="ClientId">The client id the owner registered at the provider.</param>
/// <param name="HasClientSecret">Whether a client secret is set; the server never gives it back.</param>
/// <param name="DisplayName">The name of the provider for people, such as "Contoso".</param>
/// <param name="GroupsClaim">The claim of the ID token that holds the groups (<c>groups</c> or <c>roles</c>).</param>
/// <param name="GroupRoles">The groups that give a role at each company sign-in; empty when roles are set by hand only.</param>
public sealed record CompanySignInDto(string Issuer, string ClientId, bool HasClientSecret, string? DisplayName, string GroupsClaim,
    IReadOnlyList<GroupRoleDto>? GroupRoles = null);

/// <summary>
/// A group of the company's provider and the organization role its members get at each company sign-in (<c>doc/fleet/identity.md</c>,
/// company SSO): by the group's id, as the groups claim gives it. Never the owner role.
/// </summary>
/// <param name="Group">The id of the group (or the app role) in the claim.</param>
/// <param name="Role">The role: <c>admin</c>, <c>member</c>, <c>auditor</c> or <c>guest</c>.</param>
public sealed record GroupRoleDto(string Group, string Role);

/// <summary>The sign-in settings of the organization (<c>GET /api/v1/sign-in/settings</c>), for an owner's controller device.</summary>
/// <param name="Methods">Every method, with whether it is enabled and available.</param>
/// <param name="Company">The company's provider, or null when none is configured.</param>
/// <param name="RequireSso">Whether people sign in with the company's provider only.</param>
/// <param name="BreakGlassPersonId">The owner who keeps a passkey while company sign-in is required.</param>
/// <param name="Version">The version of the settings, which the next change names.</param>
/// <param name="UpdatedAt">When they were last set, or null for the defaults.</param>
public sealed record SignInSettingsDto(IReadOnlyList<SignInMethodDto> Methods, CompanySignInDto? Company, bool RequireSso, string? BreakGlassPersonId, long Version,
    DateTimeOffset? UpdatedAt);

/// <summary>The company's provider in a change of the settings.</summary>
/// <param name="Issuer">The issuer: an <c>https://</c> address.</param>
/// <param name="ClientId">The client id.</param>
/// <param name="ClientSecret">The client secret; null keeps the one set, empty removes it (a public client with PKCE).</param>
/// <param name="DisplayName">The name for people, or null.</param>
/// <param name="GroupsClaim">The claim of the groups; null for <c>groups</c>.</param>
/// <param name="GroupRoles">The groups that give a role at each company sign-in; null keeps the ones set, empty removes them.</param>
public sealed record CompanySignInSettings(string Issuer, string ClientId, string? ClientSecret = null, string? DisplayName = null, string? GroupsClaim = null,
    IReadOnlyList<GroupRoleDto>? GroupRoles = null);

/// <summary>The body of <c>PUT /api/v1/sign-in/settings</c>, sent by an owner's controller device.</summary>
/// <param name="Version">The version it changes (<see cref="SignInSettingsDto.Version"/>).</param>
/// <param name="EnabledMethods">The methods enabled.</param>
/// <param name="Company">The company's provider; null removes it.</param>
/// <param name="RequireSso">Whether people sign in with the company's provider only (it must be configured and enabled).</param>
/// <param name="BreakGlassPersonId">The owner who keeps a passkey while company sign-in is required; null for the caller's person.</param>
public sealed record SetSignInSettingsRequest(long Version, IReadOnlyList<string> EnabledMethods, CompanySignInSettings? Company, bool RequireSso,
    string? BreakGlassPersonId = null);

/// <summary>A sign-in method linked to a person.</summary>
/// <param name="Method">The method.</param>
/// <param name="Name">What the person sees of it: an email, a GitHub login, the name of a passkey.</param>
/// <param name="CreatedAt">When it was linked.</param>
/// <param name="LastUsedAt">When it last signed in, or null.</param>
public sealed record SignInLoginDto(string Method, string Name, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt);

/// <summary>The source-generated contracts of sign-in.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SignInMethodsResponse))]
[JsonSerializable(typeof(StartSignInRequest))]
[JsonSerializable(typeof(StartSignInResponse))]
[JsonSerializable(typeof(CompleteSignInRequest))]
[JsonSerializable(typeof(CompleteSignInResponse))]
[JsonSerializable(typeof(PollSignInRequest))]
[JsonSerializable(typeof(PollSignInResponse))]
[JsonSerializable(typeof(PasskeyOptionsRequest))]
[JsonSerializable(typeof(PasskeyOptionsResponse))]
[JsonSerializable(typeof(PasskeyFinishRequest))]
[JsonSerializable(typeof(PasskeyFinishResponse))]
[JsonSerializable(typeof(SignInSettingsDto))]
[JsonSerializable(typeof(SetSignInSettingsRequest))]
[JsonSerializable(typeof(ListResponse<SignInLoginDto>))]
public sealed partial class FleetSignInJsonContext : JsonSerializerContext;
