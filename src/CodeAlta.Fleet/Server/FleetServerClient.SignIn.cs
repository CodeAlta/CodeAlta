using System.Buffers.Text;
using System.Net.Http.Json;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;

namespace CodeAlta.Fleet.Server;

// Sign-in (doc/fleet/identity.md): a person proves who they are through the system browser of this device, which comes
// back to its loopback address (or, for GitHub, by entering a code at GitHub while this device polls the server), and
// this device enrolls with the grant the server gives; or links a method to its person.
public sealed partial class FleetServerClient
{
    /// <summary>Reads the ways a person may sign in to a server now; the call needs no device.</summary>
    /// <param name="serverAddress">The address of the server.</param>
    /// <param name="handler">The HTTP handler; null for the usual one.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The methods.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="serverAddress"/> is null.</exception>
    /// <exception cref="FleetServerException">The server refused the call.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public static Task<SignInMethodsResponse> GetSignInMethodsAsync(Uri serverAddress, HttpMessageHandler? handler = null, CancellationToken cancellationToken = default)
        => GetSignInMethodsAsync(serverAddress, handler, null, cancellationToken);

    /// <summary>Reads the ways a person may sign in to a server that may pin its own certificate; the call needs no device.</summary>
    /// <param name="serverAddress">The address of the server.</param>
    /// <param name="handler">The HTTP handler; null for the usual one, or the pinned one.</param>
    /// <param name="certificatePin">The pin of the server's own certificate (<see cref="FleetServerCertificates"/>); null to validate it as usual.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The methods.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="serverAddress"/> is null.</exception>
    /// <exception cref="FleetServerException">The server refused the call.</exception>
    /// <exception cref="HttpRequestException">The server does not answer, or presents another certificate than the pinned one.</exception>
    public static async Task<SignInMethodsResponse> GetSignInMethodsAsync(Uri serverAddress, HttpMessageHandler? handler, string? certificatePin, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(serverAddress);
        using var http = handler is not null ? new HttpClient(handler, disposeHandler: false)
            : certificatePin is not null ? new HttpClient(FleetServerCertificates.CreateHandler(certificatePin), disposeHandler: true)
            : new HttpClient();
        http.Timeout = TimeSpan.FromSeconds(30);
        using var response = await http.GetAsync(new Uri(serverAddress, "api/v1/sign-in/methods"), cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new FleetServerException(response.StatusCode, FleetServerException.HttpErrorCode, null, $"The server answered {(int)response.StatusCode} to the sign-in methods.");
        return await response.Content.ReadFromJsonAsync(FleetSignInJsonContext.Default.SignInMethodsResponse, cancellationToken).ConfigureAwait(false)
            ?? throw new FleetServerException(response.StatusCode, FleetServerException.HttpErrorCode, null, "The server answered no sign-in methods.");
    }

    /// <summary>
    /// Starts a sign-in through the system browser: the server gives the address to open, which comes back to
    /// <paramref name="redirectUri"/>; or, for a method that uses a device code, the page and the code the person enters
    /// there (<see cref="StartSignInResponse.UserCode"/>).
    /// </summary>
    /// <param name="method">The method (<see cref="FleetSignInMethods"/>).</param>
    /// <param name="redirectUri">The loopback address this device listens on; null for a method that uses a device code.</param>
    /// <param name="purpose">Why (<see cref="FleetSignInPurposes"/>): to enroll this device, or to link a method from it.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The attempt, the address to open and the state the browser brings back, or the code to enter.</returns>
    /// <exception cref="ArgumentException">The method is empty, or the redirect is empty for a method that needs one.</exception>
    /// <exception cref="FleetServerException">The server refused: the method is not offered, company sign-in is required, the redirect is not a loopback.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<StartSignInResponse> StartSignInAsync(string method, string? redirectUri, string purpose, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        if (!FleetSignInMethods.UsesDeviceCode(method)) ArgumentException.ThrowIfNullOrWhiteSpace(redirectUri);
        return SendAsync(HttpMethod.Post, "api/v1/sign-in/start", Body(new StartSignInRequest(method, PublicKey, redirectUri, purpose), FleetSignInJsonContext.Default.StartSignInRequest),
            FleetSignInJsonContext.Default.StartSignInResponse, cancellationToken);
    }

    /// <summary>Completes a sign-in with what the browser brought back to the loopback address.</summary>
    /// <param name="attemptId">The attempt.</param>
    /// <param name="code">The code.</param>
    /// <param name="state">The state.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The person, and the grant that enrolls this device.</returns>
    /// <exception cref="FleetServerException">The server refused the sign-in.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<CompleteSignInResponse> CompleteSignInAsync(string attemptId, string code, string state, CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Post, "api/v1/sign-in/complete", Body(new CompleteSignInRequest(attemptId, code, state), FleetSignInJsonContext.Default.CompleteSignInRequest),
            FleetSignInJsonContext.Default.CompleteSignInResponse, cancellationToken);

    /// <summary>Asks the server whether the person entered the code of a sign-in that uses one; wait the interval it answers between two polls.</summary>
    /// <param name="attemptId">The attempt.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Pending with the interval, or what the sign-in gave.</returns>
    /// <exception cref="FleetServerException">The server refused: the person refused, the code expired, the account is linked to nobody.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<PollSignInResponse> PollSignInAsync(string attemptId, CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Post, "api/v1/sign-in/poll", Body(new PollSignInRequest(attemptId), FleetSignInJsonContext.Default.PollSignInRequest),
            FleetSignInJsonContext.Default.PollSignInResponse, cancellationToken);

    /// <summary>Registers this device as a pending controller device of the person who signed in on it, with the grant of that sign-in.</summary>
    /// <param name="grant">The grant (<see cref="CompleteSignInResponse.Grant"/>).</param>
    /// <param name="deviceName">The name of this device.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The pending device.</returns>
    /// <exception cref="ArgumentException">The grant or the name is empty.</exception>
    /// <exception cref="FleetServerException">The grant is used, expired or of another device, or this device is registered already.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<DeviceDto> EnrollWithSignInAsync(string grant, string deviceName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(grant);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceName);
        var enrolled = await SendAsync(HttpMethod.Post, "api/v1/enroll",
            Body(new EnrollRequest(string.Empty, PublicKey, deviceName.Trim(), FleetDeviceKinds.Controller, grant), ServerApiJsonContext.Default.EnrollRequest),
            ServerApiJsonContext.Default.EnrollResponse, cancellationToken).ConfigureAwait(false);
        return enrolled.Device;
    }

    /// <summary>Reads the sign-in settings of the organization; an owner's controller device only.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The settings, without the client secret.</returns>
    /// <exception cref="FleetServerException">The server refused (<c>forbidden</c> for another device).</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<SignInSettingsDto> GetSignInSettingsAsync(CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Get, "api/v1/sign-in/settings", null, FleetSignInJsonContext.Default.SignInSettingsDto, cancellationToken);

    /// <summary>Changes the sign-in settings of the organization; an owner's controller device only, from the version it read.</summary>
    /// <param name="request">The settings.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The settings now.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    /// <exception cref="FleetServerException">The server refused the settings.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<SignInSettingsDto> SetSignInSettingsAsync(SetSignInSettingsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendAsync(HttpMethod.Put, "api/v1/sign-in/settings", Body(request, FleetSignInJsonContext.Default.SetSignInSettingsRequest), FleetSignInJsonContext.Default.SignInSettingsDto,
            cancellationToken);
    }

    /// <summary>
    /// Asks another controller device of this device's person to approve a change of the company sign-in, which this device
    /// then applies with <see cref="SetSignInSettingsAsync"/> naming the approval.
    /// </summary>
    /// <param name="request">The change, as it will be applied.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The approval that waits.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    /// <exception cref="FleetServerException">The server refused.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<SignInApprovalDto> RequestSignInApprovalAsync(SetSignInSettingsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendAsync(HttpMethod.Post, "api/v1/sign-in/settings/approvals", Body(request, FleetSignInJsonContext.Default.SetSignInSettingsRequest),
            FleetSignInJsonContext.Default.SignInApprovalDto, cancellationToken);
    }

    /// <summary>Reads an approval of this device's person.</summary>
    /// <param name="id">The approval.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The approval.</returns>
    /// <exception cref="FleetServerException">The server refused (<c>sign_in.invalid_attempt</c> once used or expired).</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<SignInApprovalDto> GetSignInApprovalAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        return SendAsync(HttpMethod.Get, "api/v1/sign-in/settings/approvals/" + Uri.EscapeDataString(id), null, FleetSignInJsonContext.Default.SignInApprovalDto, cancellationToken);
    }

    /// <summary>Lists the changes of the company sign-in that wait for an approval of this device's person.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The approvals.</returns>
    /// <exception cref="FleetServerException">The server refused.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<IReadOnlyList<SignInApprovalDto>> ListSignInApprovalsAsync(CancellationToken cancellationToken = default)
        => (await SendAsync(HttpMethod.Get, "api/v1/sign-in/settings/approvals", null, FleetSignInJsonContext.Default.ListResponseSignInApprovalDto, cancellationToken)
            .ConfigureAwait(false)).Items;

    /// <summary>
    /// Approves a change another device of this device's person asks for: this device computes the digest of the change
    /// it was shown, refuses one that does not match what the server says, and signs that digest.
    /// </summary>
    /// <param name="approval">The approval, as listed.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The approval, approved.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="approval"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The change does not have the digest the server gives: nothing was signed.</exception>
    /// <exception cref="FleetServerException">The server refused.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<SignInApprovalDto> ApproveSignInChangeAsync(SignInApprovalDto approval, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(approval);
        var digest = FleetSignInApprovals.Digest(approval.Change);
        if (!string.Equals(digest, approval.Digest, StringComparison.Ordinal))
            throw new InvalidOperationException("The change shown is not the one the server would apply: nothing was signed.");
        var organization = await GetOrganizationAsync(cancellationToken).ConfigureAwait(false);
        var payload = FleetSignInApprovals.Serialize(new FleetSignInApproval(1, organization.Id, approval.Id, approval.RequesterDeviceId, digest, DeviceId, Now()));
        var signed = new FleetSignedSignInApproval(payload, Base64Url.EncodeToString(_identity.SignData(FleetSignInApprovals.GetSignedBytes(payload))));
        return await SendAsync(HttpMethod.Post, $"api/v1/sign-in/settings/approvals/{Uri.EscapeDataString(approval.Id)}/approve",
            Body(signed, FleetSignInJsonContext.Default.FleetSignedSignInApproval), FleetSignInJsonContext.Default.SignInApprovalDto, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Lists the sign-in methods and passkeys linked to the person of this device.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The logins.</returns>
    /// <exception cref="FleetServerException">The server refused the call.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<IReadOnlyList<SignInLoginDto>> ListSignInLoginsAsync(CancellationToken cancellationToken = default)
        => (await SendAsync(HttpMethod.Get, "api/v1/sign-in/logins", null, FleetSignInJsonContext.Default.ListResponseSignInLoginDto, cancellationToken).ConfigureAwait(false)).Items;

    /// <summary>
    /// Removes a way this device's person signs in, a linked account or a passkey: it signs nobody in afterwards. Not a
    /// company account (the organization's), nor the last way in of an owner without a recovery key.
    /// </summary>
    /// <param name="loginId">The id of the way in (<see cref="SignInLoginDto.Id"/>).</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The ways the person signs in now.</returns>
    /// <exception cref="ArgumentException"><paramref name="loginId"/> is empty.</exception>
    /// <exception cref="FleetServerException">The server refused (<c>sign_in.last_way_in</c>, <c>sign_in.managed_by_organization</c>, <c>sign_in.login_not_found</c>).</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<IReadOnlyList<SignInLoginDto>> RemoveSignInLoginAsync(string loginId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(loginId);
        return (await SendAsync(HttpMethod.Delete, "api/v1/sign-in/logins/" + Uri.EscapeDataString(loginId), null, FleetSignInJsonContext.Default.ListResponseSignInLoginDto,
            cancellationToken).ConfigureAwait(false)).Items;
    }
}
