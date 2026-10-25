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

        public static async Task<Fixture> CreateAsync()
        {
            var temp = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "codealta-sandbox-" + Guid.NewGuid().ToString("N"))).FullName;
            var hub = new AgentHub(new ModelProviderRegistry(), temp);
            var options = new CatalogOptions { GlobalRoot = Path.Combine(temp, "alta") };
            var sessionViewCatalog = new SessionViewCatalog(options);
            var projects = new ProjectCatalog(options);
            var project = await projects.UpsertFromPathAsync(Directory.CreateDirectory(Path.Combine(temp, "project")).FullName);
            var runtime = new SessionRuntimeService(hub, new AgentSessionCatalog(sessionViewCatalog.JournalStore.CreateSessionStore()), projects,
                sessionViewCatalog, new AgentInstructionTemplateProvider(catalogOptions: options), options);
            return new Fixture(temp, hub, runtime, sessionViewCatalog.JournalStore, projects, project, options);
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
