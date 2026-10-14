using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.LiveTool;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Tests;

public sealed partial class AltaLiveToolTests
{
    [TestMethod]
    public async Task AgentPrompts_OfAnotherSpace_AreNotListed_NorRun_AndANewSessionStartsWithTheAgentOfItsSpace()
    {
        await using var fixture = await SpaceResourceFixture.CreateAsync().ConfigureAwait(false);
        fixture.WriteConfig("""
            [prompts.spaces]
            plan = ["business"]
            """);
        await fixture.WritePromptAsync("reviewer").ConfigureAwait(false);
        Assert.AreEqual(SpaceChangeStatus.Ok, (await fixture.Spaces.UpdateSettingsAsync("business", settings => settings with { NewSessions = new SpaceNewSessions("plan") }).ConfigureAwait(false)).Status);
        var business = await fixture.CreateAsync(AltaCallerIdentity.Cli, "--global", "--space", "business").ConfigureAwait(false);
        var personal = await fixture.CreateAsync(AltaCallerIdentity.Cli, "--global", "--space", "personal").ConfigureAwait(false);

        // A new session takes the agent of its space, unless one is asked for.
        Assert.AreEqual("plan", business.GetProperty("modelSelection").GetProperty("promptId").GetString());
        Assert.AreEqual("default", personal.GetProperty("modelSelection").GetProperty("promptId").GetString());
        var asked = await fixture.CreateAsync(AltaCallerIdentity.Cli, "--global", "--space", "business", "--prompt-id", "reviewer").ConfigureAwait(false);
        Assert.AreEqual("reviewer", asked.GetProperty("modelSelection").GetProperty("promptId").GetString());

        var personalAgent = Agent(personal);
        fixture.AssertRefused(await fixture.Dispatcher.InvokeAsync(["session", "create", "--global", "--prompt-id", "plan"], caller: personalAgent).ConfigureAwait(false), "prompt.notInSpace");
        fixture.AssertRefused(await fixture.Dispatcher.InvokeAsync(["session", "set_agent", Id(personal), "--prompt-id", "plan"], caller: AltaCallerIdentity.Cli).ConfigureAwait(false), "prompt.notInSpace");
        fixture.AssertRefused(await fixture.Dispatcher.InvokeAsync(["session", "send", Id(personal), "--message", "hello", "--prompt-id", "plan"], caller: AltaCallerIdentity.Cli).ConfigureAwait(false), "prompt.notInSpace");
        fixture.AssertRefused(await fixture.Dispatcher.InvokeAsync(["prompt", "show", "plan"], caller: personalAgent).ConfigureAwait(false), "prompt.notInSpace");
        var setInBusiness = await fixture.Dispatcher.InvokeAsync(["session", "set_agent", Id(business), "--prompt-id", "plan"], caller: AltaCallerIdentity.Cli).ConfigureAwait(false);
        Assert.AreEqual(AltaExitCodes.Success, setInBusiness.ExitCode, setInBusiness.Stdout + setInBusiness.Stderr);

        // A session lists the prompts of its space; the user lists them all.
        CollectionAssert.DoesNotContain(await fixture.PromptIdsAsync(personalAgent).ConfigureAwait(false), "plan");
        CollectionAssert.Contains(await fixture.PromptIdsAsync(personalAgent).ConfigureAwait(false), "reviewer", "A prompt that lists no space is in every space.");
        CollectionAssert.Contains(await fixture.PromptIdsAsync(Agent(business)).ConfigureAwait(false), "plan");
        CollectionAssert.Contains(await fixture.PromptIdsAsync(AltaCallerIdentity.Cli).ConfigureAwait(false), "plan");
    }

    [TestMethod]
    public async Task Skills_OfAnotherSpace_AreNotListed_NorActivated()
    {
        await using var fixture = await SpaceResourceFixture.CreateAsync().ConfigureAwait(false);
        fixture.WriteConfig("""
            [skills.spaces]
            terraform = ["business", "default"]
            """);
        await WriteSkillAsync(Path.Combine(fixture.Options.GlobalRoot!, "skills", "terraform"), "terraform", "Terraform plans.").ConfigureAwait(false);
        await WriteSkillAsync(Path.Combine(fixture.Options.GlobalRoot!, "skills", "notes"), "notes", "Notes.").ConfigureAwait(false);
        var personal = Agent(await fixture.CreateAsync(AltaCallerIdentity.Cli, "--global", "--space", "personal").ConfigureAwait(false));
        var business = Agent(await fixture.CreateAsync(AltaCallerIdentity.Cli, "--global", "--space", "business").ConfigureAwait(false));

        CollectionAssert.DoesNotContain(await fixture.SkillNamesAsync(personal).ConfigureAwait(false), "terraform");
        CollectionAssert.Contains(await fixture.SkillNamesAsync(personal).ConfigureAwait(false), "notes");
        CollectionAssert.Contains(await fixture.SkillNamesAsync(business).ConfigureAwait(false), "terraform");
        CollectionAssert.Contains(await fixture.SkillNamesAsync(AltaCallerIdentity.Cli).ConfigureAwait(false), "terraform");

        var refused = await fixture.Dispatcher.InvokeAsync(["skill", "activate", "terraform"], caller: personal).ConfigureAwait(false);
        fixture.AssertRefused(refused, "skill.notInSpace");
        StringAssert.Contains(refused.Stdout + refused.Stderr, "space 'personal'");
    }

    [TestMethod]
    public async Task AnAgent_ForksAndSendsTo_OnlyTheSessionsOfItsOwnSpace()
    {
        await using var fixture = await SpaceResourceFixture.CreateAsync().ConfigureAwait(false);
        var business = await fixture.CreateAsync(AltaCallerIdentity.Cli, "--global", "--space", "business").ConfigureAwait(false);
        var personal = await fixture.CreateAsync(AltaCallerIdentity.Cli, "--global", "--space", "personal").ConfigureAwait(false);
        var personalAgent = Agent(personal);

        fixture.AssertRefused(await fixture.Dispatcher.InvokeAsync(["session", "fork", Id(business)], caller: personalAgent).ConfigureAwait(false), "space.notOwnSpace");
        foreach (var verb in new[] { "send", "queue", "message" })
        {
            fixture.AssertRefused(await fixture.Dispatcher.InvokeAsync(["session", verb, Id(business), "--message", "hello"], caller: personalAgent).ConfigureAwait(false), "space.notOwnSpace");
        }

        fixture.AssertRefused(await fixture.Dispatcher.InvokeAsync(["reminder", "create", "--session", Id(business), "--duration", "00:10:00", "--content", "check"], caller: personalAgent).ConfigureAwait(false), "space.notOwnSpace");

        // The user reaches every session.
        var queued = await fixture.Dispatcher.InvokeAsync(["session", "queue", Id(business), "--message", "hello"], caller: AltaCallerIdentity.Cli).ConfigureAwait(false);
        Assert.IsFalse((queued.Stdout + queued.Stderr).Contains("space.notOwnSpace", StringComparison.Ordinal), queued.Stdout + queued.Stderr);
    }

    private static string Id(JsonElement created) => created.GetProperty("sessionId").GetString()!;

    private static AltaCallerIdentity Agent(JsonElement created) => new() { Kind = "agent", SourceSessionId = Id(created) };

    private sealed class SpaceResourceFixture : IAsyncDisposable
    {
        private readonly TempDirectory _root;
        private readonly SessionRuntimeService _runtime;

        private SpaceResourceFixture(TempDirectory root, CatalogOptions options, SpaceCatalog spaces, SessionRuntimeService runtime, AltaCommandDispatcher dispatcher)
            => (_root, Options, Spaces, _runtime, Dispatcher) = (root, options, spaces, runtime, dispatcher);

        public CatalogOptions Options { get; }

        public SpaceCatalog Spaces { get; }

        public AltaCommandDispatcher Dispatcher { get; }

        public static async Task<SpaceResourceFixture> CreateAsync()
        {
            var root = TempDirectory.Create();
            var options = new CatalogOptions { GlobalRoot = Path.Combine(root.Path, "global") };
            Directory.CreateDirectory(options.GlobalRoot);
            var projectCatalog = new ProjectCatalog(options);
            var spaces = new SpaceCatalog(projectCatalog);
            await spaces.CreateAsync("Business").ConfigureAwait(false);
            await spaces.CreateAsync("Personal").ConfigureAwait(false);
            var descriptor = new ModelProviderDescriptor(new ModelProviderId("alpha"), "Alpha");
            var registry = new ModelProviderRegistry();
            registry.RegisterOrReplaceSessionRuntime(descriptor, () => new StatefulProviderRuntime(descriptor.ProviderId));
            var runtime = CreateRuntime(options, registry);
            var dispatcher = CreateDispatcher(new AltaServiceCollection()
                .Add(options)
                .Add(projectCatalog)
                .Add(spaces)
                .Add<IAltaSpaceView>(new ShownSpaceView { ShownSpaceId = "personal" })
                .Add(new SessionViewCatalog(options))
                .Add(runtime)
                .Add(new Catalog.Skills.SkillCatalog())
                .Add<IReadOnlyList<ModelProviderDescriptor>>([descriptor])
                .Add<IAltaSessionQueryService>(new ThrowingSessionQueryService()));
            return new SpaceResourceFixture(root, options, spaces, runtime, dispatcher);
        }

        public void WriteConfig(string content) => File.WriteAllText(Path.Combine(Options.GlobalRoot!, "config.toml"), content);

        public async Task WritePromptAsync(string id)
        {
            var folder = Path.Combine(Options.GlobalRoot!, "prompts", "agents");
            Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(Path.Combine(folder, id + ".prompt.md"),
                $"---\nname: {id}\ndescription: The {id} agent.\nsystem: default\n---\nThe {id} agent.\n").ConfigureAwait(false);
        }

        public async Task<JsonElement> CreateAsync(AltaCallerIdentity caller, params string[] more)
        {
            var result = await Dispatcher.InvokeAsync(["session", "create", .. more], caller: caller).ConfigureAwait(false);
            Assert.AreEqual(AltaExitCodes.Success, result.ExitCode, result.Stdout + result.Stderr);
            return ReadJsonLines(result.Stdout).Single(static line => line.GetProperty("type").GetString() == "alta.session.created");
        }

        public void AssertRefused(AltaCommandResult result, string code)
        {
            Assert.AreEqual(AltaExitCodes.Unsupported, result.ExitCode, result.Stdout + result.Stderr);
            var error = ReadJsonLines(result.Stdout + result.Stderr).First(static line => line.GetProperty("type").GetString() == "alta.error");
            Assert.AreEqual(code, error.GetProperty("code").GetString());
        }

        public async Task<string[]> PromptIdsAsync(AltaCallerIdentity caller)
        {
            var result = await Dispatcher.InvokeAsync(["prompt", "list"], caller: caller).ConfigureAwait(false);
            Assert.AreEqual(AltaExitCodes.Success, result.ExitCode, result.Stdout + result.Stderr);
            return [.. ReadJsonLines(result.Stdout).Where(static line => line.TryGetProperty("promptId", out _)).Select(static line => line.GetProperty("promptId").GetString()!)];
        }

        public async Task<string[]> SkillNamesAsync(AltaCallerIdentity caller)
        {
            var result = await Dispatcher.InvokeAsync(["skill", "list", "--detailed"], caller: caller).ConfigureAwait(false);
            Assert.AreEqual(AltaExitCodes.Success, result.ExitCode, result.Stdout + result.Stderr);
            return [.. ReadJsonLines(result.Stdout).Where(static line => line.TryGetProperty("name", out _)).Select(static line => line.GetProperty("name").GetString()!)];
        }

        public async ValueTask DisposeAsync()
        {
            await _runtime.DisposeAsync().ConfigureAwait(false);
            _root.Dispose();
        }
    }
}
