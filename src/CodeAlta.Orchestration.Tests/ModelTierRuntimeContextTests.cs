using CodeAlta.Catalog;
using CodeAlta.Catalog.ModelTiers;
using CodeAlta.Catalog.Skills;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Orchestration.Runtime.SystemPrompts;

namespace CodeAlta.Orchestration.Tests;

/// <summary>The Runtime Context tells an agent the tiers and the fallback of the space of its session.</summary>
[TestClass]
public sealed class ModelTierRuntimeContextTests
{
    private const string FallbackLine = "a session goes on with the next model of its tier";

    [TestMethod]
    public async Task RuntimeContext_TellsTheFallbackOfTheSpaceOfTheSession()
    {
        using var temp = new TempDirectory();
        var app = Path.Combine(temp.Path, "app");
        var prompts = Path.Combine(app, "content", "prompts");
        Directory.CreateDirectory(Path.Combine(prompts, "agents"));
        Directory.CreateDirectory(Path.Combine(prompts, "system"));
        File.WriteAllText(Path.Combine(prompts, "agents", "default.prompt.md"), "---\nname: Fixture\nskills: false\nruntime_context: true\ntool_guidance: false\n---\nFixture prompt.");
        File.WriteAllText(Path.Combine(prompts, "system", "default.system-prompt.md"), "Fixture system.");
        var options = new CatalogOptions { GlobalRoot = Path.Combine(temp.Path, "global") };
        Directory.CreateDirectory(options.GlobalRoot);
        // The usual settings: one tier, the fallback off.
        File.WriteAllText(options.ConfigPath, """
            [models.tiers]
            standard = ["prov-a:model-a@high"]

            [providers.work-b]
            type = "openai-chat"
            spaces = ["business"]
            """);
        var spaces = new SpaceCatalog(new ProjectCatalog(options));
        var business = (await spaces.CreateAsync("Business")).Space!;
        var tiers = ModelSettings.Read(new CodeAltaModelSettingsDocument
        {
            Tiers = new CodeAltaModelTiersDocument { Standard = ["work-b:model-b@high", "prov-a:model-a@high"] },
        }).Tiers;
        Assert.IsTrue((await spaces.UpdateSettingsAsync(business.Id,
            _ => new SpaceSettings(new SpaceNewSessions(null, SpaceModelTiers.From(tiers), new ModelFallbackSettings(true, true, true))))).Succeeded);
        var template = new AgentInstructionTemplateProvider(new SkillCatalog([new NoInstructionTestSkillRoots()]), options,
            new FileSystemPromptContentLocator(app), null, new(Path.Combine(temp.Path, "home"), temp.Path));

        var inBusiness = Text(template.BuildGeneralInstructions(Session("business", business.Id, options.GlobalRoot), null, "model-b"));
        StringAssert.Contains(inBusiness, "- Model tiers: `standard` (work-b:model-b@high, then 1 more).");
        StringAssert.Contains(inBusiness, FallbackLine);

        var inDefault = Text(template.BuildGeneralInstructions(Session("default", null, options.GlobalRoot), null, "model-a"));
        StringAssert.Contains(inDefault, "- Model tiers: `standard` (prov-a:model-a@high).");
        Assert.IsFalse(inDefault.Contains(FallbackLine, StringComparison.Ordinal), "The default space has the usual settings: the fallback is off.");
    }

    private static string Text(AgentInstructionBundle bundle) => $"{bundle.SystemMessage}\n{bundle.DeveloperInstructions}";

    private static SessionViewDescriptor Session(string id, string? spaceId, string root)
        => new()
        {
            SessionId = id,
            ProviderId = "prov-a",
            ProviderKey = "prov-a",
            Kind = SessionViewKind.GlobalSession,
            Status = SessionViewStatus.Active,
            Title = id,
            WorkingDirectory = root,
            SpaceId = spaceId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            LastActiveAt = DateTimeOffset.UtcNow,
        };

    private sealed class NoInstructionTestSkillRoots : ISkillRootProvider
    {
        public ValueTask<IReadOnlyList<SkillRootRegistration>> GetRootsAsync(SkillDiscoveryContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<SkillRootRegistration>>([]);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "codealta-tiers-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
