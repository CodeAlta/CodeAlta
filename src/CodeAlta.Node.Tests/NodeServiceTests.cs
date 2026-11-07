using System.Text;
using System.Xml.Linq;
using CodeAlta.Fleet.Update;
using CodeAlta.Fleet.Update.Service;
using CodeAlta.Fleet.Update.Tests;

namespace CodeAlta.Node.Tests;

/// <summary>
/// The service of a node: the definitions the service managers read (pure, checked on every OS), the stable path of the
/// executable, and what install, uninstall and status do on a <see cref="FakeServiceMachine"/>, which installs nothing.
/// </summary>
[TestClass]
public sealed class NodeServiceTests
{
    private static readonly XNamespace Task = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    private static ServiceSpec Spec(ServiceScope scope = ServiceScope.User, string? account = null) => new()
    {
        Product = UpdateProduct.Node,
        Name = "altanode",
        Executable = "/home/alice/.dotnet/tools/altanode",
        Arguments = ["run", "--port", "2600", "--name", "my node"],
        WorkingDirectory = "/home/alice",
        Environment = new Dictionary<string, string> { ["PATH"] = "/usr/bin:/bin", ["DOTNET_ROOT"] = "/home/alice/.dotnet" },
        Scope = scope,
        Account = account,
        StandardErrorPath = "/home/alice/.alta/logs/altanode.stderr.log",
    };

    [TestMethod]
    public void WindowsTask_RunsTheNodeAtLogonAsTheUser_WithoutAWindow_AndStartsItAgain()
    {
        var spec = Spec() with { Executable = @"C:\Users\alice\.dotnet\tools\altanode.exe", WorkingDirectory = @"C:\Users\alice" };
        var xml = ServiceDefinition.WindowsTaskXml(spec, "S-1-5-21-1", @"C:\Windows\System32\conhost.exe", new DateTime(2026, 11, 3, 9, 30, 0));

        StringAssert.StartsWith(xml, "<?xml version=\"1.0\" encoding=\"UTF-16\"?>");
        var task = XDocument.Parse(xml).Root!;
        Assert.AreEqual(@"\CodeAlta\altanode", task.Element(Task + "RegistrationInfo")!.Element(Task + "URI")!.Value);
        var triggers = task.Element(Task + "Triggers")!;
        Assert.AreEqual("S-1-5-21-1", triggers.Element(Task + "LogonTrigger")!.Element(Task + "UserId")!.Value);
        var time = triggers.Element(Task + "TimeTrigger")!;
        Assert.AreEqual("PT5M", time.Element(Task + "Repetition")!.Element(Task + "Interval")!.Value);
        Assert.AreEqual("2026-11-03T09:30:00", time.Element(Task + "StartBoundary")!.Value);
        var principal = task.Element(Task + "Principals")!.Element(Task + "Principal")!;
        Assert.AreEqual("S-1-5-21-1", principal.Element(Task + "UserId")!.Value);
        Assert.AreEqual("InteractiveToken", principal.Element(Task + "LogonType")!.Value, "The user's token: their Credential Manager and DPAPI keys.");
        Assert.AreEqual("LeastPrivilege", principal.Element(Task + "RunLevel")!.Value);
        var settings = task.Element(Task + "Settings")!;
        Assert.AreEqual("IgnoreNew", settings.Element(Task + "MultipleInstancesPolicy")!.Value);
        Assert.AreEqual("PT0S", settings.Element(Task + "ExecutionTimeLimit")!.Value);
        Assert.AreEqual("false", settings.Element(Task + "DisallowStartIfOnBatteries")!.Value);
        Assert.AreEqual("false", settings.Element(Task + "StopIfGoingOnBatteries")!.Value);
        var exec = task.Element(Task + "Actions")!.Element(Task + "Exec")!;
        Assert.AreEqual(@"C:\Windows\System32\conhost.exe", exec.Element(Task + "Command")!.Value);
        Assert.AreEqual(@"--headless C:\Users\alice\.dotnet\tools\altanode.exe run --port 2600 --name ""my node""", exec.Element(Task + "Arguments")!.Value);
        Assert.AreEqual(@"C:\Users\alice", exec.Element(Task + "WorkingDirectory")!.Value);
        Assert.ThrowsExactly<ArgumentException>(() => ServiceDefinition.WindowsTaskXml(Spec(ServiceScope.System, "svc"), "S-1", "conhost.exe", DateTime.Now));
    }

    [TestMethod]
    public void LaunchAgent_StartsAtLoad_RestartsOnFailure_WithThePathOfTheUser()
    {
        var plist = ServiceDefinition.LaunchdPlist(Spec());

        StringAssert.Contains(plist, "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">");
        var dict = Dict(plist);
        Assert.AreEqual("io.codealta.altanode", dict["Label"].Value);
        CollectionAssert.AreEqual(new[] { "/home/alice/.dotnet/tools/altanode", "run", "--port", "2600", "--name", "my node" },
            dict["ProgramArguments"].Elements("string").Select(static element => element.Value).ToArray());
        Assert.AreEqual("true", dict["RunAtLoad"].Name.LocalName);
        Assert.AreEqual("<dict><key>SuccessfulExit</key><false /></dict>", dict["KeepAlive"].ToString(SaveOptions.DisableFormatting));
        Assert.AreEqual("60", dict["ExitTimeOut"].Value);
        Assert.AreEqual("/home/alice", dict["WorkingDirectory"].Value);
        Assert.AreEqual("/home/alice/.alta/logs/altanode.stderr.log", dict["StandardErrorPath"].Value);
        var environment = dict["EnvironmentVariables"].Elements().ToArray();
        CollectionAssert.AreEqual(new[] { "DOTNET_ROOT", "/home/alice/.dotnet", "PATH", "/usr/bin:/bin" }, environment.Select(static element => element.Value).ToArray());
        Assert.IsFalse(dict.ContainsKey("UserName"));

        var daemon = Dict(ServiceDefinition.LaunchdPlist(Spec(ServiceScope.System, "codealta")));
        Assert.AreEqual("io.codealta.altanode-codealta", daemon["Label"].Value);
        Assert.AreEqual("codealta", daemon["UserName"].Value);
    }

    [TestMethod]
    public void SystemdUnit_OfTheUser_RestartsOnFailure_AndQuotesItsWords()
    {
        var unit = ServiceDefinition.SystemdUnit(Spec() with { Arguments = ["run", "--name", "50% $HOME"] });

        StringAssert.Contains(unit, "\nExecStart=/home/alice/.dotnet/tools/altanode run --name \"50%% $$HOME\"\n");
        StringAssert.Contains(unit, "\nWorkingDirectory=%h\n");
        StringAssert.Contains(unit, "\nEnvironment=DOTNET_ROOT=/home/alice/.dotnet\nEnvironment=PATH=/usr/bin:/bin\n");
        StringAssert.Contains(unit, "\nRestart=on-failure\n");
        StringAssert.Contains(unit, "\nRestartPreventExitStatus=2\n");
        StringAssert.Contains(unit, "\nKillMode=mixed\n");
        StringAssert.Contains(unit, "\nTimeoutStopSec=60\n");
        StringAssert.Contains(unit, "\n[Install]\nWantedBy=default.target\n");
        Assert.IsFalse(unit.Contains("User=", StringComparison.Ordinal));
        Assert.IsFalse(unit.Contains('\r', StringComparison.Ordinal));

        var system = ServiceDefinition.SystemdUnit(Spec(ServiceScope.System, "codealta"));
        StringAssert.Contains(system, "\nUser=codealta\n");
        StringAssert.Contains(system, "\nWorkingDirectory=~\n");
        StringAssert.Contains(system, "\nAfter=network-online.target\n");
        StringAssert.Contains(system, "\nWantedBy=multi-user.target\n");
    }

    [TestMethod]
    [DataRow("plain", true, "plain")]
    [DataRow("with space", true, "\"with space\"")]
    [DataRow("a\"b\\c", true, "\"a\\\"b\\\\c\"")]
    [DataRow("50%", false, "50%%")]
    [DataRow("$PATH", true, "$$PATH")]
    [DataRow("$PATH", false, "$PATH")]
    [DataRow("", true, "\"\"")]
    public void SystemdQuote_EscapesWhatSystemdReads(string value, bool command, string expected)
        => Assert.AreEqual(expected, ServiceDefinition.SystemdQuote(value, command));

    [TestMethod]
    public void WindowsCommandLine_QuotesAsCommandLineToArgvSplits()
    {
        Assert.AreEqual(@"run --port 1", ServiceDefinition.WindowsCommandLine(["run", "--port", "1"]));
        Assert.AreEqual(@"""C:\Program Files\alta\altanode.exe"" """"", ServiceDefinition.WindowsCommandLine([@"C:\Program Files\alta\altanode.exe", ""]));
        Assert.AreEqual(@"""C:\my data\\""", ServiceDefinition.WindowsCommandLine([@"C:\my data\"]));
        Assert.AreEqual(@"""say \""hi\"""" C:\a\b", ServiceDefinition.WindowsCommandLine([@"say ""hi""", @"C:\a\b"]));
    }

    [TestMethod]
    public void ServiceName_IsOnePerProfile()
    {
        Assert.AreEqual("altanode", ServiceDefinition.ServiceName(UpdateProduct.Node, "/home/alice/.alta", defaultProfile: true, developer: false));
        Assert.AreEqual("altanode-dev", ServiceDefinition.ServiceName(UpdateProduct.Node, "/home/alice/.alta/dev", defaultProfile: true, developer: true));
        var other = ServiceDefinition.ServiceName(UpdateProduct.Node, "/srv/node", defaultProfile: false, developer: false);
        StringAssert.Matches(other, new System.Text.RegularExpressions.Regex("^altanode-[0-9a-f]{8}$"));
        Assert.AreEqual(other, ServiceDefinition.ServiceName(UpdateProduct.Node, "/srv/node/", defaultProfile: false, developer: false));
        Assert.AreNotEqual(other, ServiceDefinition.ServiceName(UpdateProduct.Node, "/srv/other", defaultProfile: false, developer: false));
        Assert.IsTrue(ServiceDefinition.IsValidAccount("codealta"));
        Assert.IsFalse(ServiceDefinition.IsValidAccount("-rf"));
        Assert.IsFalse(ServiceDefinition.IsValidAccount("a b"));
        Assert.IsFalse(ServiceDefinition.IsValidAccount(null));
    }

    [TestMethod]
    public void Locator_StartsAToolFromItsShim()
    {
        var shim = "/home/alice/.dotnet/tools/altanode";
        var store = "/home/alice/.dotnet/tools/.store/codealta.node/4.0.0/codealta.node/4.0.0/tools/net10.0/any/altanode";
        // The paths are made with the separators of the OS that runs the test.
        bool Exists(string path) => path.Replace('\\', '/') is "/home/alice/.dotnet/tools/altanode" or "/home/alice/.dotnet/tools/.store/codealta.node";

        var fromShim = ServiceExecutableLocator.Locate(UpdateProduct.Node, shim, "/any/altanode.dll", Exists)!;
        Assert.AreEqual(ServiceExecutableKind.ToolShim, fromShim.Kind);
        Assert.AreEqual(shim, fromShim.Path);
        var fromStore = ServiceExecutableLocator.Locate(UpdateProduct.Node, store, "/any/altanode.dll", Exists)!;
        Assert.AreEqual(ServiceExecutableKind.ToolShim, fromStore.Kind);
        Assert.AreEqual(shim, fromStore.Path.Replace('\\', '/'));
        StringAssert.Contains(ServiceExecutableLocator.Describe(UpdateProduct.Node, fromStore), "dotnet tool update");

        var archive = ServiceExecutableLocator.Locate(UpdateProduct.Node, "/opt/altanode/altanode", "/opt/altanode/altanode.dll", Exists)!;
        Assert.AreEqual(ServiceExecutableKind.Executable, archive.Kind);
        var dotnet = ServiceExecutableLocator.Locate(UpdateProduct.Node, @"C:\Program Files\dotnet\dotnet.exe", @"C:\src\bin\altanode.dll", Exists)!;
        Assert.AreEqual(ServiceExecutableKind.DotnetHost, dotnet.Kind);
        CollectionAssert.AreEqual(new[] { @"C:\src\bin\altanode.dll" }, dotnet.PrefixArguments.ToArray());
        Assert.IsNull(ServiceExecutableLocator.Locate(UpdateProduct.Node, null, null, Exists));
    }

    [TestMethod]
    public void Locator_StartsAWindowsToolFromTheCmdShimOfDotnet10()
    {
        // The layout of a RID-specific tool installed by .NET 10 on Windows: altanode.cmd, no altanode.exe shim.
        var tools = @"C:\Users\alice\.dotnet\tools";
        var store = tools + @"\.store\codealta.node\4.0.0\codealta.node.win-x64\4.0.0\tools\net10.0\win-x64\altanode.exe";
        bool Exists(string path) => path.Replace('\\', '/') is "C:/Users/alice/.dotnet/tools/altanode.cmd" or "C:/Users/alice/.dotnet/tools/.store/codealta.node";

        var fromStore = ServiceExecutableLocator.Locate(UpdateProduct.Node, store, @"C:\any\altanode.dll", Exists)!;
        Assert.AreEqual(ServiceExecutableKind.ToolShim, fromStore.Kind);
        Assert.AreEqual("C:/Users/alice/.dotnet/tools/altanode.cmd", fromStore.Path.Replace('\\', '/'));
        Assert.AreEqual(0, fromStore.PrefixArguments.Count);
        StringAssert.Contains(ServiceExecutableLocator.Describe(UpdateProduct.Node, fromStore), "dotnet tool update");

        // The app host shim of an older tool keeps its name.
        bool ExistsExe(string path) => path.Replace('\\', '/') is "C:/Users/alice/.dotnet/tools/altanode.exe" or "C:/Users/alice/.dotnet/tools/altanode.cmd";
        Assert.AreEqual("C:/Users/alice/.dotnet/tools/altanode.exe", ServiceExecutableLocator.Locate(UpdateProduct.Node, store, null, ExistsExe)!.Path.Replace('\\', '/'));
    }

    [TestMethod]
    public void Windows_StartsTheCmdShimHeadless_AndRefusesWhatCmdCannotPass()
    {
        using var machine = new FakeServiceMachine(ServicePlatform.Windows);
        var manager = new ServiceManager(machine.Environment);
        var shim = new ServiceExecutable(ServiceExecutableKind.ToolShim, @"C:\Users\alice\.dotnet\tools\altanode.cmd", []);
        var plan = manager.Plan(Request(machine) with { Executable = shim, RunArguments = ["run", "--port", "2600", "--name", "R&D (lab) box"] });
        var exec = XDocument.Parse(plan.Definition).Root!.Element(Task + "Actions")!.Element(Task + "Exec")!;
        Assert.AreEqual(@"C:\Windows\System32\conhost.exe", exec.Element(Task + "Command")!.Value);
        // Windows runs a .cmd given to CreateProcess through cmd.exe /c, with the command line conhost passes in quotes.
        Assert.AreEqual(@"--headless C:\Users\alice\.dotnet\tools\altanode.cmd run --port 2600 --name ""R&D (lab) box""", exec.Element(Task + "Arguments")!.Value);

        foreach (var name in new[] { "a&b", "a|b", "a^b", "a<b", "50%", "50% off", "say \"hi\"", "a\nb" })
        {
            var refused = Assert.ThrowsExactly<ServiceException>(() => manager.Plan(Request(machine) with { Executable = shim, RunArguments = ["run", "--name", name] }), name);
            StringAssert.Contains(refused.Message, "altanode.cmd");
        }

        var refusedPath = Assert.ThrowsExactly<ServiceException>(() => manager.Plan(Request(machine) with
        {
            Executable = shim with { Path = @"C:\Users\R&D\.dotnet\tools\altanode.cmd" },
        }));
        StringAssert.Contains(refusedPath.Message, "cmd.exe");
        // An executable is started directly: cmd.exe never reads its arguments.
        Assert.IsNotNull(manager.Plan(Request(machine) with
        {
            Executable = new ServiceExecutable(ServiceExecutableKind.Executable, @"C:\altanode\altanode.exe", []), RunArguments = ["run", "--name", "50%&more"],
        }));
    }

    [TestMethod]
    public void Linux_InstallsAUserUnit_TurnsLingeringOn_AndRemovesIt()
    {
        using var machine = new FakeServiceMachine(ServicePlatform.Linux);
        var manager = new ServiceManager(machine.Environment);
        var plan = manager.Plan(Request(machine));
        var unitPath = Path.Combine(machine.Home, ".config", "systemd", "user", "altanode.service");
        Assert.AreEqual(unitPath, plan.DefinitionPath);
        var before = manager.Query(plan);
        Assert.IsFalse(before.Installed);
        StringAssert.Contains(string.Join('\n', ServiceManager.Describe(before)), "not installed");

        var output = new StringWriter();
        Assert.AreEqual(NodeExitCodes.Success, manager.Install(plan, output, new StringWriter()));

        StringAssert.Contains(File.ReadAllText(unitPath), "ExecStart=/home/alice/.dotnet/tools/altanode run --port 2600");
        CollectionAssert.AreEqual(new[]
        {
            "systemctl --user daemon-reload", "systemctl --user enable altanode.service", "systemctl --user restart altanode.service", "loginctl enable-linger alice",
        }, machine.Commands);
        StringAssert.Contains(output.ToString(), "Lingering is now on for alice");
        var installed = manager.Query(plan);
        Assert.IsTrue(installed.Installed);
        Assert.IsTrue(installed.Enabled);
        Assert.IsTrue(installed.Lingering);
        var answer = string.Join('\n', ServiceManager.Describe(installed));
        StringAssert.Contains(answer, "After a restart: it starts at boot, without a login");
        StringAssert.Contains(answer, "After a logout: it keeps running.");

        machine.Commands.Clear();
        output = new StringWriter();
        Assert.AreEqual(NodeExitCodes.Success, manager.Uninstall(plan, output, new StringWriter()));
        Assert.IsFalse(File.Exists(unitPath));
        CollectionAssert.Contains(machine.Commands, "systemctl --user disable --now altanode.service");
        CollectionAssert.Contains(machine.Commands, "systemctl --user daemon-reload");
        StringAssert.Contains(output.ToString(), "Lingering stays on for alice (other services may need it): loginctl disable-linger alice turns it off.");
        Assert.IsFalse(output.ToString().Contains("sudo", StringComparison.Ordinal), "turned on without privileges, it is turned off without them");
        Assert.IsFalse(manager.Query(plan).Installed);
    }

    [TestMethod]
    public void Linux_WithoutTheRightToLinger_SaysWhatToRun()
    {
        using var machine = new FakeServiceMachine(ServicePlatform.Linux) { LingerAllowed = false };
        var manager = new ServiceManager(machine.Environment);
        var plan = manager.Plan(Request(machine));
        var output = new StringWriter();

        Assert.AreEqual(NodeExitCodes.Success, manager.Install(plan, output, new StringWriter()));

        StringAssert.Contains(output.ToString(), "sudo loginctl enable-linger alice");
        var answer = string.Join('\n', ServiceManager.Describe(manager.Query(plan)));
        StringAssert.Contains(answer, "After a restart: it waits until alice logs in");
        StringAssert.Contains(answer, "After a logout: it stops once the last session of alice ends");
    }

    [TestMethod]
    public void Linux_SystemUnit_NeedsRoot_AndAnAccount()
    {
        using var machine = new FakeServiceMachine(ServicePlatform.Linux);
        var system = Request(machine) with { Scope = ServiceScope.System, Account = "codealta", StateRoot = null };
        var refused = Assert.ThrowsExactly<ServiceException>(() => new ServiceManager(machine.Environment).Plan(system));
        StringAssert.Contains(refused.Message, "sudo");

        machine.IsRoot = true;
        var manager = new ServiceManager(machine.Environment);
        Assert.ThrowsExactly<ServiceException>(() => manager.Plan(system with { Account = "nobody-here" }));
        var plan = manager.Plan(system);
        Assert.AreEqual(Path.Combine(machine.SystemUnitDirectory, "altanode-codealta.service"), plan.DefinitionPath);
        Assert.AreEqual(NodeExitCodes.Success, manager.Install(plan, new StringWriter(), new StringWriter()));
        StringAssert.Contains(File.ReadAllText(plan.DefinitionPath), "\nUser=codealta\n");
        CollectionAssert.AreEqual(new[] { "systemctl daemon-reload", "systemctl enable altanode-codealta.service", "systemctl restart altanode-codealta.service" },
            machine.Commands);
        var answer = string.Join('\n', ServiceManager.Describe(manager.Query(plan)));
        StringAssert.Contains(answer, "After a restart: it starts at boot as codealta, without a login.");

        // The node it runs reads it as that account, without root.
        machine.IsRoot = false;
        var read = new ServiceManager(machine.Environment).QuerySystemService(UpdateProduct.Node, "altanode", "codealta");
        Assert.IsTrue(read.Installed);
        Assert.IsTrue(ServiceManager.StartsAtBoot(read));
        Assert.IsFalse(new ServiceManager(machine.Environment).QuerySystemService(UpdateProduct.Node, "altanode", "alice").Installed);
    }

    [TestMethod]
    public void MacOS_InstallsALaunchAgent_InTheGuiDomainOfTheUser()
    {
        using var machine = new FakeServiceMachine(ServicePlatform.MacOS) { AutoLoginUser = "alice" };
        var manager = new ServiceManager(machine.Environment);
        var plan = manager.Plan(Request(machine));
        var plistPath = Path.Combine(machine.Home, "Library", "LaunchAgents", "io.codealta.altanode.plist");
        Assert.AreEqual(plistPath, plan.DefinitionPath);

        Assert.AreEqual(NodeExitCodes.Success, manager.Install(plan, new StringWriter(), new StringWriter()));

        Assert.AreEqual("io.codealta.altanode", Dict(File.ReadAllText(plistPath))["Label"].Value);
        CollectionAssert.AreEqual(new[]
        {
            "launchctl print gui/501", "launchctl bootout gui/501/io.codealta.altanode", "launchctl print gui/501/io.codealta.altanode",
            "launchctl enable gui/501/io.codealta.altanode", $"launchctl bootstrap gui/501 {plistPath}",
        }, machine.Commands);
        var answer = string.Join('\n', ServiceManager.Describe(manager.Query(plan)));
        StringAssert.Contains(answer, "a LaunchAgent of alice");
        StringAssert.Contains(answer, "it starts when macOS logs alice in, which it does automatically");
        StringAssert.Contains(answer, "After a logout: it stops with the session");

        Assert.AreEqual(NodeExitCodes.Success, manager.Uninstall(plan, new StringWriter(), new StringWriter()));
        Assert.IsFalse(File.Exists(plistPath));
    }

    // Over SSH with nobody logged in at the screen of the Mac, its gui domain does not exist and a bootstrap would fail: the agent
    // is written, and starts at the next login; a system service is the way to run without one.
    [TestMethod]
    public void MacOS_WithNobodyLoggedInAtTheScreen_WritesTheAgentForTheNextLogin()
    {
        using var machine = new FakeServiceMachine(ServicePlatform.MacOS) { GuiSession = false };
        var manager = new ServiceManager(machine.Environment);
        var plan = manager.Plan(Request(machine));
        var output = new StringWriter();
        Assert.AreEqual(NodeExitCodes.Success, manager.Install(plan, output, new StringWriter()));
        Assert.IsTrue(File.Exists(plan.DefinitionPath));
        CollectionAssert.AreEqual(new[] { "launchctl print gui/501" }, machine.Commands, "no bootstrap that would fail");
        StringAssert.Contains(output.ToString(), "it starts at the next login of alice");
        StringAssert.Contains(output.ToString(), "install --system --account alice");
    }

    [TestMethod]
    public void MacOS_InstallAgainWhileTheAgentRuns_WaitsUntilLaunchdRemovedTheOldJob()
    {
        // As on a Mac: launchd keeps the job it booted out while its process drains, and refuses a bootstrap meanwhile.
        using var machine = new FakeServiceMachine(ServicePlatform.MacOS);
        var manager = new ServiceManager(machine.Environment);
        var plan = manager.Plan(Request(machine));
        Assert.AreEqual(NodeExitCodes.Success, manager.Install(plan, new StringWriter(), new StringWriter()));
        machine.LaunchdPrintsWhileStopping = 3;
        machine.Commands.Clear();

        var error = new StringWriter();
        Assert.AreEqual(NodeExitCodes.Success, manager.Install(plan, new StringWriter(), error), error.ToString());

        Assert.IsTrue(machine.IsRegistered("io.codealta.altanode"), "the new job is loaded");
        CollectionAssert.AreEqual(new[]
        {
            "launchctl print gui/501", "launchctl bootout gui/501/io.codealta.altanode", "launchctl print gui/501/io.codealta.altanode", "launchctl print gui/501/io.codealta.altanode",
            "launchctl print gui/501/io.codealta.altanode", "launchctl print gui/501/io.codealta.altanode", "launchctl enable gui/501/io.codealta.altanode",
            $"launchctl bootstrap gui/501 {plan.DefinitionPath}",
        }, machine.Commands);
        Assert.IsTrue(machine.Slept > TimeSpan.Zero, "it waits between two reads");

        // Uninstall returns once the job is gone too, so that an install right after it finds none.
        machine.Commands.Clear();
        Assert.AreEqual(NodeExitCodes.Success, manager.Uninstall(plan, new StringWriter(), error), error.ToString());
        Assert.AreEqual(5, machine.Commands.Count(static command => command == "launchctl print gui/501/io.codealta.altanode"), string.Join('\n', machine.Commands));
        Assert.AreEqual(NodeExitCodes.Success, manager.Install(plan, new StringWriter(), error), error.ToString());
    }

    [TestMethod]
    public void Windows_RegistersATaskFromAUtf16File_DeletesTheFile_AndRefusesASystemService()
    {
        using var machine = new FakeServiceMachine(ServicePlatform.Windows);
        var manager = new ServiceManager(machine.Environment);
        var plan = manager.Plan(Request(machine) with { Executable = new ServiceExecutable(ServiceExecutableKind.ToolShim, @"C:\Users\alice\.dotnet\tools\altanode.exe", []) });
        Assert.IsTrue(plan.LeavesProcessRunning, "Task Scheduler ends conhost, not the node: uninstall stops it.");

        Assert.AreEqual(NodeExitCodes.Success, manager.Install(plan, new StringWriter(), new StringWriter()));

        var (text, encoding) = machine.TaskFiles.Single();
        Assert.AreEqual(Encoding.Unicode, encoding);
        StringAssert.Contains(text, @"<UserId>S-1-5-21-1-2-3-1001</UserId>");
        StringAssert.Contains(text, @"<Command>C:\Windows\System32\conhost.exe</Command>");
        Assert.IsFalse(File.Exists(plan.DefinitionPath), "The task XML is given to schtasks, then deleted.");
        Assert.AreEqual($@"schtasks /Run /TN \CodeAlta\altanode", machine.Commands[^1]);
        var answer = string.Join('\n', ServiceManager.Describe(manager.Query(plan)));
        StringAssert.Contains(answer, @"a task at logon of PC\alice");
        StringAssert.Contains(answer, @"it waits until PC\alice signs in");

        Assert.AreEqual(NodeExitCodes.Success, manager.Uninstall(plan, new StringWriter(), new StringWriter()));
        Assert.IsFalse(machine.IsRegistered(@"\CodeAlta\altanode"));
        var refused = Assert.ThrowsExactly<ServiceException>(() => manager.Plan(Request(machine) with { Scope = ServiceScope.System, Account = null, StateRoot = null }));
        StringAssert.Contains(refused.Message, "elevated terminal", "a Windows service is installed by an administrator");
    }

    // A node that starts at boot on Windows with nobody signed in (doc/fleet/node.md, running as a service): a Windows service,
    // as its own virtual account with its profile under %ProgramData%, or as a named account whose password goes to Windows only.
    [TestMethod]
    public void Windows_SystemService_IsAServiceOfTheSCM_AsItsVirtualAccount_OrANamedOneWithItsPasswordGivenToWindowsOnly()
    {
        using var machine = new FakeServiceMachine(ServicePlatform.Windows) { IsRoot = true };
        var shim = Path.Combine(machine.Root, "tools", "altanode.cmd");
        var exe = Path.Combine(machine.Root, "tools", ".store", "codealta.node", "4.1.0", "altanode.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllText(exe, "");
        File.WriteAllText(shim, "@echo off\r\n\"%~dp0.store\\codealta.node\\4.1.0\\altanode.exe\" %*\r\n");
        var manager = new ServiceManager(machine.Environment);
        var request = Request(machine) with { Scope = ServiceScope.System, Account = null, StateRoot = null, Executable = new ServiceExecutable(ServiceExecutableKind.ToolShim, shim, []) };

        var plan = manager.Plan(request);
        var root = Path.Combine(machine.ProgramData, "CodeAlta", "Node", "altanode");
        Assert.AreEqual(@"a Windows service run as NT SERVICE\altanode", plan.Kind);
        Assert.AreEqual(exe, plan.Spec.Executable, "the program the batch file of the tool starts, never cmd.exe");
        CollectionAssert.AreEqual(new[] { "run", "--port", "2600", "--data-root", root }, plan.Spec.Arguments.ToArray());
        StringAssert.Contains(plan.Definition, "--service");
        Assert.IsTrue(plan.Notes.Any(static note => note.Contains("dotnet tool update", StringComparison.Ordinal)));
        Assert.AreEqual(ServiceManager.Success, manager.Install(plan, new StringWriter(), new StringWriter()));
        Assert.IsTrue(Directory.Exists(root), "its profile is made before the service is given it");
        Assert.IsTrue(machine.IsRegistered("altanode"));
        CollectionAssert.AreEqual(new[] { "create", "config", "description", "failure", "protect", "config", "start" },
            machine.Commands.Select(static command => command.StartsWith("protect", StringComparison.Ordinal) ? "protect" : command.Split(' ')[1]).ToArray());
        StringAssert.Contains(machine.Commands[0], @"obj= NT SERVICE\altanode");
        StringAssert.Contains(machine.Commands[0], "start= demand", "on demand only until its data root is protected");
        // Its access list is replaced in one write once the account exists (nothing a folder made there before allowed remains,
        // nothing inherited from %ProgramData%); only then does it start at boot.
        Assert.AreEqual($@"protect {root} NT SERVICE\altanode", machine.Commands[4]);
        Assert.AreEqual("sc.exe config altanode start= delayed-auto", machine.Commands[5]);

        // What a folder made there before holds becomes the administrators', then inherits from it alone; a protection that
        // fails leaves a service started on demand only.
        Directory.CreateDirectory(Path.Combine(root, "auth"));
        machine.Commands.Clear();
        Assert.AreEqual(ServiceManager.Success, manager.Install(plan, new StringWriter(), new StringWriter()));
        CollectionAssert.AreEqual(new[] { $@"icacls.exe {Path.Combine(root, "*")} /setowner *S-1-5-32-544 /T /C /Q", $@"icacls.exe {Path.Combine(root, "*")} /reset /T /C /Q" },
            machine.Commands.Where(static command => command.StartsWith("icacls", StringComparison.Ordinal)).ToArray());
        machine.Commands.Clear();
        machine.ProtectFails = true;
        Assert.AreEqual(ServiceManager.Failure, manager.Install(plan, new StringWriter(), new StringWriter()));
        Assert.IsFalse(machine.Commands.Any(static command => command.Contains("delayed-auto", StringComparison.Ordinal)), "no start at boot on an unprotected root");
        machine.ProtectFails = false;
        Assert.AreEqual(0, machine.Logons.Count, "a virtual account has no password");
        var answer = string.Join('\n', ServiceManager.Describe(manager.Query(plan)));
        StringAssert.Contains(answer, @"it starts at boot as NT SERVICE\altanode, without a login");
        // status reads it without a plan, which needs an administrator for a system service.
        var read = manager.QuerySystemService(UpdateProduct.Node, "altanode", "alice");
        Assert.IsTrue(read.Installed);
        Assert.IsTrue(ServiceManager.StartsAtBoot(read));
        Assert.AreEqual(@"NT SERVICE\altanode", read.User);
        Assert.IsFalse(manager.QuerySystemService(UpdateProduct.Node, "altanode-other", "alice").Installed);

        // As a named account ("at boot as me"): its password is needed, and goes to Windows through the API only.
        machine.Commands.Clear();
        var named = manager.Plan(request with { Account = @".\codealta" });
        Assert.AreEqual(@".\codealta", named.LogonAccount);
        var error = new StringWriter();
        Assert.AreEqual(ServiceManager.Failure, manager.Install(named, new StringWriter(), error));
        StringAssert.Contains(error.ToString(), "--password-stdin");
        Assert.AreEqual(ServiceManager.Success, manager.Install(named, new StringWriter(), new StringWriter(), "s3cret-pass"));
        Assert.AreEqual((@"altanode", @".\codealta", "s3cret-pass"), machine.Logons.Single());
        Assert.IsFalse(machine.Commands.Any(static command => command.Contains("s3cret-pass", StringComparison.Ordinal)), "never on a command line");
        Assert.IsFalse(machine.Commands.Any(static command => command.StartsWith("icacls", StringComparison.Ordinal)), "its own profile is the account's");
        // Never LocalSystem, never at boot before Windows took the logon of the account.
        Assert.IsTrue(machine.Commands.Where(static command => command.Contains(" config altanode binPath=", StringComparison.Ordinal)).All(static command => command.Contains("start= demand", StringComparison.Ordinal)));
        Assert.IsFalse(machine.Commands.Any(static command => command.Contains("LocalSystem", StringComparison.OrdinalIgnoreCase)));
        Assert.AreEqual("sc.exe config altanode start= delayed-auto", machine.Commands[^2]);
        machine.Commands.Clear();
        Assert.AreEqual(ServiceManager.Failure, manager.Install(named, new StringWriter(), new StringWriter(), "wrong"), "the fake Windows refuses this password");
        Assert.IsFalse(machine.Commands.Any(static command => command.Contains("delayed-auto", StringComparison.Ordinal)), "a refused logon leaves a service started on demand only");

        Assert.AreEqual(ServiceManager.Success, manager.Uninstall(plan, new StringWriter(), new StringWriter()));
        Assert.IsFalse(machine.IsRegistered("altanode"));
    }

    [TestMethod]
    public void Describe_SaysWhenTheServiceWillNotStart()
    {
        var disabled = new ServiceFacts(UpdateProduct.Node, ServicePlatform.Windows, ServiceScope.User, "a task", @"\CodeAlta\altanode", "alice", true, false, null, null);
        StringAssert.Contains(ServiceManager.Describe(disabled)[0], "the task is disabled");
        var autoLogin = disabled with { Enabled = true, AutoLoginUser = "alice", User = @"PC\alice" };
        StringAssert.Contains(ServiceManager.Describe(autoLogin)[1], "which it does automatically");
        Assert.IsTrue(ServiceManager.IsTaskEnabled("<Task><Settings><Enabled>true</Enabled></Settings></Task>"));
        Assert.IsFalse(ServiceManager.IsTaskEnabled("<Task xmlns=\"x\"><Settings><Enabled>false</Enabled></Settings></Task>"));
        Assert.IsFalse(ServiceManager.IsTaskEnabled("not xml"));
    }

    // The node of a Windows service of its own account runs a profile under %ProgramData% but keeps the default name: its page
    // finds it by the profile its command line names, and a service of another profile is not taken for it.
    [TestMethod]
    public void TheNodeOfAWindowsService_FindsItsService_ByTheProfileItRuns()
    {
        using var machine = new FakeServiceMachine(ServicePlatform.Windows) { IsRoot = true };
        var exe = Path.Combine(machine.Root, "tools", "altanode.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllText(exe, "");
        var manager = new ServiceManager(machine.Environment);
        var plan = manager.Plan(Request(machine) with { Scope = ServiceScope.System, Account = null, StateRoot = null, Executable = new ServiceExecutable(ServiceExecutableKind.Executable, exe, []) });
        Assert.AreEqual(ServiceManager.Success, manager.Install(plan, new StringWriter(), new StringWriter()));
        var root = Path.Combine(machine.ProgramData, "CodeAlta", "Node", "altanode");
        var userPlan = manager.Plan(Request(machine) with { Name = "altanode-1a2b3c", StateRoot = root });

        Assert.IsTrue(manager.QueryInstalled(userPlan, "altanode", root).Installed);
        Assert.IsFalse(manager.QueryInstalled(userPlan, "altanode", Path.Combine(machine.Home, ".alta-other")).Installed, "another profile's service");
        Assert.IsFalse(manager.QueryInstalled(userPlan, "altanode", root[..^1]).Installed, "a data root that only starts the same is another one");
        // The default profile of the person is not the one of the service of its own account, which has the same name.
        var personal = manager.Plan(Request(machine) with { StateRoot = Path.Combine(machine.Home, ".alta") });
        Assert.IsFalse(manager.QueryInstalled(personal, null, Path.Combine(machine.Home, ".alta")).Installed);
        Assert.AreEqual(root, ServiceManager.WindowsDataRoot($"BINARY_PATH_NAME   : C:\\tools\\altanode.exe run --data-root \"{root}\" --service\n"));

        // A service of the default profile of another person (no data root, their account) is not this person's.
        Assert.AreEqual(ServiceManager.Success, manager.Uninstall(plan, new StringWriter(), new StringWriter()));
        var other = manager.Plan(Request(machine) with { Scope = ServiceScope.System, Account = @".\codealta", StateRoot = null, Executable = new ServiceExecutable(ServiceExecutableKind.Executable, exe, []) });
        Assert.AreEqual(ServiceManager.Success, manager.Install(other, new StringWriter(), new StringWriter(), "pw"));
        Assert.IsFalse(manager.QueryInstalled(personal, null, Path.Combine(machine.Home, ".alta")).Installed, "the service of the account codealta");
    }

    // The commands that make a node start at boot keep its options and its profile, and on Windows its person: a service of
    // its own account would start another profile, with another identity and none of its pairings.
    [TestMethod]
    public void TheCommandsToStartAtBoot_KeepTheOptionsAndTheProfileOfTheNode()
    {
        CollectionAssert.AreEqual(new[] { "--listen-all", "--port", "2585", "--data-root", "/home/alice/.alta node" },
            NodeServiceStatus.RunOptions(["/opt/altanode.dll", "run", "--listen-all", "--port", "2585", "--data-root", "/home/alice/.alta node", "--service"]).ToArray());
        string[] options = ["--listen-all", "--port", "2585", "--data-root", "/Users/alice/.alta node"];
        using var mac = new FakeServiceMachine(ServicePlatform.MacOS);
        var agent = new ServiceFacts(UpdateProduct.Node, ServicePlatform.MacOS, ServiceScope.User, "a LaunchAgent", "x", "alice", true, true, null, null);
        Assert.AreEqual("altanode secrets key-file --data-root '/Users/alice/.alta node' && altanode uninstall --data-root '/Users/alice/.alta node' && "
            + "sudo altanode install --system --account alice --listen-all --port 2585 --data-root '/Users/alice/.alta node'", NodeServiceStatus.Fix(mac.Environment, agent, options),
            "each step only once the one before succeeded, on the same profile");
        StringAssert.StartsWith(NodeServiceStatus.Fix(mac.Environment, agent, ["--dev"]), "altanode secrets key-file --dev && altanode uninstall --dev && ", "the developer instance, not the normal one");
        using var windows = new FakeServiceMachine(ServicePlatform.Windows);
        var task = agent with { Platform = ServicePlatform.Windows };
        var fix = NodeServiceStatus.Fix(windows.Environment, task, ["--port", "2585"]);
        StringAssert.StartsWith(fix, "altanode uninstall\naltanode install --system --account ");
        StringAssert.EndsWith(fix, " --password-stdin --port 2585");
        // A name of the node is a word for PowerShell, never a statement.
        StringAssert.EndsWith(NodeServiceStatus.Fix(windows.Environment, task, ["--name", "lab;Write-Output 'x'", "--data-root", @"C:\n odes\a"]),
            @" --name 'lab;Write-Output ''x''' --data-root 'C:\n odes\a'");
        StringAssert.StartsWith(NodeServiceStatus.Fix(windows.Environment, task, ["--data-root", @"C:\n odes\a"]), @"altanode uninstall --data-root 'C:\n odes\a'" + "\n");
        Assert.IsNull(NodeServiceStatus.Fix(windows.Environment, task, ["--name", "lab&echo.x"]), "the batch file of a .NET tool would run what follows &");
        // A value is never read as an option: a node named --dev is not the developer instance.
        Assert.AreEqual(0, NodeServiceStatus.ProfileOptions(["--name", "--dev", "--port", "--data-root=x"]).Count());
        CollectionAssert.AreEqual(new[] { "--dev", "--data-root", "/n" }, NodeServiceStatus.ProfileOptions(["--dev", "--name", "a", "--data-root", "/n"]).ToArray());
        StringAssert.StartsWith(NodeServiceStatus.Fix(mac.Environment, agent, ["--data-root=/Users/alice/n"]), "altanode secrets key-file --data-root=/Users/alice/n && altanode uninstall --data-root=/Users/alice/n && ");
        using var linux = new FakeServiceMachine(ServicePlatform.Linux);
        Assert.AreEqual("sudo loginctl enable-linger alice", NodeServiceStatus.Fix(linux.Environment, agent with { Platform = ServicePlatform.Linux }, options));
    }

    [TestMethod]
    public void StartsAtBoot_OnlyASystemService_ALingeringUnit_OrTheUserTheMachineSignsIn()
    {
        var task = new ServiceFacts(UpdateProduct.Node, ServicePlatform.Windows, ServiceScope.User, "a task", @"\CodeAlta\altanode", @"PC\alice", true, true, null, null);
        Assert.IsFalse(ServiceManager.StartsAtBoot(task), "a logon task waits for a sign-in");
        Assert.IsTrue(ServiceManager.StartsAtBoot(task with { AutoLoginUser = "alice" }));
        Assert.IsFalse(ServiceManager.StartsAtBoot(task with { AutoLoginUser = "bob" }));
        Assert.IsFalse(ServiceManager.StartsAtBoot(task with { Enabled = false, AutoLoginUser = "alice" }));
        Assert.IsTrue(ServiceManager.StartsAtBoot(task with { Scope = ServiceScope.System }));
        Assert.IsFalse(ServiceManager.StartsAtBoot(task with { Scope = ServiceScope.System, Installed = false }));
        var unit = task with { Platform = ServicePlatform.Linux, User = "alice", Lingering = false };
        Assert.IsFalse(ServiceManager.StartsAtBoot(unit));
        Assert.IsTrue(ServiceManager.StartsAtBoot(unit with { Lingering = true }));
        var agent = task with { Platform = ServicePlatform.MacOS, User = "alice" };
        Assert.IsFalse(ServiceManager.StartsAtBoot(agent), "FileVault or no automatic login");
    }

    private static ServiceRequest Request(FakeServiceMachine machine) => new()
    {
        Product = UpdateProduct.Node,
        Name = "altanode",
        Executable = new ServiceExecutable(ServiceExecutableKind.ToolShim, "/home/alice/.dotnet/tools/altanode", []),
        RunArguments = ["run", "--port", "2600"],
        StateRoot = Path.Combine(machine.Home, ".alta"),
    };

    private static Dictionary<string, XElement> Dict(string plist)
    {
        var elements = XDocument.Parse(plist).Root!.Element("dict")!.Elements().ToArray();
        var dict = new Dictionary<string, XElement>(StringComparer.Ordinal);
        for (var index = 0; index + 1 < elements.Length; index += 2) dict[elements[index].Value] = elements[index + 1];
        return dict;
    }
}
