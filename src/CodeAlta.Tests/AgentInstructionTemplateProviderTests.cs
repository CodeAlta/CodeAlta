using System.Globalization;

using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Roles;
using CodeAlta.Catalog.Skills;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Orchestration.Runtime.SystemPrompts;

namespace CodeAlta.Tests;

[TestClass]
public sealed class AgentInstructionTemplateProviderTests
{
    [TestMethod]
    public void BuildGeneralInstructions_LoadsFileBackedDefaultSystemPrompt()
    {
        var provider = new AgentInstructionTemplateProvider();

        var instructions = provider.BuildGeneralInstructions(CreateThread(), project: null, CreateProfile());

        AssertLoadsDefaultSystemPrompt(instructions);
        Assert.IsNotNull(instructions.DeveloperInstructions);
        StringAssert.Contains(instructions.DeveloperInstructions, "# Role");
        StringAssert.Contains(instructions.DeveloperInstructions, "# Runtime Context");
        StringAssert.Contains(instructions.DeveloperInstructions, "# Tool Guidance");
        var developerInstructions = NormalizeNewlines(instructions.DeveloperInstructions);
        StringAssert.Contains(developerInstructions, "# Runtime Context\n\n- Current date:");
        StringAssert.Contains(developerInstructions, "\n- Platform:");
        StringAssert.Contains(developerInstructions, "\n- Default shell for shell commands:");
        StringAssert.Contains(developerInstructions, "\n- Current working directory:");
        StringAssert.Contains(developerInstructions, "\n- Thread kind:");
        Assert.IsNotNull(instructions.PromptBundle);
    }

    [TestMethod]
    public void BuildCoordinatorInstructions_LoadsFileBackedDefaultSystemPrompt()
    {
        var provider = new AgentInstructionTemplateProvider();

        var instructions = provider.BuildCoordinatorInstructions(CreateThread(), project: null, CreateProfile());

        AssertLoadsDefaultSystemPrompt(instructions);
        Assert.IsNotNull(instructions.DeveloperInstructions);
        StringAssert.Contains(instructions.DeveloperInstructions, "# Role");
    }

    [TestMethod]
    public void PromptContentLocator_ResolvesCopiedShippedContent()
    {
        var locator = new FileSystemPromptContentLocator();

        var basePath = locator.ResolveBuiltInPromptPath(Path.Combine("base", "default.system-prompt.md"));
        var rolePath = locator.ResolveBuiltInPromptPath(Path.Combine("roles", "default.role.md"));
        var authoringDocPath = locator.ResolveBuiltInDocPath("system-prompt-authoring.md");

        Assert.IsTrue(File.Exists(basePath), basePath);
        Assert.IsTrue(File.Exists(rolePath), rolePath);
        Assert.IsTrue(File.Exists(authoringDocPath), authoringDocPath);
    }

    [TestMethod]
    public void ShippedDefaultSystemPrompt_RemainsWithinFrontmatterTokenBudget()
    {
        var locator = new FileSystemPromptContentLocator();
        var basePath = locator.ResolveBuiltInPromptPath(Path.Combine("base", "default.system-prompt.md"));
        var text = File.ReadAllText(basePath).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var frontmatterEnd = text.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        Assert.IsTrue(text.StartsWith("---\n", StringComparison.Ordinal) && frontmatterEnd > 0, "Default system prompt should include YAML frontmatter.");
        var frontmatter = text[4..frontmatterEnd];
        var body = text[(frontmatterEnd + 5)..].Trim();
        var maxTokensLine = frontmatter.Split('\n').FirstOrDefault(static line => line.StartsWith("max_tokens:", StringComparison.OrdinalIgnoreCase));
        Assert.IsNotNull(maxTokensLine, "Default system prompt should declare max_tokens.");
        var maxTokens = int.Parse(maxTokensLine[(maxTokensLine.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim(), CultureInfo.InvariantCulture);
        var approximateTokens = Math.Max(1, (int)Math.Ceiling(body.Length / 4.0));

        Assert.IsTrue(
            approximateTokens <= maxTokens,
            $"Default system prompt is approximately {approximateTokens} tokens, exceeding max_tokens {maxTokens}.");
    }

    [TestMethod]
    public async Task BuildCoordinatorInstructions_AdvertisesAvailableSkills()
    {
        using var temp = TestTempDirectory.Create();
        var projectRoot = Path.Combine(temp.Path, "repo");
        Directory.CreateDirectory(projectRoot);
        await WriteSkillAsync(
            projectRoot,
            "dotnet-test",
            "Run focused .NET tests for the current task.").ConfigureAwait(false);
        await WriteSkillAsync(
            projectRoot,
            "code-review",
            "Review code for correctness and regressions.").ConfigureAwait(false);

        var provider = new AgentInstructionTemplateProvider(
            new SkillCatalog(),
            new CatalogOptions { GlobalRoot = temp.Path });
        var project = CreateProject(projectRoot);

        var instructions = provider.BuildCoordinatorInstructions(
            CreateThread(projectRoot, project.Id),
            project,
            CreateProfile(skills: ["code-review"]));

        Assert.IsNotNull(instructions.DeveloperInstructions);
        StringAssert.Contains(instructions.DeveloperInstructions, "# Available Skills");
        StringAssert.Contains(instructions.DeveloperInstructions, "<available_skills>");
        StringAssert.Contains(instructions.DeveloperInstructions, "code-review");
        StringAssert.Contains(instructions.DeveloperInstructions, "preferred=\"true\"");
        StringAssert.Contains(instructions.DeveloperInstructions, "project .alta/skills");

        var preferredIndex = instructions.DeveloperInstructions.IndexOf("code-review", StringComparison.Ordinal);
        var otherIndex = instructions.DeveloperInstructions.IndexOf("dotnet-test", StringComparison.Ordinal);
        Assert.IsTrue(preferredIndex >= 0 && otherIndex >= 0 && preferredIndex < otherIndex);
    }

    [TestMethod]
    public void SystemPromptBuilder_AppliesTemplateAndUserOverrides()
    {
        using var temp = TestTempDirectory.Create();
        var roots = CreatePromptRoots(temp.Path);
        File.WriteAllText(Path.Combine(roots.UserPromptRoot, "template.yml"), "base: team\nrole: reviewer\nproject_context: false\ntool_guidance: false");
        File.WriteAllText(Path.Combine(roots.ShippedPromptRoot, "base", "team.system-prompt.md"), "Built-in team base.");
        File.WriteAllText(Path.Combine(roots.ShippedPromptRoot, "roles", "reviewer.role.md"), "Built-in reviewer role.");
        Directory.CreateDirectory(Path.Combine(roots.UserPromptRoot, "base"));
        Directory.CreateDirectory(Path.Combine(roots.UserPromptRoot, "roles"));
        File.WriteAllText(Path.Combine(roots.UserPromptRoot, "base", "team.system-prompt.md"), "User team base.");
        File.WriteAllText(Path.Combine(roots.UserPromptRoot, "roles", "reviewer.role.md"), "User reviewer role.");
        var builder = new SystemPromptBuilder(new FixedPromptContentLocator(roots));

        var bundle = builder.Build(new SystemPromptBuildRequest
        {
            ProviderKey = "provider",
            Thread = CreateThread(temp.Path),
        });

        Assert.AreEqual("User team base.", bundle.SystemMessage);
        Assert.IsNotNull(bundle.DeveloperInstructions);
        StringAssert.Contains(bundle.DeveloperInstructions, "User reviewer role.");
        Assert.IsFalse(bundle.DeveloperInstructions.Contains("# Project Context", StringComparison.Ordinal));
        Assert.IsFalse(bundle.DeveloperInstructions.Contains("# Tool Guidance", StringComparison.Ordinal));
        Assert.AreEqual("team", bundle.Manifest.Template.BaseName);
        Assert.AreEqual("reviewer", bundle.Manifest.Template.RoleName);
        Assert.IsTrue(bundle.Manifest.Parts.Any(static part => part.Status == "replaced"));
    }

    [TestMethod]
    public void SystemPromptBuilder_RendersProjectContextUsingLargestRecognizedFilePerDirectory()
    {
        using var temp = TestTempDirectory.Create();
        var roots = CreatePromptRoots(temp.Path);
        var repoRoot = Path.Combine(temp.Path, "repo");
        var projectRoot = Path.Combine(repoRoot, "src", "Project");
        Directory.CreateDirectory(projectRoot);
        Directory.CreateDirectory(Path.Combine(projectRoot, ".github"));
        File.WriteAllText(Path.Combine(repoRoot, "AGENTS.md"), "root");
        File.WriteAllText(Path.Combine(repoRoot, "CLAUDE.md"), "root claude instructions are longer");
        File.WriteAllText(Path.Combine(projectRoot, "AGENTS.md"), "project agents");
        File.WriteAllText(Path.Combine(projectRoot, ".github", "copilot-instructions.md"), "project copilot instructions are much longer than agents");
        var builder = new SystemPromptBuilder(new FixedPromptContentLocator(roots));

        var bundle = builder.Build(new SystemPromptBuildRequest
        {
            ProviderKey = "provider",
            Thread = CreateThread(projectRoot),
            ProjectRoots = [projectRoot],
        });

        Assert.IsNotNull(bundle.DeveloperInstructions);
        StringAssert.Contains(bundle.DeveloperInstructions, "# Project Context");
        StringAssert.Contains(bundle.DeveloperInstructions, Path.Combine(repoRoot, "CLAUDE.md"));
        Assert.IsFalse(bundle.DeveloperInstructions.Contains(Path.Combine(repoRoot, "AGENTS.md"), StringComparison.Ordinal));
        StringAssert.Contains(bundle.DeveloperInstructions, Path.Combine(projectRoot, ".github", "copilot-instructions.md"));
        Assert.IsFalse(bundle.DeveloperInstructions.Contains(Path.Combine(projectRoot, "AGENTS.md"), StringComparison.Ordinal));
        var developerInstructions = NormalizeNewlines(bundle.DeveloperInstructions);
        StringAssert.Contains(developerInstructions, "<INSTRUCTIONS>\n\nroot claude instructions are longer\n\n</INSTRUCTIONS>");
        StringAssert.Contains(developerInstructions, "<INSTRUCTIONS>\n\nproject copilot instructions are much longer than agents\n\n</INSTRUCTIONS>");
    }

    private static string NormalizeNewlines(string text)
        => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static void AssertLoadsDefaultSystemPrompt(AgentInstructionBundle instructions)
    {
        Assert.IsFalse(string.IsNullOrWhiteSpace(instructions.SystemMessage));
        Assert.IsNotNull(instructions.PromptBundle);
        Assert.AreEqual(instructions.PromptBundle.SystemMessage, instructions.SystemMessage);
        Assert.AreEqual("default", instructions.PromptBundle.Manifest.Template.BaseName);
        Assert.IsFalse(
            instructions.PromptBundle.Diagnostics.Any(static diagnostic => diagnostic.Severity == SystemPromptDiagnosticSeverity.Error),
            string.Join(Environment.NewLine, instructions.PromptBundle.Diagnostics.Select(static diagnostic => diagnostic.Message)));

        var basePart = instructions.PromptBundle.Manifest.Parts.SingleOrDefault(static part =>
            part.Kind == "base" &&
            part.Name == "default" &&
            part.Target == "system" &&
            part.Status == "selected");
        Assert.IsNotNull(basePart);
        Assert.IsTrue(
            basePart.Path?.EndsWith(Path.Combine("base", "default.system-prompt.md"), StringComparison.OrdinalIgnoreCase) == true,
            basePart.Path);
        Assert.IsTrue(File.Exists(basePart.Path), basePart.Path);
    }

    private static async Task WriteSkillAsync(string projectRoot, string name, string description)
    {
        var skillRoot = Path.Combine(projectRoot, ".alta", "skills", name);
        Directory.CreateDirectory(skillRoot);
        await File.WriteAllTextAsync(
            Path.Combine(skillRoot, "SKILL.md"),
            $$"""
            ---
            name: {{name}}
            description: {{description}}
            ---
            # {{name}}

            {{description}}
            """).ConfigureAwait(false);
    }

    private static WorkThreadDescriptor CreateThread(string workingDirectory = @"C:\code\CodeAlta", string? projectId = null)
        => new()
        {
            ThreadId = "thread-1",
            Kind = string.IsNullOrWhiteSpace(projectId) ? WorkThreadKind.GlobalThread : WorkThreadKind.ProjectThread,
            BackendId = AgentBackendIds.OpenAIResponses.Value,
            BackendSessionId = "backend-session-1",
            ProjectRef = projectId,
            WorkingDirectory = workingDirectory,
            Title = "Thread",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            LastActiveAt = DateTimeOffset.UtcNow,
        };

    private static ProjectDescriptor CreateProject(string projectPath)
        => new()
        {
            Id = ProjectId.NewVersion7().ToString(),
            Slug = "repo",
            DisplayName = "Repo",
            ProjectPath = projectPath,
            DefaultBranch = "main",
        };

    private static RoleProfile CreateProfile(IReadOnlyList<string>? skills = null)
        => new()
        {
            RoleId = "general",
            Name = "General",
            Description = "General role",
            Instructions = "Follow the task.",
            ToolsPolicy = new RoleToolsPolicy(),
            Skills = skills ?? [],
            SourcePath = "role.md",
        };

    private static SystemPromptContentRoots CreatePromptRoots(string root)
    {
        var shippedRoot = Path.Combine(root, "app", "content", "system_prompts");
        var docsRoot = Path.Combine(root, "app", "content", "docs");
        var userRoot = Path.Combine(root, "user", ".alta", "system_prompts");
        Directory.CreateDirectory(Path.Combine(shippedRoot, "base"));
        Directory.CreateDirectory(Path.Combine(shippedRoot, "roles"));
        Directory.CreateDirectory(docsRoot);
        Directory.CreateDirectory(userRoot);
        File.WriteAllText(Path.Combine(shippedRoot, "base", "default.system-prompt.md"), "Built-in base.");
        File.WriteAllText(Path.Combine(shippedRoot, "roles", "default.role.md"), "Built-in role.");
        return new SystemPromptContentRoots(shippedRoot, docsRoot, userRoot, null, false);
    }

    private sealed class FixedPromptContentLocator : ISystemPromptContentLocator
    {
        private readonly SystemPromptContentRoots _roots;

        public FixedPromptContentLocator(SystemPromptContentRoots roots)
        {
            _roots = roots;
        }

        public SystemPromptContentRoots GetRoots(SystemPromptDiscoveryContext context) => _roots;

        public string ResolveBuiltInPromptPath(string relativePromptPath) => Path.Combine(_roots.ShippedPromptRoot, relativePromptPath);

        public string ResolveBuiltInDocPath(string fileName) => Path.Combine(_roots.ShippedDocsRoot, fileName);
    }
}
