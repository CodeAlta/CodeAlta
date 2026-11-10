using CodeAlta.Hosting;
using CodeAlta.Plugin.Mcp;
using ModelContextProtocol.Authentication;

namespace CodeAlta.Tests;

/// <summary>
/// A sign-in that runs here while its browser is on another computer (a node signed in from a desktop, <c>doc/fleet/node.md</c>,
/// credentials on a node): the other computer forwards the callback its browser brought, which is checked here as a listener of
/// this computer checks it.
/// </summary>
[TestClass]
public sealed class ForwardedSignInTests
{
    [TestMethod]
    public async Task AForwardedCallback_IsTakenOnce_OnItsPath_WithItsQuery()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new ForwardedBrowserCallback(new Uri("https://127.0.0.1:50123/callback")), "http only");
        Assert.ThrowsExactly<ArgumentException>(() => new ForwardedBrowserCallback(new Uri("http://example.com:50123/callback")), "a loopback only");
        Assert.ThrowsExactly<ArgumentException>(() => new ForwardedBrowserCallback(new Uri("http://127.0.0.1:80/callback")), "a port of its own");

        var forwarded = new ForwardedBrowserCallback(new Uri("http://127.0.0.1:50123/mcp/oauth/callback"));
        Assert.IsFalse(forwarded.TryComplete(new Uri("http://127.0.0.1:50123/other?code=a")));
        Assert.IsFalse(forwarded.TryComplete(new Uri("http://127.0.0.1:50123/mcp/oauth/callback?code=a#fragment")));
        Assert.IsTrue(forwarded.TryComplete(new Uri("http://localhost:50123/mcp/oauth/callback?code=a%20b&state=s")));
        Assert.IsFalse(forwarded.TryComplete(new Uri("http://127.0.0.1:50123/mcp/oauth/callback?code=c")), "once");
        var callback = await forwarded.WaitAsync(TestTime.Seconds(5), CancellationToken.None); // not a Task
        Assert.AreEqual("127.0.0.1", callback.Host, "read as the redirect it named");
        Assert.AreEqual("a b", ForwardedBrowserCallback.QueryValue(callback, "code"));

        var expires = new ForwardedBrowserCallback(new Uri("http://127.0.0.1:50124/callback"));
        await Assert.ThrowsExactlyAsync<TimeoutException>(() => expires.WaitAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None)); // not a Task, not a limit: the wait that expires
    }

    [TestMethod]
    public async Task AnMcpAuthorization_WithTheBrowserOfAnotherComputer_TakesOnlyACallbackOfItsState()
    {
        var authorization = new AuthorizationCallbackContext
        {
            AuthorizationUri = new Uri("https://auth.example.com/authorize?client_id=c&state=expected"),
            RedirectUri = new Uri("http://127.0.0.1:50125/mcp/oauth/callback"),
        };
        Uri? shownPage = null, shownRedirect = null;
        McpOAuthForwardedAuthorize forward(string query) => (page, redirect, _) =>
        {
            (shownPage, shownRedirect) = (page, redirect);
            return Task.FromResult<Uri?>(new Uri("http://127.0.0.1:50125/mcp/oauth/callback?" + query));
        };

        var result = await McpOAuthForwarding.AuthorizeAsync(authorization, forward("code=the-code&state=expected&iss=https%3A%2F%2Fauth.example.com"), null, CancellationToken.None);
        Assert.AreEqual(("the-code", "expected", "https://auth.example.com"), (result?.Code, result?.State, result?.Iss));
        Assert.AreEqual(authorization.AuthorizationUri, shownPage, "the other computer opens the page");
        Assert.AreEqual(authorization.RedirectUri, shownRedirect, "and listens on the redirect");

        Assert.IsNull(await McpOAuthForwarding.AuthorizeAsync(authorization, forward("code=the-code&state=other"), null, CancellationToken.None), "another state");
        Assert.IsNull(await McpOAuthForwarding.AuthorizeAsync(authorization, forward("error=access_denied&state=expected"), null, CancellationToken.None), "refused");
        Assert.IsNull(await McpOAuthForwarding.AuthorizeAsync(authorization, static (_, _, _) => Task.FromResult<Uri?>(null), null, CancellationToken.None), "cancelled");
    }
}
