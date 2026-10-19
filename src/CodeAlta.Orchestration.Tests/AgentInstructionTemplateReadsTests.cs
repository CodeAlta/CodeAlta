using CodeAlta.Catalog;
using CodeAlta.Catalog.Skills;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Orchestration.Runtime.SystemPrompts;

namespace CodeAlta.Orchestration.Tests;

/// <summary>
/// The provider keeps its stores and its catalog of spaces from one prompt to the next: a change of the configuration or
/// of a space still reaches the next prompt.
/// </summary>
[TestClass]
public sealed class AgentInstructionTemplateReadsTests
{
    [TestMethod]
    public async Task EachPrompt_ReadsTheConfigurationAndTheSpacesAsTheyAreNow()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-template-reads-" + Guid.NewGuid().ToString("N"));
        try
        {
            var app = Path.Combine(root, "app");
            var prompts = Path.Combine(app, "content", "prompts");
            Directory.CreateDirectory(Path.Combine(prompts, "agents"));
            Directory.CreateDirectory(Path.Combine(prompts, "system"));
            File.WriteAllText(Path.Combine(prompts, "agents", "default.prompt.md"), "---\nname: Fixture\nskills: false\nruntime_context: true\ntool_guidance: false\n---\nFixture prompt.");
            File.WriteAllText(Path.Combine(prompts, "system", "default.system-prompt.md"), "Fixture system.");
            var options = new CatalogOptions { GlobalRoot = Path.Combine(root, "global") };
            Directory.CreateDirectory(options.GlobalRoot);
            File.WriteAllText(options.ConfigPath, "[models.tiers]\nstandard = [\"prov-a:model-a@high\"]\n\n[tools]\ndisabled = [\"webget\"]\n");
            var spaces = new SpaceCatalog(new ProjectCatalog(options));
            var business = (await spaces.CreateAsync("Business")).Space!;
            var template = new AgentInstructionTemplateProvider(new SkillCatalog([new NoSkillRoots()]), options,
                new FileSystemPromptContentLocator(app), null, new(Path.Combine(root, "home"), root));
            var session = Session(business.Id, options.GlobalRoot);

            StringAssert.Contains(Text(template.BuildGeneralInstructions(session, null, "model-a")), "`standard` (prov-a:model-a@high)");
            CollectionAssert.AreEquivalent(new[] { "webget" }, template.ResolveDisabledTools(session, null, null).ToArray());

            // Another writer changes the global file, and the space is given tools of its own to do without.
            File.WriteAllText(options.ConfigPath, "[models.tiers]\nstandard = [\"prov-b:model-b@low\"]\n\n[tools]\ndisabled = [\"shell_command\"]\n");
            Assert.IsTrue((await spaces.UpdateSettingsAsync(business.Id, settings => settings with { Tools = new SpaceTools(["view_image"]) })).Succeeded);

            StringAssert.Contains(Text(template.BuildGeneralInstructions(session, null, "model-b")), "`standard` (prov-b:model-b@low)");
            CollectionAssert.AreEquivalent(new[] { "shell_command", "view_image" }, template.ResolveDisabledTools(session, null, null).ToArray());

            Assert.IsTrue((await spaces.DeleteAsync(business.Id)).Succeeded);
            CollectionAssert.AreEquivalent(new[] { "shell_command" }, template.ResolveDisabledTools(session, null, null).ToArray(),
                "A space deleted meanwhile is read as the default space.");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static string Text(AgentInstructionBundle bundle) => $"{bundle.SystemMessage}\n{bundle.DeveloperInstructions}";

    private static SessionViewDescriptor Session(string spaceId, string root)
        => new()
        {
            SessionId = "session",
            ProviderId = "prov-a",
            ProviderKey = "prov-a",
            Kind = SessionViewKind.GlobalSession,
            Status = SessionViewStatus.Active,
            Title = "session",
            WorkingDirectory = root,
            SpaceId = spaceId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            LastActiveAt = DateTimeOffset.UtcNow,
        };

    private sealed class NoSkillRoots : ISkillRootProvider
    {
        public ValueTask<IReadOnlyList<SkillRootRegistration>> GetRootsAsync(SkillDiscoveryContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<SkillRootRegistration>>([]);
    }
}
