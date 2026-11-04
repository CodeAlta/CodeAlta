using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Fleet.Server;

namespace CodeAlta.Desktop.Fleet;

/// <summary>A person signed in through the system browser and refused, cancelled or not answered.</summary>
internal sealed class DesktopSignInException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="DesktopSignInException"/> class.</summary>
    /// <param name="code">The stable code: <c>sign_in.cancelled</c>, <c>sign_in.browser</c>, <c>sign_in.timeout</c> or <c>sign_in.invalid_attempt</c>.</param>
    /// <param name="message">What happened.</param>
    public DesktopSignInException(string code, string message) : base(message) => Code = code;

    /// <summary>Gets the stable code.</summary>
    public string Code { get; }
}

/// <summary>The code a person enters at the provider to finish a sign-in that uses one (GitHub).</summary>
/// <param name="Handle">What names the sign-in that waits, to continue it (<see cref="DesktopFleetServers.JoinWithSignInAsync"/>) or open its page.</param>
/// <param name="UserCode">The code.</param>
/// <param name="Page">The page of the provider where it is entered.</param>
/// <param name="ExpiresAt">When it stops working.</param>
internal sealed record FleetSignInCode(string Handle, string UserCode, Uri Page, DateTimeOffset ExpiresAt);

// Sign-in (doc/fleet/identity.md): Join an organization signs a person in through the system browser, with a method the
// server offers, and enrolls this computer with the grant it gives; Settings > Fleet > Server links a method from here
// and changes the methods of an owner's organization. GitHub has no redirect, and takes two calls: the first gets the code
// the page shows (StartJoinCodeAsync, StartLinkCodeAsync), the second polls the server until the person entered it. The
// page sees none of the rest: no token reaches it.
internal sealed partial class DesktopFleetServers
{
    // Sign-ins with a code that wait for their second call, by handle; few at once, each until its code expires.
    private const int MaximumCodes = 8;
    private readonly ConcurrentDictionary<string, PendingCode> _codes = new(StringComparer.Ordinal);

    /// <summary>Gets what opens an address in the browser of the system; tests follow the address themselves.</summary>
    internal Func<Uri, bool> OpenBrowser { get; init; } = static address => DesktopLinks.Open(address.AbsoluteUri);

    /// <summary>Reads the ways a person may sign in to a server now.</summary>
    /// <param name="address">The address of the server.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The methods.</returns>
    /// <exception cref="FleetServerException">The server refused.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<SignInMethodsResponse> GetSignInMethodsAsync(Uri address, CancellationToken cancellationToken)
        => GetSignInMethodsAsync(address, null, cancellationToken);

    /// <summary>Reads the ways a person may sign in to a server that may pin its own certificate.</summary>
    /// <param name="address">The address of the server.</param>
    /// <param name="certificatePin">The pin of the server's own certificate; null for one that validates as usual.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The methods.</returns>
    /// <exception cref="FleetServerException">The server refused.</exception>
    /// <exception cref="HttpRequestException">The server does not answer, or presents another certificate than the pinned one.</exception>
    public Task<SignInMethodsResponse> GetSignInMethodsAsync(Uri address, string? certificatePin, CancellationToken cancellationToken)
        => FleetServerClient.GetSignInMethodsAsync(address, Handler, certificatePin, cancellationToken);

    /// <summary>
    /// Starts a sign-in with a code (GitHub) to join a server: the server asks the provider for the code the person enters;
    /// <see cref="JoinWithSignInAsync"/> with its handle then waits for it.
    /// </summary>
    /// <param name="address">The address of the server.</param>
    /// <param name="method">The method; one that uses a device code.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The code, and its handle.</returns>
    /// <exception cref="ArgumentException">The method does not use a device code.</exception>
    /// <exception cref="InvalidOperationException">This computer is linked to that server already.</exception>
    /// <exception cref="DesktopSignInException">Too many sign-ins wait.</exception>
    /// <exception cref="FleetServerException">The server refused, or gave no usable code.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<FleetSignInCode> StartJoinCodeAsync(Uri address, string method, CancellationToken cancellationToken) => StartJoinCodeAsync(address, method, null, cancellationToken);

    /// <summary>Starts a sign-in with a code (GitHub) to join a server that may pin its own certificate.</summary>
    /// <param name="address">The address of the server.</param>
    /// <param name="method">The method; one that uses a device code.</param>
    /// <param name="certificatePin">The pin of the server's own certificate; null for one that validates as usual.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The code, and its handle.</returns>
    /// <exception cref="ArgumentException">The method does not use a device code.</exception>
    /// <exception cref="InvalidOperationException">This computer is linked to that server already.</exception>
    /// <exception cref="DesktopSignInException">Too many sign-ins wait.</exception>
    /// <exception cref="FleetServerException">The server refused, or gave no usable code.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<FleetSignInCode> StartJoinCodeAsync(Uri address, string method, string? certificatePin, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);
        ObjectDisposedException.ThrowIf(_disposed, this);
        RequireNoLink(address);
        return StartCodeAsync(new FleetServerClient(address, Identity(), Handler, null, certificatePin), FleetServerAddress.Format(address), method, FleetSignInPurposes.Enroll,
            cancellationToken);
    }

    /// <summary>
    /// Starts a sign-in with a code (GitHub) to link the account to the person of this computer at a server;
    /// <see cref="LinkSignInAsync"/> with its handle then waits for it.
    /// </summary>
    /// <param name="linkId">The link.</param>
    /// <param name="method">The method; one that uses a device code.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The code, and its handle.</returns>
    /// <exception cref="ArgumentException">The method does not use a device code.</exception>
    /// <exception cref="KeyNotFoundException">No such link.</exception>
    /// <exception cref="DesktopSignInException">Too many sign-ins wait.</exception>
    /// <exception cref="FleetServerException">The server refused, or gave no usable code.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<FleetSignInCode> StartLinkCodeAsync(string linkId, string method, CancellationToken cancellationToken)
        => StartLinkCodeAsync(linkId, method, null, cancellationToken);

    /// <summary>
    /// Starts a sign-in with a code (GitHub) to link the account, with the confirmation of a sign-in of the person with a way
    /// in they have (<see cref="ConfirmSignInAsync"/>), which a link needs unless it is their first way in.
    /// </summary>
    /// <param name="linkId">The link.</param>
    /// <param name="method">The method; one that uses a device code.</param>
    /// <param name="confirmation">The confirmation; null for a first way in.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The code, and its handle.</returns>
    /// <exception cref="ArgumentException">The method does not use a device code.</exception>
    /// <exception cref="KeyNotFoundException">No such link.</exception>
    /// <exception cref="DesktopSignInException">Too many sign-ins wait.</exception>
    /// <exception cref="FleetServerException">The server refused (<c>sign_in.confirmation_required</c> without a confirmation), or gave no usable code.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<FleetSignInCode> StartLinkCodeAsync(string linkId, string method, string? confirmation, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return StartCodeAsync(Client(linkId), linkId, method, FleetSignInPurposes.Link, cancellationToken, confirmation);
    }

    /// <summary>
    /// Starts a sign-in with a code (GitHub) by which the person of this computer confirms a dangerous action at a server;
    /// <see cref="ConfirmSignInAsync"/> with its handle then waits for it.
    /// </summary>
    /// <param name="linkId">The link.</param>
    /// <param name="method">The method; one that uses a device code.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The code, and its handle.</returns>
    /// <exception cref="ArgumentException">The method does not use a device code.</exception>
    /// <exception cref="KeyNotFoundException">No such link.</exception>
    /// <exception cref="DesktopSignInException">Too many sign-ins wait.</exception>
    /// <exception cref="FleetServerException">The server refused, or gave no usable code.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<FleetSignInCode> StartConfirmCodeAsync(string linkId, string method, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return StartCodeAsync(Client(linkId), linkId, method, FleetSignInPurposes.Confirm, cancellationToken);
    }

    /// <summary>
    /// The person of this computer signs in again at a server with a method linked to them, through the system browser
    /// (or, for a method that uses a code, by entering the code of <paramref name="handle"/>), to confirm a dangerous
    /// action: the single-use confirmation the server gives, which only this computer presents, a few minutes.
    /// </summary>
    /// <param name="linkId">The link.</param>
    /// <param name="method">The method.</param>
    /// <param name="handle">The handle <see cref="StartConfirmCodeAsync"/> gave, for a method that uses a code; null otherwise.</param>
    /// <param name="cancellationToken">Cancels the sign-in.</param>
    /// <returns>The confirmation.</returns>
    /// <exception cref="KeyNotFoundException">No such link.</exception>
    /// <exception cref="DesktopSignInException">The browser did not open, the person cancelled, the code is not one that waits, or the sign-in expired.</exception>
    /// <exception cref="FleetServerException">The server refused (a method not linked to the person, among others).</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<string> ConfirmSignInAsync(string linkId, string method, string? handle, CancellationToken cancellationToken)
    {
        CompleteSignInResponse signedIn;
        if (FleetSignInMethods.UsesDeviceCode(method))
        {
            var pending = TakeCode(handle, linkId, method, FleetSignInPurposes.Confirm);
            try
            {
                signedIn = await WaitForCodeAsync(pending, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Release(pending);
            }
        }
        else
        {
            using var client = Client(linkId);
            signedIn = await SignInThroughBrowserAsync(client, method, FleetSignInPurposes.Confirm, cancellationToken).ConfigureAwait(false);
        }

        return signedIn.Grant ?? throw new FleetServerException(HttpStatusCode.OK, FleetServerException.HttpErrorCode, null, "The server gave no confirmation.");
    }

    /// <summary>Opens, in the browser of the system, the page where the person enters the code of a sign-in that waits.</summary>
    /// <param name="handle">The handle of the code.</param>
    /// <returns>Whether it opened; false for a handle of no sign-in that waits, or when no browser opens.</returns>
    public bool OpenSignInPage(string? handle)
        => handle is not null && _codes.TryGetValue(handle, out var pending) && OpenBrowser(pending.Code.Page);

    /// <summary>
    /// Joins a server by signing in: the system browser opens the sign-in of the method and comes back to a loopback
    /// address of this computer, or, for a method that uses a code, this computer waits until the person entered the code
    /// of <paramref name="handle"/>; the server checks the answer and gives a grant that enrolls this computer as a pending
    /// controller device of the person, which one of their devices, their recovery key or the owner approves.
    /// </summary>
    /// <param name="address">The address of the server.</param>
    /// <param name="method">The method (<see cref="FleetSignInMethods"/>).</param>
    /// <param name="handle">The handle <see cref="StartJoinCodeAsync"/> gave, for a method that uses a code; null otherwise.</param>
    /// <param name="cancellationToken">Cancels the sign-in (the person gave up).</param>
    /// <returns>The link, pending, and the person who signed in.</returns>
    /// <exception cref="InvalidOperationException">This computer is linked to that server already.</exception>
    /// <exception cref="DesktopSignInException">The browser did not open, the person cancelled, the code is not one that waits, or the sign-in expired.</exception>
    /// <exception cref="FleetServerException">The server refused the sign-in or the enrollment.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<(FleetServerLink Link, SignInPersonDto Person)> JoinWithSignInAsync(Uri address, string method, string? handle, CancellationToken cancellationToken)
        => JoinWithSignInAsync(address, method, handle, null, cancellationToken);

    /// <summary>Joins a server that may pin its own certificate by signing in (<see cref="JoinWithSignInAsync(Uri, string, string?, CancellationToken)"/>).</summary>
    /// <param name="address">The address of the server.</param>
    /// <param name="method">The method (<see cref="FleetSignInMethods"/>).</param>
    /// <param name="handle">The handle <see cref="StartJoinCodeAsync(Uri, string, string?, CancellationToken)"/> gave, for a method that uses a code; null otherwise.</param>
    /// <param name="certificatePin">The pin of the server's own certificate, which the link keeps; null for one that validates as usual.</param>
    /// <param name="cancellationToken">Cancels the sign-in (the person gave up).</param>
    /// <returns>The link, pending, and the person who signed in.</returns>
    /// <exception cref="InvalidOperationException">This computer is linked to that server already.</exception>
    /// <exception cref="DesktopSignInException">The browser did not open, the person cancelled, the code is not one that waits, or the sign-in expired.</exception>
    /// <exception cref="FleetServerException">The server refused the sign-in or the enrollment.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<(FleetServerLink Link, SignInPersonDto Person)> JoinWithSignInAsync(Uri address, string method, string? handle, string? certificatePin,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);
        ObjectDisposedException.ThrowIf(_disposed, this);
        RequireNoLink(address);
        var identity = Identity();
        var pending = FleetSignInMethods.UsesDeviceCode(method) ? TakeCode(handle, FleetServerAddress.Format(address), method, FleetSignInPurposes.Enroll) : null;
        using var owned = pending is null ? new FleetServerClient(address, identity, Handler, null, certificatePin) : null;
        var client = pending?.Client ?? owned!;
        try
        {
            // The browser takes minutes: nothing else of the servers waits for it, the gate is taken only to keep the link.
            var signedIn = pending is null
                ? await SignInThroughBrowserAsync(client, method, FleetSignInPurposes.Enroll, cancellationToken).ConfigureAwait(false)
                : await WaitForCodeAsync(pending, cancellationToken).ConfigureAwait(false);
            if (signedIn.Grant is null) throw new FleetServerException(HttpStatusCode.OK, FleetServerException.HttpErrorCode, null, "The server gave no grant to enroll this computer.");
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                RequireNoLink(address);
                var device = await client.EnrollWithSignInAsync(signedIn.Grant, _deviceName, cancellationToken).ConfigureAwait(false);
                var now = DateTimeOffset.UtcNow;
                var link = new FleetServerLink(FleetServerLinkStore.NewId(), FleetServerAddress.Format(address), null, null, identity.DeviceId, device.Kind,
                    FleetServerLinkStates.Pending, now, now, certificatePin ?? pending?.Client.CertificatePin);
                _store.Save(link);
                await EnsureConnectionsAsync(cancellationToken).ConfigureAwait(false);
                return (link, signedIn.Person);
            }
            finally
            {
                _gate.Release();
            }
        }
        finally
        {
            if (pending is not null) Release(pending);
        }
    }

    /// <summary>
    /// Links a sign-in method to the person of this computer at a server, or registers a passkey, through the system
    /// browser; or, for a method that uses a code, waits until the person entered the code of <paramref name="handle"/>.
    /// </summary>
    /// <param name="linkId">The link.</param>
    /// <param name="method">The method.</param>
    /// <param name="handle">The handle <see cref="StartLinkCodeAsync"/> gave, for a method that uses a code; null otherwise.</param>
    /// <param name="cancellationToken">Cancels the sign-in.</param>
    /// <returns>The person it was linked to.</returns>
    /// <exception cref="KeyNotFoundException">No such link.</exception>
    /// <exception cref="DesktopSignInException">The browser did not open, the person cancelled, the code is not one that waits, or the sign-in expired.</exception>
    /// <exception cref="FleetServerException">The server refused.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<SignInPersonDto> LinkSignInAsync(string linkId, string method, string? handle, CancellationToken cancellationToken)
        => LinkSignInAsync(linkId, method, handle, null, cancellationToken);

    /// <summary>
    /// Links a sign-in method as <see cref="LinkSignInAsync(string, string, string?, CancellationToken)"/> does, with the
    /// confirmation of a sign-in of the person with a way in they have, which a link needs unless it is their first way in.
    /// </summary>
    /// <param name="linkId">The link.</param>
    /// <param name="method">The method.</param>
    /// <param name="handle">The handle <see cref="StartLinkCodeAsync(string, string, string?, CancellationToken)"/> gave, for a method that uses a code (which took the confirmation); null otherwise.</param>
    /// <param name="confirmation">The confirmation, for a method through the browser; null for a first way in.</param>
    /// <param name="cancellationToken">Cancels the sign-in.</param>
    /// <returns>The person it was linked to.</returns>
    /// <exception cref="KeyNotFoundException">No such link.</exception>
    /// <exception cref="DesktopSignInException">The browser did not open, the person cancelled, the code is not one that waits, or the sign-in expired.</exception>
    /// <exception cref="FleetServerException">The server refused (<c>sign_in.confirmation_required</c> without a confirmation).</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<SignInPersonDto> LinkSignInAsync(string linkId, string method, string? handle, string? confirmation, CancellationToken cancellationToken)
    {
        if (FleetSignInMethods.UsesDeviceCode(method))
        {
            var pending = TakeCode(handle, linkId, method, FleetSignInPurposes.Link);
            try
            {
                return (await WaitForCodeAsync(pending, cancellationToken).ConfigureAwait(false)).Person;
            }
            finally
            {
                Release(pending);
            }
        }

        using var client = Client(linkId);
        return (await SignInThroughBrowserAsync(client, method, FleetSignInPurposes.Link, cancellationToken, confirmation).ConfigureAwait(false)).Person;
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

    /// <summary>
    /// Asks another computer of this computer's person to approve a change of the company sign-in, and waits until it did
    /// (at most the 10 minutes an approval waits).
    /// </summary>
    /// <param name="linkId">The link.</param>
    /// <param name="change">The change, as it will be applied.</param>
    /// <param name="cancellationToken">Cancels the wait (the person gave up).</param>
    /// <returns>The id of the approval, to apply the change with.</returns>
    /// <exception cref="KeyNotFoundException">No such link.</exception>
    /// <exception cref="DesktopSignInException">The approval did not come before it expired.</exception>
    /// <exception cref="FleetServerException">The server refused.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<string> ApproveOnAnotherDeviceAsync(string linkId, SetSignInSettingsRequest change, CancellationToken cancellationToken)
    {
        using var client = Client(linkId);
        var asked = await client.RequestSignInApprovalAsync(change, cancellationToken).ConfigureAwait(false);
        while (true)
        {
            if (DateTimeOffset.UtcNow >= asked.ExpiresAt) throw new DesktopSignInException("sign_in.timeout", "No other computer approved the change in time: start it again.");
            await Task.Delay(ApprovalPollInterval, cancellationToken).ConfigureAwait(false);
            SignInApprovalDto read;
            try
            {
                read = await client.GetSignInApprovalAsync(asked.Id, cancellationToken).ConfigureAwait(false);
            }
            catch (FleetServerException exception) when (exception.Code == "sign_in.invalid_attempt")
            {
                throw new DesktopSignInException("sign_in.timeout", "No other computer approved the change in time: start it again.");
            }

            if (read.Status == "approved") return asked.Id;
        }
    }

    /// <summary>Gets how often a change that waits for an approval is read again.</summary>
    internal TimeSpan ApprovalPollInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Lists the changes of the company sign-in that wait for an approval of this computer's person at a server.</summary>
    /// <param name="linkId">The link.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The approvals other computers of the person ask for.</returns>
    /// <exception cref="KeyNotFoundException">No such link.</exception>
    /// <exception cref="FleetServerException">The server refused.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<IReadOnlyList<SignInApprovalDto>> ListSignInApprovalsAsync(string linkId, CancellationToken cancellationToken)
    {
        using var client = Client(linkId);
        return [.. (await client.ListSignInApprovalsAsync(cancellationToken).ConfigureAwait(false)).Where(approval => approval.RequesterDeviceId != client.DeviceId && approval.Status == "pending")];
    }

    /// <summary>
    /// Approves from this computer a change of the company sign-in another computer of its person asks for: exactly the
    /// change its page showed, named by the digest this computer computed of it. The server is read again to sign, and a
    /// server that answers another change then (whatever digest it gives) gets nothing signed.
    /// </summary>
    /// <param name="linkId">The link.</param>
    /// <param name="approvalId">The approval.</param>
    /// <param name="shownDigest">The digest of the change the page showed (<see cref="FleetSignInApprovals.Digest"/>).</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes once approved.</returns>
    /// <exception cref="ArgumentException"><paramref name="shownDigest"/> is null or empty.</exception>
    /// <exception cref="KeyNotFoundException">No such link, or no such approval.</exception>
    /// <exception cref="InvalidOperationException">The change the server holds now is not the one shown: nothing was signed.</exception>
    /// <exception cref="FleetServerException">The server refused.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task ApproveSignInChangeAsync(string linkId, string approvalId, string shownDigest, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(shownDigest);
        using var client = Client(linkId);
        var approval = (await client.ListSignInApprovalsAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(approval => approval.Id == approvalId)
            ?? throw new KeyNotFoundException("No change waits for this approval.");
        if (!string.Equals(FleetSignInApprovals.Digest(approval.Change), shownDigest, StringComparison.Ordinal))
            throw new InvalidOperationException("The change the server holds now is not the one shown: nothing was signed.");
        await client.ApproveSignInChangeAsync(approval, cancellationToken).ConfigureAwait(false);
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

    /// <summary>Removes a way the person of this computer signs in at a server: a linked account or a passkey.</summary>
    /// <param name="linkId">The link.</param>
    /// <param name="loginId">The id of the way in.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes once it is removed.</returns>
    /// <exception cref="KeyNotFoundException">No such link.</exception>
    /// <exception cref="FleetServerException">The server refused.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task RemoveSignInLoginAsync(string linkId, string loginId, CancellationToken cancellationToken)
    {
        using var client = Client(linkId);
        await client.RemoveSignInLoginAsync(loginId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CompleteSignInResponse> SignInThroughBrowserAsync(FleetServerClient client, string method, string purpose, CancellationToken cancellationToken,
        string? confirmation = null)
    {
        // A company registers its own client, and Entra ID's Web platform takes only https:// or http://localhost there; the clients
        // CodeAlta registers come back to 127.0.0.1.
        using var redirect = FleetLoopbackRedirect.Start(method == FleetSignInMethods.Company ? FleetLoopbackHost.Localhost : FleetLoopbackHost.IPAddress);
        var start = await client.StartSignInAsync(method, redirect.RedirectUri, purpose, confirmation, cancellationToken).ConfigureAwait(false);
        if (start.State is not { Length: > 0 } state)
            throw new FleetServerException(HttpStatusCode.OK, FleetServerException.HttpErrorCode, null, "The server gave no state for the sign-in.");
        if (!Uri.TryCreate(start.AuthorizationUrl, UriKind.Absolute, out var authorization) || authorization.Scheme is not ("https" or "http") || !OpenBrowser(authorization))
            throw new DesktopSignInException("sign_in.browser", "The browser of this computer did not open the sign-in.");
        using var expiry = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var left = start.ExpiresAt - DateTimeOffset.UtcNow;
        expiry.CancelAfter(left > TimeSpan.Zero ? left : TimeSpan.FromSeconds(1));
        FleetLoopbackAnswer answer;
        try
        {
            answer = await redirect.WaitAsync(state, expiry.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DesktopSignInException("sign_in.timeout", "The sign-in took too long: start it again.");
        }

        if (answer.Error is not null || answer.Code is null)
            throw new DesktopSignInException("sign_in.cancelled", answer.Error == "access_denied" ? "The sign-in was cancelled." : $"The sign-in did not complete: {answer.Error}.");
        return await client.CompleteSignInAsync(start.AttemptId, answer.Code, answer.State!, cancellationToken).ConfigureAwait(false);
    }

    // The first call of a device flow: the server asks the provider for a code, which waits here, with the client of its
    // device key, for the second call. The client is the code's from now on: it is disposed with it.
    private async Task<FleetSignInCode> StartCodeAsync(FleetServerClient client, string target, string method, string purpose, CancellationToken cancellationToken,
        string? confirmation = null)
    {
        try
        {
            if (!FleetSignInMethods.UsesDeviceCode(method)) throw new ArgumentException($"'{method}' does not sign in with a code.", nameof(method));
            Sweep();
            if (_codes.Count >= MaximumCodes) throw new DesktopSignInException("sign_in.busy", "Too many sign-ins wait: finish or cancel one first.");
            var start = await client.StartSignInAsync(method, null, purpose, confirmation, cancellationToken).ConfigureAwait(false);
            if (start.UserCode is not { Length: > 0 and <= 64 } userCode || userCode.Any(static c => c is < '!' or > '~')
                || !Uri.TryCreate(start.AuthorizationUrl, UriKind.Absolute, out var page) || !(page.Scheme == Uri.UriSchemeHttps || page.Scheme == Uri.UriSchemeHttp && page.IsLoopback))
                throw new FleetServerException(HttpStatusCode.OK, FleetServerException.HttpErrorCode, null, "The server gave no code to enter.");
            var code = new FleetSignInCode(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)), userCode, page, start.ExpiresAt);
            _codes[code.Handle] = new PendingCode(client, start, code, target, method, purpose);
            return code;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    // The second call takes the code once, for the server, the method and the purpose it was started for.
    private PendingCode TakeCode(string? handle, string target, string method, string purpose)
    {
        if (handle is null || !_codes.TryGetValue(handle, out var pending) || pending.Target != target || pending.Method != method || pending.Purpose != purpose
            || pending.Code.ExpiresAt <= DateTimeOffset.UtcNow || !pending.TryTake())
            throw new DesktopSignInException("sign_in.invalid_attempt", "This sign-in is not one that waits: start it again.");
        return pending;
    }

    private void Release(PendingCode pending)
    {
        _codes.TryRemove(new KeyValuePair<string, PendingCode>(pending.Code.Handle, pending));
        pending.Client.Dispose();
    }

    // Codes whose second call never came (the page went away between the two) end when they expire.
    private void Sweep()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in _codes)
        {
            if (pair.Value.Code.ExpiresAt <= now && pair.Value.TryTake()) Release(pair.Value);
        }
    }

    // Disposes the codes still waiting, with the host.
    private void DisposeCodes()
    {
        foreach (var pair in _codes)
        {
            if (pair.Value.TryTake()) Release(pair.Value);
        }
    }

    // Polls the server at the interval it answers until the person entered the code, refused it or let it expire.
    private static async Task<CompleteSignInResponse> WaitForCodeAsync(PendingCode pending, CancellationToken cancellationToken)
    {
        using var expiry = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var left = pending.Start.ExpiresAt - DateTimeOffset.UtcNow;
        expiry.CancelAfter(left > TimeSpan.Zero ? left : TimeSpan.FromSeconds(1));
        try
        {
            var interval = pending.Start.PollInterval ?? 5;
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(interval, 1, 60)), expiry.Token).ConfigureAwait(false);
                var poll = await pending.Client.PollSignInAsync(pending.Start.AttemptId, expiry.Token).ConfigureAwait(false);
                if (!poll.Pending)
                    return poll.Completed ?? throw new FleetServerException(HttpStatusCode.OK, FleetServerException.HttpErrorCode, null, "The server ended the sign-in without its result.");
                interval = poll.PollInterval;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DesktopSignInException("sign_in.timeout", "The sign-in took too long: start it again.");
        }
    }

    private sealed class PendingCode(FleetServerClient client, StartSignInResponse start, FleetSignInCode code, string target, string method, string purpose)
    {
        private int _taken;

        public FleetServerClient Client { get; } = client;

        public StartSignInResponse Start { get; } = start;

        public FleetSignInCode Code { get; } = code;

        public string Target { get; } = target;

        public string Method { get; } = method;

        public string Purpose { get; } = purpose;

        public bool TryTake() => Interlocked.Exchange(ref _taken, 1) == 0;
    }
}
