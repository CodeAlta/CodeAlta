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

    [TestMethod]
    public void TheSkillsOfCodeAlta_AndThePreferredOnes_ComeFirst_InFull_InEveryFormOfTheList()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "codealta-skill-preferred-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var app = Path.Combine(root, "app");
            var prompts = Path.Combine(app, "content", "prompts");
            Directory.CreateDirectory(Path.Combine(prompts, "agents"));
            Directory.CreateDirectory(Path.Combine(prompts, "system"));
            File.WriteAllText(Path.Combine(prompts, "agents", "default.prompt.md"), "---\nname: Fixture\nskills: true\nruntime_context: false\ntool_guidance: false\n---\nFixture prompt.");
            File.WriteAllText(Path.Combine(prompts, "system", "default.system-prompt.md"), "Fixture system.");
            var options = new CatalogOptions { GlobalRoot = Path.Combine(root, "global") };
            var builtIn = Path.Combine(root, "builtin");
            var builtInFolder = Directory.CreateDirectory(Path.Combine(builtIn, "codealta-runtime")).FullName;
            File.WriteAllText(Path.Combine(builtInFolder, "SKILL.md"), "---\nname: codealta-runtime\ndescription: How CodeAlta works, in full whatever the number of skills.\n---\n# CodeAlta runtime\n");
            void Skills(int count)
            {
                for (var i = 0; i < count; i++)
                {
                    var folder = Directory.CreateDirectory(Path.Combine(options.GlobalRoot, "skills", $"skill-{i:00}")).FullName;
                    File.WriteAllText(Path.Combine(folder, "SKILL.md"), $"---\nname: skill-{i:00}\ndescription: Does task {i}. It has a long description that the short form leaves out.\n---\n# Skill {i}\n");
                }
            }

            IReadOnlyList<string> preferred = ["skill-03", "not-installed", "SKILL-01"];
            var asked = new List<PreferredSkillsRequest>();
            var catalog = new SkillCatalog([new UserCodeAltaSkillRootProvider(), new BuiltInCodeAltaSkillRootProvider(builtIn)]);
            string Instructions()
            {
                var template = new AgentInstructionTemplateProvider(catalog, options, new FileSystemPromptContentLocator(app), null, new(Path.Combine(root, "home"), root))
                {
                    SkillPromptCatalog = (_, _) => "auto",
                    PreferredSkills = request => { asked.Add(request); return preferred; },
                };
                var bundle = template.BuildGeneralInstructions(Session(options.GlobalRoot), null, "model");
                return $"{bundle.SystemMessage}\n{bundle.DeveloperInstructions}";
            }

            static int At(string text, string name) => text.IndexOf($"<skill name=\"{name}\"", StringComparison.Ordinal);

            // Few skills: the whole list, CodeAlta's first, then the preferred ones in their order.
            Skills(5);
            var few = Instructions();
            StringAssert.Contains(few, AgentInstructionTemplateProvider.PreferredHint);
            Assert.IsTrue(At(few, "codealta-runtime") < At(few, "skill-03") && At(few, "skill-03") < At(few, "skill-01") && At(few, "skill-01") < At(few, "skill-00"), few);
            StringAssert.Contains(few, "<skill name=\"skill-03\" preferred=\"true\"");
            StringAssert.Contains(few, "<skill name=\"skill-00\" preferred=\"false\"");
            Assert.DoesNotContain("not-installed", few);
            Assert.AreEqual(("s1", "default"), (asked[0].SessionId, asked[0].SpaceId));

            // Beyond twelve: the preferred skills and CodeAlta's keep their description, the others have one line.
            Skills(20);
            var many = Instructions();
            StringAssert.Contains(many, "How CodeAlta works, in full");
            StringAssert.Contains(many, "Does task 3. It has a long description");
            Assert.DoesNotContain("Does task 0.", many);
            StringAssert.Contains(many, ">Skill 0</skill>");
            Assert.AreEqual(many, Instructions(), "the same ranking gives the same prompt");

            // Beyond sixty: they stay listed, the others are counted.
            Skills(70);
            var counts = Instructions();
            Assert.IsTrue(At(counts, "codealta-runtime") >= 0 && At(counts, "codealta-runtime") < At(counts, "skill-03") && At(counts, "skill-03") < At(counts, "skill-01"), counts);
            StringAssert.Contains(counts, "count=\"68\"");
            Assert.AreEqual(-1, At(counts, "skill-00"));

            // At most eight are preferred, and none without a ranking.
            preferred = [.. Enumerable.Range(0, 20).Select(static i => $"skill-{i:00}")];
            Assert.AreEqual(8, Instructions().Split("\" preferred=\"true\" location=").Length - 1);
            preferred = [];
            var none = Instructions();
            Assert.DoesNotContain(AgentInstructionTemplateProvider.PreferredHint, none);
            Assert.DoesNotContain("preferred=\"true\"", none);
            StringAssert.Contains(none, "How CodeAlta works, in full");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }

    private static SessionViewDescriptor Session(string root)
        => new()
        {
            SessionId = "s1", ProviderId = "prov", ProviderKey = "prov", Kind = SessionViewKind.GlobalSession, Status = SessionViewStatus.Active,
            Title = "s1", WorkingDirectory = root, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, LastActiveAt = DateTimeOffset.UtcNow,
        };
}
