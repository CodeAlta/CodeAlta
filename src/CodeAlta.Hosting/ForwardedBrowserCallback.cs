namespace CodeAlta.Hosting;

/// <summary>
/// The browser of another computer, for a sign-in that runs here (on a node) while the person signs in there (on their desktop):
/// the sign-in sends the browser back to a loopback address of that computer, which catches the callback and forwards it here.
/// The code verifier and the state of the sign-in stay in this process, so that the callback is worth nothing elsewhere; the
/// exchange of the code, and the tokens, happen here only.
/// </summary>
public sealed class ForwardedBrowserCallback
{
    private readonly TaskCompletionSource<Uri> _callback = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Initializes a new instance of the <see cref="ForwardedBrowserCallback"/> class.</summary>
    /// <param name="redirectUri">The address the browser comes back to: <c>http</c>, a loopback host, an explicit port.</param>
    /// <exception cref="ArgumentNullException"><paramref name="redirectUri"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="redirectUri"/> is not an <c>http</c> loopback address with a port.</exception>
    public ForwardedBrowserCallback(Uri redirectUri)
    {
        ArgumentNullException.ThrowIfNull(redirectUri);
        if (!IsLoopbackRedirect(redirectUri)) throw new ArgumentException("A forwarded sign-in comes back to an http loopback address with an explicit port.", nameof(redirectUri));
        RedirectUri = redirectUri;
    }

    /// <summary>Gets the address the browser comes back to, on the other computer.</summary>
    public Uri RedirectUri { get; }

    /// <summary>Gets whether the callback came.</summary>
    public bool IsCompleted => _callback.Task.IsCompleted;

    /// <summary>Tells whether an address is one a browser can come back to on a computer: <c>http</c>, a loopback host, a port of 1024 and up, no fragment.</summary>
    /// <param name="address">The address.</param>
    /// <returns>Whether it is.</returns>
    public static bool IsLoopbackRedirect(Uri? address)
        => address is { IsAbsoluteUri: true } && address.Scheme == Uri.UriSchemeHttp && address.IsLoopback && !address.IsDefaultPort && address.Port >= 1024
           && string.IsNullOrEmpty(address.Fragment) && string.IsNullOrEmpty(address.Query);

    /// <summary>Gives the callback the other computer caught: the address the browser came back to, with its query.</summary>
    /// <param name="callback">The callback.</param>
    /// <returns>Whether it was taken: the first one that matches the path of <see cref="RedirectUri"/>, once.</returns>
    public bool TryComplete(Uri callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        // The other computer gives the address on its own loopback: only its path and its query count here.
        if (!callback.IsAbsoluteUri || !string.Equals(callback.AbsolutePath, RedirectUri.AbsolutePath, StringComparison.Ordinal) || !string.IsNullOrEmpty(callback.Fragment))
            return false;
        // The flow reads it as if the browser had come back to the redirect it named.
        var builder = new UriBuilder(RedirectUri) { Query = callback.Query.TrimStart('?') };
        return _callback.TrySetResult(builder.Uri);
    }

    /// <summary>Ends the wait without a callback: the person gave up, or the other computer could not catch it.</summary>
    /// <returns>Whether the wait was still open.</returns>
    public bool Cancel() => _callback.TrySetCanceled();

    /// <summary>Waits for the callback.</summary>
    /// <param name="timeout">How long the browser has to come back.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The callback, as an address of <see cref="RedirectUri"/> with the query the browser brought.</returns>
    /// <exception cref="TimeoutException">The browser did not come back in time.</exception>
    /// <exception cref="OperationCanceledException">The wait was cancelled, or <see cref="Cancel"/> was called.</exception>
    public async Task<Uri> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            return await _callback.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException("The browser did not come back to the sign-in in time: start it again.");
        }
    }

    /// <summary>Reads a parameter of the query of a callback.</summary>
    /// <param name="callback">The callback.</param>
    /// <param name="name">The name of the parameter.</param>
    /// <returns>Its value, unescaped; null when it is not there.</returns>
    public static string? QueryValue(Uri callback, string name)
    {
        ArgumentNullException.ThrowIfNull(callback);
        ArgumentException.ThrowIfNullOrEmpty(name);
        foreach (var pair in callback.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            var key = Uri.UnescapeDataString(separator < 0 ? pair : pair[..separator]);
            if (string.Equals(key, name, StringComparison.Ordinal))
                return separator < 0 ? string.Empty : Uri.UnescapeDataString(pair[(separator + 1)..].Replace('+', ' '));
        }

        return null;
    }
}
