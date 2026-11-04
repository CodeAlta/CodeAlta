using System.Net.Http.Json;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;

namespace CodeAlta.Fleet.Server;

// Sign-in (doc/fleet/identity.md): a person proves who they are through the system browser of this device, which comes
// back to its loopback address, and this device enrolls with the grant the server gives; or links a method to its person.
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
    public static async Task<SignInMethodsResponse> GetSignInMethodsAsync(Uri serverAddress, HttpMessageHandler? handler = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serverAddress);
        using var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.Timeout = TimeSpan.FromSeconds(30);
        using var response = await http.GetAsync(new Uri(serverAddress, "api/v1/sign-in/methods"), cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new FleetServerException(response.StatusCode, FleetServerException.HttpErrorCode, null, $"The server answered {(int)response.StatusCode} to the sign-in methods.");
        return await response.Content.ReadFromJsonAsync(FleetSignInJsonContext.Default.SignInMethodsResponse, cancellationToken).ConfigureAwait(false)
            ?? throw new FleetServerException(response.StatusCode, FleetServerException.HttpErrorCode, null, "The server answered no sign-in methods.");
    }

    /// <summary>Starts a sign-in through the system browser: the server gives the address to open, which comes back to <paramref name="redirectUri"/>.</summary>
    /// <param name="method">The method (<see cref="FleetSignInMethods"/>).</param>
    /// <param name="redirectUri">The loopback address this device listens on.</param>
    /// <param name="purpose">Why (<see cref="FleetSignInPurposes"/>): to enroll this device, or to link a method from it.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The attempt, the address to open and the state the browser brings back.</returns>
    /// <exception cref="ArgumentException">The method or the redirect is empty.</exception>
    /// <exception cref="FleetServerException">The server refused: the method is not offered, company sign-in is required, the redirect is not a loopback.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<StartSignInResponse> StartSignInAsync(string method, string redirectUri, string purpose, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentException.ThrowIfNullOrWhiteSpace(redirectUri);
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

    /// <summary>Lists the sign-in methods and passkeys linked to the person of this device.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The logins.</returns>
    /// <exception cref="FleetServerException">The server refused the call.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<IReadOnlyList<SignInLoginDto>> ListSignInLoginsAsync(CancellationToken cancellationToken = default)
        => (await SendAsync(HttpMethod.Get, "api/v1/sign-in/logins", null, FleetSignInJsonContext.Default.ListResponseSignInLoginDto, cancellationToken).ConfigureAwait(false)).Items;
}
