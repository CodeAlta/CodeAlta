namespace CodeAlta.Plugin.Mobile.Tests;

[TestClass]
public sealed class MobileTailscaleTests
{
    private const string Serve = "serve --bg --https=2584 http://127.0.0.1:2584";

    [TestMethod]
    public async Task Publish_ServesTheLocalPortOnAnHttpsPortOfItsOwn()
    {
        var cli = new FakeTailscaleCli().Answer("status --json", FakeTailscaleCli.Running).Answer("serve status --json", "{}").Answer(Serve, "Available within your tailnet");

        var status = await new MobileTailscale(cli).PublishAsync(2584, 2584, default);

        Assert.AreEqual(("serving", "https://desk.tail1234.ts.net:2584"), (status.State, status.Url));
        CollectionAssert.AreEqual(new[] { "status --json", "serve status --json", Serve }, cli.Calls);
    }

    [TestMethod]
    public async Task Publish_LeavesAPortThatServesSomethingElse()
    {
        // The user serves an application of their own on that port: it is not replaced.
        var cli = new FakeTailscaleCli().Answer("status --json", FakeTailscaleCli.Running)
            .Answer("serve status --json", """{"TCP":{"2584":{"HTTPS":true}},"Web":{"desk.tail1234.ts.net:2584":{"Handlers":{"/":{"Proxy":"http://127.0.0.1:3000"}}}}}""");

        var status = await new MobileTailscale(cli).PublishAsync(2584, 2584, default);

        Assert.AreEqual("port-taken", status.State);
        Assert.IsFalse(cli.Calls.Contains(Serve));
    }

    [TestMethod]
    public async Task Publish_KeepsWhatItServedBefore_AndWhatTheUserServesOnOtherPorts()
    {
        var cli = new FakeTailscaleCli().Answer("status --json", FakeTailscaleCli.Running)
            .Answer("serve status --json", """{"TCP":{"443":{"HTTPS":true},"2584":{"HTTPS":true}},"Web":{"desk.tail1234.ts.net:443":{"Handlers":{"/":{"Proxy":"http://127.0.0.1:3000"}}},"desk.tail1234.ts.net:2584":{"Handlers":{"/":{"Proxy":"http://127.0.0.1:2584"}}}}}""");

        var status = await new MobileTailscale(cli).PublishAsync(2584, 2584, default);

        Assert.AreEqual("serving", status.State, "A handler left by an earlier run of the plugin is the plugin's.");
        Assert.IsFalse(cli.Calls.Contains(Serve));
    }

    [TestMethod]
    public async Task Publish_SaysWhatIsMissing()
    {
        Assert.AreEqual(("not-installed", MobileTailscale.DownloadUrl), Of(await new MobileTailscale(new FakeTailscaleCli(installed: false)).PublishAsync(2584, 2584, default)));

        var stopped = new FakeTailscaleCli().Answer("status --json", """{"BackendState":"NeedsLogin","Self":{"DNSName":""}}""");
        Assert.AreEqual("not-running", (await new MobileTailscale(stopped).PublishAsync(2584, 2584, default)).State);

        var down = new FakeTailscaleCli().Answer("status --json", "failed to connect to local tailscaled", exitCode: 1);
        Assert.AreEqual("not-running", (await new MobileTailscale(down).PublishAsync(2584, 2584, default)).State);

        // Without Serve on the tailnet, the command prints the page that enables it and waits: it is stopped after a while.
        var disabled = new FakeTailscaleCli().Answer("status --json", FakeTailscaleCli.Running).Answer("serve status --json", "{}")
            .Answer(Serve, "Serve is not enabled on your tailnet.\nTo enable, visit:\n\n         https://login.tailscale.com/f/serve?node=abc\n", exitCode: -1, timedOut: true);
        Assert.AreEqual(("serve-disabled", "https://login.tailscale.com/f/serve?node=abc"), Of(await new MobileTailscale(disabled).PublishAsync(2584, 2584, default)));

        var failed = new FakeTailscaleCli().Answer("status --json", FakeTailscaleCli.Running).Answer("serve status --json", "{}").Answer(Serve, "error: bad\n", exitCode: 1);
        Assert.AreEqual("failed", (await new MobileTailscale(failed).PublishAsync(2584, 2584, default)).State);
    }

    [TestMethod]
    public async Task Unpublish_TakesOffOnlyTheHandlerOfThePlugin()
    {
        var ours = new FakeTailscaleCli()
            .Answer("serve status --json", """{"TCP":{"2584":{"HTTPS":true}},"Web":{"desk.tail1234.ts.net:2584":{"Handlers":{"/":{"Proxy":"http://127.0.0.1:2584"}}}}}""")
            .Answer("serve --https=2584 off", "");
        await new MobileTailscale(ours).UnpublishAsync(2584, 2584, default);
        CollectionAssert.Contains(ours.Calls, "serve --https=2584 off");

        var theirs = new FakeTailscaleCli()
            .Answer("serve status --json", """{"TCP":{"2584":{"HTTPS":true}},"Web":{"desk.tail1234.ts.net:2584":{"Handlers":{"/":{"Proxy":"http://127.0.0.1:3000"}}}}}""");
        await new MobileTailscale(theirs).UnpublishAsync(2584, 2584, default);
        Assert.IsFalse(theirs.Calls.Contains("serve --https=2584 off"), "Another program now serves that port.");

        var more = new FakeTailscaleCli()
            .Answer("serve status --json", """{"Web":{"desk.tail1234.ts.net:2584":{"Handlers":{"/":{"Proxy":"http://127.0.0.1:2584"},"/docs":{"Path":"C:\\docs"}}}}}""");
        await new MobileTailscale(more).UnpublishAsync(2584, 2584, default);
        Assert.IsFalse(more.Calls.Contains("serve --https=2584 off"), "The user added a handler to the port: it is theirs too.");
    }

    private static (string State, string? FixUrl) Of(MobileTailscaleStatus status) => (status.State, status.FixUrl);
}
