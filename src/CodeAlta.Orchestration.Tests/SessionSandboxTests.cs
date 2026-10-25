using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Sandbox;

namespace CodeAlta.Orchestration.Tests;

[TestClass]
public sealed class SessionSandboxTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task TheSandboxOfASession_IsDecidedByTheMostSpecificScope_AndItsOwnChoiceIsSaved()
    {
        await using var f = await Fixture.CreateAsync();
        await f.CreateSessionAsync("s");
        Assert.IsFalse((await f.Runtime.GetSessionSandboxAsync("s")).IsSandboxed, "nothing sandboxes a session by default");

        f.WriteConfig("[sandbox]\nall_sessions = \"workspace\"\n");
        var all = await f.Runtime.GetSessionSandboxAsync("s");
        Assert.AreEqual(new SandboxChoice("workspace", SandboxScope.AllSessions), all.Choice);
        Assert.AreEqual(SandboxNetwork.Hosts, all.Profile!.Network);

        f.Project.Sandbox = SandboxProfiles.Off;
        await f.Projects.SaveAsync(f.Project);
        Assert.AreEqual(new SandboxChoice(null, SandboxScope.Project), (await f.Runtime.GetSessionSandboxAsync("s")).Choice, "the project turns it off");

        f.WriteConfig("[sandbox]\nall_sessions = \"workspace\"\n\n[providers.codex]\ntype = \"codex\"\nsandbox = \"strict\"\n");
        var provider = await f.Runtime.GetSessionSandboxAsync("s");
        Assert.AreEqual(new SandboxChoice("strict", SandboxScope.Provider), provider.Choice, "distrust of a provider follows it into every project");
        Assert.AreEqual(SandboxNetwork.Off, provider.Profile!.Network);

        var session = (await f.Runtime.ResolveOwnedSessionAsync("s", CancellationToken.None))!;
        await f.Runtime.SetOwnedSandboxAsync(session, SandboxProfiles.Off);
        Assert.AreEqual(new SandboxChoice(null, SandboxScope.Session), (await f.Runtime.GetSessionSandboxAsync("s")).Choice, "the user's choice for the session wins");
        Assert.AreEqual(SandboxProfiles.Off, f.LastStateRecord("s").GetProperty("sandbox").GetString(), "the choice is saved with the session");

        await using var restarted = f.CreateRuntime();
        Assert.AreEqual(new SandboxChoice(null, SandboxScope.Session), (await restarted.GetSessionSandboxAsync("s")).Choice, "and read again after a restart");
    }

    [TestMethod]
    public async Task AProfileThatCannotBeUsed_KeepsTheSessionSandboxed_WithAProblem()
    {
        await using var f = await Fixture.CreateAsync();
        await f.CreateSessionAsync("s");
        f.WriteConfig("[sandbox]\nall_sessions = \"missing\"\n");

        var sandbox = await f.Runtime.GetSessionSandboxAsync("s");

        Assert.IsTrue(sandbox.IsSandboxed, "a sandbox that cannot run never becomes no sandbox");
        Assert.IsNull(sandbox.Profile);
        Assert.Contains("'missing'", sandbox.Problem!);
    }

    [TestMethod]
    public async Task ASessionCreatedByASandboxedSession_GetsItsProfile_WhateverItAsks()
    {
        await using var f = await Fixture.CreateAsync();
        await f.CreateSessionAsync("creator");
        await f.CreateSessionAsync("plain");
        var creator = (await f.Runtime.ResolveOwnedSessionAsync("creator", CancellationToken.None))!;
        await f.Runtime.SetOwnedSandboxAsync(creator, "strict");

        Assert.AreEqual("strict", await f.Runtime.GetCreatedSessionSandboxAsync("creator"));
        Assert.IsNull(await f.Runtime.GetCreatedSessionSandboxAsync("plain"));
        Assert.IsNull(await f.Runtime.GetCreatedSessionSandboxAsync(null), "the user, with no session, decides");
    }

    [TestMethod]
    public async Task TheProfilesOfTheUser_AreMapped_AndAValueNoVersionKnowsIsTheSafestOne()
    {
        await using var f = await Fixture.CreateAsync();
        f.WriteConfig("[sandbox.profiles.mine]\nextends = \"workspace\"\nnetwork = \"everything\"\noutside = \"sometimes\"\nhosts = [\"pkgs.dev.azure.com\"]\n");

        var profile = SandboxProfiles.Resolve("mine", f.Runtime.ReadSandboxSettings().Profiles);

        Assert.AreEqual(SandboxNetwork.Off, profile.Network);
        Assert.AreEqual(SandboxOutside.Never, profile.Outside);
        CollectionAssert.Contains(profile.Hosts.ToList(), "pkgs.dev.azure.com");
    }

    [TestMethod]
    public async Task ARepositoryTable_IsReportedAndIgnored()
    {
        await using var f = await Fixture.CreateAsync();
        await f.CreateSessionAsync("s");
        Directory.CreateDirectory(Path.Combine(f.Project.ProjectPath, ".alta"));
        File.WriteAllText(Path.Combine(f.Project.ProjectPath, ".alta", "config.toml"), "[sandbox]\nall_sessions = \"off\"\n");
        f.WriteConfig("[sandbox]\nall_sessions = \"workspace\"\n");

        var sandbox = await f.Runtime.GetSessionSandboxAsync("s");

        Assert.IsTrue(sandbox.ProjectSetsSandbox);
        Assert.AreEqual("workspace", sandbox.Choice.Profile);
    }

    [TestMethod]
    public async Task TheToolsOfASandboxedSession_RunInItsWorker_WhichFollowsTheProfileOfTheMoment()
    {
        var backend = new InProcessBackend();
        await using var f = await Fixture.CreateAsync(backend);
        await f.CreateSessionAsync("s");
        var tools = f.RoutedTools("s");

        var plain = await tools.CallAsync("write_file", new { path = "plain.txt", content = "here" });
        Assert.IsTrue(plain.Success, plain.Error);
        Assert.AreEqual(0, backend.Starts.Count, "a session that is not sandboxed runs its tools here");

        f.WriteConfig("[sandbox]\nall_sessions = \"strict\"\n");
        var written = await tools.CallAsync("write_file", new { path = "boxed.txt", content = "in the box" });
        Assert.IsTrue(written.Success, written.Error);
        Assert.AreEqual("in the box", File.ReadAllText(Path.Combine(f.Project.ProjectPath, "boxed.txt")));
        var read = await tools.CallAsync("read_file", new { path = "boxed.txt" });
        Assert.IsTrue(read.Success, read.Error);
        Assert.AreEqual(1, backend.Starts.Count, "one worker per session, kept between calls");
        var policy = backend.Starts[0];
        Assert.AreEqual("strict", policy.ProfileName);
        Assert.AreEqual(Path.GetFullPath(f.Project.ProjectPath), policy.WorkingDirectory);

        f.WriteConfig("[sandbox]\nall_sessions = \"workspace\"\n");
        Assert.IsTrue((await tools.CallAsync("read_file", new { path = "boxed.txt" })).Success);
        Assert.AreEqual(2, backend.Starts.Count, "another profile is another container");
        Assert.AreEqual("workspace", backend.Starts[1].ProfileName);

        await f.Runtime.SandboxWorkers.StopIfUnusedAsync("s");
        Assert.IsTrue((await tools.CallAsync("read_file", new { path = "boxed.txt" })).Success);
        Assert.AreEqual(3, backend.Starts.Count, "a session that went away left no worker: the next call starts one");

        f.WriteConfig("[sandbox]\nall_sessions = \"missing\"\n");
        var refused = await tools.CallAsync("read_file", new { path = "boxed.txt" });
        Assert.IsFalse(refused.Success, "a profile that cannot be used runs nothing, here or there");
        Assert.Contains("'missing'", refused.Error!);
    }

    [TestMethod]
    public async Task ASandboxThatCannotRun_RefusesTheRunAndTheTools_AndSaysWhy()
    {
        var backend = new InProcessBackend { Available = false };
        await using var f = await Fixture.CreateAsync(backend);
        await f.CreateSessionAsync("s");
        var router = f.Runtime.CreateSandboxToolRouter("s", "codex", [f.Project.ProjectPath]);
        Assert.IsNull(await router.RefuseRunAsync(providerRunsItsOwnTools: true, CancellationToken.None), "nothing is refused to a session that is not sandboxed");

        f.WriteConfig("[sandbox]\nall_sessions = \"workspace\"\n");
        Assert.Contains("not available on this computer", (await router.RefuseRunAsync(false, CancellationToken.None))!);
        backend.Available = true;
        Assert.IsNull(await router.RefuseRunAsync(false, CancellationToken.None));
        Assert.Contains("runs its own tools", (await router.RefuseRunAsync(true, CancellationToken.None))!);

        backend.Fails = true;
        var result = await f.RoutedTools("s").CallAsync("shell_command", new { command = "echo no" });
        Assert.IsFalse(result.Success);
        Assert.Contains("did not run", result.Error!);
        Assert.Contains("refused by the test", result.Error!);

        var (starter, refusal) = await f.Runtime.GetJobStarterAsync("s");
        Assert.IsNull(starter);
        Assert.Contains("not attached", refusal!, "a background command waits for a session that runs");
    }

    [TestMethod]
    public async Task WebGetOfASandboxWithoutNetwork_IsRefused_BeforeAnyRequest()
    {
        await using var f = await Fixture.CreateAsync(new InProcessBackend());
        await f.CreateSessionAsync("s");
        f.WriteConfig("[sandbox]\nall_sessions = \"strict\"\n");

        var result = await f.RoutedTools("s").CallAsync("webget", new { url = "http://127.0.0.1:9/" });

        Assert.IsFalse(result.Success);
        Assert.Contains("has no network", result.Error!);
    }

    // The tools of a session as its agent gets them, through the router of the runtime.
    private sealed class RoutedToolSet(IReadOnlyList<AgentToolDefinition> tools, string sessionId)
    {
        public async Task<AgentToolResult> CallAsync(string tool, object arguments)
            => await tools.Single(candidate => candidate.Spec.Name == tool).Handler(
                new AgentToolInvocation(new ModelProviderId("codex"), sessionId, Guid.NewGuid().ToString("N"), tool, System.Text.Json.JsonSerializer.SerializeToElement(arguments)),
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(60));
    }

    // A sandbox that runs the worker in this process, on two pipes, and records the policies it is given.
    private sealed class InProcessBackend : ISandboxBackend
    {
        public List<SandboxPolicy> Starts { get; } = [];

        public bool Available { get; set; } = true;

        public bool Fails { get; set; }

        public SandboxAvailability GetAvailability() => Available
            ? new SandboxAvailability(true, "test", null, null, new HashSet<SandboxNetwork> { SandboxNetwork.Off, SandboxNetwork.Hosts, SandboxNetwork.On })
            : SandboxAvailability.Unavailable("test", "the test has none.", null);

        public Task<ISandboxProcess> StartAsync(SandboxPolicy policy, string commandLine, string? proxyEndpoint, CancellationToken cancellationToken)
        {
            if (Fails) throw new SandboxUnavailableException("refused by the test.");
            lock (Starts) Starts.Add(policy);
            return Task.FromResult<ISandboxProcess>(new Process());
        }

        private sealed class Process : ISandboxProcess
        {
            private readonly System.IO.Pipes.AnonymousPipeServerStream _toWorker = new(System.IO.Pipes.PipeDirection.Out);
            private readonly System.IO.Pipes.AnonymousPipeServerStream _fromWorker = new(System.IO.Pipes.PipeDirection.Out);
            private readonly System.IO.Pipes.AnonymousPipeClientStream _workerInput;
            private readonly System.IO.Pipes.AnonymousPipeClientStream _hostInput;
            private readonly CancellationTokenSource _kill = new();
            private readonly Task _worker;

            public Process()
            {
                _workerInput = new(System.IO.Pipes.PipeDirection.In, _toWorker.ClientSafePipeHandle);
                _hostInput = new(System.IO.Pipes.PipeDirection.In, _fromWorker.ClientSafePipeHandle);
                _worker = Task.Run(() => CodeAlta.Sandbox.Worker.SandboxWorker.RunAsync(_workerInput, _fromWorker, _kill.Token));
            }

            public int Id => Environment.ProcessId;

            public Stream StandardInput => _toWorker;

            public Stream StandardOutput => _hostInput;

            public Stream StandardError { get; } = new MemoryStream();

            public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
            {
                try
                {
                    await _worker.WaitAsync(cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                }

                return 0;
            }

            public void Kill()
            {
                _kill.Cancel();
                _toWorker.Dispose();
            }

            public async ValueTask DisposeAsync()
            {
                Kill();
                await WaitForExitAsync(CancellationToken.None);
                _fromWorker.Dispose();
                _workerInput.Dispose();
                _hostInput.Dispose();
                _kill.Dispose();
            }
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _temp;
        private readonly AgentHub _hub;

        private Fixture(string temp, AgentHub hub, SessionRuntimeService runtime, SessionViewJournalStore journal, ProjectCatalog projects, ProjectDescriptor project, CatalogOptions options)
        {
            _temp = temp;
            _hub = hub;
            Runtime = runtime;
            Journal = journal;
            Projects = projects;
            Project = project;
            Options = options;
        }

        public SessionRuntimeService Runtime { get; }

        public SessionViewJournalStore Journal { get; }

        public ProjectCatalog Projects { get; }

        public ProjectDescriptor Project { get; }

        public CatalogOptions Options { get; }

        public static async Task<Fixture> CreateAsync(ISandboxBackend? backend = null)
        {
            var temp = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "codealta-sandbox-" + Guid.NewGuid().ToString("N"))).FullName;
            var hub = new AgentHub(new ModelProviderRegistry(), temp);
            var options = new CatalogOptions { GlobalRoot = Path.Combine(temp, "alta") };
            var sessionViewCatalog = new SessionViewCatalog(options);
            var projects = new ProjectCatalog(options);
            var project = await projects.UpsertFromPathAsync(Directory.CreateDirectory(Path.Combine(temp, "project")).FullName);
            var runtime = new SessionRuntimeService(hub, new AgentSessionCatalog(sessionViewCatalog.JournalStore.CreateSessionStore()), projects,
                sessionViewCatalog, new AgentInstructionTemplateProvider(catalogOptions: options), options)
            {
                SandboxBackend = backend,
                SandboxDataRoot = Path.Combine(temp, "sandbox"),
                ScratchpadRoot = Path.Combine(temp, "scratchpad"),
            };
            return new Fixture(temp, hub, runtime, sessionViewCatalog.JournalStore, projects, project, options);
        }

        public RoutedToolSet RoutedTools(string sessionId)
        {
            var options = new CodeAlta.Agent.Runtime.Tools.AgentBuiltInToolOptions
            {
                ProviderId = new ModelProviderId("codex"),
                SessionId = sessionId,
                WorkingDirectory = Project.ProjectPath,
                OnPermissionRequest = (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
            };
            var router = Runtime.CreateSandboxToolRouter(sessionId, "codex", [Project.ProjectPath]);
            return new RoutedToolSet(router.Route(options, CodeAlta.Agent.Runtime.Tools.AgentBuiltInToolFactory.CreateDefaultTools(options)), sessionId);
        }

        public SessionRuntimeService CreateRuntime()
        {
            var sessionViewCatalog = new SessionViewCatalog(Options);
            return new SessionRuntimeService(_hub, new AgentSessionCatalog(sessionViewCatalog.JournalStore.CreateSessionStore()), new ProjectCatalog(Options),
                sessionViewCatalog, new AgentInstructionTemplateProvider(catalogOptions: Options), Options);
        }

        public void WriteConfig(string toml)
        {
            Directory.CreateDirectory(Options.GlobalRoot);
            File.WriteAllText(Path.Combine(Options.GlobalRoot, "config.toml"), toml);
        }

        public System.Text.Json.JsonElement LastStateRecord(string sessionId)
        {
            var path = new AgentRuntimePathLayout(Options.StateRoot).GetSessionFilePath(sessionId, CreatedAt);
            var line = File.ReadAllLines(path).Last(static line => line.Contains(SessionViewJournalStore.SessionStateEventType, StringComparison.Ordinal));
            using var document = System.Text.Json.JsonDocument.Parse(line);
            return document.RootElement.GetProperty("raw").Clone();
        }

        public async Task CreateSessionAsync(string id)
        {
            var session = new SessionViewDescriptor
            {
                SessionId = id, Kind = SessionViewKind.ProjectSession, ProviderId = "codex", ProviderKey = "codex", ProjectRef = Project.Id,
                WorkingDirectory = Project.ProjectPath, Title = id, Status = SessionViewStatus.Active, CreatedAt = CreatedAt, UpdatedAt = CreatedAt, LastActiveAt = CreatedAt,
            };
            await Journal.EnsureHeaderAsync(session);
            await Journal.CreateSessionStore().UpsertSessionAsync(new() { SessionId = id, ProviderId = new("codex"), ProviderKey = "codex", ProtocolFamily = "codex",
                WorkingDirectory = Project.ProjectPath, Title = id, CreatedAt = CreatedAt, UpdatedAt = CreatedAt });
            await Journal.AppendStateAsync(session, new SessionViewLocalState { ProviderKey = "codex" });
        }

        public async ValueTask DisposeAsync()
        {
            await Runtime.DisposeAsync();
            await _hub.DisposeAsync();
            try
            {
                Directory.Delete(_temp, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
