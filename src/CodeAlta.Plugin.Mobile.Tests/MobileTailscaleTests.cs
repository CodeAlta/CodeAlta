using CodeAlta.Plugins.Abstractions;
using CodeAlta.Plugins.PrivateNetworks;

namespace CodeAlta.Plugin.Mobile.Tests;

[TestClass]
public sealed class MobileTailscaleTests
{
    private const string Serve = "serve --bg --https=2584 http://127.0.0.1:2584";

    [TestMethod]
    public async Task Publish_ServesTheLocalPortOnAnHttpsPortOfItsOwn()
    {
        var cli = new FakeTailscaleCli().Answer("status --json", FakeTailscaleCli.Running).Answer("serve status --json", "{}").Answer(Serve, "Available within your tailnet");

        var status = await new TailscaleServe(cli).PublishAsync(2584, 2584, default);

        Assert.AreEqual(("serving", "https://desk.tail1234.ts.net:2584"), (status.State, status.Url));
        CollectionAssert.AreEqual(new[] { "status --json", "serve status --json", Serve }, cli.Calls);
    }

    [TestMethod]
    public async Task Check_SaysWhatThePortServes_AndChangesNothing()
    {
        var free = new FakeTailscaleCli().Answer("status --json", FakeTailscaleCli.Running).Answer("serve status --json", "{}");
        var status = await new TailscaleServe(free).CheckAsync(2584, 2584, default);
        Assert.AreEqual(("not-serving", "https://desk.tail1234.ts.net:2584"), (status.State, status.Url), "the address a publication would have");
        CollectionAssert.AreEqual(new[] { "status --json", "serve status --json" }, free.Calls);

        var ours = new FakeTailscaleCli().Answer("status --json", FakeTailscaleCli.Running)
            .Answer("serve status --json", """{"TCP":{"2584":{"HTTPS":true}},"Web":{"desk.tail1234.ts.net:2584":{"Handlers":{"/":{"Proxy":"http://127.0.0.1:2584"}}}}}""");
        Assert.AreEqual("serving", (await new TailscaleServe(ours).CheckAsync(2584, 2584, default)).State);

        var theirs = new FakeTailscaleCli().Answer("status --json", FakeTailscaleCli.Running)
            .Answer("serve status --json", """{"TCP":{"2584":{"HTTPS":true}},"Web":{"desk.tail1234.ts.net:2584":{"Handlers":{"/":{"Proxy":"http://127.0.0.1:3000"}}}}}""");
        Assert.AreEqual("port-taken", (await new TailscaleServe(theirs).CheckAsync(2584, 2584, default)).State);

        // Published under an older name of the computer: nothing answers at its current name, and the check takes nothing off.
        var stale = new FakeTailscaleCli().Answer("status --json", FakeTailscaleCli.Running)
            .Answer("serve status --json", """{"TCP":{"2584":{"HTTPS":true}},"Web":{"old-desk.tail1234.ts.net:2584":{"Handlers":{"/":{"Proxy":"http://127.0.0.1:2584"}}}}}""");
        Assert.AreEqual("not-serving", (await new TailscaleServe(stale).CheckAsync(2584, 2584, default)).State);
        Assert.IsFalse(stale.Calls.Contains("serve reset"));
    }

    [TestMethod]
    public async Task Publish_LeavesAPortThatServesSomethingElse()
    {
        // The user serves an application of their own on that port: it is not replaced.
        var cli = new FakeTailscaleCli().Answer("status --json", FakeTailscaleCli.Running)
            .Answer("serve status --json", """{"TCP":{"2584":{"HTTPS":true}},"Web":{"desk.tail1234.ts.net:2584":{"Handlers":{"/":{"Proxy":"http://127.0.0.1:3000"}}}}}""");

        var status = await new TailscaleServe(cli).PublishAsync(2584, 2584, default);

        Assert.AreEqual("port-taken", status.State);
        Assert.IsFalse(cli.Calls.Contains(Serve));
    }

    [TestMethod]
    public async Task Publish_KeepsWhatItServedBefore_AndWhatTheUserServesOnOtherPorts()
    {
        var cli = new FakeTailscaleCli().Answer("status --json", FakeTailscaleCli.Running)
            .Answer("serve status --json", """{"TCP":{"443":{"HTTPS":true},"2584":{"HTTPS":true}},"Web":{"desk.tail1234.ts.net:443":{"Handlers":{"/":{"Proxy":"http://127.0.0.1:3000"}}},"desk.tail1234.ts.net:2584":{"Handlers":{"/":{"Proxy":"http://127.0.0.1:2584"}}}}}""");

        var status = await new TailscaleServe(cli).PublishAsync(2584, 2584, default);

        Assert.AreEqual("serving", status.State, "A handler left by an earlier run of the plugin is the plugin's.");
        Assert.IsFalse(cli.Calls.Contains(Serve));
    }

    [TestMethod]
    public async Task Publish_AfterARenameOfTheComputer_ServesThePageUnderItsNewName()
    {
        // The computer was "old-desk" when the page was published: that rule answers no one, Tailscale has no certificate for it.
        var cli = new FakeTailscaleCli().Answer("status --json", FakeTailscaleCli.Running)
            .Answer("serve status --json", """{"TCP":{"2584":{"HTTPS":true}},"Web":{"old-desk.tail1234.ts.net:2584":{"Handlers":{"/":{"Proxy":"http://127.0.0.1:2584"}}}}}""")
            .Answer("serve reset", "")
            .Answer(Serve, "Available within your tailnet");

        var status = await new TailscaleServe(cli).PublishAsync(2584, 2584, default);

        Assert.AreEqual(("serving", "https://desk.tail1234.ts.net:2584"), (status.State, status.Url));
        CollectionAssert.AreEqual(new[] { "status --json", "serve status --json", "serve reset", Serve }, cli.Calls, "The old rule is the only one: a reset takes it off.");
    }

    [TestMethod]
    public async Task Publish_AfterARenameOfTheComputer_KeepsWhatTheUserServes()
    {
        // The command line cannot take off the rule of another name alone: with the user's own rules beside it, it is left.
        var cli = new FakeTailscaleCli().Answer("status --json", FakeTailscaleCli.Running)
            .Answer("serve status --json", """{"TCP":{"443":{"HTTPS":true},"2584":{"HTTPS":true}},"Web":{"desk.tail1234.ts.net:443":{"Handlers":{"/":{"Proxy":"http://127.0.0.1:3000"}}},"old-desk.tail1234.ts.net:2584":{"Handlers":{"/":{"Proxy":"http://127.0.0.1:2584"}}}}}""")
            .Answer(Serve, "Available within your tailnet");

        var status = await new TailscaleServe(cli).PublishAsync(2584, 2584, default);

        Assert.AreEqual("serving", status.State);
        CollectionAssert.AreEqual(new[] { "status --json", "serve status --json", Serve }, cli.Calls);
    }

    [TestMethod]
    public async Task Publish_LeavesAPortThatServesSomethingElseUnderAnotherName()
    {
        var cli = new FakeTailscaleCli().Answer("status --json", FakeTailscaleCli.Running)
            .Answer("serve status --json", """{"TCP":{"2584":{"HTTPS":true}},"Web":{"old-desk.tail1234.ts.net:2584":{"Handlers":{"/":{"Proxy":"http://127.0.0.1:3000"}}}}}""");

        Assert.AreEqual("port-taken", (await new TailscaleServe(cli).PublishAsync(2584, 2584, default)).State);
        Assert.IsFalse(cli.Calls.Contains("serve reset"));
    }

    [TestMethod]
    public async Task Publish_SaysWhatIsMissing()
    {
        Assert.AreEqual(("not-installed", TailscaleServe.DownloadUrl), Of(await new TailscaleServe(new FakeTailscaleCli(installed: false)).PublishAsync(2584, 2584, default)));

        var stopped = new FakeTailscaleCli().Answer("status --json", """{"BackendState":"NeedsLogin","Self":{"DNSName":""}}""");
        Assert.AreEqual("not-running", (await new TailscaleServe(stopped).PublishAsync(2584, 2584, default)).State);

        var down = new FakeTailscaleCli().Answer("status --json", "failed to connect to local tailscaled", exitCode: 1);
        Assert.AreEqual("not-running", (await new TailscaleServe(down).PublishAsync(2584, 2584, default)).State);

        // Without Serve on the tailnet, the command prints the page that enables it and waits: it is stopped after a while.
        var disabled = new FakeTailscaleCli().Answer("status --json", FakeTailscaleCli.Running).Answer("serve status --json", "{}")
            .Answer(Serve, "Serve is not enabled on your tailnet.\nTo enable, visit:\n\n         https://login.tailscale.com/f/serve?node=abc\n", exitCode: -1, timedOut: true);
        Assert.AreEqual(("serve-disabled", "https://login.tailscale.com/f/serve?node=abc"), Of(await new TailscaleServe(disabled).PublishAsync(2584, 2584, default)));

        var failed = new FakeTailscaleCli().Answer("status --json", FakeTailscaleCli.Running).Answer("serve status --json", "{}").Answer(Serve, "error: bad\n", exitCode: 1);
        Assert.AreEqual("failed", (await new TailscaleServe(failed).PublishAsync(2584, 2584, default)).State);
    }

    [TestMethod]
    public async Task Unpublish_TakesOffOnlyTheHandlerOfThePlugin()
    {
        var ours = new FakeTailscaleCli().Answer("status --json", FakeTailscaleCli.Running)
            .Answer("serve status --json", """{"TCP":{"2584":{"HTTPS":true}},"Web":{"desk.tail1234.ts.net:2584":{"Handlers":{"/":{"Proxy":"http://127.0.0.1:2584"}}}}}""")
            .Answer("serve --https=2584 off", "");
        await new TailscaleServe(ours).UnpublishAsync(2584, 2584, default);
        CollectionAssert.Contains(ours.Calls, "serve --https=2584 off");

        var theirs = new FakeTailscaleCli().Answer("status --json", FakeTailscaleCli.Running)
            .Answer("serve status --json", """{"TCP":{"2584":{"HTTPS":true}},"Web":{"desk.tail1234.ts.net:2584":{"Handlers":{"/":{"Proxy":"http://127.0.0.1:3000"}}}}}""");
        await new TailscaleServe(theirs).UnpublishAsync(2584, 2584, default);
        Assert.IsFalse(theirs.Calls.Contains("serve --https=2584 off"), "Another program now serves that port.");

        var more = new FakeTailscaleCli().Answer("status --json", FakeTailscaleCli.Running)
            .Answer("serve status --json", """{"Web":{"desk.tail1234.ts.net:2584":{"Handlers":{"/":{"Proxy":"http://127.0.0.1:2584"},"/docs":{"Path":"C:\\docs"}}}}}""");
        await new TailscaleServe(more).UnpublishAsync(2584, 2584, default);
        Assert.IsFalse(more.Calls.Contains("serve --https=2584 off"), "The user added a handler to the port: it is theirs too.");
    }

    [TestMethod]
    public async Task Unpublish_AfterARenameOfTheComputer_TakesOffThePageOfTheOldName()
    {
        var alone = new FakeTailscaleCli().Answer("status --json", FakeTailscaleCli.Running)
            .Answer("serve status --json", """{"TCP":{"2584":{"HTTPS":true}},"Web":{"old-desk.tail1234.ts.net:2584":{"Handlers":{"/":{"Proxy":"http://127.0.0.1:2584"}}}}}""")
            .Answer("serve reset", "");
        Assert.AreEqual("Tailscale no longer serves the page.", await new TailscaleServe(alone).UnpublishAsync(2584, 2584, default));
        CollectionAssert.Contains(alone.Calls, "serve reset");
        Assert.IsFalse(alone.Calls.Contains("serve --https=2584 off"), "The current name has no rule to take off.");

        // Beside the user's own rules, a reset would take them off too: the old rule is left, and the log says how to remove it.
        var beside = new FakeTailscaleCli().Answer("status --json", FakeTailscaleCli.Running)
            .Answer("serve status --json", """{"TCP":{"443":{"HTTPS":true},"2584":{"HTTPS":true}},"Web":{"desk.tail1234.ts.net:443":{"Handlers":{"/":{"Proxy":"http://127.0.0.1:3000"}}},"old-desk.tail1234.ts.net:2584":{"Handlers":{"/":{"Proxy":"http://127.0.0.1:2584"}}}}}""");
        StringAssert.Contains(await new TailscaleServe(beside).UnpublishAsync(2584, 2584, default), "older name");
        Assert.IsFalse(beside.Calls.Contains("serve reset"));
    }

    private static (string State, string? FixUrl) Of(PrivateNetworkStatus status) => (status.State, status.FixUrl);
}
