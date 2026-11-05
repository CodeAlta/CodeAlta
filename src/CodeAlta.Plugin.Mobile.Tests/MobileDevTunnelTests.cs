using System.Threading.Channels;
using CodeAlta.Plugins.Abstractions;
using static CodeAlta.Plugin.Mobile.Tests.MobileSettingsTests;

using CodeAlta.Plugins.PrivateNetworks;

namespace CodeAlta.Plugin.Mobile.Tests;

/// <summary>The Dev Tunnel of the mobile view, against a command line of the tests that answers as <c>devtunnel</c> does.</summary>
[TestClass]
public sealed class MobileDevTunnelTests
{
    [TestMethod]
    public void TheOutputsOfTheCommandLine_GiveTheTunnel_TheAddressOfThePort_AndTheSignIn()
    {
        Assert.AreEqual("bold-tree-x1y2.euw", DevTunnel.TunnelIdIn("Tunnel ID             : bold-tree-x1y2.euw\nDescription           :\nAccess control        : {+Anonymous [connect]}"));
        Assert.IsNull(DevTunnel.TunnelIdIn("Error: unauthorized"));
        Assert.AreEqual("https://bold-tree-x1y2-2584.euw.devtunnels.ms", DevTunnel.UrlIn("Connect via browser: https://bold-tree-x1y2-2584.euw.devtunnels.ms", 2584));
        Assert.IsNull(DevTunnel.UrlIn("Inspect network activity: https://bold-tree-x1y2-2584-inspect.euw.devtunnels.ms", 2584), "the page that inspects the traffic is not the view");
        Assert.IsNull(DevTunnel.UrlIn("Connect via browser: https://bold-tree-x1y2-3000.euw.devtunnels.ms", 2584), "another port");
        Assert.IsTrue(DevTunnel.IsSignedIn(new CommandLineResult(0, "Logged in as someone using GitHub.")));
        Assert.IsFalse(DevTunnel.IsSignedIn(new CommandLineResult(0, "Not logged in.")));
        Assert.IsFalse(DevTunnel.IsSignedIn(new CommandLineResult(1, "")));
        Assert.IsFalse(DevTunnel.IsSignedIn(new CommandLineResult(0, "Logged in as someone@example.com using Microsoft.")), "the sign-in is a GitHub one");
        Assert.AreEqual("Error: Forbidden. The user is not allowed to change the tunnel.",
            DevTunnel.Summary("Welcome to dev tunnels!\nError: Forbidden. The user is not allowed to change the tunnel.\nRequest ID: 9f818614-208b-477d-9084-9a0a82e60a26"),
            "the request id says nothing to a person");
    }

    [TestMethod]
    public async Task ATunnelKeptThatRefusesThePort_IsReplacedByANewOne()
    {
        var cli = new FakeDevTunnelCli()
            .Answer("port create old-tunnel.euw -p 2584 --protocol http", 1, "Error: Forbidden.\nRequest ID: 1")
            .Answer("show old-tunnel.euw", 0, "Tunnel ID : old-tunnel.euw");

        var published = await new DevTunnel(cli).PublishAsync(2584, "old-tunnel.euw", CancellationToken.None);

        Assert.AreEqual(("serving", "bold-tree-x1y2.euw"), (published.Status.State, published.TunnelId), published.Status.Detail);
        CollectionAssert.Contains(cli.Calls.ToArray(), "create");
    }

    [TestMethod]
    public async Task ATunnelIsMadeOnce_ForItsAccountAlone_ThePortAdded_AndHosted()
    {
        var cli = new FakeDevTunnelCli();

        var published = await new DevTunnel(cli).PublishAsync(2584, null, CancellationToken.None);

        Assert.AreEqual(("serving", "https://bold-tree-x1y2-2584.euw.devtunnels.ms"), (published.Status.State, published.Status.Url), published.Status.Detail);
        Assert.AreEqual("bold-tree-x1y2.euw", published.TunnelId);
        CollectionAssert.AreEqual(new[] { "user show", "create", "port create bold-tree-x1y2.euw -p 2584 --protocol http" }, cli.Calls.ToArray(),
            "a tunnel made now lets in its account alone: its access is not read");
        Assert.AreEqual("host bold-tree-x1y2.euw", cli.Hosts.Single().Args);

        // The tunnel kept is used again: its address does not change.
        cli.Calls.Clear();
        cli.Answer("port create bold-tree-x1y2.euw -p 2584 --protocol http", 1, "Conflict: the port already exists.");
        var again = await new DevTunnel(cli).PublishAsync(2584, "bold-tree-x1y2.euw", CancellationToken.None);
        Assert.AreEqual("serving", again.Status.State, again.Status.Detail);
        CollectionAssert.DoesNotContain(cli.Calls.ToArray(), "create");
    }

    [TestMethod]
    public void TheAccessOfThePort_SaysWhetherAnyoneGetsIn()
    {
        const string inherited = """{"accessControlEntries":[{"type":"Anonymous","isDeny":false,"isInherited":true,"subjects":[],"scopes":["connect"]}]}""";
        const string denied = """{"accessControlEntries":[{"type":"Anonymous","isDeny":false,"isInherited":true,"subjects":[],"scopes":["connect"]},{"type":"Anonymous","isDeny":true,"isInherited":false,"subjects":[],"scopes":["connect"]}]}""";
        Assert.IsTrue(DevTunnel.AnonymousIsDenied("""{"accessControlEntries":[]}"""), "the account of the tunnel alone");
        Assert.IsFalse(DevTunnel.AnonymousIsDenied(inherited), "an allow the port inherits from the tunnel lets anyone in");
        Assert.IsTrue(DevTunnel.AnonymousIsDenied(denied), "a deny wins");
        Assert.IsTrue(DevTunnel.AnonymousIsDenied("""{"accessControlEntries":[{"type":"Anonymous","isInverse":true,"scopes":["connect"]}]}"""), "an inverse entry is about the users who signed in");
        Assert.IsTrue(DevTunnel.AnonymousIsDenied("""{"accessControlEntries":[{"type":"Anonymous","scopes":["connect"],"expiration":"2001-01-01T00:00:00Z"}]}"""), "an expired allow");
        Assert.IsNull(DevTunnel.AnonymousIsDenied("Error: unauthorized"));
        Assert.IsFalse(DevTunnel.AnonymousIsDenied("""{"accessControlEntries":[{"type":"Anonymous","isInherited":true,"scopes":["connect"]},{"type":"Anonymous","isDeny":true,"scopes":["connect"],"expiration":"2999-01-01T00:00:00Z"}]}"""),
            "a deny that ends is not a closed port");
        Assert.IsTrue(DevTunnel.AnonymousIsDenied("""{"accessControlEntries":[]}""" + "\nA new version {1.2} is available."), "what the command line writes after the JSON is left");
    }

    [TestMethod]
    public async Task APortThatMayLetAnyoneIn_IsDeniedToAnonymousVisitors_OrNotServed()
    {
        const string list = "access list bold-tree-x1y2.euw --port-number 2584 --json --nologo";
        const string deny = "access create bold-tree-x1y2.euw --port-number 2584 --anonymous --deny --nologo";
        var open = """{"accessControlEntries":[{"type":"Anonymous","isDeny":false,"isInherited":true,"subjects":[],"scopes":["connect"]}]}""";
        var cli = new FakeDevTunnelCli().Answer(list, 0, open);

        var published = await new DevTunnel(cli).PublishAsync(2584, "bold-tree-x1y2.euw", CancellationToken.None);

        Assert.AreEqual("serving", published.Status.State, published.Status.Detail);
        CollectionAssert.Contains(cli.Calls.ToArray(), deny, "anonymous visitors are denied on the port before it is hosted");
        Assert.AreEqual(1, cli.Hosts.Count);

        // A deny that does not close the port: it is not served.
        cli = new FakeDevTunnelCli { DenyIgnored = true }.Answer(list, 0, open);
        var still = await new DevTunnel(cli).PublishAsync(2584, "bold-tree-x1y2.euw", CancellationToken.None);
        Assert.AreEqual(("failed", 0), (still.Status.State, cli.Hosts.Count), still.Status.Detail);
        StringAssert.Contains(still.Status.Detail, "did not close it");

        // A port that stays open is not served.
        cli = new FakeDevTunnelCli().Answer(list, 0, open).Answer(deny, 1, "Error: Forbidden.");
        var refused = await new DevTunnel(cli).PublishAsync(2584, "bold-tree-x1y2.euw", CancellationToken.None);
        Assert.AreEqual("failed", refused.Status.State);
        StringAssert.Contains(refused.Status.Detail, "may let anyone in");
        Assert.AreEqual(0, cli.Hosts.Count, "nothing is hosted");

        // A port closed already is left as it is.
        cli = new FakeDevTunnelCli();
        await new DevTunnel(cli).PublishAsync(2584, "bold-tree-x1y2.euw", CancellationToken.None);
        CollectionAssert.DoesNotContain(cli.Calls.ToArray(), deny);
    }

    [TestMethod]
    public async Task ASignInTheAccountNoLongerAccepts_AsksForTheSignInAgain()
    {
        // GitHub revoked the token, while the command line still says it is signed in.
        var cli = new FakeDevTunnelCli().Answer("create", 1, "GitHub login required.\nRequest ID: 42");

        var published = await new DevTunnel(cli).PublishAsync(2584, null, CancellationToken.None);

        Assert.AreEqual("not-signed-in", published.Status.State, published.Status.Detail);
        Assert.IsTrue(DevTunnel.NeedsSignIn("Error: Unauthorized"));
        Assert.IsFalse(DevTunnel.NeedsSignIn("Error: Forbidden. Quota exceeded."));
    }

    [TestMethod]
    public async Task WhatKeepsTheTunnelFromServing_IsSaid()
    {
        var missing = await new DevTunnel(new FakeDevTunnelCli { Installed = false }).PublishAsync(2584, null, CancellationToken.None);
        Assert.AreEqual(("not-installed", DevTunnel.InstallUrl), (missing.Status.State, missing.Status.FixUrl));

        var cli = new FakeDevTunnelCli().Answer("user show", 0, "Not logged in.");
        Assert.AreEqual("not-signed-in", (await new DevTunnel(cli).PublishAsync(2584, null, CancellationToken.None)).Status.State);

        cli = new FakeDevTunnelCli { HostLines = ["Error: the tunnel host failed: quota exceeded"] };
        var failed = await new DevTunnel(cli).PublishAsync(2584, null, CancellationToken.None);
        Assert.AreEqual("failed", failed.Status.State);
        StringAssert.Contains(failed.Status.Detail, "quota exceeded");
        Assert.IsTrue(cli.Hosts.Single().Disposed, "a host that did not start is ended");
    }

    [TestMethod]
    public async Task ThePlugin_ServesThroughTheTunnel_KeepsIt_AndStopsItWhenPhonesGoBackToTailscale()
    {
        var port = FreePort.Next();
        var tunnel = new FakeDevTunnelCli { Port = port };
        await using var fixture = new Fixture(new FakeTailscaleCli(installed: false), plugin: MobileFakes.Plugin(new FakeTailscaleCli(installed: false), tunnel));
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
        var kept = await fixture.State.ReadJsonAsync<Dictionary<string, string>>(PluginStateScope.User, "mobile-networks.json");
        Assert.AreEqual(("devtunnel", "bold-tree-x1y2.euw"), (fixture.State.Read().Mode, kept?["devtunnel/" + DevTunnelNetwork.TunnelKey]), "the tunnel is kept for the next start");

        await page.InvokeAsync<MobileSettingsChange, MobileSettingsView>("mobile.settings.set", new MobileSettingsChange("tailscale", port, 4443));
        await WaitAsync(() => fixture.Plugin.Settings().Provider.State != "pending", "Tailscale is asked");
        Assert.IsTrue(tunnel.Hosts.Single().Disposed, "the host of the tunnel stops");
        Assert.IsNull(fixture.Plugin.Settings().Address);
    }

    [TestMethod]
    public async Task ThePortTheViewLeft_IsTakenOffTheTunnel_EvenWhenItsHostDidNotStart()
    {
        var cli = new FakeDevTunnelCli { HostLines = ["Error: the tunnel host failed"] };
        var host = new FakeProviderHost();
        await using var provider = new DevTunnelNetwork(cli).CreatePublisher(host);

        Assert.AreEqual("failed", (await provider.PublishAsync(new PrivateNetworkPublication(2584), CancellationToken.None)).State);
        await provider.PublishAsync(new PrivateNetworkPublication(2600), CancellationToken.None);

        var calls = cli.Calls.ToList();
        CollectionAssert.Contains(calls, "port delete bold-tree-x1y2.euw -p 2584", "the port given before the host failed is taken off");
        Assert.IsTrue(calls.IndexOf("port delete bold-tree-x1y2.euw -p 2584") < calls.IndexOf("port create bold-tree-x1y2.euw -p 2600 --protocol http"));
    }

    [TestMethod]
    public async Task ASignIn_IsAStepThatRuns_ThenSaysHowItEnded()
    {
        var port = FreePort.Next();
        var tunnel = new FakeDevTunnelCli { Port = port }.Answer("user show", 0, "Not logged in.");
        await using var fixture = new Fixture(new FakeTailscaleCli(installed: false), plugin: MobileFakes.Plugin(new FakeTailscaleCli(installed: false), tunnel));
        await fixture.State.WriteJsonAsync(PluginStateScope.User, "mobile.json", new MobileSettings { Mode = DevTunnelNetwork.ProviderId, Port = port });
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
        IPrivateNetworkHost? replaced = null;
        IReadOnlyList<IPrivateNetworkProvider> networks = [new TailscaleNetwork(new FakeTailscaleCli(installed: false)), new SilentNetwork(host => replaced = host)];
        var plugin = new MobilePlugin(() => networks);
        await using var fixture = new Fixture(new FakeTailscaleCli(installed: false), plugin: plugin);
        await fixture.State.WriteJsonAsync(PluginStateScope.User, "mobile.json", new MobileSettings { Mode = DevTunnelNetwork.ProviderId, Port = port });
        await fixture.StartAsync(port, 4443, write: false);
        var (page, _) = await fixture.OpenAsync(MobilePlugin.SettingsCanvasId);

        await page.InvokeAsync<MobileSettingsChange, MobileSettingsView>("mobile.settings.set", new MobileSettingsChange("tailscale", port, 4443));
        await WaitAsync(() => fixture.Plugin.Settings().Provider.State == "not-installed", "Tailscale is asked");
        await replaced!.ReportAsync(new PrivateNetworkStatus("serving", "https://stale.example", "A tunnel of before."));
        await replaced.RepublishAsync();

        Assert.AreEqual(("tailscale", "not-installed"), (fixture.Plugin.Settings().Provider.Id, fixture.Plugin.Settings().Provider.State));
    }

    // A network of the tests in the place of Dev Tunnels, whose publisher publishes nothing and keeps its host.
    private sealed class SilentNetwork(Action<IPrivateNetworkHost> created) : IPrivateNetworkProvider
    {
        public string Id => DevTunnelNetwork.ProviderId;

        public string DisplayName => "Silent";

        public PrivateNetworkCapabilities Capabilities => PrivateNetworkCapabilities.Publish;

        public IPrivateNetworkPublisher CreatePublisher(IPrivateNetworkHost host)
        {
            created(host);
            return new SilentProvider();
        }
    }

    private sealed class SilentProvider : IPrivateNetworkPublisher
    {
        public Task<PrivateNetworkStatus> PublishAsync(PrivateNetworkPublication publication, CancellationToken cancellationToken)
            => Task.FromResult(new PrivateNetworkStatus("not-signed-in", null, "Silent."));

        public Task UnpublishAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // What the plugin gives a publisher, in memory.
    private sealed class FakeProviderHost : IPrivateNetworkHost
    {
        private readonly Dictionary<string, string> _state = [];

        public List<PrivateNetworkStatus> Reports { get; } = [];

        public Task<string?> ReadStateAsync(string key, CancellationToken cancellationToken) => Task.FromResult(_state.TryGetValue(key, out var value) ? value : null);

        public Task WriteStateAsync(string key, string? value, CancellationToken cancellationToken)
        {
            if (value is null) _state.Remove(key);
            else _state[key] = value;
            return Task.CompletedTask;
        }

        public Task ReportAsync(PrivateNetworkStatus status)
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
    private sealed class FakeDevTunnelCli : IDevTunnelCli
    {
        private readonly Dictionary<string, CommandLineResult> _answers = new(StringComparer.Ordinal)
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

        /// <summary>Whether a deny of anonymous access leaves the access of the port as it was.</summary>
        internal bool DenyIgnored { get; init; }

        // The access lists a deny closed.
        private readonly HashSet<string> _denied = new(StringComparer.Ordinal);

        public bool IsInstalled => Installed;

        internal FakeDevTunnelCli Answer(string args, int exitCode, string output)
        {
            _answers[args] = new CommandLineResult(exitCode, output);
            return this;
        }

        public Task<CommandLineResult> RunAsync(IReadOnlyList<string> args, TimeSpan patience, CancellationToken cancellationToken)
        {
            var line = string.Join(' ', args);
            lock (Calls) Calls.Add(line);
            if (_denied.Contains(line)) return Task.FromResult(new CommandLineResult(0, """{"accessControlEntries":[{"type":"Anonymous","isDeny":true,"scopes":["connect"]}]}"""));
            if (_answers.TryGetValue(line, out var answer))
            {
                if (answer.ExitCode == 0 && !DenyIgnored && line.StartsWith("access create ", StringComparison.Ordinal) && line.EndsWith("--anonymous --deny --nologo", StringComparison.Ordinal))
                    _denied.Add(line.Replace("access create", "access list", StringComparison.Ordinal).Replace("--anonymous --deny --nologo", "--json --nologo", StringComparison.Ordinal));
                return Task.FromResult(answer);
            }

            if (!DenyIgnored && line.StartsWith("access create ", StringComparison.Ordinal) && line.EndsWith("--anonymous --deny --nologo", StringComparison.Ordinal))
                _denied.Add(line.Replace("access create", "access list", StringComparison.Ordinal).Replace("--anonymous --deny --nologo", "--json --nologo", StringComparison.Ordinal));
            if (line.StartsWith("access list ", StringComparison.Ordinal)) return Task.FromResult(new CommandLineResult(0, """{"accessControlEntries":[]}"""));
            return Task.FromResult(line.StartsWith("port ", StringComparison.Ordinal) || line.StartsWith("access create ", StringComparison.Ordinal)
                ? new CommandLineResult(0, "") : new CommandLineResult(1, "unknown command: " + line));
        }

        public IHostedCommand Start(IReadOnlyList<string> args)
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
    internal sealed class FakeHost : IHostedCommand
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
