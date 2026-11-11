using System.Buffers.Text;
using System.Text;
using System.Text.Json;
using CodeAlta.Fleet.Update;
using CodeAlta.Fleet.Update.Service;
using CodeAlta.Fleet.Update.Tests;
using CodeAlta.Server.Hosting;

namespace CodeAlta.Server.Tests;

[TestClass]
public sealed class ServerDevTunnelTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task Install_CreatesTwoUserServices_ReusesTheTunnelId_AndUninstallKeepsIt()
    {
        using var machine = new FakeServiceMachine(ServicePlatform.Linux);
        var root = Path.Combine(machine.Root, "tunnel-server");
        var cli = new Commands();
        var output = new StringWriter();
        var error = new StringWriter();
        var app = new ServerApplication(output, error)
        {
            ServiceEnvironment = () => machine.Environment,
            ServiceExecutable = () => new ServiceExecutable(ServiceExecutableKind.ToolShim, "/home/alice/.dotnet/tools/altaserver", []),
            DevTunnelCommands = cli,
        };
        Assert.AreEqual(0, await app.RunAsync(["install", "--data-root", root, "--port", "2693", "--dev-tunnel"]), error.ToString());
        var name = ServiceDefinition.ServiceName(UpdateProduct.Server, root, false, false);
        var directory = Path.Combine(machine.Home, ".config", "systemd", "user");
        StringAssert.Contains(File.ReadAllText(Path.Combine(directory, name + ".service")), "--urls http://127.0.0.1:2693 --trusted-proxy");
        StringAssert.Contains(File.ReadAllText(Path.Combine(directory, name + "-tunnel.service")), "tunnel host --data-root");
        Assert.AreEqual("personal.euw", new ServerDevTunnel(new ServerPaths(root), cli).Read()!.TunnelId);
        Assert.AreEqual(0, await app.RunAsync(["install", "--data-root", root, "--port", "2693", "--dev-tunnel"]), error.ToString());
        Assert.AreEqual(1, cli.Calls.Count(static args => args[0] == "create"), "the URL's identity survives reinstall");
        Assert.IsFalse(cli.Calls.SelectMany(static args => args).Any(static arg => arg.Contains("anonymous", StringComparison.Ordinal)), "no anonymous allow is ever created");
        Assert.AreEqual(0, await app.RunAsync(["uninstall", "--data-root", root]), error.ToString());
        Assert.IsFalse(File.Exists(Path.Combine(directory, name + "-tunnel.service")));
        Assert.IsNotNull(new ServerDevTunnel(new ServerPaths(root), cli).Read(), "removing services does not delete the person's tunnel");
    }

    [TestMethod]
    public async Task ADeviceCodeLogin_IsStreamed_PrivateAccessIsRequired_AndDryRunStartsNothing()
    {
        using var machine = new FakeServiceMachine(ServicePlatform.Linux);
        var root = Path.Combine(machine.Root, "tunnel-server");
        var cli = new Commands { SignedIn = false };
        var lines = new List<string>();
        var service = new ServerDevTunnel(new ServerPaths(root), cli);
        await service.ConfigureAsync(2586, lines.Add, TestContext.CancellationToken);
        Assert.IsTrue(lines.Any(static line => line.Contains("ABCD-EFGH", StringComparison.Ordinal)));
        CollectionAssert.AreEqual(new[] { "user", "login", "--github", "-d" }, cli.Calls.Single(static args => args is ["user", "login", ..]));
        cli.Access = """{"accessControlEntries":[{"type":"Anonymous","scopes":["connect"]}]}""";
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.ConfigureAsync(2586, lines.Add, TestContext.CancellationToken));
        Assert.AreEqual(1, cli.Calls.Count(static args => args[0] == "create"), "an access failure does not make a different tunnel");

        var dry = new Commands();
        var app = new ServerApplication(new StringWriter(), new StringWriter())
        {
            ServiceEnvironment = () => machine.Environment,
            ServiceExecutable = () => new ServiceExecutable(ServiceExecutableKind.ToolShim, "/home/alice/.dotnet/tools/altaserver", []),
            DevTunnelCommands = dry,
        };
        Assert.AreEqual(0, await app.RunAsync(["install", "--dry-run", "--data-root", root, "--dev-tunnel"]));
        Assert.HasCount(0, dry.Calls);
        Assert.HasCount(0, machine.Commands, "dry run never changes the machine");
    }

    [TestMethod]
    public async Task TheHostReportsTheRealAddress_AndConnectTokensAreBoundedByTheirActualExpiration()
    {
        using var folder = new TempDirectory();
        var paths = new ServerPaths(folder.Path);
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var cli = new Commands { TokenExpiry = clock.GetUtcNow().AddHours(3) };
        var service = new ServerDevTunnel(paths, cli, clock);
        await service.ConfigureAsync(2586, static _ => { }, TestContext.CancellationToken);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        var reported = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = service.HostAsync(value => reported.TrySetResult(value), stop.Token);
        try
        {
            Assert.AreEqual("https://personal-2586.euw.devtunnels.ms", await reported.Task.WithinAsync(TestTime.Seconds(10), TestContext.CancellationToken));
            var access = await service.IssueAsync(TestContext.CancellationToken);
            Assert.IsNotNull(access);
            Assert.IsTrue(access.ExpiresAt < cli.TokenExpiry && access.ExpiresAt > clock.GetUtcNow().AddHours(2));
            Assert.IsFalse(File.ReadAllText(Path.Combine(folder.Path, "devtunnel.json")).Contains(access.Token, StringComparison.Ordinal));
            CollectionAssert.Contains(cli.Calls.Select(static args => string.Join(' ', args)).ToArray(), "token personal.euw --scopes connect --json --nologo");
            var recovery = new StringWriter();
            var application = new ServerApplication(recovery, new StringWriter()) { DevTunnelCommands = cli };
            Assert.AreEqual(0, await application.RunAsync(["tunnel", "token", "--data-root", folder.Path, "--invitation"]));
            using var identity = CodeAlta.Fleet.Identity.DeviceIdentity.LoadOrCreate(Path.Combine(folder.Path, "recovery"));
            var store = new CodeAlta.Fleet.Server.FleetTunnelAccessStore(identity);
            Assert.AreEqual("transport-refresh", store.ImportInvitation(new Uri(access.Address), recovery.ToString().Trim()),
                "SSH can recover an existing link even when every device's transport token expired");
        }
        finally { await stop.CancelAsync(); await host.WithinAsync(TestTime.Seconds(10), TestContext.CancellationToken); }
    }

    [TestMethod]
    public void TheTunnelService_UsesTheServerAccountAndExecutablePrefix_OnEveryPlatform()
    {
        foreach (var platform in new[] { ServicePlatform.Linux, ServicePlatform.MacOS, ServicePlatform.Windows })
        {
            using var machine = new FakeServiceMachine(platform);
            var paths = new ServerPaths(Path.Combine(machine.Root, "data"));
            var executable = new ServiceExecutable(ServiceExecutableKind.DotnetHost, "/dotnet/dotnet", ["/server/altaserver.dll"]);
            var request = ServerApplication.ServiceRequestOf(new ServerApplication.InstallOptions { DataRoot = paths.DataRoot }, paths, ServiceScope.User, executable);
            var tunnel = ServerApplication.TunnelServiceRequest(request, paths);
            var plan = new ServiceManager(machine.Environment).Plan(tunnel);
            Assert.AreEqual(request.Name + "-tunnel", plan.Spec.Name);
            Assert.AreEqual(request.Account, plan.Spec.Account);
            CollectionAssert.AreEqual(new[] { "/server/altaserver.dll", "tunnel", "host", "--data-root", paths.DataRoot }, plan.Spec.Arguments.ToArray());
        }
    }

    [TestMethod]
    public void ASystemTunnelService_RunsAsItsNamedAccount_NotTheInstaller()
    {
        foreach (var platform in new[] { ServicePlatform.Linux, ServicePlatform.MacOS, ServicePlatform.Windows })
        {
            using var machine = new FakeServiceMachine(platform) { IsRoot = true };
            var paths = new ServerPaths(Path.Combine(machine.Root, "data"));
            var request = ServerApplication.ServiceRequestOf(new ServerApplication.InstallOptions { DataRoot = paths.DataRoot, Account = "codealta" },
                paths, ServiceScope.System, new ServiceExecutable(ServiceExecutableKind.Executable, "/opt/altaserver", []));
            var plan = new ServiceManager(machine.Environment).Plan(ServerApplication.TunnelServiceRequest(request, paths));
            Assert.AreEqual("codealta", plan.Spec.Account);
            Assert.AreEqual(ServiceScope.System, plan.Spec.Scope);
            if (platform == ServicePlatform.Windows)
            {
                Assert.AreEqual("codealta", plan.LogonAccount);
                StringAssert.Contains(plan.Definition, "--service");
            }
            else StringAssert.Contains(plan.Definition, "codealta");
        }
    }

    [TestMethod]
    public void UnknownOrAnonymousAccess_IsRefused_AndTokenDiagnosticsAreRedacted()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.IsFalse(ServerDevTunnel.AnonymousDenied("unreadable", now));
        Assert.IsFalse(ServerDevTunnel.AnonymousDenied("""{"accessControlEntries":[{"type":"Anonymous"}]}""", now), "unknown scope information is not proof of private access");
        Assert.IsFalse(ServerDevTunnel.AnonymousDenied("""{"accessControlEntries":[{"type":"Anonymous","scopes":"connect"}]}""", now));
        Assert.IsFalse(ServerDevTunnel.AnonymousDenied("""{"accessControlEntries":[{"type":"Anonymous","scopes":["connect"]}]}""", now));
        Assert.IsTrue(ServerDevTunnel.AnonymousDenied("""{"accessControlEntries":[]}""", now));
        Assert.IsTrue(ServerDevTunnel.AnonymousDenied("""{"accessControlEntries":[{"type":"Anonymous","scopes":["connect"]},{"type":"Anonymous","scopes":["connect"],"isDeny":true}]}""", now));
        Assert.IsNull(ServerDevTunnel.Token("a failure with words"));
        Assert.IsNull(ServerDevTunnel.TunnelId("not json"));
    }

    [TestMethod]
    public async Task ReconfiguringWhileTheHostReportsItsFirstAddress_DoesNotLoseIt()
    {
        using var folder = new TempDirectory();
        var cli = new Commands { ReportHostImmediately = false };
        var service = new ServerDevTunnel(new ServerPaths(folder.Path), cli);
        await service.ConfigureAsync(2586, static _ => { }, TestContext.CancellationToken);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        var host = service.HostAsync(static _ => { }, stop.Token);
        try
        {
            await cli.HostStarted.Task.WithinAsync(TestTime.Seconds(10), TestContext.CancellationToken);
            cli.PortCreating = () => cli.HostOutput?.Invoke("Hosting port 2586 at https://personal-2586.euw.devtunnels.ms/");
            await service.ConfigureAsync(2586, static _ => { }, TestContext.CancellationToken);
            Assert.AreEqual("https://personal-2586.euw.devtunnels.ms", service.Read()!.Address,
                "the host's update during the CLI calls must not be replaced by the older configuration snapshot");
        }
        finally { await stop.CancelAsync(); await host.WithinAsync(TestTime.Seconds(10), TestContext.CancellationToken); }
    }

    [TestMethod]
    public async Task TwoConfiguratorsOfTheSameRoot_CreateOnlyOnePersistentTunnel()
    {
        using var folder = new TempDirectory();
        var paths = new ServerPaths(folder.Path);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cli = new Commands
        {
            Creating = async cancellationToken =>
            {
                entered.TrySetResult();
                await release.Task.WithinAsync(TestTime.Seconds(10), cancellationToken);
            },
        };
        var first = new ServerDevTunnel(paths, cli).ConfigureAsync(2586, static _ => { }, TestContext.CancellationToken);
        await entered.Task.WithinAsync(TestTime.Seconds(10), TestContext.CancellationToken);
        var second = new ServerDevTunnel(paths, cli).ConfigureAsync(2586, static _ => { }, TestContext.CancellationToken);
        release.TrySetResult();
        await Task.WhenAll(first, second).WithinAsync(TestTime.Seconds(10), TestContext.CancellationToken);
        Assert.AreEqual(1, cli.Calls.Count(static args => args[0] == "create"));
    }

    internal sealed class Commands : IServerDevTunnelCommands
    {
        public string? Executable => "/home/alice/.devtunnel/bin/devtunnel";
        public List<string[]> Calls { get; } = [];
        public bool SignedIn { get; set; } = true;
        public string Access { get; set; } = """{"accessControlEntries":[]}""";
        public DateTimeOffset TokenExpiry { get; set; } = DateTimeOffset.UtcNow.AddHours(24);
        public bool ReportHostImmediately { get; init; } = true;
        public Action? PortCreating { get; set; }
        public Action<string>? HostOutput { get; private set; }
        public TaskCompletionSource HostStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Func<CancellationToken, Task>? Creating { get; init; }

        public async Task<DevTunnelCommandResult> RunAsync(IReadOnlyList<string> arguments, TimeSpan patience, Action<string>? output, bool capture, CancellationToken cancellationToken)
        {
            Calls.Add([.. arguments]);
            if (arguments is ["user", "show"]) return new(0, SignedIn ? "Logged in as owner using GitHub." : "Not logged in.");
            if (arguments is ["user", "login", ..]) { SignedIn = true; output?.Invoke("Open https://github.com/login/device and enter ABCD-EFGH"); return new(0, ""); }
            if (arguments[0] == "create")
            {
                if (Creating is { } creating) await creating(cancellationToken);
                return new(0, """{"tunnelId":"personal","clusterId":"euw"}""");
            }
            if (arguments[0] == "port") { PortCreating?.Invoke(); return new(0, ""); }
            if (arguments[0] == "access") return new(0, Access);
            if (arguments[0] == "token")
            {
                var token = "header." + Base64Url.EncodeToString(Encoding.UTF8.GetBytes($"{{\"exp\":{TokenExpiry.ToUnixTimeSeconds()}}}")) + ".signature";
                return new(0, JsonSerializer.Serialize(new { token }));
            }
            if (arguments[0] == "host")
            {
                HostOutput = output;
                HostStarted.TrySetResult();
                if (ReportHostImmediately) output?.Invoke("Hosting port 2586 at https://personal-2586.euw.devtunnels.ms/");
                await new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task.WithinAsync(TestTime.Seconds(30), cancellationToken);
                return new(0, "");
            }
            throw new AssertFailedException("Unexpected Dev Tunnel command.");
        }
    }
}
