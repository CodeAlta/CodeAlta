using CodeAlta.Desktop.Fleet.MachineSetup;

namespace CodeAlta.Desktop.Tests;

/// <summary>Create my server on this computer (<see cref="LocalServerSetup"/>) against scripted processes: install, start, read the setup code, claim.</summary>
[TestClass]
public sealed class LocalServerSetupTests
{
    private const string Running = """
        CodeAlta Server 4.0.0, process 1234, since 03/11/2026 15:00
        Listens on http://127.0.0.1:2692
        Health: ok, no owner yet (altaserver setup-code), schema 7
        """;

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task OnAComputerWithoutTheServer_InstallsStartsAndClaimsIt_WithTheCodeReadHere()
    {
        var tools = Path.Combine(Path.GetTempPath(), "codealta-server-tools");
        var processes = new ScriptedProcesses();
        processes.Answers.Enqueue(new SshResult(0, "Tool 'codealta.server' was installed.", ""));
        processes.Answers.Enqueue(new SshResult(1, "No server runs on the data root.", ""));
        processes.Answers.Enqueue(new SshResult(0, Running.ReplaceLineEndings("\n"), ""));
        processes.Answers.Enqueue(new SshResult(0, "ABCD-EFGH\nValid until 15:30, once, from this machine only.\n", ""));
        processes.OnInstall = () => processes.Files.Add(Path.Combine(tools, OperatingSystem.IsWindows() ? "altaserver.exe" : "altaserver"));
        var steps = new List<SshSetupProgress>();
        (Uri Address, string Code)? claimed = null;

        var link = await new LocalServerSetup(processes, "4.0.0+abc").RunAsync(new LocalServerRequest { OwnerName = "Alex", OrganizationName = "Home", Port = 2692, ToolPath = tools,
            DataRoot = @"C:\data\server" }, (address, code, _) => { claimed = (address, code); return Task.FromResult("link-1"); }, steps.Add, TestContext.CancellationToken);

        Assert.AreEqual("link-1", link);
        Assert.AreEqual((new Uri("http://127.0.0.1:2692"), "ABCD-EFGH"), claimed, "the code goes from the server to the claim without the person");
        CollectionAssert.AreEqual(new[] { "dotnet tool install CodeAlta.Server --version 4.0.0 --tool-path " + tools,
            "status --data-root C:\\data\\server", "status --data-root C:\\data\\server", "setup-code --data-root C:\\data\\server" }, processes.Commands.ToArray());
        CollectionAssert.AreEqual(new[] { "run --port 2692 --data-root C:\\data\\server" }, processes.Started.ToArray());
        Assert.AreEqual("http://127.0.0.1:2692", steps.Single(static step => step is { Step: "start", State: "done" }).Detail);
    }

    [TestMethod]
    public async Task AServerThatRunsAndHasAnOwner_SaysToJoinItInstead()
    {
        var processes = new ScriptedProcesses();
        processes.Files.Add(Path.Combine("/t", OperatingSystem.IsWindows() ? "altaserver.exe" : "altaserver"));
        processes.Answers.Enqueue(new SshResult(0, Running.ReplaceLineEndings("\n"), ""));
        processes.Answers.Enqueue(new SshResult(1, "", "The server already has an owner: there is no setup code."));
        var failure = await Assert.ThrowsExactlyAsync<SshSetupException>(() => new LocalServerSetup(processes, "4.0.0").RunAsync(
            new LocalServerRequest { OwnerName = "Alex", ToolPath = "/t" }, (_, _, _) => Task.FromResult("x"), static _ => { }, TestContext.CancellationToken));
        Assert.AreEqual("server_owned", failure.Code);
        Assert.AreEqual(0, processes.Started.Count, "a server that runs already is not started again");
    }

    [TestMethod]
    public async Task OnWindows_TheProgramOfACmdLauncherIsStarted_NotTheLauncher()
    {
        if (!OperatingSystem.IsWindows()) return;
        const string tools = @"C:\tools";
        const string program = @"C:\tools\.store\codealta.server\4.0.0\codealta.server.win-x64\4.0.0\tools\net10.0\win-x64\altaserver.exe";
        var processes = new ScriptedProcesses();
        processes.Files.Add(@"C:\tools\altaserver.cmd");
        processes.Files.Add(program);
        processes.Lines.AddRange(["@echo off", "\"%~dp0.store\\codealta.server\\4.0.0\\codealta.server.win-x64\\4.0.0\\tools\\net10.0\\win-x64\\altaserver.exe\" %*"]);
        processes.Answers.Enqueue(new SshResult(0, Running.ReplaceLineEndings("\n"), ""));
        processes.Answers.Enqueue(new SshResult(0, "CODE\n", ""));
        var steps = new List<SshSetupProgress>();
        await new LocalServerSetup(processes, "4.0.0").RunAsync(new LocalServerRequest { OwnerName = "Alex", ToolPath = tools }, (_, _, _) => Task.FromResult("l"), steps.Add,
            TestContext.CancellationToken);
        Assert.AreEqual(program, steps.Single(static step => step is { Step: "find", State: "done" }).Detail);
    }

    [TestMethod]
    public void TheAddressAndThePackageVersion_AreReadFromWhatTheServerAndTheFolderSay()
    {
        Assert.AreEqual(new Uri("http://127.0.0.1:2586"), LocalServerSetup.ListenAddress("CodeAlta Server 4.0.0\nListens on http://127.0.0.1:2586, https://box:443\n"));
        Assert.IsNull(LocalServerSetup.ListenAddress("No server runs on x."));
        var folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "codealta-server-packages-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            foreach (var name in new[] { "CodeAlta.Server.3.4.1-alpha.0.43.nupkg", "CodeAlta.Server.win-x64.3.4.1-alpha.0.43.nupkg", "CodeAlta.Server.3.4.0.nupkg" })
                File.WriteAllText(Path.Combine(folder, name), "");
            Assert.AreEqual("3.4.1-alpha.0.43", LocalServerSetup.PackageVersion(folder));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private sealed class ScriptedProcesses : ILocalProcesses
    {
        public Queue<SshResult> Answers { get; } = new();

        public List<string> Commands { get; } = [];

        public List<string> Started { get; } = [];

        public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Action? OnInstall { get; set; }

        public Task<SshResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            Commands.Add(executable == "dotnet" ? "dotnet " + string.Join(' ', arguments) : string.Join(' ', arguments));
            if (executable == "dotnet") OnInstall?.Invoke();
            return Task.FromResult(Answers.Count > 0 ? Answers.Dequeue() : new SshResult(0, "", ""));
        }

        public void StartDetached(string executable, IReadOnlyList<string> arguments) => Started.Add(string.Join(' ', arguments));

        public bool Exists(string path) => Files.Contains(path);

        public List<string> Lines { get; } = [];

        public IReadOnlyList<string> ReadLines(string path) => Lines;
    }
}
