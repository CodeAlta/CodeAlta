using CodeAlta.Plugins.Abstractions;
using CodeAlta.Plugins.PrivateNetworks;
using static CodeAlta.Plugin.Mobile.Tests.MobileSettingsTests;

namespace CodeAlta.Plugin.Mobile.Tests;

/// <summary>
/// The Mobile plugin over a private network a plugin brings (<see cref="IPrivateNetworkSource"/>): offered beside Tailscale and Dev Tunnels
/// with its own name, published through, its actions shown with its own words; a choice of it made before that plugin runs is kept.
/// </summary>
[TestClass]
public sealed class MobilePrivateNetworkTests
{
    [TestMethod]
    public async Task ANetworkOfAPlugin_ChosenBeforeItRuns_IsKept_ThenServesTheView()
    {
        var port = FreePort.Next();
        var acme = new AcmeNetwork();
        var present = false;
        IReadOnlyList<IPrivateNetworkProvider> builtIns = [new TailscaleNetwork(new FakeTailscaleCli(installed: false))];
        var plugin = new MobilePlugin(() => present ? [.. builtIns, acme] : builtIns);
        await using var fixture = new Fixture(new FakeTailscaleCli(installed: false), plugin: plugin);
        await fixture.State.WriteJsonAsync(PluginStateScope.User, "mobile.json", new MobileSettings { Mode = "acme-vpn", Port = port });
        await fixture.StartAsync(port, 4443, write: false);

        // The plugin that brings it does not run yet: Tailscale serves meanwhile, and the choice stays.
        Assert.AreEqual("tailscale", fixture.Plugin.Settings().Provider.Id);
        Assert.AreEqual("acme-vpn", fixture.State.Read().Mode, "a network a plugin brings later stays chosen");

        present = true;
        var (tab, _) = await fixture.OpenAsync(MobilePlugin.CanvasId);
        var retried = await tab.InvokeAsync<MobileEmpty, MobileStatus>("mobile.retry", new MobileEmpty());
        Assert.AreEqual(("acme-vpn", "not-signed-in"), (retried.Provider.Id, retried.Provider.State), "Try again takes the network chosen once it is there");
        Assert.AreEqual("Acme VPN", retried.Provider.Name);
        CollectionAssert.AreEqual(new[] { ("acme-login", "Sign in to Acme") }, retried.Provider.Actions.Select(static action => (action.Action, action.Label)).ToArray(),
            "the button of its state has its own words");

        var acted = await tab.InvokeAsync<MobileAction, MobileStatus>("mobile.act", new MobileAction("acme-login"));
        Assert.AreEqual("serving", acted.Provider.State);
        StringAssert.StartsWith(acted.ConnectUrl, $"https://desk.acme.example:{port}/#k=", "the code holds the address of the network");
        Assert.AreEqual((port, (int?)null), (acme.Published!.LocalPort, acme.Published.PublicPort), "a network without a port of its own is given none");

        var (page, _) = await fixture.OpenAsync(MobilePlugin.SettingsCanvasId);
        var settings = await page.InvokeAsync<MobileEmpty, MobileSettingsView>("mobile.settings.get", new MobileEmpty());
        CollectionAssert.AreEqual(new[] { ("tailscale", "Tailscale", true), ("acme-vpn", "Acme VPN", false) },
            settings.Providers.Select(static network => (network.Id, network.Name, network.PublicPort)).ToArray(), "it is offered beside Tailscale, with its name");
        Assert.AreEqual("serving", (await fixture.State.ReadJsonAsync<Dictionary<string, string>>(PluginStateScope.User, "mobile-networks.json"))?["acme-vpn/session"],
            "what a network keeps is kept for it apart");
    }

    // A network a plugin brings: not signed in until its action runs, then it serves at an address of its own.
    private sealed class AcmeNetwork : IPrivateNetworkProvider
    {
        public PrivateNetworkPublication? Published { get; private set; }

        public string Id => "acme-vpn";

        public string DisplayName => "Acme VPN";

        public PrivateNetworkCapabilities Capabilities => PrivateNetworkCapabilities.Publish;

        public IPrivateNetworkPublisher CreatePublisher(IPrivateNetworkHost host) => new Publisher(this, host);

        private sealed class Publisher(AcmeNetwork network, IPrivateNetworkHost host) : IPrivateNetworkPublisher
        {
            private bool _signedIn;

            public async Task<PrivateNetworkStatus> PublishAsync(PrivateNetworkPublication publication, CancellationToken cancellationToken)
            {
                network.Published = publication;
                if (!_signedIn) return new("not-signed-in", null, "Sign in to Acme first.") { Actions = [new PrivateNetworkAction("acme-login", "Sign in to Acme")] };
                await host.WriteStateAsync("session", "serving", cancellationToken);
                return new("serving", $"https://desk.acme.example:{publication.LocalPort}", "Acme serves the page.");
            }

            public Task UnpublishAsync(CancellationToken cancellationToken) => Task.CompletedTask;

            public async Task<PrivateNetworkStatus?> ActAsync(string action, CancellationToken cancellationToken)
            {
                if (action != "acme-login") return null;
                _signedIn = true;
                return await PublishAsync(network.Published!, cancellationToken);
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
