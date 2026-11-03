using System.Threading.Channels;
using CodeAlta.Plugins.Abstractions;
using static CodeAlta.Plugin.Mobile.Tests.MobileSettingsTests;

namespace CodeAlta.Plugin.Mobile.Tests;

/// <summary>The Dev Tunnel of the mobile view, against a command line of the tests that answers as <c>devtunnel</c> does.</summary>
[TestClass]
public sealed class MobileDevTunnelTests
{
    [TestMethod]
    public void TheOutputsOfTheCommandLine_GiveTheTunnel_TheAddressOfThePort_AndTheSignIn()
    {
        Assert.AreEqual("bold-tree-x1y2.euw", MobileDevTunnel.TunnelIdIn("Tunnel ID             : bold-tree-x1y2.euw\nDescription           :\nAccess control        : {+Anonymous [connect]}"));
        Assert.IsNull(MobileDevTunnel.TunnelIdIn("Error: unauthorized"));
        Assert.AreEqual("https://bold-tree-x1y2-2584.euw.devtunnels.ms", MobileDevTunnel.UrlIn("Connect via browser: https://bold-tree-x1y2-2584.euw.devtunnels.ms", 2584));
        Assert.IsNull(MobileDevTunnel.UrlIn("Inspect network activity: https://bold-tree-x1y2-2584-inspect.euw.devtunnels.ms", 2584), "the page that inspects the traffic is not the view");
        Assert.IsNull(MobileDevTunnel.UrlIn("Connect via browser: https://bold-tree-x1y2-3000.euw.devtunnels.ms", 2584), "another port");
        Assert.IsTrue(MobileDevTunnel.IsSignedIn(new MobileCliResult(0, "Logged in as someone using GitHub.")));
        Assert.IsFalse(MobileDevTunnel.IsSignedIn(new MobileCliResult(0, "Not logged in.")));
        Assert.IsFalse(MobileDevTunnel.IsSignedIn(new MobileCliResult(1, "")));
        Assert.IsFalse(MobileDevTunnel.IsSignedIn(new MobileCliResult(0, "Logged in as someone@example.com using Microsoft.")), "the sign-in is a GitHub one");
        Assert.AreEqual("Error: Forbidden. The user is not allowed to change the tunnel.",
            MobileDevTunnel.Summary("Welcome to dev tunnels!\nError: Forbidden. The user is not allowed to change the tunnel.\nRequest ID: 9f818614-208b-477d-9084-9a0a82e60a26"),
            "the request id says nothing to a person");
    }

    [TestMethod]
    public async Task ATunnelKeptThatRefusesThePort_IsReplacedByANewOne()
    {
        var cli = new FakeDevTunnelCli()
            .Answer("port create old-tunnel.euw -p 2584 --protocol http", 1, "Error: Forbidden.\nRequest ID: 1")
            .Answer("show old-tunnel.euw", 0, "Tunnel ID : old-tunnel.euw");

        var published = await new MobileDevTunnel(cli).PublishAsync(2584, "old-tunnel.euw", true, CancellationToken.None);

        Assert.AreEqual(("serving", "bold-tree-x1y2.euw"), (published.Status.State, published.TunnelId), published.Status.Detail);
        CollectionAssert.Contains(cli.Calls.ToArray(), "create");
    }

    [TestMethod]
    public async Task ATunnelIsMadeOnce_ForItsAccountAlone_ThePortAdded_AndHosted()
    {
        var cli = new FakeDevTunnelCli();

        var published = await new MobileDevTunnel(cli).PublishAsync(2584, null, true, CancellationToken.None);

        Assert.AreEqual(("serving", "https://bold-tree-x1y2-2584.euw.devtunnels.ms"), (published.Status.State, published.Status.Url), published.Status.Detail);
        Assert.AreEqual("bold-tree-x1y2.euw", published.TunnelId);
        CollectionAssert.AreEqual(new[] { "user show", "create", "port create bold-tree-x1y2.euw -p 2584 --protocol http", "access list bold-tree-x1y2.euw --port-number 2584 --json --nologo" }, cli.Calls.ToArray());
        Assert.AreEqual("host bold-tree-x1y2.euw", cli.Hosts.Single().Args);

        // The tunnel kept is used again: its address does not change.
        cli.Calls.Clear();
        cli.Answer("port create bold-tree-x1y2.euw -p 2584 --protocol http", 1, "Conflict: the port already exists.");
        var again = await new MobileDevTunnel(cli).PublishAsync(2584, "bold-tree-x1y2.euw", true, CancellationToken.None);
        Assert.AreEqual("serving", again.Status.State, again.Status.Detail);
        CollectionAssert.DoesNotContain(cli.Calls.ToArray(), "create");
    }

    [TestMethod]
    public async Task ATunnelOfBefore_ThatLetAnyoneIn_LetsItsAccountAloneIn()
    {
        var cli = new FakeDevTunnelCli().Answer("access reset bold-tree-x1y2.euw", 0, "Access control entries reset.");

        var published = await new MobileDevTunnel(cli).PublishAsync(2584, "bold-tree-x1y2.euw", false, CancellationToken.None);

        Assert.AreEqual(("serving", "bold-tree-x1y2.euw", true), (published.Status.State, published.TunnelId, published.Private), published.Status.Detail);
        var calls = cli.Calls.ToList();
        Assert.IsTrue(calls.IndexOf("access reset bold-tree-x1y2.euw") is >= 0 and var reset && reset < calls.IndexOf("port create bold-tree-x1y2.euw -p 2584 --protocol http"),
            "the access is reset before the view is published");
        CollectionAssert.DoesNotContain(calls, "create");

        // A tunnel already reset is not reset again.
        cli.Calls.Clear();
        await new MobileDevTunnel(cli).PublishAsync(2584, "bold-tree-x1y2.euw", true, CancellationToken.None);
        CollectionAssert.DoesNotContain(cli.Calls.ToArray(), "access reset bold-tree-x1y2.euw");
    }

    [TestMethod]
    public void TheAccessOfThePort_SaysWhetherAnyoneGetsIn()
    {
        const string inherited = """{"accessControlEntries":[{"type":"Anonymous","isDeny":false,"isInherited":true,"subjects":[],"scopes":["connect"]}]}""";
        const string denied = """{"accessControlEntries":[{"type":"Anonymous","isDeny":false,"isInherited":true,"subjects":[],"scopes":["connect"]},{"type":"Anonymous","isDeny":true,"isInherited":false,"subjects":[],"scopes":["connect"]}]}""";
        Assert.IsTrue(MobileDevTunnel.AnonymousIsDenied("""{"accessControlEntries":[]}"""), "the account of the tunnel alone");
        Assert.IsFalse(MobileDevTunnel.AnonymousIsDenied(inherited), "an allow the port inherits from the tunnel lets anyone in");
        Assert.IsTrue(MobileDevTunnel.AnonymousIsDenied(denied), "a deny wins");
        Assert.IsTrue(MobileDevTunnel.AnonymousIsDenied("""{"accessControlEntries":[{"type":"Anonymous","isInverse":true,"scopes":["connect"]}]}"""), "an inverse entry is about the users who signed in");
        Assert.IsTrue(MobileDevTunnel.AnonymousIsDenied("""{"accessControlEntries":[{"type":"Anonymous","scopes":["connect"],"expiration":"2001-01-01T00:00:00Z"}]}"""), "an expired allow");
        Assert.IsNull(MobileDevTunnel.AnonymousIsDenied("Error: unauthorized"));
    }

    [TestMethod]
    public async Task APortThatMayLetAnyoneIn_IsDeniedToAnonymousVisitors_OrNotServed()
    {
        const string list = "access list bold-tree-x1y2.euw --port-number 2584 --json --nologo";
        const string deny = "access create bold-tree-x1y2.euw --port-number 2584 --anonymous --deny --nologo";
        var open = """{"accessControlEntries":[{"type":"Anonymous","isDeny":false,"isInherited":true,"subjects":[],"scopes":["connect"]}]}""";
        var cli = new FakeDevTunnelCli().Answer(list, 0, open);

        var published = await new MobileDevTunnel(cli).PublishAsync(2584, "bold-tree-x1y2.euw", true, CancellationToken.None);

        Assert.AreEqual("serving", published.Status.State, published.Status.Detail);
        CollectionAssert.Contains(cli.Calls.ToArray(), deny, "anonymous visitors are denied on the port before it is hosted");
        Assert.AreEqual(1, cli.Hosts.Count);

        // A port that stays open is not served.
        cli = new FakeDevTunnelCli().Answer(list, 0, open).Answer(deny, 1, "Error: Forbidden.");
        var refused = await new MobileDevTunnel(cli).PublishAsync(2584, "bold-tree-x1y2.euw", true, CancellationToken.None);
        Assert.AreEqual("failed", refused.Status.State);
        StringAssert.Contains(refused.Status.Detail, "may let anyone in");
        Assert.AreEqual(0, cli.Hosts.Count, "nothing is hosted");

        // A port closed already is left as it is.
        cli = new FakeDevTunnelCli();
        await new MobileDevTunnel(cli).PublishAsync(2584, "bold-tree-x1y2.euw", true, CancellationToken.None);
        CollectionAssert.DoesNotContain(cli.Calls.ToArray(), deny);
    }

    [TestMethod]
    public async Task ATunnelOfBefore_WhoseAccessCannotBeReset_IsReplacedByANewOne()
    {
        var cli = new FakeDevTunnelCli().Answer("access reset old-tunnel.euw", 1, "Error: Forbidden.");

        var published = await new MobileDevTunnel(cli).PublishAsync(2584, "old-tunnel.euw", false, CancellationToken.None);

        Assert.AreEqual(("serving", "bold-tree-x1y2.euw", true), (published.Status.State, published.TunnelId, published.Private), published.Status.Detail);
        CollectionAssert.Contains(cli.Calls.ToArray(), "create");
        CollectionAssert.DoesNotContain(cli.Calls.ToArray(), "port create old-tunnel.euw -p 2584 --protocol http", "a tunnel that may let anyone in serves nothing");
    }

    [TestMethod]
    public async Task ASignInTheAccountNoLongerAccepts_AsksForTheSignInAgain()
    {
        // GitHub revoked the token, while the command line still says it is signed in.
        var cli = new FakeDevTunnelCli().Answer("create", 1, "GitHub login required.\nRequest ID: 42");

        var published = await new MobileDevTunnel(cli).PublishAsync(2584, null, true, CancellationToken.None);

        Assert.AreEqual("not-signed-in", published.Status.State, published.Status.Detail);
        Assert.IsTrue(MobileDevTunnel.NeedsSignIn("Error: Unauthorized"));
        Assert.IsFalse(MobileDevTunnel.NeedsSignIn("Error: Forbidden. Quota exceeded."));
    }

    [TestMethod]
    public async Task WhatKeepsTheTunnelFromServing_IsSaid()
    {
        var missing = await new MobileDevTunnel(new FakeDevTunnelCli { Installed = false }).PublishAsync(2584, null, true, CancellationToken.None);
        Assert.AreEqual(("not-installed", MobileDevTunnel.InstallUrl), (missing.Status.State, missing.Status.FixUrl));

        var cli = new FakeDevTunnelCli().Answer("user show", 0, "Not logged in.");
        Assert.AreEqual("not-signed-in", (await new MobileDevTunnel(cli).PublishAsync(2584, null, true, CancellationToken.None)).Status.State);

        cli = new FakeDevTunnelCli { HostLines = ["Error: the tunnel host failed: quota exceeded"] };
        var failed = await new MobileDevTunnel(cli).PublishAsync(2584, null, true, CancellationToken.None);
        Assert.AreEqual("failed", failed.Status.State);
        StringAssert.Contains(failed.Status.Detail, "quota exceeded");
        Assert.IsTrue(cli.Hosts.Single().Disposed, "a host that did not start is ended");
    }

    [TestMethod]
    public async Task ThePlugin_ServesThroughTheTunnel_KeepsIt_AndStopsItWhenPhonesGoBackToTailscale()
    {
        var port = FreePort.Next();
        var tunnel = new FakeDevTunnelCli { Port = port };
        await using var fixture = new Fixture(new FakeTailscaleCli(installed: false), plugin: new MobilePlugin(new FakeTailscaleCli(installed: false), tunnel));
        await fixture.StartAsync(port, 4443);
        var (page, _) = await fixture.OpenAsync(MobilePlugin.SettingsCanvasId);
        var (tab, _) = await fixture.OpenAsync(MobilePlugin.CanvasId);

        var answer = await page.InvokeAsync<MobileSettingsChange, MobileSettingsView>("mobile.settings.set", new MobileSettingsChange("devtunnel", port, 4443));
        Assert.AreEqual(("devtunnel", "pending"), (answer.Provider.Id, answer.Provider.State));
        await WaitAsync(() => fixture.Plugin.Settings().Provider.State != "pending", "the tunnel opens");

        var url = $"https://bold-tree-x1y2-{port}.euw.devtunnels.ms";
        Assert.AreEqual(url, fixture.Plugin.Settings().Address);
        var status = await tab.InvokeAsync<MobileEmpty, MobileStatus>("mobile.status", new MobileEmpty());
        Assert.AreEqual("devtunnel", status.Provider.Id);
        StringAssert.StartsWith(status.ConnectUrl, url + "/#k=", "the code holds the address of the tunnel and the pairing token");
        Assert.AreEqual(("devtunnel", "bold-tree-x1y2.euw", true), (fixture.State.Read().Mode, fixture.State.Read().TunnelId, fixture.State.Read().TunnelPrivate),
            "the tunnel is kept for the next start, known to let its account alone in");

        await page.InvokeAsync<MobileSettingsChange, MobileSettingsView>("mobile.settings.set", new MobileSettingsChange("tailscale", port, 4443));
        await WaitAsync(() => fixture.Plugin.Settings().Provider.State != "pending", "Tailscale is asked");
        Assert.IsTrue(tunnel.Hosts.Single().Disposed, "the host of the tunnel stops");
        Assert.IsNull(fixture.Plugin.Settings().Address);
    }

    [TestMethod]
    public async Task ThePortTheViewLeft_IsTakenOffTheTunnel_EvenWhenItsHostDidNotStart()
    {
        var cli = new FakeDevTunnelCli { HostLines = ["Error: the tunnel host failed"] };
        var host = new FakeProviderHost(new MobileSettings { Mode = MobileDevTunnelProvider.ProviderId, Port = 2584 });
        await using var provider = new MobileDevTunnelProvider(cli, host);

        Assert.AreEqual("failed", (await provider.PublishAsync(CancellationToken.None)).State);
        await host.SaveAsync(settings => settings with { Port = 2600 });
        await provider.PublishAsync(CancellationToken.None);

        var calls = cli.Calls.ToList();
        CollectionAssert.Contains(calls, "port delete bold-tree-x1y2.euw -p 2584", "the port given before the host failed is taken off");
        Assert.IsTrue(calls.IndexOf("port delete bold-tree-x1y2.euw -p 2584") < calls.IndexOf("port create bold-tree-x1y2.euw -p 2600 --protocol http"));
    }

    [TestMethod]
    public async Task ASignIn_IsAStepThatRuns_ThenSaysHowItEnded()
    {
        var port = FreePort.Next();
        var tunnel = new FakeDevTunnelCli { Port = port }.Answer("user show", 0, "Not logged in.");
        await using var fixture = new Fixture(new FakeTailscaleCli(installed: false), plugin: new MobilePlugin(new FakeTailscaleCli(installed: false), tunnel));
        await fixture.State.WriteJsonAsync(PluginStateScope.User, "mobile.json", new MobileSettings { Mode = MobileDevTunnelProvider.ProviderId, Port = port });
        await fixture.StartAsync(port, 4443, write: false);
        Assert.AreEqual("not-signed-in", fixture.Plugin.Settings().Provider.State);
        var (tab, _) = await fixture.OpenAsync(MobilePlugin.CanvasId);

        var acted = await tab.InvokeAsync<MobileAction, MobileStatus>("mobile.act", new MobileAction("signin-github"));

        Assert.AreEqual(("signing-in", true), (acted.Provider.State, acted.Provider.Working), "the sign-in runs: a wait, not a problem");
        await WaitAsync(() => fixture.Plugin.Settings().Provider is { State: "not-signed-in", Working: false }, "the sign-in that did not end is said");
        var unknown = await tab.InvokeAsync<MobileAction, MobileStatus>("mobile.act", new MobileAction("dance"));
        Assert.AreEqual("not-signed-in", unknown.Provider.State, "an action the provider does not know changes nothing");
    }

    [TestMethod]
    public async Task AProviderThatWasReplaced_SaysNothingAnyMore()
    {
        var port = FreePort.Next();
        IMobileProviderHost? replaced = null;
        var plugin = new MobilePlugin((id, host) => id switch
        {
            MobileTailscaleProvider.ProviderId => new MobileTailscaleProvider(new FakeTailscaleCli(installed: false), host),
            MobileDevTunnelProvider.ProviderId => new SilentProvider(replaced = host),
            _ => null,
        });
        await using var fixture = new Fixture(new FakeTailscaleCli(installed: false), plugin: plugin);
        await fixture.State.WriteJsonAsync(PluginStateScope.User, "mobile.json", new MobileSettings { Mode = MobileDevTunnelProvider.ProviderId, Port = port });
        await fixture.StartAsync(port, 4443, write: false);
        var (page, _) = await fixture.OpenAsync(MobilePlugin.SettingsCanvasId);

        await page.InvokeAsync<MobileSettingsChange, MobileSettingsView>("mobile.settings.set", new MobileSettingsChange("tailscale", port, 4443));
        await WaitAsync(() => fixture.Plugin.Settings().Provider.State == "not-installed", "Tailscale is asked");
        await replaced!.ReportAsync(new MobileProviderStatus("serving", "https://stale.example", "A tunnel of before."));
        await replaced.RepublishAsync();

        Assert.AreEqual(("tailscale", "not-installed"), (fixture.Plugin.Settings().Provider.Id, fixture.Plugin.Settings().Provider.State));
    }

    // A provider of the tests that publishes nothing and keeps its host.
    private sealed class SilentProvider(IMobileProviderHost host) : IMobileProvider
    {
        public string Id => MobileDevTunnelProvider.ProviderId;

        public MobileListen Listen(MobileSettings settings) => new(settings.Port);

        public Task<MobileProviderStatus> PublishAsync(CancellationToken cancellationToken) => Task.FromResult(new MobileProviderStatus("not-signed-in", null, host.Settings.Mode));

        public Task UnpublishAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // What the plugin gives a provider, in memory.
    private sealed class FakeProviderHost(MobileSettings settings) : IMobileProviderHost
    {
        public MobileSettings Settings { get; private set; } = settings;

        public List<MobileProviderStatus> Reports { get; } = [];

        public Task SaveAsync(Func<MobileSettings, MobileSettings> change)
        {
            Settings = change(Settings);
            return Task.CompletedTask;
        }

        public Task ReportAsync(MobileProviderStatus status)
        {
            lock (Reports) Reports.Add(status);
            return Task.CompletedTask;
        }

        public Task RepublishAsync() => Task.CompletedTask;

        public void Run(string name, string description, Func<CancellationToken, ValueTask> work) => _ = Task.Run(async () => await work(CancellationToken.None));

        public void Log(string line)
        {
        }
    }

    /// <summary>A Dev Tunnels command line of the tests: signed in, it makes the tunnel <c>bold-tree-x1y2.euw</c> and hosts it.</summary>
    private sealed class FakeDevTunnelCli : IMobileDevTunnelCli
    {
        private readonly Dictionary<string, MobileCliResult> _answers = new(StringComparer.Ordinal)
        {
            ["user show"] = new(0, "Logged in as someone using GitHub."),
            ["create"] = new(0, "Tunnel ID             : bold-tree-x1y2.euw\nAccess control        : {}"),
            ["show bold-tree-x1y2.euw"] = new(0, "Tunnel ID             : bold-tree-x1y2.euw"),
        };

        internal bool Installed { get; init; } = true;

        internal int Port { get; init; } = 2584;

        /// <summary>What the host writes; by default the address of the port and that it is ready.</summary>
        internal string[]? HostLines { get; init; }

        internal List<string> Calls { get; } = [];

        internal List<FakeHost> Hosts { get; } = [];

        public bool IsInstalled => Installed;

        internal FakeDevTunnelCli Answer(string args, int exitCode, string output)
        {
            _answers[args] = new MobileCliResult(exitCode, output);
            return this;
        }

        public Task<MobileCliResult> RunAsync(IReadOnlyList<string> args, TimeSpan patience, CancellationToken cancellationToken)
        {
            var line = string.Join(' ', args);
            lock (Calls) Calls.Add(line);
            if (_answers.TryGetValue(line, out var answer)) return Task.FromResult(answer);
            if (line.StartsWith("access list ", StringComparison.Ordinal)) return Task.FromResult(new MobileCliResult(0, """{"accessControlEntries":[]}"""));
            return Task.FromResult(line.StartsWith("port ", StringComparison.Ordinal) || line.StartsWith("access create ", StringComparison.Ordinal)
                ? new MobileCliResult(0, "") : new MobileCliResult(1, "unknown command: " + line));
        }

        public IMobileHostedProcess Start(IReadOnlyList<string> args)
        {
            var host = new FakeHost(string.Join(' ', args), HostLines ?? [
                "Hosting port: " + Port,
                $"Connect via browser: https://bold-tree-x1y2-{Port}.euw.devtunnels.ms",
                $"Inspect network activity: https://bold-tree-x1y2-{Port}-inspect.euw.devtunnels.ms",
                "Ready to accept connections for tunnel: bold-tree-x1y2.euw",
            ], exits: HostLines is not null);
            Hosts.Add(host);
            return host;
        }
    }

    /// <summary>A host of the tests: it writes its lines, then runs until disposed, or exits at once when told so.</summary>
    internal sealed class FakeHost : IMobileHostedProcess
    {
        private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();

        internal FakeHost(string args, IEnumerable<string> lines, bool exits)
        {
            Args = args;
            foreach (var line in lines) _lines.Writer.TryWrite(line);
            if (exits) _lines.Writer.TryComplete();
            IsRunning = !exits;
        }

        internal string Args { get; }

        internal bool Disposed { get; private set; }

        public ChannelReader<string> Lines => _lines.Reader;

        public bool IsRunning { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            IsRunning = false;
            _lines.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
