using CodeAlta.Catalog;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Orchestration.Runtime.SystemPrompts;

namespace CodeAlta.Orchestration.Tests;

/// <summary>The Runtime Context asks agents to name CodeAlta as a co-author of their commits, unless the user turned it off.</summary>
[TestClass]
public sealed class CommitCoAuthorPromptTests
{
    [TestMethod]
    public void TheCoAuthorLine_IsAskedForByDefault_AndLeftOutOnceTheSettingIsOff()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "codealta-co-author-prompt-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var app = Path.Combine(root, "app");
            var prompts = Path.Combine(app, "content", "prompts");
            Directory.CreateDirectory(Path.Combine(prompts, "agents"));
            Directory.CreateDirectory(Path.Combine(prompts, "system"));
            File.WriteAllText(Path.Combine(prompts, "agents", "default.prompt.md"), "---\nname: Fixture\n---\nFixture prompt.");
            File.WriteAllText(Path.Combine(prompts, "system", "default.system-prompt.md"), "Fixture system.");
            var options = new CatalogOptions { GlobalRoot = Path.Combine(root, "global") };
            Directory.CreateDirectory(options.GlobalRoot);
            var store = new CodeAltaConfigStore(options);
            string Instructions()
            {
                var template = new AgentInstructionTemplateProvider(null, options, new FileSystemPromptContentLocator(app), store, new(Path.Combine(root, "home"), root));
                var bundle = template.BuildGeneralInstructions(Session(options.GlobalRoot), null, "model");
                return $"{bundle.SystemMessage}\n{bundle.DeveloperInstructions}";
            }

            var on = Instructions();
            StringAssert.Contains(on, "- Commit messages: end the message of every git commit you create with the trailer `Co-authored-by: CodeAlta <noreply@codealta.io>`");
            StringAssert.Contains(on, "never in place of them");

            store.SaveGlobalCommitCoAuthor(false);
            Assert.DoesNotContain(SystemPromptBuilder.CoAuthorTrailer, Instructions());

            store.SaveGlobalCommitCoAuthor(true);
            StringAssert.Contains(Instructions(), SystemPromptBuilder.CoAuthorTrailer);
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
