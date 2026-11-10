using System.Net;
using CodeAlta.Plugins.Abstractions;
using CodeAlta.Plugins.PrivateNetworks;

namespace CodeAlta.Plugins.Tests;

/// <summary>
/// The private networks of this computer: those of CodeAlta first, then those the active plugins bring, one id for one network; and this
/// computer found on the first one that says where it is.
/// </summary>
[TestClass]
public sealed class PrivateNetworkRegistryTests
{
    [TestMethod]
    public void TheNetworksOfPlugins_ComeAfterThoseOfCodeAlta_OneIdForOneNetwork()
    {
        var tailscale = new Network("tailscale", PrivateNetworkCapabilities.Presence | PrivateNetworkCapabilities.Publish);
        var registry = new PrivateNetworkRegistry([tailscale], () =>
        [
            new Source(new Network("acme-vpn", PrivateNetworkCapabilities.Publish), new Network("tailscale", PrivateNetworkCapabilities.Publish), new Network("no spaces!", PrivateNetworkCapabilities.Publish)),
            "a plugin that brings no network",
            new FailingSource(),
            new Source(new Network("acme-vpn", PrivateNetworkCapabilities.Presence)),
        ]);

        CollectionAssert.AreEqual(new[] { "tailscale", "acme-vpn" }, registry.Providers.Select(static network => network.Id).ToArray(),
            "the first network of an id keeps it, an id that is not one is left out, and a plugin that fails gives none");
        Assert.AreSame(tailscale, registry.Providers[0], "CodeAlta keeps its own network of an id");
    }

    [TestMethod]
    public async Task ThisComputer_IsFoundOnTheFirstNetworkThatSaysWhereItIs()
    {
        var off = new Network("tailscale", PrivateNetworkCapabilities.Presence | PrivateNetworkCapabilities.Publish);
        var tunnel = new Network("devtunnel", PrivateNetworkCapabilities.Publish) { Presence = new("never.example", [IPAddress.Parse("10.0.0.9")]) };
        var failing = new Network("broken", PrivateNetworkCapabilities.Presence) { Fails = true };
        var vpn = new Network("acme-vpn", PrivateNetworkCapabilities.Presence) { Presence = new("desk.acme.example", [IPAddress.Parse("10.8.0.2")]) };
        var registry = new PrivateNetworkRegistry([off, tunnel], () => [new Source(failing, vpn)]);

        var found = await registry.FindThisComputerAsync(CancellationToken.None);

        Assert.IsNotNull(found);
        Assert.AreSame(vpn, found.Network, "a network off, one that only publishes and one that fails are passed over");
        Assert.AreEqual("desk.acme.example", found.Presence.Name);
        Assert.IsNull(await new PrivateNetworkRegistry([off], () => []).FindThisComputerAsync(CancellationToken.None), "on no network, nothing is found");
    }

    [TestMethod]
    public void TailscaleStatus_GivesTheNameAndTheAddressesOfThisComputer()
    {
        var presence = TailscaleNetwork.Parse("""{"BackendState":"Running","Self":{"DNSName":"desk.tail1234.ts.net.","TailscaleIPs":["fd7a:115c:a1e0::7","100.64.0.7"]}}""");
        Assert.IsNotNull(presence);
        Assert.AreEqual("desk.tail1234.ts.net", presence.Name);
        CollectionAssert.AreEqual(new[] { IPAddress.Parse("100.64.0.7"), IPAddress.Parse("fd7a:115c:a1e0::7") }, presence.Addresses.ToArray(), "IPv4 first");
        Assert.IsNull(TailscaleNetwork.Parse("""{"BackendState":"Stopped","Self":{"TailscaleIPs":["100.64.0.7"]}}"""), "Tailscale that does not run");
        Assert.AreEqual("100.64.0.7", TailscaleNetwork.Parse("""{"BackendState":"Running","Self":{"TailscaleIPs":["100.64.0.7"]}}""")!.Name, "without MagicDNS, its address");
    }

    [TestMethod]
    public void AServerAddress_IsRecognizedByThePublishingProviders_IncludingPlugins()
    {
        var presenceOnly = new Network("presence-only", PrivateNetworkCapabilities.Presence) { Suffix = ".example" };
        var plugin = new Network("vpn", PrivateNetworkCapabilities.Publish) { Suffix = ".private.example" };
        var registry = new PrivateNetworkRegistry(() => [new Source(presenceOnly, plugin)]);
        Assert.AreEqual("tailscale", registry.FindForAddress(new Uri("https://box.tail1234.ts.net:8443"))?.Id);
        Assert.AreEqual("devtunnel", registry.FindForAddress(new Uri("https://personal-2586.euw.devtunnels.ms"))?.Id);
        Assert.AreSame(plugin, registry.FindForAddress(new Uri("https://box.private.example")));
        Assert.IsNull(registry.FindForAddress(new Uri("https://mine.example")), "a provider without Publish is not asked to publish");
        Assert.IsNull(registry.FindForAddress(new Uri("https://devtunnels.ms.attacker.example")));
        Assert.IsNull(registry.FindForAddress(new Uri("https://ts.net.attacker.example")));
        Assert.IsNull(registry.FindForAddress(new Uri("http://box.tail1234.ts.net")));
    }

    private sealed class Network(string id, PrivateNetworkCapabilities capabilities) : IPrivateNetworkProvider
    {
        public PrivateNetworkPresence? Presence { get; init; }

        public bool Fails { get; init; }

        public string? Suffix { get; init; }

        public string Id => id;

        public string DisplayName => id;

        public PrivateNetworkCapabilities Capabilities => capabilities;

        public bool OwnsAddress(Uri address) => Suffix is not null && address.Host.EndsWith(Suffix, StringComparison.Ordinal);

        public ValueTask<PrivateNetworkPresence?> FindThisComputerAsync(CancellationToken cancellationToken)
            => Fails ? throw new InvalidOperationException("The network fails.") : ValueTask.FromResult(Presence);
    }

    private sealed class Source(params IPrivateNetworkProvider[] networks) : IPrivateNetworkSource
    {
        public IReadOnlyList<IPrivateNetworkProvider> PrivateNetworks => networks;
    }

    private sealed class FailingSource : IPrivateNetworkSource
    {
        public IReadOnlyList<IPrivateNetworkProvider> PrivateNetworks => throw new InvalidOperationException("The plugin fails.");
    }
}
