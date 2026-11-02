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

        var published = await new MobileDevTunnel(cli).PublishAsync(2584, "old-tunnel.euw", CancellationToken.None);

        Assert.AreEqual(("serving", "bold-tree-x1y2.euw"), (published.Status.State, published.TunnelId), published.Status.Detail);
        CollectionAssert.Contains(cli.Calls.ToArray(), "create --allow-anonymous");
    }

    [TestMethod]
    public async Task ATunnelIsMadeOnce_WithAnonymousAccess_ThePortAdded_AndHosted()
    {
        var cli = new FakeDevTunnelCli();

        var published = await new MobileDevTunnel(cli).PublishAsync(2584, null, CancellationToken.None);

        Assert.AreEqual(("serving", "https://bold-tree-x1y2-2584.euw.devtunnels.ms"), (published.Status.State, published.Status.Url), published.Status.Detail);
        Assert.AreEqual("bold-tree-x1y2.euw", published.TunnelId);
        CollectionAssert.AreEqual(new[] { "user show", "create --allow-anonymous", "port create bold-tree-x1y2.euw -p 2584 --protocol http" }, cli.Calls.ToArray());
        Assert.AreEqual("host bold-tree-x1y2.euw", cli.Hosts.Single().Args);

        // The tunnel kept is used again: its address does not change.
        cli.Calls.Clear();
        cli.Answer("port create bold-tree-x1y2.euw -p 2584 --protocol http", 1, "Conflict: the port already exists.");
        var again = await new MobileDevTunnel(cli).PublishAsync(2584, "bold-tree-x1y2.euw", CancellationToken.None);
        Assert.AreEqual("serving", again.Status.State, again.Status.Detail);
        CollectionAssert.DoesNotContain(cli.Calls.ToArray(), "create --allow-anonymous");
    }

    [TestMethod]
    public async Task ASignInTheAccountNoLongerAccepts_AsksForTheSignInAgain()
    {
        // GitHub revoked the token, while the command line still says it is signed in.
        var cli = new FakeDevTunnelCli().Answer("create --allow-anonymous", 1, "GitHub login required.\nRequest ID: 42");

        var published = await new MobileDevTunnel(cli).PublishAsync(2584, null, CancellationToken.None);

        Assert.AreEqual("not-signed-in", published.Status.State, published.Status.Detail);
        Assert.IsTrue(MobileDevTunnel.NeedsSignIn("Error: Unauthorized"));
        Assert.IsFalse(MobileDevTunnel.NeedsSignIn("Error: Forbidden. Quota exceeded."));
    }

    [TestMethod]
    public async Task WhatKeepsTheTunnelFromServing_IsSaid()
    {
        var missing = await new MobileDevTunnel(new FakeDevTunnelCli { Installed = false }).PublishAsync(2584, null, CancellationToken.None);
        Assert.AreEqual(("not-installed", MobileDevTunnel.InstallUrl), (missing.Status.State, missing.Status.FixUrl));

        var cli = new FakeDevTunnelCli().Answer("user show", 0, "Not logged in.");
        Assert.AreEqual("not-signed-in", (await new MobileDevTunnel(cli).PublishAsync(2584, null, CancellationToken.None)).Status.State);

        cli = new FakeDevTunnelCli { HostLines = ["Error: the tunnel host failed: quota exceeded"] };
        var failed = await new MobileDevTunnel(cli).PublishAsync(2584, null, CancellationToken.None);
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

        var answer = await page.InvokeAsync<MobileSettingsChange, MobileSettingsView>("mobile.settings.set", new MobileSettingsChange("devtunnel", port, 4443, null));
        Assert.AreEqual(("devtunnel", "pending"), (answer.Provider.Id, answer.Provider.State));
        await WaitAsync(() => fixture.Plugin.Settings().Provider.State != "pending", "the tunnel opens");

        var url = $"https://bold-tree-x1y2-{port}.euw.devtunnels.ms";
        Assert.AreEqual(url, fixture.Plugin.Settings().Address);
        var status = await tab.InvokeAsync<MobileEmpty, MobileStatus>("mobile.status", new MobileEmpty());
        Assert.AreEqual("devtunnel", status.Provider.Id);
        StringAssert.StartsWith(status.ConnectUrl, url + "/#k=", "the code holds the address of the tunnel and the pairing token");
        Assert.AreEqual(("devtunnel", "bold-tree-x1y2.euw"), (fixture.State.Read().Mode, fixture.State.Read().TunnelId), "the tunnel is kept for the next start");

        await page.InvokeAsync<MobileSettingsChange, MobileSettingsView>("mobile.settings.set", new MobileSettingsChange("tailscale", port, 4443, null));
        await WaitAsync(() => fixture.Plugin.Settings().Provider.State != "pending", "Tailscale is asked");
        Assert.IsTrue(tunnel.Hosts.Single().Disposed, "the host of the tunnel stops");
        Assert.IsNull(fixture.Plugin.Settings().Address);
    }

    /// <summary>A Dev Tunnels command line of the tests: signed in, it makes the tunnel <c>bold-tree-x1y2.euw</c> and hosts it.</summary>
    private sealed class FakeDevTunnelCli : IMobileDevTunnelCli
    {
        private readonly Dictionary<string, MobileCliResult> _answers = new(StringComparer.Ordinal)
        {
            ["user show"] = new(0, "Logged in as someone using GitHub."),
            ["create --allow-anonymous"] = new(0, "Tunnel ID             : bold-tree-x1y2.euw\nAccess control        : {+Anonymous [connect]}"),
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
            return Task.FromResult(line.StartsWith("port ", StringComparison.Ordinal) ? new MobileCliResult(0, "") : new MobileCliResult(1, "unknown command: " + line));
        }

        public Task<int> RunInWindowAsync(IReadOnlyList<string> args, TimeSpan patience, CancellationToken cancellationToken)
        {
            lock (Calls) Calls.Add("window: " + string.Join(' ', args));
            return Task.FromResult(0);
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
