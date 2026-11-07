using ModelContextProtocol.Authentication;

namespace CodeAlta.Plugin.Mcp;

/// <summary>
/// The browser of another computer for an MCP OAuth login (a desktop that signs a node in): given the authorization page and the
/// loopback address the browser comes back to, it returns the callback that computer caught, with its query.
/// </summary>
/// <param name="authorizationUri">The page to open.</param>
/// <param name="redirectUri">The loopback address the browser comes back to, on that computer.</param>
/// <param name="cancellationToken">Cancels the wait.</param>
/// <returns>The callback, or null when the person gave up.</returns>
public delegate Task<Uri?> McpOAuthForwardedAuthorize(Uri authorizationUri, Uri redirectUri, CancellationToken cancellationToken);

// The authorization of a login whose browser is on another computer: the code verifier stays here (the SDK keeps it), and
// only the callback crosses, checked as the loopback listener of this computer checks it.
internal static class McpOAuthForwarding
{
    internal static async Task<AuthorizationResult?> AuthorizeAsync(AuthorizationCallbackContext authorization, McpOAuthForwardedAuthorize forwarded,
        Action<string>? reportStatus, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        var authorizationUri = authorization.AuthorizationUri ?? throw new InvalidOperationException("The MCP authorization gave no page to open.");
        var redirectUri = authorization.RedirectUri ?? throw new InvalidOperationException("The MCP authorization gave no address to come back to.");
        var callback = await forwarded(authorizationUri, redirectUri, cancellationToken).ConfigureAwait(false);
        if (callback is null)
        {
            reportStatus?.Invoke("MCP authorization was cancelled.");
            return null;
        }

        if (!string.Equals(callback.AbsolutePath, redirectUri.AbsolutePath, StringComparison.Ordinal))
        {
            reportStatus?.Invoke("MCP authorization failed: the callback did not come back to the address of the authorization.");
            return null;
        }

        var error = Query(callback, "error");
        var code = Query(callback, "code");
        var state = Query(callback, "state");
        if (!string.IsNullOrWhiteSpace(error) || string.IsNullOrWhiteSpace(code))
        {
            reportStatus?.Invoke("MCP authorization failed: " + McpRedactor.RedactValue(null, error ?? "authorization code was missing"));
            return null;
        }

        var expectedState = Query(authorizationUri, "state");
        if (!string.IsNullOrEmpty(expectedState) && !string.Equals(expectedState, state, StringComparison.Ordinal))
        {
            reportStatus?.Invoke("MCP authorization failed: callback state did not match the authorization request.");
            return null;
        }

        return new AuthorizationResult { Code = code, State = state, Iss = Query(callback, "iss") };
    }

    private static string? Query(Uri address, string name)
    {
        foreach (var pair in address.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            var key = Uri.UnescapeDataString(separator < 0 ? pair : pair[..separator]);
            if (string.Equals(key, name, StringComparison.Ordinal))
                return separator < 0 ? string.Empty : Uri.UnescapeDataString(pair[(separator + 1)..].Replace('+', ' '));
        }

        return null;
    }
}
