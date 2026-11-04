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
