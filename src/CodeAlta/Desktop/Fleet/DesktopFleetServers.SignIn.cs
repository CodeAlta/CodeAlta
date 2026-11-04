using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Fleet.Server;

namespace CodeAlta.Desktop.Fleet;

/// <summary>A person signed in through the system browser and refused, cancelled or not answered.</summary>
internal sealed class DesktopSignInException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="DesktopSignInException"/> class.</summary>
    /// <param name="code">The stable code: <c>sign_in.cancelled</c>, <c>sign_in.browser</c> or <c>sign_in.timeout</c>.</param>
    /// <param name="message">What happened.</param>
    public DesktopSignInException(string code, string message) : base(message) => Code = code;

    /// <summary>Gets the stable code.</summary>
    public string Code { get; }
}

// Sign-in (doc/fleet/identity.md): Join an organization signs a person in through the system browser, with a method the
// server offers, and enrolls this computer with the grant it gives; Settings > Fleet > Server links a method from here
// and changes the methods of an owner's organization. The page sees none of it: no token reaches it.
internal sealed partial class DesktopFleetServers
{
    /// <summary>Gets what opens an address in the browser of the system; tests follow the address themselves.</summary>
    internal Func<Uri, bool> OpenBrowser { get; init; } = static address => DesktopLinks.Open(address.AbsoluteUri);

    /// <summary>Reads the ways a person may sign in to a server now.</summary>
    /// <param name="address">The address of the server.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The methods.</returns>
    /// <exception cref="FleetServerException">The server refused.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<SignInMethodsResponse> GetSignInMethodsAsync(Uri address, CancellationToken cancellationToken)
        => FleetServerClient.GetSignInMethodsAsync(address, Handler, cancellationToken);

    /// <summary>
    /// Joins a server by signing in: the system browser opens the sign-in of the method and comes back to a loopback
    /// address of this computer; the server checks the answer and gives a grant that enrolls this computer as a pending
    /// controller device of the person, which one of their devices, their recovery key or the owner approves.
    /// </summary>
    /// <param name="address">The address of the server.</param>
    /// <param name="method">The method (<see cref="FleetSignInMethods"/>).</param>
    /// <param name="cancellationToken">Cancels the sign-in (the person gave up).</param>
    /// <returns>The link, pending, and the person who signed in.</returns>
    /// <exception cref="InvalidOperationException">This computer is linked to that server already.</exception>
    /// <exception cref="DesktopSignInException">The browser did not open, the person cancelled, or the sign-in expired.</exception>
    /// <exception cref="FleetServerException">The server refused the sign-in or the enrollment.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<(FleetServerLink Link, SignInPersonDto Person)> JoinWithSignInAsync(Uri address, string method, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);
        ObjectDisposedException.ThrowIf(_disposed, this);
        RequireNoLink(address);
        var identity = Identity();
        using var client = new FleetServerClient(address, identity, Handler);
        // The browser takes minutes: nothing else of the servers waits for it, the gate is taken only to keep the link.
        var signedIn = await SignInThroughBrowserAsync(client, method, FleetSignInPurposes.Enroll, cancellationToken).ConfigureAwait(false);
        if (signedIn.Grant is null) throw new FleetServerException(System.Net.HttpStatusCode.OK, FleetServerException.HttpErrorCode, null, "The server gave no grant to enroll this computer.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            RequireNoLink(address);
            var device = await client.EnrollWithSignInAsync(signedIn.Grant, _deviceName, cancellationToken).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            var link = new FleetServerLink(FleetServerLinkStore.NewId(), FleetServerAddress.Format(address), null, null, identity.DeviceId, device.Kind,
                FleetServerLinkStates.Pending, now, now);
            _store.Save(link);
            await EnsureConnectionsAsync(cancellationToken).ConfigureAwait(false);
            return (link, signedIn.Person);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Links a sign-in method to the person of this computer at a server, or registers a passkey, through the system browser.</summary>
    /// <param name="linkId">The link.</param>
    /// <param name="method">The method.</param>
    /// <param name="cancellationToken">Cancels the sign-in.</param>
    /// <returns>The person it was linked to.</returns>
    /// <exception cref="KeyNotFoundException">No such link.</exception>
    /// <exception cref="DesktopSignInException">The browser did not open, the person cancelled, or the sign-in expired.</exception>
    /// <exception cref="FleetServerException">The server refused.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<SignInPersonDto> LinkSignInAsync(string linkId, string method, CancellationToken cancellationToken)
    {
        using var client = Client(linkId);
        return (await SignInThroughBrowserAsync(client, method, FleetSignInPurposes.Link, cancellationToken).ConfigureAwait(false)).Person;
    }

    /// <summary>Reads the sign-in settings of the organization of a server; an owner's computer only.</summary>
    /// <param name="linkId">The link.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The settings.</returns>
    /// <exception cref="KeyNotFoundException">No such link.</exception>
    /// <exception cref="FleetServerException">The server refused (<c>forbidden</c> for another device).</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<SignInSettingsDto> GetSignInSettingsAsync(string linkId, CancellationToken cancellationToken)
    {
        using var client = Client(linkId);
        return await client.GetSignInSettingsAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Changes the sign-in settings of the organization of a server; an owner's computer only.</summary>
    /// <param name="linkId">The link.</param>
    /// <param name="request">The settings.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The settings now.</returns>
    /// <exception cref="KeyNotFoundException">No such link.</exception>
    /// <exception cref="FleetServerException">The server refused.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<SignInSettingsDto> SetSignInSettingsAsync(string linkId, SetSignInSettingsRequest request, CancellationToken cancellationToken)
    {
        using var client = Client(linkId);
        return await client.SetSignInSettingsAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Lists the sign-in methods linked to the person of this computer at a server.</summary>
    /// <param name="linkId">The link.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The logins.</returns>
    /// <exception cref="KeyNotFoundException">No such link.</exception>
    /// <exception cref="FleetServerException">The server refused.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<IReadOnlyList<SignInLoginDto>> ListSignInLoginsAsync(string linkId, CancellationToken cancellationToken)
    {
        using var client = Client(linkId);
        return await client.ListSignInLoginsAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<CompleteSignInResponse> SignInThroughBrowserAsync(FleetServerClient client, string method, string purpose, CancellationToken cancellationToken)
    {
        using var redirect = FleetLoopbackRedirect.Start();
        var start = await client.StartSignInAsync(method, redirect.RedirectUri, purpose, cancellationToken).ConfigureAwait(false);
        if (!Uri.TryCreate(start.AuthorizationUrl, UriKind.Absolute, out var authorization) || authorization.Scheme is not ("https" or "http") || !OpenBrowser(authorization))
            throw new DesktopSignInException("sign_in.browser", "The browser of this computer did not open the sign-in.");
        using var expiry = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var left = start.ExpiresAt - DateTimeOffset.UtcNow;
        expiry.CancelAfter(left > TimeSpan.Zero ? left : TimeSpan.FromSeconds(1));
        FleetLoopbackAnswer answer;
        try
        {
            answer = await redirect.WaitAsync(start.State, expiry.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DesktopSignInException("sign_in.timeout", "The sign-in took too long: start it again.");
        }

        if (answer.Error is not null || answer.Code is null)
            throw new DesktopSignInException("sign_in.cancelled", answer.Error == "access_denied" ? "The sign-in was cancelled." : $"The sign-in did not complete: {answer.Error}.");
        return await client.CompleteSignInAsync(start.AttemptId, answer.Code, answer.State!, cancellationToken).ConfigureAwait(false);
    }
}
