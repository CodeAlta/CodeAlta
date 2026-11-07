using System.Net;
using System.Net.Sockets;
using CodeAlta.Fleet.Server;

namespace CodeAlta.Fleet.Tests;

/// <summary>
/// The loopback redirect of a sign-in through the system browser: on <c>127.0.0.1</c> for the clients CodeAlta registers,
/// and on <c>localhost</c> for a company's own client, since Entra ID's Web platform takes only <c>https://</c> or
/// <c>http://localhost</c>. A browser resolves <c>localhost</c> to <c>127.0.0.1</c> or <c>[::1]</c>: both are answered.
/// </summary>
[TestClass]
public sealed class FleetLoopbackRedirectTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task TheRedirectOfARegisteredClient_IsOnTheLoopbackAddress()
    {
        using var redirect = FleetLoopbackRedirect.Start();
        StringAssert.StartsWith(redirect.RedirectUri, "http://127.0.0.1:");
        var answer = await AnswerAsync(redirect, IPAddress.Loopback);
        Assert.AreEqual(("the-code", "the-state"), (answer.Code, answer.State));
    }

    [TestMethod]
    public async Task TheRedirectOfACompanyClient_IsOnLocalhost_AndAnsweredOnIPv4AndIPv6()
    {
        using (var redirect = FleetLoopbackRedirect.Start(FleetLoopbackHost.Localhost))
        {
            StringAssert.StartsWith(redirect.RedirectUri, "http://localhost:");
            StringAssert.EndsWith(redirect.RedirectUri, "/callback");
            Assert.AreEqual("the-code", (await AnswerAsync(redirect, IPAddress.Loopback)).Code, "a browser that resolved localhost to 127.0.0.1");
        }

        if (!Socket.OSSupportsIPv6) return;
        using var other = FleetLoopbackRedirect.Start(FleetLoopbackHost.Localhost);
        Assert.AreEqual("the-code", (await AnswerAsync(other, IPAddress.IPv6Loopback)).Code, "a browser that resolved localhost to [::1]");
    }

    // A machine signed in from this computer names the address its sign-in comes back to: the listener takes that port and that
    // path, and gives the whole callback, which goes back to the machine.
    [TestMethod]
    public async Task TheRedirectAMachineNames_IsListenedOnExactly_AndGivesTheWholeCallback()
    {
        var token = TestContext.CancellationToken;
        int port;
        using (var free = new TcpListener(IPAddress.Loopback, 0)) { free.Start(); port = ((IPEndPoint)free.LocalEndpoint).Port; }
        Assert.ThrowsExactly<ArgumentException>(() => FleetLoopbackRedirect.StartAt(new Uri($"https://127.0.0.1:{port}/auth/callback")));
        using var redirect = FleetLoopbackRedirect.StartAt(new Uri($"http://127.0.0.1:{port}/auth/callback"));
        Assert.AreEqual($"http://127.0.0.1:{port}/auth/callback", redirect.RedirectUri);
        Assert.ThrowsExactly<SocketException>(() => FleetLoopbackRedirect.StartAt(new Uri($"http://127.0.0.1:{port}/auth/callback")), "the port is taken");

        var waiting = redirect.WaitForCallbackAsync("s1", token);
        using var browser = new HttpClient();
        Assert.AreEqual(HttpStatusCode.NotFound, (await browser.GetAsync($"http://127.0.0.1:{port}/callback?code=c&state=s1", token)).StatusCode, "another path");
        Assert.AreEqual(HttpStatusCode.OK, (await browser.GetAsync($"http://127.0.0.1:{port}/auth/callback?code=c&state=s1&client_id=app", token)).StatusCode);
        var (answer, callback) = await waiting;
        Assert.AreEqual("c", answer.Code);
        Assert.AreEqual($"http://127.0.0.1:{port}/auth/callback?code=c&state=s1&client_id=app", callback.AbsoluteUri);
    }

    // The browser coming back: one request on the port of the redirect, at the address given.
    private async Task<FleetLoopbackAnswer> AnswerAsync(FleetLoopbackRedirect redirect, IPAddress address)
    {
        var token = TestContext.CancellationToken;
        var waiting = redirect.WaitAsync("the-state", token);
        var port = new Uri(redirect.RedirectUri).Port;
        using var client = new TcpClient(address.AddressFamily);
        await client.ConnectAsync(address, port, token);
        var stream = client.GetStream();
        await stream.WriteAsync("GET /callback?code=the-code&state=the-state HTTP/1.1\r\nHost: localhost\r\n\r\n"u8.ToArray(), token);
        return await waiting.WaitAsync(TimeSpan.FromSeconds(10), token);
    }
}
