using CodeAlta.Catalog;
using CodeAlta.Catalog.Skills;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Orchestration.Runtime.SystemPrompts;

namespace CodeAlta.Orchestration.Tests;

/// <summary>How the prompt lists the skills: whole while there are few, then short, and the same from turn to turn.</summary>
[TestClass]
public sealed class SkillPromptCatalogTests
{
    [TestMethod]
    public async Task ManySkills_AreListedShort_WhenSearchCanFindThem_AndThePromptStaysTheSame()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "codealta-skill-prompt-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var app = Path.Combine(root, "app");
            var prompts = Path.Combine(app, "content", "prompts");
            Directory.CreateDirectory(Path.Combine(prompts, "agents"));
            Directory.CreateDirectory(Path.Combine(prompts, "system"));
            File.WriteAllText(Path.Combine(prompts, "agents", "default.prompt.md"), "---\nname: Fixture\nskills: true\nruntime_context: false\ntool_guidance: false\n---\nFixture prompt.");
            File.WriteAllText(Path.Combine(prompts, "system", "default.system-prompt.md"), "Fixture system.");
            var options = new CatalogOptions { GlobalRoot = Path.Combine(root, "global") };
            var skills = Path.Combine(options.GlobalRoot, "skills");
            void Skills(int count)
            {
                for (var i = 0; i < count; i++)
                {
                    var folder = Directory.CreateDirectory(Path.Combine(skills, $"skill-{i:00}")).FullName;
                    File.WriteAllText(Path.Combine(folder, "SKILL.md"), $"---\nname: skill-{i:00}\ndescription: Does task {i}. It has a long description that the short form leaves out.\n---\n# Skill {i}\n");
                }
            }

            Skills(5);
            var mode = "auto";
            string Instructions()
            {
                var template = new AgentInstructionTemplateProvider(new SkillCatalog([new UserCodeAltaSkillRootProvider()]), options,
                    new FileSystemPromptContentLocator(app), null, new(Path.Combine(root, "home"), root))
                {
                    SkillPromptCatalog = (_, _) => mode,
                };
                var bundle = template.BuildGeneralInstructions(Session(options.GlobalRoot), null, "model");
                return $"{bundle.SystemMessage}\n{bundle.DeveloperInstructions}";
            }

            // Few skills: the whole list, as before search.
            var few = Instructions();
            StringAssert.Contains(few, "It has a long description");
            Assert.DoesNotContain(AgentInstructionTemplateProvider.SearchHint, few);

            // Beyond twelve: names and one line each, and how to find them.
            Skills(20);
            var many = Instructions();
            StringAssert.Contains(many, AgentInstructionTemplateProvider.SearchHint);
            StringAssert.Contains(many, "<skill name=\"skill-19\"");
            Assert.DoesNotContain("the short form leaves out", many);
            // The one line is the title of the skill, from its first heading.
            StringAssert.Contains(many, ">Skill 19</skill>");
            Assert.AreEqual(many, Instructions(), "the prompt is the same from turn to turn while nothing is installed");

            // Beyond sixty: the count per scope.
            Skills(70);
            var counts = Instructions();
            StringAssert.Contains(counts, "count=\"70\"");
            Assert.DoesNotContain("<skill name=", counts);

            // full keeps the whole list whatever the number, as a host without search does.
            mode = "full";
            StringAssert.Contains(Instructions(), "the short form leaves out");
            mode = "compact";
            Skills(1);
            var compact = Instructions();
            Assert.DoesNotContain("the short form leaves out", compact);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }

        await Task.CompletedTask;
    }

    private static SessionViewDescriptor Session(string root)
        => new()
        {
            SessionId = "s1", ProviderId = "prov", ProviderKey = "prov", Kind = SessionViewKind.GlobalSession, Status = SessionViewStatus.Active,
            Title = "s1", WorkingDirectory = root, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, LastActiveAt = DateTimeOffset.UtcNow,
        };
}
