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

        Assert.IsFalse(string.IsNullOrWhiteSpace(instructions.SystemMessage));
        StringAssert.Contains(instructions.SystemMessage, "You are CodeAlta");
        Assert.IsNull(instructions.DeveloperInstructions);
    }

    [TestMethod]
    public void BuildCoordinatorInstructions_LoadsFileBackedDefaultSystemPrompt()
    {
        var provider = new AgentInstructionTemplateProvider();

        var instructions = provider.BuildCoordinatorInstructions(CreateThread(), project: null, CreateProfile());

        Assert.IsFalse(string.IsNullOrWhiteSpace(instructions.SystemMessage));
        StringAssert.Contains(instructions.SystemMessage, "software engineering agent");
        Assert.IsNull(instructions.DeveloperInstructions);
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
        StringAssert.Contains(instructions.DeveloperInstructions, "<available_skills>");
        StringAssert.Contains(instructions.DeveloperInstructions, "code-review");
        StringAssert.Contains(instructions.DeveloperInstructions, "preferred=\"true\"");
        StringAssert.Contains(instructions.DeveloperInstructions, "project .alta/skills");

        var preferredIndex = instructions.DeveloperInstructions.IndexOf("code-review", StringComparison.Ordinal);
        var otherIndex = instructions.DeveloperInstructions.IndexOf("dotnet-test", StringComparison.Ordinal);
        Assert.IsTrue(preferredIndex >= 0 && otherIndex >= 0 && preferredIndex < otherIndex);
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
}
