using System.Text;
using CodeAlta.Fleet.Update;
using CodeAlta.Fleet.Update.Service;
using CodeAlta.Fleet.Update.Tests;
using CodeAlta.Server.Hosting;

namespace CodeAlta.Server.Tests;

/// <summary>
/// <c>altaserver install</c>, <c>uninstall</c> and the service lines of <c>status</c>, on a <see cref="FakeServiceMachine"/>
/// that installs nothing: the service of each OS runs <c>altaserver run</c> for its data root, as <c>altanode install</c> does
/// for a profile, and on Windows <c>uninstall</c> stops the server the task at logon leaves running.
/// </summary>
[TestClass]
public sealed class ServerServiceTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task Linux_InstallsAUserUnitForTheDataRoot_TurnsLingeringOn_TellsItInStatus_AndRemovesIt()
    {
        using var machine = new FakeServiceMachine(ServicePlatform.Linux);
        var root = Path.Combine(machine.Root, "data");
        var app = Application(machine, "/home/alice/.dotnet/tools/altaserver", out var output, out var error);

        // Plain HTTP beyond the loopback is refused before a service is installed, unless forced.
        Assert.AreEqual(ServerExitCodes.InvalidUsage, await app.RunAsync(["install", "--data-root", root, "--urls", "http://0.0.0.0:2690"]));
        StringAssert.Contains(error.ToString(), "--allow-insecure-http");
        Assert.AreEqual(0, machine.Commands.Count, "nothing was installed");
        Assert.AreEqual(ServerExitCodes.Success, await app.RunAsync(["install", "--data-root", root, "--urls", "http://0.0.0.0:2690", "--allow-insecure-http"]), error.ToString());

        var name = ServiceDefinition.ServiceName(UpdateProduct.Server, root, defaultProfile: false, developer: false);
        StringAssert.Matches(name, new System.Text.RegularExpressions.Regex("^altaserver-[0-9a-f]{8}$"), "one service per data root");
        var unitPath = Path.Combine(machine.Home, ".config", "systemd", "user", name + ".service");
        var unit = File.ReadAllText(unitPath);
        StringAssert.Contains(unit, "Description=CodeAlta Server (altaserver)\n");
        StringAssert.Contains(unit, "# Written by altaserver install; altaserver uninstall removes it.");
        StringAssert.Contains(unit, "ExecStart=/home/alice/.dotnet/tools/altaserver run --data-root " + ServiceDefinition.SystemdQuote(new ServerPaths(root).DataRoot, command: true)
            + " --urls http://0.0.0.0:2690 --allow-insecure-http\n");
        StringAssert.Contains(unit, "Environment=DOTNET_ROOT=/home/alice/.dotnet\n");
        StringAssert.Contains(unit, "RestartPreventExitStatus=2\n");
        StringAssert.Contains(unit, "StandardError=journal\n", "where the server writes its log for the journal");
        CollectionAssert.AreEqual(new[]
        {
            "systemctl --user daemon-reload", $"systemctl --user enable {name}.service", $"systemctl --user restart {name}.service", "loginctl enable-linger alice",
        }, machine.Commands);
        StringAssert.Contains(output.ToString(), "Lingering is now on for alice: the server starts at boot and keeps running after logouts.");
        StringAssert.Contains(output.ToString(), "It starts the .NET tool's shim /home/alice/.dotnet/tools/altaserver: dotnet tool update changes the version it runs.");

        output.GetStringBuilder().Clear();
        Assert.AreEqual(ServerExitCodes.Failure, await app.RunAsync(["status", "--data-root", root]), "no server runs: status says so, and still tells the service");
        StringAssert.Contains(output.ToString(), $"Service: a systemd --user unit of alice ({unitPath}).");
        StringAssert.Contains(output.ToString(), "After a restart: it starts at boot, without a login (lingering is on for alice).");

        machine.Commands.Clear();
        output.GetStringBuilder().Clear();
        Assert.AreEqual(ServerExitCodes.Success, await app.RunAsync(["uninstall", "--data-root", root]), error.ToString());
        Assert.IsFalse(File.Exists(unitPath));
        CollectionAssert.Contains(machine.Commands, $"systemctl --user disable --now {name}.service");
        StringAssert.Contains(output.ToString(), "The data root stays.");
        StringAssert.Contains(output.ToString(), "loginctl disable-linger alice turns it off.");

        output.GetStringBuilder().Clear();
        await app.RunAsync(["status", "--data-root", root]);
        StringAssert.Contains(output.ToString(), "Service: not installed (altaserver install sets it up).");
        StringAssert.Contains(output.ToString(), "After a restart or a logout: nothing starts the server again.");
    }

    [TestMethod]
    public async Task MacOS_InstallsALaunchAgentOfTheDefaultDataRoot()
    {
        using var machine = new FakeServiceMachine(ServicePlatform.MacOS);
        var app = Application(machine, "/Users/alice/.dotnet/tools/altaserver", out _, out var error);

        Assert.AreEqual(ServerExitCodes.Success, await app.RunAsync(["install", "--port", "2690"]), error.ToString());

        var plist = File.ReadAllText(Path.Combine(machine.Home, "Library", "LaunchAgents", "io.codealta.altaserver.plist"));
        StringAssert.Contains(plist, "<string>io.codealta.altaserver</string>", "the service of ~/.altaserver has the name of the command");
        var arguments = System.Xml.Linq.XDocument.Parse(plist).Descendants("array").Single().Elements("string").Select(static element => element.Value).ToArray();
        CollectionAssert.AreEqual(new[] { "/Users/alice/.dotnet/tools/altaserver", "run", "--port", "2690" }, arguments);
        StringAssert.Contains(plist, "altaserver.stderr.log");
        CollectionAssert.Contains(machine.Commands, $"launchctl bootstrap gui/501 {Path.Combine(machine.Home, "Library", "LaunchAgents", "io.codealta.altaserver.plist")}");
    }

    [TestMethod]
    public async Task Windows_RegistersATaskAtLogonThroughConhostAndTheCmdShim_AndUninstallStopsTheServerItLeavesRunning()
    {
        var token = TestContext.CancellationToken;
        using var machine = new FakeServiceMachine(ServicePlatform.Windows);
        var root = Path.Combine(machine.Root, "data");
        var shim = @"C:\Users\alice\.dotnet\tools\altaserver.cmd";
        var app = Application(machine, shim, out var output, out var error);

        Assert.AreEqual(ServerExitCodes.Success, await app.RunAsync(["install", "--data-root", root, "--port", "2691"]), error.ToString());

        var (text, encoding) = machine.TaskFiles.Single();
        Assert.AreEqual(Encoding.Unicode, encoding);
        StringAssert.Contains(text, "<Description>CodeAlta Server (altaserver run), the coordinator of a CodeAlta fleet. Written by altaserver install; altaserver uninstall removes it.</Description>");
        StringAssert.Contains(text, @"<Command>C:\Windows\System32\conhost.exe</Command>");
        StringAssert.Contains(text, $"<Arguments>--headless {ServiceDefinition.WindowsCommandLine([shim, "run", "--data-root", new ServerPaths(root).DataRoot, "--port", "2691"])}</Arguments>");
        var task = ServiceDefinition.WindowsTaskName(ServiceDefinition.ServiceName(UpdateProduct.Server, root, defaultProfile: false, developer: false));
        Assert.AreEqual($"schtasks /Run /TN {task}", machine.Commands[^1]);
        Assert.IsTrue(machine.IsRegistered(task));

        // The task's server runs; removing the task leaves it running, so uninstall asks it to stop through its data root.
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new ServerApplication(TextWriter.Synchronized(new StringWriter()), TextWriter.Synchronized(new StringWriter()))
        {
            Started = (_, _) => started.TrySetResult(),
            ConfigureServer = static options => new CoordinatorServerOptions { DataRoot = options.DataRoot, Urls = options.Urls, ConfigureLogging = static _ => { } },
            StopRequestInterval = TimeSpan.FromMilliseconds(50),
        };
        var running = server.RunAsync(["run", "--data-root", root, "--urls", "http://127.0.0.1:0"]);
        await started.Task.WithinAsync(TestTime.Seconds(30), token);

        output.GetStringBuilder().Clear();
        Assert.AreEqual(ServerExitCodes.Success, await app.RunAsync(["uninstall", "--data-root", root]), error.ToString());
        Assert.IsFalse(machine.IsRegistered(task));
        StringAssert.Contains(output.ToString(), "Stopping the server of this data root (process ");
        StringAssert.Contains(output.ToString(), "The server stopped.");
        Assert.AreEqual(ServerExitCodes.Success, await running.WithinAsync(TestTime.Seconds(30), token), "it stopped as on Ctrl+C");
        Assert.IsFalse(CoordinatorServer.IsRunning(new ServerPaths(root)));
        Assert.IsFalse(File.Exists(new ServerPaths(root).StopRequestPath), "the request is consumed");
    }

    [TestMethod]
    public async Task MacOS_InstallAgain_NamesOnlyAServerThatStillHoldsTheDataRoot()
    {
        var token = TestContext.CancellationToken;
        using var machine = new FakeServiceMachine(ServicePlatform.MacOS);
        var root = Path.Combine(machine.Root, "data");
        var app = Application(machine, "/Users/alice/.dotnet/tools/altaserver", out var output, out var error);
        var started = new TaskCompletionSource<Action>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new ServerApplication(TextWriter.Synchronized(new StringWriter()), TextWriter.Synchronized(new StringWriter()))
        {
            Started = (_, stop) => started.TrySetResult(stop),
            ConfigureServer = static options => new CoordinatorServerOptions { DataRoot = options.DataRoot, Urls = options.Urls, ConfigureLogging = static _ => { } },
        };
        var running = server.RunAsync(["run", "--data-root", root, "--urls", "http://127.0.0.1:0"]);
        var stop = await started.Task.WithinAsync(TestTime.Seconds(30), token);

        // A server the service does not stop keeps the data root: the service's server starts once it exits.
        Assert.AreEqual(ServerExitCodes.Success, await app.RunAsync(["install", "--data-root", root]), error.ToString());
        StringAssert.Contains(output.ToString(), "A server already runs on ");

        // The service's own server stops on the bootout of install again, and launchd starts the new one: nothing to wait for.
        machine.CommandRan = command =>
        {
            if (!command.StartsWith("launchctl bootout", StringComparison.Ordinal)) return;
            stop();
            running.Wait(TestTime.Seconds(30), token);
        };
        output.GetStringBuilder().Clear();
        Assert.AreEqual(ServerExitCodes.Success, await app.RunAsync(["install", "--data-root", root, "--port", "2691"]), error.ToString());
        Assert.IsTrue(running.IsCompleted);
        Assert.IsFalse(output.ToString().Contains("already runs", StringComparison.Ordinal), output.ToString());
    }

    [TestMethod]
    public async Task ARequestToStopLeftFromBefore_DoesNotStopTheNextServer()
    {
        var token = TestContext.CancellationToken;
        using var machine = new FakeServiceMachine(ServicePlatform.Windows);
        var paths = new ServerPaths(Path.Combine(machine.Root, "data"));
        Directory.CreateDirectory(paths.DataRoot);
        File.WriteAllText(paths.StopRequestPath, "old");
        var started = new TaskCompletionSource<Action>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new ServerApplication(TextWriter.Synchronized(new StringWriter()), TextWriter.Synchronized(new StringWriter()))
        {
            Started = (_, stop) => started.TrySetResult(stop),
            ConfigureServer = static options => new CoordinatorServerOptions { DataRoot = options.DataRoot, Urls = options.Urls, ConfigureLogging = static _ => { } },
            StopRequestInterval = TimeSpan.FromMilliseconds(20),
        };
        var running = server.RunAsync(["run", "--data-root", paths.DataRoot, "--urls", "http://127.0.0.1:0"]);
        var stop = await started.Task.WithinAsync(TestTime.Seconds(30), token);
        await Task.Delay(300, token);
        Assert.IsFalse(running.IsCompleted, "the request was for the server before this one");
        Assert.IsFalse(File.Exists(paths.StopRequestPath));
        stop();
        await running.WithinAsync(TestTime.Seconds(30), token);
    }

    [TestMethod]
    public async Task Status_ChecksTheHealthOfAServerThatListensOnEveryNetwork()
    {
        // A server set up for the local network listens on 0.0.0.0, the address its state gives, which no client can connect
        // to: status asks its loopback instead. (Bound here to the loopback, with the state of a wildcard address.)
        var token = TestContext.CancellationToken;
        using var machine = new FakeServiceMachine(ServicePlatform.Linux);
        var paths = new ServerPaths(Path.Combine(machine.Root, "data"));
        await using var server = await CoordinatorServer.StartAsync(new CoordinatorServerOptions
        {
            DataRoot = paths.DataRoot, Urls = ["http://127.0.0.1:0"], ConfigureLogging = static _ => { },
        }, token);
        File.WriteAllText(paths.StatePath, File.ReadAllText(paths.StatePath).Replace("127.0.0.1", "0.0.0.0", StringComparison.Ordinal));
        var app = Application(machine, "/home/alice/.dotnet/tools/altaserver", out var output, out _);

        Assert.AreEqual(ServerExitCodes.Success, await app.RunAsync(["status", "--data-root", paths.DataRoot]));
        StringAssert.Contains(output.ToString(), "Listens on http://0.0.0.0:");
        StringAssert.Contains(output.ToString(), "Health: ok, no owner yet");
    }

    [TestMethod]
    public async Task DryRun_PrintsThePlan_AndRefusesWhatCannotBe()
    {
        using var machine = new FakeServiceMachine(ServicePlatform.Linux);
        var root = Path.Combine(machine.Root, "data");
        var app = Application(machine, "/home/alice/.dotnet/tools/altaserver", out var output, out var error);

        Assert.AreEqual(ServerExitCodes.Success, await app.RunAsync(["install", "--dry-run", "--data-root", root]), error.ToString());
        StringAssert.Contains(output.ToString(), "altaserver install would install a systemd --user unit of alice");
        StringAssert.Contains(output.ToString(), "loginctl enable-linger alice (when lingering is off)");
        Assert.AreEqual(0, machine.Commands.Count, "a dry run changes nothing");
        Assert.AreEqual(0, Directory.GetFiles(machine.Home, "*.service", SearchOption.AllDirectories).Length);

        Assert.AreEqual(ServerExitCodes.InvalidUsage, await app.RunAsync(["install", "--account", "codealta", "--data-root", root]));
        StringAssert.Contains(error.ToString(), "--account goes with --system only.");
        Assert.AreEqual(ServerExitCodes.InvalidUsage, await app.RunAsync(["install", "--port", "70000", "--data-root", root]));
        Assert.AreEqual(ServerExitCodes.Failure, await app.RunAsync(["install", "--system", "--account", "codealta", "--data-root", root]), "a system unit is installed by root");
        StringAssert.Contains(error.ToString(), "altaserver install: A system unit is installed by root: run it again with sudo.");

        using var windows = new FakeServiceMachine(ServicePlatform.Windows);
        var onWindows = Application(windows, @"C:\Users\alice\.dotnet\tools\altaserver.cmd", out _, out var windowsError);
        // A Windows service is installed by an administrator, from an elevated terminal.
        Assert.AreEqual(ServerExitCodes.Failure, await onWindows.RunAsync(["install", "--system", "--account", "codealta", "--data-root", root]));
        StringAssert.Contains(windowsError.ToString(), "altaserver install: A Windows service is installed by an administrator");
    }

    [TestMethod]
    public void TheSystemUnitOfAServerRunsAsItsAccount_WithTheDataRootOfItsHome()
    {
        using var machine = new FakeServiceMachine(ServicePlatform.Linux) { IsRoot = true };
        var paths = new ServerPaths(ServerPaths.GetDefaultDataRoot());
        var request = ServerApplication.ServiceRequestOf(new ServerApplication.InstallOptions { Port = "2690" }, paths, ServiceScope.System,
            new ServiceExecutable(ServiceExecutableKind.Executable, "/opt/altaserver/altaserver", []));
        var plan = new ServiceManager(machine.Environment).Plan(request with { Account = "codealta" });

        Assert.AreEqual(Path.Combine(machine.SystemUnitDirectory, "altaserver-codealta.service"), plan.DefinitionPath);
        StringAssert.Contains(plan.Definition, "User=codealta\n");
        StringAssert.Contains(plan.Definition, "ExecStart=/opt/altaserver/altaserver run --port 2690\n", "its data root is the default one of the account");
        StringAssert.Contains(plan.Definition, "WantedBy=multi-user.target");
        Assert.IsNull(plan.LingerUser);
    }

    private static ServerApplication Application(FakeServiceMachine machine, string executable, out StringWriter output, out StringWriter error)
    {
        output = new StringWriter();
        error = new StringWriter();
        return new ServerApplication(TextWriter.Synchronized(output), TextWriter.Synchronized(error))
        {
            ServiceEnvironment = () => machine.Environment,
            ServiceExecutable = () => new ServiceExecutable(ServiceExecutableKind.ToolShim, executable, []),
            StopPatience = TimeSpan.FromSeconds(30),
        };
    }
}
