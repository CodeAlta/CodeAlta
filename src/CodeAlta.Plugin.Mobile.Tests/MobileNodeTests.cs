using System.Text.Json;
using CodeAlta.Plugins.Abstractions;
using CodeAlta.Plugins.PrivateNetworks;

namespace CodeAlta.Plugin.Mobile.Tests;

/// <summary>
/// The plugin on a host without a window (CodeAlta Node): a pairing code is asked for when needed, connects one phone and
/// ends by itself, and the calls of the tab and of the settings page are the host's (<see cref="IMobileHostControl"/>).
/// </summary>
[TestClass]
public sealed class MobileNodeTests
{
    [TestMethod]
    public async Task APairingTokenWithALife_SignsOnePhoneIn_AndEndsByItself()
    {
        var clock = new ManualClock();
        var access = await MobileAccess.LoadAsync(new MobileSettingsTests.MemoryState(), TimeSpan.FromMinutes(5), clock, CancellationToken.None);
        Assert.IsTrue(access.PairsOnce);
        Assert.IsNull(access.PairingToken);

        var token = await access.RenewAsync(CancellationToken.None);
        Assert.AreEqual(TimeSpan.FromMinutes(5), access.PairingEndsIn);
        var phone = await access.SignInAsync(token, CancellationToken.None);
        Assert.IsNotNull(phone);
        Assert.IsNull(access.PairingToken, "the code connected its phone");
        Assert.IsNull(await access.SignInAsync(token, CancellationToken.None), "a second phone is refused");
        Assert.AreEqual(phone, await access.SignInAsync(phone, CancellationToken.None), "the phone keeps its key");

        var late = await access.RenewAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.IsNull(access.PairingToken, "the code ended");
        Assert.IsNull(access.PairingEndsIn);
        Assert.IsNull(await access.SignInAsync(late, CancellationToken.None));
        Assert.IsTrue(access.IsPhone(phone));

        // A new code takes the place of the one before.
        var replaced = await access.RenewAsync(CancellationToken.None);
        var next = await access.RenewAsync(CancellationToken.None);
        Assert.IsNull(await access.SignInAsync(replaced, CancellationToken.None));
        Assert.IsNotNull(await access.SignInAsync(next, CancellationToken.None));
    }

    [TestMethod]
    public async Task ATokenWithALife_IsNeverKept_AndAResetLeavesNoCode()
    {
        var state = new MobileSettingsTests.MemoryState();
        var access = await MobileAccess.LoadAsync(state, TimeSpan.FromMinutes(5), new ManualClock(), CancellationToken.None);
        var token = await access.RenewAsync(CancellationToken.None);
        Assert.IsFalse(state.AllText.Contains(MobileAccess.Hash(token), StringComparison.Ordinal), "not even its hash is written");

        await access.ResetAsync(CancellationToken.None);
        Assert.IsNull(access.PairingToken, "a code is made when it is asked for, never kept ready");
        Assert.IsNull(await access.SignInAsync(token, CancellationToken.None));

        // A standing token of a window is unchanged: it stays until a reset.
        var window = await MobileAccess.LoadAsync(new MobileSettingsTests.MemoryState(), CancellationToken.None);
        Assert.IsFalse(window.PairsOnce);
        var standing = await window.PairAsync(CancellationToken.None);
        Assert.IsNotNull(await window.SignInAsync(standing, CancellationToken.None));
        Assert.IsNotNull(await window.SignInAsync(standing, CancellationToken.None), "the code of a window connects every phone that scans it");
        Assert.IsNull(window.PairingEndsIn);
    }

    [TestMethod]
    public async Task TheHost_AsksTheCallsOfTheTab_AndGetsACodeOnlyWhenItAsksForOne()
    {
        var port = FreePort.Next();
        // A tailnet that already serves the page at the ports of the plugin.
        var cli = new FakeTailscaleCli().Answer("status --json", FakeTailscaleCli.Running)
            .Answer("serve status --json", "{\"TCP\":{\"4443\":{\"HTTPS\":true}},\"Web\":{\"desk.tail1234.ts.net:4443\":{\"Handlers\":{\"/\":{\"Proxy\":\"http://127.0.0.1:" + port + "\"}}}}}");
        await using var fixture = new MobileSettingsTests.Fixture(cli) { Frontend = PluginFrontends.Node };
        await fixture.StartAsync(port, 4443);
        IMobileHostControl control = fixture.Plugin;

        using var status = JsonDocument.Parse(await control.InvokeAsync("mobile.status", null));
        Assert.AreEqual("listening", status.RootElement.GetProperty("server").GetString());
        Assert.IsTrue(status.RootElement.GetProperty("pairsOnce").GetBoolean());
        Assert.IsNotNull(status.RootElement.GetProperty("served").GetString());
        Assert.AreEqual(JsonValueKind.Null, status.RootElement.GetProperty("connectUrl").ValueKind, "the status alone makes no code");
        Assert.IsNull(fixture.Plugin.PairingToken);

        using var paired = JsonDocument.Parse(await control.InvokeAsync("mobile.pair", null));
        var token = fixture.Plugin.PairingToken!;
        StringAssert.EndsWith(paired.RootElement.GetProperty("connectUrl").GetString(), "#k=" + token);
        Assert.AreEqual(300, paired.RootElement.GetProperty("pairingSeconds").GetInt32());
        Assert.IsTrue(paired.RootElement.GetProperty("qr").GetArrayLength() > 20);
        Assert.AreEqual(0, paired.RootElement.GetProperty("phones").GetInt32());

        Assert.IsNotNull(await fixture.Plugin.Access!.SignInAsync(token, CancellationToken.None));
        using var after = JsonDocument.Parse(await control.InvokeAsync("mobile.status", null));
        Assert.AreEqual(1, after.RootElement.GetProperty("phones").GetInt32());
        Assert.AreEqual(JsonValueKind.Null, after.RootElement.GetProperty("qr").ValueKind, "the code that connected its phone is no longer shown");

        using var settings = JsonDocument.Parse(await control.InvokeAsync("mobile.settings.get", null));
        Assert.AreEqual(port, settings.RootElement.GetProperty("port").GetInt32());
        using var refused = JsonDocument.Parse(await control.InvokeAsync("mobile.settings.set", """{"provider":null,"port":80,"httpsPort":null}"""));
        StringAssert.Contains(refused.RootElement.GetProperty("error").GetString(), "1024");
        using var reset = JsonDocument.Parse(await control.InvokeAsync("mobile.reset", null));
        Assert.IsFalse(fixture.Plugin.Access!.IsPhone(token));

        await Assert.ThrowsExactlyAsync<ArgumentException>(async () => await control.InvokeAsync("mobile.unknown", null));
        await Assert.ThrowsExactlyAsync<ArgumentException>(async () => await control.InvokeAsync("mobile.act", null));
        await Assert.ThrowsExactlyAsync<ArgumentException>(async () => await control.InvokeAsync("mobile.settings.set", "{ not json"));
    }

    [TestMethod]
    public void ThePlugin_RunsInTheDesktopAndOnANode_NeverInTheCommandLineApplication()
    {
        var supported = PluginDescriptorFactory.FromType(typeof(MobilePlugin)).Frontends;
        Assert.IsTrue(supported.Supports(PluginFrontends.Desktop));
        Assert.IsTrue(supported.Supports(PluginFrontends.Node));
        Assert.IsFalse(supported.Supports(PluginFrontends.Cli), "altacli ends with its prompt: it serves no page");
        Assert.IsFalse(supported.Supports(PluginFrontends.Terminal));
    }
}
