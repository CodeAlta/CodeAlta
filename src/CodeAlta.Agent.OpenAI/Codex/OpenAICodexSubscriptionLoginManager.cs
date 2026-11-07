using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace CodeAlta.Agent.OpenAI.Codex;

internal sealed class OpenAICodexSubscriptionLoginManager(
    IOpenAICodexSubscriptionCredentialStore credentialStore,
    OpenAICodexSubscriptionOAuthClient oauthClient,
    string providerKey)
{
    public async ValueTask<OpenAICodexSubscriptionBrowserLogin> BeginBrowserLoginAsync(string hostId, CancellationToken cancellationToken = default)
    {
        var listener = StartListener();
        try
        {
            return await BeginBrowserLoginAsync(hostId, listener.Prefixes.Single() + "auth/callback", listener, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            listener.Close();
            throw;
        }
    }

    /// <summary>
    /// Begins a sign-in whose browser comes back to another computer (a desktop that signs a node in): no listener here, the
    /// callback that computer forwards completes it (<see cref="CompleteBrowserLoginAsync"/>).
    /// </summary>
    public ValueTask<OpenAICodexSubscriptionBrowserLogin> BeginForwardedBrowserLoginAsync(string hostId, string redirectUri, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(redirectUri);
        // Dynamic registrations permit the loopback port to vary, but not its host/path.
        var redirect = new Uri(redirectUri);
        if (redirect.Host != "127.0.0.1" || redirect.AbsolutePath != CallbackPath)
            throw new ArgumentException($"A ChatGPT sign-in comes back to http://127.0.0.1:<port>{CallbackPath}.", nameof(redirectUri));
        return BeginBrowserLoginAsync(hostId, redirectUri, null, cancellationToken);
    }

    /// <summary>The path a ChatGPT sign-in comes back to on the loopback.</summary>
    public const string CallbackPath = "/auth/callback";

    private async ValueTask<OpenAICodexSubscriptionBrowserLogin> BeginBrowserLoginAsync(string hostId, string redirectUri, HttpListener? listener, CancellationToken cancellationToken)
    {
        var saved = await credentialStore.LoadAsync(providerKey, cancellationToken).ConfigureAwait(false);
        var registration = IsRegistration(saved) || IsPendingRegistration(saved, hostId) ? saved : null;
        var pkce = OpenAICodexSubscriptionOAuthClient.CreatePkce();
        var state = OpenAICodexSubscriptionOAuthClient.CreateState();
        var nonce = OpenAICodexSubscriptionOAuthClient.CreateState();
        return new OpenAICodexSubscriptionBrowserLogin(
            OpenAICodexSubscriptionOAuthClient.BuildAuthorizeUri(pkce, state, nonce, hostId, redirectUri, registration),
            pkce, state, nonce, hostId, redirectUri, registration, listener);
    }

    public async ValueTask<OpenAICodexSubscriptionCredential> CompleteBrowserLoginAsync(
        OpenAICodexSubscriptionBrowserLogin login, Uri callbackUri, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(login);
        ArgumentNullException.ThrowIfNull(callbackUri);
        login.Consume();
        var expected = new Uri(login.RedirectUri);
        if (callbackUri.GetLeftPart(UriPartial.Path) != expected.GetLeftPart(UriPartial.Path) || !string.IsNullOrEmpty(callbackUri.Fragment))
        {
            throw new InvalidOperationException("OAuth callback URI did not match this sign-in attempt.");
        }

        var query = ParseQuery(callbackUri.Query);
        OpenAICodexSubscriptionOAuthClient.ValidateState(login.State, query.GetValueOrDefault("state") ?? string.Empty);
        if (query.ContainsKey("error"))
        {
            throw new InvalidOperationException("ChatGPT authorization was declined or could not be completed. Continue with ChatGPT to try again.");
        }

        var clientId = query.GetValueOrDefault("client_id") ?? login.Registration?.ClientId;
        if (string.IsNullOrWhiteSpace(clientId) || clientId == OpenAICodexSubscriptionOAuthDefaults.ClientId ||
            (login.Registration is not null && clientId != login.Registration.ClientId))
        {
            throw new InvalidOperationException("ChatGPT registration did not return the expected issued client ID.");
        }

        var code = query.GetValueOrDefault("code");
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new InvalidOperationException("OAuth callback did not include an authorization code.");
        }

        OpenAICodexSubscriptionCredential credential;
        try
        {
            credential = await oauthClient.ExchangeAuthorizationCodeAsync(
                code, login.Pkce.Verifier, login.RedirectUri, clientId, login.Nonce, cancellationToken).ConfigureAwait(false);
        }
        catch (OpenAICodexSubscriptionTokenException ex) when (ex.ErrorCode == "invalid_grant" && login.Registration is null)
        {
            // The code cannot be reused, but OpenAI already issued a client ID. Keep
            // only that ID and host, never an unvalidated identity or token set.
            await using var pendingLease = await credentialStore.AcquireLockAsync(providerKey, cancellationToken).ConfigureAwait(false);
            var current = await credentialStore.LoadAsync(providerKey, cancellationToken).ConfigureAwait(false);
            if (!IsRegistration(current) && !IsPendingRegistration(current, login.HostId))
            {
                await credentialStore.SaveAsync(providerKey, new OpenAICodexSubscriptionCredential
                {
                    ClientId = clientId,
                    AgentHostId = login.HostId,
                }, cancellationToken).ConfigureAwait(false);
            }

            throw;
        }

        if (IsRegistration(login.Registration) && credential.Subject != login.Registration.Subject)
        {
            throw new InvalidOperationException("ChatGPT sign-in returned a different account. The saved registration was not changed.");
        }

        credential.AgentHostId = login.HostId;
        await using var lease = await credentialStore.AcquireLockAsync(providerKey, cancellationToken).ConfigureAwait(false);
        await credentialStore.SaveAsync(providerKey, credential, cancellationToken).ConfigureAwait(false);
        return credential;
    }

    public async ValueTask<OpenAICodexSubscriptionCredential> WaitForBrowserCallbackAsync(
        OpenAICodexSubscriptionBrowserLogin login, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        var listener = login.Listener ?? throw new InvalidOperationException("This sign-in comes back to another computer: its callback completes it.");
        return await WaitForBrowserCallbackAsync(listener.GetContextAsync, listener.Stop, login.Dispose,
            (uri, token) => CompleteBrowserLoginAsync(login, uri, token), timeout.Token, cancellationToken).ConfigureAwait(false);
    }

    internal static async ValueTask<OpenAICodexSubscriptionCredential> WaitForBrowserCallbackAsync(
        Func<Task<HttpListenerContext>> getContext,
        Action stopListener,
        Action disposeLogin,
        Func<Uri, CancellationToken, ValueTask<OpenAICodexSubscriptionCredential>> completeLogin,
        CancellationToken timeoutToken,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(getContext);
        ArgumentNullException.ThrowIfNull(stopListener);
        ArgumentNullException.ThrowIfNull(disposeLogin);
        ArgumentNullException.ThrowIfNull(completeLogin);
        try
        {
            while (true)
            {
                timeoutToken.ThrowIfCancellationRequested();
                var contextTask = getContext();
                // Stop cancels the accept itself; WaitAsync would abandon its later fault.
                // Start the accept before registering Stop, and join both before disposal.
                await using var registration = timeoutToken.Register(stopListener).ConfigureAwait(false);
                var context = await contextTask.ConfigureAwait(false);
                if (context.Request.HttpMethod != "GET" || context.Request.Url?.AbsolutePath != "/auth/callback")
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    continue;
                }

                try
                {
                    var credential = await completeLogin(context.Request.Url, timeoutToken).ConfigureAwait(false);
                    await WriteResponseAsync(context.Response, "CodeAlta login complete. You may close this browser tab.", 200).ConfigureAwait(false);
                    return credential;
                }
                catch
                {
                    await WriteResponseAsync(context.Response, "CodeAlta login failed. Return to CodeAlta and try again.", 400).ConfigureAwait(false);
                    throw;
                }
            }
        }
        catch (Exception ex) when (timeoutToken.IsCancellationRequested && ex is HttpListenerException or ObjectDisposedException or OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            throw new TimeoutException("ChatGPT sign-in expired. Continue with ChatGPT to try again.");
        }
        finally
        {
            disposeLogin();
        }
    }

    public async ValueTask<bool> SignOutAsync(CancellationToken cancellationToken = default)
    {
        await using var lease = await credentialStore.AcquireLockAsync(providerKey, cancellationToken).ConfigureAwait(false);
        var credential = await credentialStore.LoadAsync(providerKey, cancellationToken).ConfigureAwait(false);
        if (!IsRegistration(credential))
        {
            await credentialStore.DeleteAsync(providerKey, cancellationToken).ConfigureAwait(false);
            return true;
        }

        var revoked = true;
        try
        {
            await oauthClient.RevokeAsync(credential!, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or OperationCanceledException)
        {
            revoked = false;
        }

        credential!.AccessToken = string.Empty;
        credential.RefreshToken = null;
        credential.IdToken = null;
        credential.ExpiresAt = DateTimeOffset.MinValue;
        // Complete local sign-out even when remote revocation fails or is canceled.
        await credentialStore.SaveAsync(providerKey, credential, CancellationToken.None).ConfigureAwait(false);
        return revoked;
    }

    internal static bool IsRegistration([NotNullWhen(true)] OpenAICodexSubscriptionCredential? credential)
        => HasIssuedClientId(credential) && !string.IsNullOrWhiteSpace(credential.Subject);

    private static bool HasIssuedClientId([NotNullWhen(true)] OpenAICodexSubscriptionCredential? credential)
        => credential is not null && credential.Issuer == OpenAICodexSubscriptionOAuthDefaults.Issuer &&
           !string.IsNullOrWhiteSpace(credential.ClientId) && credential.ClientId != OpenAICodexSubscriptionOAuthDefaults.ClientId;

    private static bool IsPendingRegistration(OpenAICodexSubscriptionCredential? credential, string hostId)
        => HasIssuedClientId(credential) && credential.Subject is null && credential.AgentHostId == hostId &&
           credential.AccessToken == string.Empty && credential.RefreshToken is null && credential.IdToken is null && credential.Scopes.Count == 0;

    private static HttpListener StartListener()
    {
        // Dynamic registrations permit the loopback port to vary, but not its host/path.
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        try
        {
            listener.Start();
            return listener;
        }
        catch
        {
            listener.Close();
            throw;
        }
    }

    private static async Task WriteResponseAsync(HttpListenerResponse response, string message, int status)
    {
        response.StatusCode = status;
        response.ContentType = "text/plain; charset=utf-8";
        response.Headers["Cache-Control"] = "no-store";
        var bytes = Encoding.UTF8.GetBytes(message);
        response.ContentLength64 = bytes.Length;
        response.KeepAlive = false;
        try
        {
            await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        }
        finally
        {
            response.Close();
        }
    }

    private static Dictionary<string, string> ParseQuery(string query)
        => query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(static pair => pair.Split('=', 2))
            .Where(static parts => parts.Length == 2).ToDictionary(static parts => Uri.UnescapeDataString(parts[0]),
                static parts => Uri.UnescapeDataString(parts[1].Replace('+', ' ')), StringComparer.Ordinal);
}

internal sealed class OpenAICodexSubscriptionBrowserLogin(
    Uri authorizeUri, OpenAICodexSubscriptionPkce pkce, string state, string nonce, string hostId,
    string redirectUri, OpenAICodexSubscriptionCredential? registration, HttpListener? listener) : IDisposable
{
    private int _consumed;
    private readonly DateTimeOffset _expiresAt = DateTimeOffset.UtcNow.AddMinutes(10);
    public Uri AuthorizeUri { get; } = authorizeUri;
    public OpenAICodexSubscriptionPkce Pkce { get; } = pkce;
    public string State { get; } = state;
    public string Nonce { get; } = nonce;
    public string HostId { get; } = hostId;
    public string RedirectUri { get; } = redirectUri;
    public OpenAICodexSubscriptionCredential? Registration { get; } = registration;
    /// <summary>Gets the listener the browser comes back to; null for a sign-in whose browser comes back to another computer.</summary>
    public HttpListener? Listener { get; } = listener;

    public void Consume()
    {
        if (Interlocked.Exchange(ref _consumed, 1) != 0 || DateTimeOffset.UtcNow >= _expiresAt)
        {
            throw new InvalidOperationException("ChatGPT sign-in attempt expired or was already used.");
        }
    }

    public void Dispose() => Listener?.Close();
}
