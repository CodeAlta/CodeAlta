using System.Text.Json;
using CodeAlta.Desktop.Rpc;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class MarkdownLinksRpcTests
{
    private const string Epoch = "3b0a7c1e-52c9-4f0b-8a55-6d0c2f9e1b77";

    [TestMethod]
    public async Task OnlyBoundedCredentialFreeAbsoluteWebAddressesReachTheOpener()
    {
        var opened = new List<string>();
        var service = new MarkdownLinksService(Epoch, address => { opened.Add(address); return true; });
        foreach (var address in new[] { "https://example.invalid/path?q=one%20two#section", "http://localhost:8080/", "HTTPS://example.invalid/", "https://[::1]/" })
        {
            var reply = await service.OpenAsync(new(Epoch, address), default);
            Assert.AreEqual("ok", reply.Status, address);
            Assert.AreEqual(Epoch, reply.HostEpoch);
        }

        var count = opened.Count;
        foreach (var address in new[] { "", "/relative", "//example.invalid/", "mailto:a@b.invalid", "app://codealta/", "file:///tmp/a", "javascript:alert(1)",
            "https://user:pass@example.invalid/", "https://@example.invalid/", "https:///example.invalid", "https://", "https://example.invalid:bad/",
            " https://example.invalid/", "https://example.invalid/a b", "https://example.invalid/\n", "https://example.invalid/\u0085", "https://example.invalid/\u00a0",
            "https://example.invalid/\\path", "https://example.invalid/%", "https://example.invalid/%xx", "https://example.invalid/" + new string('x', 2048) })
        {
            Assert.AreEqual("invalid_request", (await service.OpenAsync(new(Epoch, address), default)).Status, address);
        }

        Assert.AreEqual(count, opened.Count);
    }

    [TestMethod]
    public async Task StaleInvalidAndCanceledRequestsNeverOpenAnything()
    {
        var calls = 0;
        var service = new MarkdownLinksService(Epoch, _ => { calls++; return true; });
        Assert.AreEqual("invalid_request", (await service.OpenAsync(null!, default)).Status);
        Assert.AreEqual("invalid_request", (await service.OpenAsync(new("not-an-epoch", "https://example.invalid/"), default)).Status);
        Assert.AreEqual("stale_epoch", (await service.OpenAsync(new("4b0a7c1e-52c9-4f0b-8a55-6d0c2f9e1b77", "file:///tmp/a"), default)).Status);
        Assert.AreEqual("invalid_request", (await service.OpenAsync(new(Epoch, null!), default)).Status);
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.OpenAsync(new(Epoch, "https://example.invalid/"), new CancellationToken(true)));
        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public async Task BrowserFailureReturnsOnlyABoundedStatus()
    {
        foreach (var open in new Func<string, bool>[] { _ => false, _ => throw new InvalidOperationException("private OS diagnostics") })
        {
            var reply = await new MarkdownLinksService(Epoch, open).OpenAsync(new(Epoch, "https://example.invalid/"), default);
            Assert.AreEqual("failed", reply.Status);
            Assert.AreEqual(Epoch, reply.HostEpoch);
            Assert.AreEqual($"{{\"status\":\"failed\",\"hostEpoch\":\"{Epoch}\"}}", JsonSerializer.Serialize(reply, DesktopJsonContext.Default.MarkdownLinkResponse));
        }
    }
}
