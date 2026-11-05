using CodeAlta.Catalog;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Orchestration.Runtime.SystemPrompts;

namespace CodeAlta.Orchestration.Tests;

/// <summary>
/// The instructions ask agents to name CodeAlta as a co-author of their commits and to say that what they write in an
/// external system comes from CodeAlta, with the texts the user wrote, unless the user turned either off.
/// </summary>
[TestClass]
public sealed class CommitCoAuthorPromptTests
{
    [TestMethod]
    public void TheCoAuthorLine_IsAskedForByDefault_AndLeftOutOnceTheSettingIsOff()
    {
        using var fixture = new PromptFixture();
        var on = fixture.Instructions();
        StringAssert.Contains(on, "- Commit messages: end the message of every git commit you create with the trailer `Co-authored-by: CodeAlta <noreply@codealta.io>`");
        StringAssert.Contains(on, "never in place of them");

        fixture.Store.SaveGlobalCommitCoAuthor(false);
        Assert.DoesNotContain(SystemPromptBuilder.CoAuthorTrailer, fixture.Instructions());

        fixture.Store.SaveGlobalCommitCoAuthor(true);
        StringAssert.Contains(fixture.Instructions(), SystemPromptBuilder.CoAuthorTrailer);
    }

    [TestMethod]
    public void TheCoAuthorLine_AsksForTheTrailerTheUserWrote_WhichOffKeepsForLater()
    {
        using var fixture = new PromptFixture();
        const string trailer = "Assisted-by: Contoso Agent <agents@contoso.example>";
        fixture.Store.SaveGlobalAttributionSettings(fixture.Store.LoadGlobalAttributionSettings() with { CoAuthorTrailer = trailer });
        var custom = fixture.Instructions();
        StringAssert.Contains(custom, $"- Commit messages: end the message of every git commit you create with the trailer `{trailer}`, in its last paragraph");
        Assert.DoesNotContain(SystemPromptBuilder.CoAuthorTrailer, custom);

        // Off, the line is gone, trailer and all; on again, it asks for the trailer of the user.
        fixture.Store.SaveGlobalCommitCoAuthor(false);
        var off = fixture.Instructions();
        Assert.DoesNotContain("- Commit messages:", off);
        Assert.DoesNotContain(trailer, off);
        fixture.Store.SaveGlobalCommitCoAuthor(true);
        StringAssert.Contains(fixture.Instructions(), $"`{trailer}`");
    }

    [TestMethod]
    public void TheExternalWritingInstruction_IsToldByDefault_AsTheUserWordsIt_AndLeftOutEntirelyOnceOff()
    {
        using var fixture = new PromptFixture();
        StringAssert.Contains(fixture.Instructions(), AttributionSettings.DefaultExternalWritingInstruction);

        const string instruction = "When you post a comment for the user, end it with `_Posted by the Contoso agent._`.";
        fixture.Store.SaveGlobalAttributionSettings(fixture.Store.LoadGlobalAttributionSettings() with { ExternalWritingInstruction = instruction });
        var custom = fixture.Instructions();
        StringAssert.Contains(custom, instruction);
        Assert.DoesNotContain("comes from CodeAlta", custom);

        fixture.Store.SaveGlobalAttributionSettings(fixture.Store.LoadGlobalAttributionSettings() with { ExternalWriting = false });
        var off = fixture.Instructions();
        Assert.DoesNotContain(instruction, off);
        Assert.DoesNotContain("external system", off);
        Assert.DoesNotContain("comes from CodeAlta", off);
        // The commits are another choice: their line stays.
        StringAssert.Contains(off, SystemPromptBuilder.CoAuthorTrailer);
    }

    // An application folder with a prompt of its own and a configuration of the user, in a folder of the test.
    private sealed class PromptFixture : IDisposable
    {
        private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "codealta-co-author-prompt-" + Guid.NewGuid().ToString("N"))).FullName;
        private readonly CatalogOptions _options;
        private readonly string _app;

        public PromptFixture()
        {
            _app = Path.Combine(_root, "app");
            var prompts = Path.Combine(_app, "content", "prompts");
            Directory.CreateDirectory(Path.Combine(prompts, "agents"));
            Directory.CreateDirectory(Path.Combine(prompts, "system"));
            File.WriteAllText(Path.Combine(prompts, "agents", "default.prompt.md"), "---\nname: Fixture\n---\nFixture prompt.");
            File.WriteAllText(Path.Combine(prompts, "system", "default.system-prompt.md"), "Fixture system.");
            _options = new CatalogOptions { GlobalRoot = Path.Combine(_root, "global") };
            Directory.CreateDirectory(_options.GlobalRoot);
            Store = new CodeAltaConfigStore(_options);
        }

        public CodeAltaConfigStore Store { get; }

        public string Instructions()
        {
            var template = new AgentInstructionTemplateProvider(null, _options, new FileSystemPromptContentLocator(_app), Store, new(Path.Combine(_root, "home"), _root));
            var bundle = template.BuildGeneralInstructions(Session(_options.GlobalRoot), null, "model");
            return $"{bundle.SystemMessage}\n{bundle.DeveloperInstructions}";
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); }
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
