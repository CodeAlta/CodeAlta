using System.Text;
using System.Xml.Linq;
using CodeAlta.Node.Service;

namespace CodeAlta.Node.Tests;

/// <summary>
/// The service of a node: the definitions the service managers read (pure, checked on every OS), the stable path of the
/// executable, and what install, uninstall and status do on a <see cref="FakeServiceMachine"/>, which installs nothing.
/// </summary>
[TestClass]
public sealed class NodeServiceTests
{
    private static readonly XNamespace Task = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    private static NodeServiceSpec Spec(NodeServiceScope scope = NodeServiceScope.User, string? account = null) => new()
    {
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
        var xml = NodeServiceDefinition.WindowsTaskXml(spec, "S-1-5-21-1", @"C:\Windows\System32\conhost.exe", new DateTime(2026, 11, 3, 9, 30, 0));

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
        Assert.ThrowsExactly<ArgumentException>(() => NodeServiceDefinition.WindowsTaskXml(Spec(NodeServiceScope.System, "svc"), "S-1", "conhost.exe", DateTime.Now));
    }

    [TestMethod]
    public void LaunchAgent_StartsAtLoad_RestartsOnFailure_WithThePathOfTheUser()
    {
        var plist = NodeServiceDefinition.LaunchdPlist(Spec());

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

        var daemon = Dict(NodeServiceDefinition.LaunchdPlist(Spec(NodeServiceScope.System, "codealta")));
        Assert.AreEqual("io.codealta.altanode-codealta", daemon["Label"].Value);
        Assert.AreEqual("codealta", daemon["UserName"].Value);
    }

    [TestMethod]
    public void SystemdUnit_OfTheUser_RestartsOnFailure_AndQuotesItsWords()
    {
        var unit = NodeServiceDefinition.SystemdUnit(Spec() with { Arguments = ["run", "--name", "50% $HOME"] });

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

        var system = NodeServiceDefinition.SystemdUnit(Spec(NodeServiceScope.System, "codealta"));
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
        => Assert.AreEqual(expected, NodeServiceDefinition.SystemdQuote(value, command));

    [TestMethod]
    public void WindowsCommandLine_QuotesAsCommandLineToArgvSplits()
    {
        Assert.AreEqual(@"run --port 1", NodeServiceDefinition.WindowsCommandLine(["run", "--port", "1"]));
        Assert.AreEqual(@"""C:\Program Files\alta\altanode.exe"" """"", NodeServiceDefinition.WindowsCommandLine([@"C:\Program Files\alta\altanode.exe", ""]));
        Assert.AreEqual(@"""C:\my data\\""", NodeServiceDefinition.WindowsCommandLine([@"C:\my data\"]));
        Assert.AreEqual(@"""say \""hi\"""" C:\a\b", NodeServiceDefinition.WindowsCommandLine([@"say ""hi""", @"C:\a\b"]));
    }

    [TestMethod]
    public void ServiceName_IsOnePerProfile()
    {
        Assert.AreEqual("altanode", NodeServiceDefinition.ServiceName("/home/alice/.alta", defaultProfile: true, developer: false));
        Assert.AreEqual("altanode-dev", NodeServiceDefinition.ServiceName("/home/alice/.alta/dev", defaultProfile: true, developer: true));
        var other = NodeServiceDefinition.ServiceName("/srv/node", defaultProfile: false, developer: false);
        StringAssert.Matches(other, new System.Text.RegularExpressions.Regex("^altanode-[0-9a-f]{8}$"));
        Assert.AreEqual(other, NodeServiceDefinition.ServiceName("/srv/node/", defaultProfile: false, developer: false));
        Assert.AreNotEqual(other, NodeServiceDefinition.ServiceName("/srv/other", defaultProfile: false, developer: false));
        Assert.IsTrue(NodeServiceDefinition.IsValidAccount("codealta"));
        Assert.IsFalse(NodeServiceDefinition.IsValidAccount("-rf"));
        Assert.IsFalse(NodeServiceDefinition.IsValidAccount("a b"));
        Assert.IsFalse(NodeServiceDefinition.IsValidAccount(null));
    }

    [TestMethod]
    public void Locator_StartsAToolFromItsShim()
    {
        var shim = "/home/alice/.dotnet/tools/altanode";
        var store = "/home/alice/.dotnet/tools/.store/codealta.node/4.0.0/codealta.node/4.0.0/tools/net10.0/any/altanode";
        // The paths are made with the separators of the OS that runs the test.
        bool Exists(string path) => path.Replace('\\', '/') is "/home/alice/.dotnet/tools/altanode" or "/home/alice/.dotnet/tools/.store/codealta.node";

        var fromShim = NodeExecutableLocator.Locate(shim, "/any/altanode.dll", Exists)!;
        Assert.AreEqual(NodeExecutableKind.ToolShim, fromShim.Kind);
        Assert.AreEqual(shim, fromShim.Path);
        var fromStore = NodeExecutableLocator.Locate(store, "/any/altanode.dll", Exists)!;
        Assert.AreEqual(NodeExecutableKind.ToolShim, fromStore.Kind);
        Assert.AreEqual(shim, fromStore.Path.Replace('\\', '/'));
        StringAssert.Contains(NodeExecutableLocator.Describe(fromStore), "dotnet tool update");

        var archive = NodeExecutableLocator.Locate("/opt/altanode/altanode", "/opt/altanode/altanode.dll", Exists)!;
        Assert.AreEqual(NodeExecutableKind.Executable, archive.Kind);
        var dotnet = NodeExecutableLocator.Locate(@"C:\Program Files\dotnet\dotnet.exe", @"C:\src\bin\altanode.dll", Exists)!;
        Assert.AreEqual(NodeExecutableKind.DotnetHost, dotnet.Kind);
        CollectionAssert.AreEqual(new[] { @"C:\src\bin\altanode.dll" }, dotnet.PrefixArguments.ToArray());
        Assert.IsNull(NodeExecutableLocator.Locate(null, null, Exists));
    }

    [TestMethod]
    public void Locator_StartsAWindowsToolFromTheCmdShimOfDotnet10()
    {
        // The layout of a RID-specific tool installed by .NET 10 on Windows: altanode.cmd, no altanode.exe shim.
        var tools = @"C:\Users\alice\.dotnet\tools";
        var store = tools + @"\.store\codealta.node\4.0.0\codealta.node.win-x64\4.0.0\tools\net10.0\win-x64\altanode.exe";
        bool Exists(string path) => path.Replace('\\', '/') is "C:/Users/alice/.dotnet/tools/altanode.cmd" or "C:/Users/alice/.dotnet/tools/.store/codealta.node";

        var fromStore = NodeExecutableLocator.Locate(store, @"C:\any\altanode.dll", Exists)!;
        Assert.AreEqual(NodeExecutableKind.ToolShim, fromStore.Kind);
        Assert.AreEqual("C:/Users/alice/.dotnet/tools/altanode.cmd", fromStore.Path.Replace('\\', '/'));
        Assert.AreEqual(0, fromStore.PrefixArguments.Count);
        StringAssert.Contains(NodeExecutableLocator.Describe(fromStore), "dotnet tool update");

        // The app host shim of an older tool keeps its name.
        bool ExistsExe(string path) => path.Replace('\\', '/') is "C:/Users/alice/.dotnet/tools/altanode.exe" or "C:/Users/alice/.dotnet/tools/altanode.cmd";
        Assert.AreEqual("C:/Users/alice/.dotnet/tools/altanode.exe", NodeExecutableLocator.Locate(store, null, ExistsExe)!.Path.Replace('\\', '/'));
    }

    [TestMethod]
    public void Windows_StartsTheCmdShimHeadless_AndRefusesWhatCmdCannotPass()
    {
        using var machine = new FakeServiceMachine(NodeServicePlatform.Windows);
        var manager = new NodeServiceManager(machine.Environment);
        var shim = new NodeExecutable(NodeExecutableKind.ToolShim, @"C:\Users\alice\.dotnet\tools\altanode.cmd", []);
        var plan = manager.Plan(Request(machine) with { Executable = shim, RunArguments = ["run", "--port", "2600", "--name", "R&D (lab) box"] });
        var exec = XDocument.Parse(plan.Definition).Root!.Element(Task + "Actions")!.Element(Task + "Exec")!;
        Assert.AreEqual(@"C:\Windows\System32\conhost.exe", exec.Element(Task + "Command")!.Value);
        // Windows runs a .cmd given to CreateProcess through cmd.exe /c, with the command line conhost passes in quotes.
        Assert.AreEqual(@"--headless C:\Users\alice\.dotnet\tools\altanode.cmd run --port 2600 --name ""R&D (lab) box""", exec.Element(Task + "Arguments")!.Value);

        foreach (var name in new[] { "a&b", "a|b", "a^b", "a<b", "50%", "50% off", "say \"hi\"", "a\nb" })
        {
            var refused = Assert.ThrowsExactly<NodeServiceException>(() => manager.Plan(Request(machine) with { Executable = shim, RunArguments = ["run", "--name", name] }), name);
            StringAssert.Contains(refused.Message, "altanode.cmd");
        }

        var refusedPath = Assert.ThrowsExactly<NodeServiceException>(() => manager.Plan(Request(machine) with
        {
            Executable = shim with { Path = @"C:\Users\R&D\.dotnet\tools\altanode.cmd" },
        }));
        StringAssert.Contains(refusedPath.Message, "cmd.exe");
        // An executable is started directly: cmd.exe never reads its arguments.
        Assert.IsNotNull(manager.Plan(Request(machine) with
        {
            Executable = new NodeExecutable(NodeExecutableKind.Executable, @"C:\altanode\altanode.exe", []), RunArguments = ["run", "--name", "50%&more"],
        }));
    }

    [TestMethod]
    public void Linux_InstallsAUserUnit_TurnsLingeringOn_AndRemovesIt()
    {
        using var machine = new FakeServiceMachine(NodeServicePlatform.Linux);
        var manager = new NodeServiceManager(machine.Environment);
        var plan = manager.Plan(Request(machine));
        var unitPath = Path.Combine(machine.Home, ".config", "systemd", "user", "altanode.service");
        Assert.AreEqual(unitPath, plan.DefinitionPath);
        var before = manager.Query(plan);
        Assert.IsFalse(before.Installed);
        StringAssert.Contains(string.Join('\n', NodeServiceManager.Describe(before)), "not installed");

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
        var answer = string.Join('\n', NodeServiceManager.Describe(installed));
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
        using var machine = new FakeServiceMachine(NodeServicePlatform.Linux) { LingerAllowed = false };
        var manager = new NodeServiceManager(machine.Environment);
        var plan = manager.Plan(Request(machine));
        var output = new StringWriter();

        Assert.AreEqual(NodeExitCodes.Success, manager.Install(plan, output, new StringWriter()));

        StringAssert.Contains(output.ToString(), "sudo loginctl enable-linger alice");
        var answer = string.Join('\n', NodeServiceManager.Describe(manager.Query(plan)));
        StringAssert.Contains(answer, "After a restart: it waits until alice logs in");
        StringAssert.Contains(answer, "After a logout: it stops once the last session of alice ends");
    }

    [TestMethod]
    public void Linux_SystemUnit_NeedsRoot_AndAnAccount()
    {
        using var machine = new FakeServiceMachine(NodeServicePlatform.Linux);
        var system = Request(machine) with { Scope = NodeServiceScope.System, Account = "codealta", StateRoot = null };
        var refused = Assert.ThrowsExactly<NodeServiceException>(() => new NodeServiceManager(machine.Environment).Plan(system));
        StringAssert.Contains(refused.Message, "sudo");

        machine.IsRoot = true;
        var manager = new NodeServiceManager(machine.Environment);
        Assert.ThrowsExactly<NodeServiceException>(() => manager.Plan(system with { Account = "nobody-here" }));
        var plan = manager.Plan(system);
        Assert.AreEqual(Path.Combine(machine.SystemUnitDirectory, "altanode-codealta.service"), plan.DefinitionPath);
        Assert.AreEqual(NodeExitCodes.Success, manager.Install(plan, new StringWriter(), new StringWriter()));
        StringAssert.Contains(File.ReadAllText(plan.DefinitionPath), "\nUser=codealta\n");
        CollectionAssert.AreEqual(new[] { "systemctl daemon-reload", "systemctl enable altanode-codealta.service", "systemctl restart altanode-codealta.service" },
            machine.Commands);
        var answer = string.Join('\n', NodeServiceManager.Describe(manager.Query(plan)));
        StringAssert.Contains(answer, "After a restart: it starts at boot as codealta, without a login.");
    }

    [TestMethod]
    public void MacOS_InstallsALaunchAgent_InTheGuiDomainOfTheUser()
    {
        using var machine = new FakeServiceMachine(NodeServicePlatform.MacOS) { AutoLoginUser = "alice" };
        var manager = new NodeServiceManager(machine.Environment);
        var plan = manager.Plan(Request(machine));
        var plistPath = Path.Combine(machine.Home, "Library", "LaunchAgents", "io.codealta.altanode.plist");
        Assert.AreEqual(plistPath, plan.DefinitionPath);

        Assert.AreEqual(NodeExitCodes.Success, manager.Install(plan, new StringWriter(), new StringWriter()));

        Assert.AreEqual("io.codealta.altanode", Dict(File.ReadAllText(plistPath))["Label"].Value);
        CollectionAssert.AreEqual(new[]
        {
            "launchctl bootout gui/501/io.codealta.altanode", "launchctl enable gui/501/io.codealta.altanode", $"launchctl bootstrap gui/501 {plistPath}",
        }, machine.Commands);
        var answer = string.Join('\n', NodeServiceManager.Describe(manager.Query(plan)));
        StringAssert.Contains(answer, "a LaunchAgent of alice");
        StringAssert.Contains(answer, "it starts when macOS logs alice in, which it does automatically");
        StringAssert.Contains(answer, "After a logout: it stops with the session");

        Assert.AreEqual(NodeExitCodes.Success, manager.Uninstall(plan, new StringWriter(), new StringWriter()));
        Assert.IsFalse(File.Exists(plistPath));
    }

    [TestMethod]
    public void Windows_RegistersATaskFromAUtf16File_DeletesTheFile_AndRefusesASystemService()
    {
        using var machine = new FakeServiceMachine(NodeServicePlatform.Windows);
        var manager = new NodeServiceManager(machine.Environment);
        var plan = manager.Plan(Request(machine) with { Executable = new NodeExecutable(NodeExecutableKind.ToolShim, @"C:\Users\alice\.dotnet\tools\altanode.exe", []) });
        Assert.IsTrue(plan.LeavesNodeRunning, "Task Scheduler ends conhost, not the node: uninstall stops it.");

        Assert.AreEqual(NodeExitCodes.Success, manager.Install(plan, new StringWriter(), new StringWriter()));

        var (text, encoding) = machine.TaskFiles.Single();
        Assert.AreEqual(Encoding.Unicode, encoding);
        StringAssert.Contains(text, @"<UserId>S-1-5-21-1-2-3-1001</UserId>");
        StringAssert.Contains(text, @"<Command>C:\Windows\System32\conhost.exe</Command>");
        Assert.IsFalse(File.Exists(plan.DefinitionPath), "The task XML is given to schtasks, then deleted.");
        Assert.AreEqual($@"schtasks /Run /TN \CodeAlta\altanode", machine.Commands[^1]);
        var answer = string.Join('\n', NodeServiceManager.Describe(manager.Query(plan)));
        StringAssert.Contains(answer, @"a task at logon of PC\alice");
        StringAssert.Contains(answer, @"it waits until PC\alice signs in");

        Assert.AreEqual(NodeExitCodes.Success, manager.Uninstall(plan, new StringWriter(), new StringWriter()));
        Assert.IsFalse(machine.IsRegistered(@"\CodeAlta\altanode"));
        var refused = Assert.ThrowsExactly<NodeServiceException>(() => manager.Plan(Request(machine) with { Scope = NodeServiceScope.System, Account = "codealta" }));
        StringAssert.Contains(refused.Message, "not available on Windows");
    }

    [TestMethod]
    public void Describe_SaysWhenTheServiceWillNotStart()
    {
        var disabled = new NodeServiceFacts(NodeServicePlatform.Windows, NodeServiceScope.User, "a task", @"\CodeAlta\altanode", "alice", true, false, null, null);
        StringAssert.Contains(NodeServiceManager.Describe(disabled)[0], "the task is disabled");
        var autoLogin = disabled with { Enabled = true, AutoLoginUser = "alice", User = @"PC\alice" };
        StringAssert.Contains(NodeServiceManager.Describe(autoLogin)[1], "which it does automatically");
        Assert.IsTrue(NodeServiceManager.IsTaskEnabled("<Task><Settings><Enabled>true</Enabled></Settings></Task>"));
        Assert.IsFalse(NodeServiceManager.IsTaskEnabled("<Task xmlns=\"x\"><Settings><Enabled>false</Enabled></Settings></Task>"));
        Assert.IsFalse(NodeServiceManager.IsTaskEnabled("not xml"));
    }

    private static NodeServiceRequest Request(FakeServiceMachine machine) => new()
    {
        Name = "altanode",
        Executable = new NodeExecutable(NodeExecutableKind.ToolShim, "/home/alice/.dotnet/tools/altanode", []),
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
