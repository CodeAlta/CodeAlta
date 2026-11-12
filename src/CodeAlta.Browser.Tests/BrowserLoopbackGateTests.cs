using CodeAlta.Browser.Drivers;
using CodeAlta.Browser.Drivers.BiDi;
using CodeAlta.Browser.Drivers.Cdp;

namespace CodeAlta.Browser.Tests;

/// <summary>What the drivers ask the browser to hold for the gate: the ports of CodeAlta change while a context lives.</summary>
[TestClass]
public sealed class BrowserLoopbackGateTests
{
    private static readonly BrowserRequestGate Gate = static (_, _, _) => ValueTask.FromResult<string?>(null);

    [TestMethod]
    public void AContextThatGatesTheLoopback_HoldsEveryRequestToIt_WhateverItsPort()
    {
        var options = new BrowserContextOptions { Gate = Gate, GatesLoopback = true, BlockedPorts = [2582] };
        Assert.IsTrue(options.Intercepts);
        CollectionAssert.AreEqual(new[] { "*://127.0.0.1*", "*://localhost*", "*://[::1]*", "*://0.0.0.0*" },
            CdpPage.LoopbackPatterns(options).Select(static pattern => pattern.UrlPattern).ToArray());
        var bidi = BiDiPage.LoopbackPatterns(options).ToArray();
        CollectionAssert.AreEqual(new[] { "127.0.0.1", "localhost", "[::1]", "0.0.0.0" }, bidi.Select(static pattern => pattern.HostName).ToArray());
        Assert.IsTrue(bidi.All(static pattern => pattern.Port is null), "any port");

        // The ports of the creation alone, as before, for a context that does not.
        var fixedPorts = options with { GatesLoopback = false };
        CollectionAssert.AreEqual(new[] { "*://127.0.0.1:2582/*", "*://localhost:2582/*", "*://[::1]:2582/*" },
            CdpPage.LoopbackPatterns(fixedPorts).Select(static pattern => pattern.UrlPattern).ToArray());
        Assert.IsFalse((fixedPorts with { BlockedPorts = [] }).Intercepts);
        Assert.IsFalse((options with { Gate = null }).Intercepts);
    }
}
