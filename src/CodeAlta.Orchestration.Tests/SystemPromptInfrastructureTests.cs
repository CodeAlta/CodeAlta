using CodeAlta.Catalog;
using CodeAlta.Orchestration.Runtime.SystemPrompts;

namespace CodeAlta.Orchestration.Tests;

[TestClass]
public sealed class SystemPromptInfrastructureTests
{
    [TestMethod]
    public void AgentPromptCatalog_ListsBuiltInGlobalProjectAndMarksOverrides()
    {
        using var temp = TempDirectory.Create();
        var appBase = Path.Combine(temp.Path, "app");
        var globalRoot = Path.Combine(temp.Path, "global");
        var projectRoot = Path.Combine(temp.Path, "project");
        WritePrompt(appBase, "default", "Default Built-in", "default", "Built-in body.");
        WritePrompt(globalRoot, "default", "Default Global", "default", "Global body.");
        WritePrompt(globalRoot, "global-extra", "Global Extra", "default", "Global extra body.");
        WritePrompt(projectRoot, "default", "Default Project", "project-system", "Project body.");

        var catalog = new AgentPromptCatalog(new FileSystemPromptContentLocator(appBase));
        var allPrompts = catalog.ListPrompts(new AgentPromptCatalogQuery
        {
            UserCodeAltaRoot = globalRoot,
            ProjectRoot = projectRoot,
            ProjectPromptResourcesTrusted = true,
        });

        CollectionAssert.AreEqual(
            new[]
            {
                AgentPromptSourceKind.BuiltIn,
                AgentPromptSourceKind.UserGlobal,
                AgentPromptSourceKind.UserGlobal,
                AgentPromptSourceKind.Project,
            },
            allPrompts.Select(static prompt => prompt.SourceKind).ToArray());
        Assert.IsTrue(allPrompts.Single(prompt => prompt.SourceKind == AgentPromptSourceKind.BuiltIn).IsShadowed);
        Assert.IsTrue(allPrompts.Single(prompt => prompt.SourceKind == AgentPromptSourceKind.UserGlobal && prompt.PromptName == "default").IsShadowed);

        var effectiveDefault = catalog.ResolvePrompt(
            new AgentPromptCatalogQuery
            {
                UserCodeAltaRoot = globalRoot,
                ProjectRoot = projectRoot,
                ProjectPromptResourcesTrusted = true,
            },
            "default");

        Assert.IsNotNull(effectiveDefault);
        Assert.AreEqual(AgentPromptSourceKind.Project, effectiveDefault.SourceKind);
        Assert.AreEqual("Default Project", effectiveDefault.DisplayName);
        Assert.AreEqual("project-system", effectiveDefault.SystemPromptName);
    }

    [TestMethod]
    public void AgentPromptCatalog_AppendsEffectiveAgentPromptBodyAndMetadata()
    {
        using var temp = TempDirectory.Create();
        var appBase = Path.Combine(temp.Path, "app");
        var globalRoot = Path.Combine(temp.Path, "global");
        WritePrompt(appBase, "default", "Default Built-in", "built-in-system", "Built-in body.", "Built-in description.");
        WritePrompt(
            globalRoot,
            "default",
            name: null,
            system: "default",
            body: "Global appended body.",
            description: null,
            frontmatterLines: ["mode: append"]);

        var catalog = new AgentPromptCatalog(new FileSystemPromptContentLocator(appBase));
        var allPrompts = catalog.ListPrompts(new AgentPromptCatalogQuery
        {
            UserCodeAltaRoot = globalRoot,
        });
        var effectiveDefault = catalog.ResolvePrompt(
            new AgentPromptCatalogQuery
            {
                UserCodeAltaRoot = globalRoot,
            },
            "default");

        Assert.IsNotNull(effectiveDefault);
        Assert.AreEqual(AgentPromptSourceKind.UserGlobal, effectiveDefault.SourceKind);
        Assert.AreEqual(PromptCompositionMode.Append, effectiveDefault.Mode);
        Assert.AreEqual("Default Built-in", effectiveDefault.DisplayName);
        Assert.AreEqual("Built-in description.", effectiveDefault.Description);
        Assert.AreEqual("built-in-system", effectiveDefault.SystemPromptName);
        Assert.AreEqual($"Built-in body.{Environment.NewLine}{Environment.NewLine}Global appended body.", effectiveDefault.Body);
        Assert.IsFalse(allPrompts.Single(prompt => prompt.SourceKind == AgentPromptSourceKind.BuiltIn).IsShadowed);
        Assert.IsFalse(allPrompts.Single(prompt => prompt.SourceKind == AgentPromptSourceKind.UserGlobal).IsShadowed);
    }

    [TestMethod]
    public void SystemPromptBuilder_UsesSelectedAgentPromptBodyAndSystemProperty()
    {
        using var temp = TempDirectory.Create();
        var appBase = Path.Combine(temp.Path, "app");
        var globalRoot = Path.Combine(temp.Path, "global");
        var projectRoot = Path.Combine(temp.Path, "project");
        Directory.CreateDirectory(projectRoot);
        WriteSystem(appBase, "default", "Built-in default system.");
        WritePrompt(appBase, "default", "Default", "default", "Built-in default prompt.");
        WriteSystem(projectRoot, "custom-system", "Project custom system.");
        WritePrompt(projectRoot, "review", "Review", "custom-system", "Review the current change set.");

        var builder = new SystemPromptBuilder(new FileSystemPromptContentLocator(appBase));
        var bundle = builder.Build(new SystemPromptBuildRequest
        {
            ProviderKey = "codex",
            ProviderType = "codex",
            ProtocolFamily = "codex",
            Session = new SessionViewDescriptor
            {
                SessionId = "session-1",
                ProviderId = "codex",
                ProviderKey = "codex",
                WorkingDirectory = projectRoot,
                Kind = SessionViewKind.ProjectSession,
            },
            Project = new ProjectDescriptor
            {
                Id = "project-1",
                Slug = "project-1",
                DisplayName = "Project 1",
                ProjectPath = projectRoot,
            },
            UserCodeAltaRoot = globalRoot,
            SelectedPromptName = "review",
            PartOptionsOverride = new PartialSystemPromptPartOptions(
                Skills: false,
                ProjectContext: false,
                RuntimeContext: false,
                ToolGuidance: false),
        });

        Assert.AreEqual("Project custom system.", bundle.SystemMessage);
        StringAssert.Contains(bundle.DeveloperInstructions!, "# Agent Prompt");
        StringAssert.Contains(bundle.DeveloperInstructions!, "Review the current change set.");
        Assert.AreEqual("custom-system", bundle.Manifest.Composition.SystemPromptName);
        Assert.AreEqual("review", bundle.Manifest.Composition.AgentPromptName);
        Assert.IsTrue(bundle.Manifest.Parts.Any(static part => part.Key == "agents/review" && part.Status == "selected"));
    }

    [TestMethod]
    public void SystemPromptBuilder_UsesAgentPromptFrontmatterCompositionOverrides()
    {
        using var temp = TempDirectory.Create();
        var appBase = Path.Combine(temp.Path, "app");
        var globalRoot = Path.Combine(temp.Path, "global");
        var projectRoot = Path.Combine(temp.Path, "project");
        Directory.CreateDirectory(projectRoot);
        WriteSystem(appBase, "default", "Built-in default system.");
        WritePrompt(appBase, "default", "Default", "default", "Built-in default prompt.");
        WritePrompt(
            projectRoot,
            "minimal",
            "Minimal",
            "default",
            "Minimal prompt body.",
            frontmatterLines:
            [
                "skills: false",
                "project_context: false",
                "runtime_context: false",
                "tool_guidance: false",
            ]);

        var builder = new SystemPromptBuilder(new FileSystemPromptContentLocator(appBase));
        var bundle = builder.Build(new SystemPromptBuildRequest
        {
            ProviderKey = "codex",
            ProviderType = "codex",
            ProtocolFamily = "codex",
            Session = new SessionViewDescriptor
            {
                SessionId = "session-1",
                ProviderId = "codex",
                ProviderKey = "codex",
                WorkingDirectory = projectRoot,
                Kind = SessionViewKind.ProjectSession,
            },
            Project = new ProjectDescriptor
            {
                Id = "project-1",
                Slug = "project-1",
                DisplayName = "Project 1",
                ProjectPath = projectRoot,
            },
            UserCodeAltaRoot = globalRoot,
            SelectedPromptName = "minimal",
            AvailableSkillsMarkdown = "Available skill guidance.",
        });

        var developerInstructions = bundle.DeveloperInstructions!;
        StringAssert.Contains(developerInstructions, "Minimal prompt body.");
        Assert.IsFalse(developerInstructions.Contains("# Runtime Context", StringComparison.Ordinal));
        Assert.IsFalse(developerInstructions.Contains("# Tool Guidance", StringComparison.Ordinal));
        Assert.IsFalse(developerInstructions.Contains("# Agent Prompts", StringComparison.Ordinal));
        Assert.IsFalse(developerInstructions.Contains("# Available Skills", StringComparison.Ordinal));
        Assert.IsFalse(bundle.Manifest.Parts.Any(static part => part.Kind is "runtime_context" or "tool_guidance" or "agent_prompts" or "available_skills"));
        Assert.AreEqual("minimal", bundle.Manifest.Composition.AgentPromptName);
        Assert.IsFalse(bundle.Manifest.Composition.PartOptions.Skills);
        Assert.IsFalse(bundle.Manifest.Composition.PartOptions.ProjectContext);
        Assert.IsFalse(bundle.Manifest.Composition.PartOptions.RuntimeContext);
        Assert.IsFalse(bundle.Manifest.Composition.PartOptions.ToolGuidance);
    }

    [TestMethod]
    public void SystemPromptBuilder_ReadsTheInstructionsOfTheCopilotLayout()
    {
        using var temp = TempDirectory.Create();
        var appBase = Path.Combine(temp.Path, "app");
        var projectRoot = Path.Combine(temp.Path, "project");
        var home = Path.Combine(temp.Path, "home");
        var instructions = Directory.CreateDirectory(Path.Combine(projectRoot, ".github", "instructions", "web")).Parent!.FullName;
        Directory.CreateDirectory(Path.Combine(home, ".copilot"));
        File.WriteAllText(Path.Combine(home, ".copilot", "copilot-instructions.md"), "Answer in short sentences.");
        File.WriteAllText(Path.Combine(projectRoot, "AGENTS.md"), "Project instructions.");
        File.WriteAllText(Path.Combine(instructions, "web", "frontend.instructions.md"), "---\napplyTo: \"src/**/*.ts,src/**/*.tsx\"\n---\nUse function components.");
        File.WriteAllText(Path.Combine(instructions, "csharp.instructions.md"), "---\ndescription: C# conventions\napplyTo: '**/*.cs'\n---\nFile-scoped namespaces.");
        // Not for a coding agent, or for no file: neither is named.
        File.WriteAllText(Path.Combine(instructions, "review.instructions.md"), "---\napplyTo: \"**\"\nexcludeAgent: \"coding-agent\"\n---\nOnly for reviews.");
        File.WriteAllText(Path.Combine(instructions, "manual.instructions.md"), "---\ndescription: attached by hand\n---\nNothing applies it.");
        File.WriteAllText(Path.Combine(instructions, "notes.md"), "---\napplyTo: \"**\"\n---\nNot an instructions file.");
        WriteSystem(appBase, "default", "Built-in default system.");
        WritePrompt(appBase, "default", "Default", "default", "Built-in default prompt.");
        var builder = new SystemPromptBuilder(new FileSystemPromptContentLocator(appBase));

        SystemPromptBundle Build(string? profile) => builder.Build(new SystemPromptBuildRequest
        {
            ProviderKey = "codex", ProviderType = "codex", ProtocolFamily = "codex", UserProfileRoot = profile,
            Session = new SessionViewDescriptor { SessionId = "session-1", ProviderId = "codex", ProviderKey = "codex", WorkingDirectory = projectRoot, Kind = SessionViewKind.ProjectSession },
            Project = new ProjectDescriptor { Id = "project-1", Slug = "project-1", DisplayName = "Project 1", ProjectPath = projectRoot },
            PartOptionsOverride = new PartialSystemPromptPartOptions(Skills: false, ProjectContext: true, RuntimeContext: false, ToolGuidance: false),
        });

        var text = Build(home).DeveloperInstructions!;
        // The instructions the user wrote for every project come first, then those of the project.
        var personal = text.IndexOf($"File: `{Path.Combine(home, ".copilot", "copilot-instructions.md")}`", StringComparison.Ordinal);
        var project = text.IndexOf($"File: `{Path.Combine(projectRoot, "AGENTS.md")}`", StringComparison.Ordinal);
        Assert.IsTrue(personal >= 0 && project > personal, text);
        StringAssert.Contains(text, "Answer in short sentences.");
        // The instructions for some files are named with their patterns, and not included.
        StringAssert.Contains(text, "Before you change a file that matches a pattern, read the file of instructions and follow it:");
        StringAssert.Contains(text, $"- `{Path.Combine(instructions, "csharp.instructions.md")}` applies to `**/*.cs`");
        StringAssert.Contains(text, $"- `{Path.Combine(instructions, "web", "frontend.instructions.md")}` applies to `src/**/*.ts, src/**/*.tsx`");
        foreach (var absent in new[] { "File-scoped namespaces.", "Use function components.", "review.instructions.md", "manual.instructions.md", "notes.md" })
        {
            Assert.IsFalse(text.Contains(absent, StringComparison.Ordinal), absent);
        }

        // A request that names no profile reads none.
        Assert.IsFalse(Build(null).DeveloperInstructions!.Contains("Answer in short sentences.", StringComparison.Ordinal));
    }

    [TestMethod]
    public void SystemPromptBuilder_CodeFormatsPathsInGeneratedMarkdown()
    {
        using var temp = TempDirectory.Create();
        var appBase = Path.Combine(temp.Path, "app");
        var projectRoot = Path.Combine(temp.Path, "project");
        var workingDirectory = Path.Combine(projectRoot, ".alta");
        var projectContextFile = Path.Combine(projectRoot, "AGENTS.md");
        Directory.CreateDirectory(workingDirectory);
        File.WriteAllText(projectContextFile, "Project instructions.");
        WriteSystem(appBase, "default", "Built-in default system.");
        WritePrompt(appBase, "default", "Default", "default", "Built-in default prompt.");

        var builder = new SystemPromptBuilder(new FileSystemPromptContentLocator(appBase));
        var bundle = builder.Build(new SystemPromptBuildRequest
        {
            ProviderKey = "codex",
            ProviderType = "codex",
            ProtocolFamily = "codex",
            Session = new SessionViewDescriptor
            {
                SessionId = "session-1",
                ProviderId = "codex",
                ProviderKey = "codex",
                WorkingDirectory = workingDirectory,
                Kind = SessionViewKind.ProjectSession,
            },
            Project = new ProjectDescriptor
            {
                Id = "project-1",
                Slug = "project-1",
                DisplayName = "Project 1",
                ProjectPath = projectRoot,
            },
            PartOptionsOverride = new PartialSystemPromptPartOptions(
                Skills: false,
                ProjectContext: true,
                RuntimeContext: true,
                ToolGuidance: false),
        });

        var developerInstructions = bundle.DeveloperInstructions!;
        StringAssert.Contains(developerInstructions, $"- Current working directory: `{Path.GetFullPath(workingDirectory)}`");
        StringAssert.Contains(developerInstructions, $"- Project root: `{Path.GetFullPath(projectRoot)}`");
        StringAssert.Contains(developerInstructions, $"File: `{Path.GetFullPath(projectContextFile)}`");
        // Where long instructions of the user are written down: a file the agent names after the day and the request,
        // which ends with a short hash of the session, under the CodeAlta root of the user (of their profile by default).
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData("session-1"u8), 0, 3);
        StringAssert.Contains(developerInstructions, $"- Scratchpad file: `{Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".alta", "scratchpad", $"<yyyy-mm-dd>-<short-name>-{hash}.md")}`");
        var rooted = builder.Build(new SystemPromptBuildRequest
        {
            ProviderKey = "codex", ProviderType = "codex", ProtocolFamily = "codex", UserCodeAltaRoot = projectRoot,
            Session = new SessionViewDescriptor { SessionId = "session-1", ProviderId = "codex", ProviderKey = "codex", WorkingDirectory = workingDirectory, Kind = SessionViewKind.ProjectSession },
            PartOptionsOverride = new PartialSystemPromptPartOptions(Skills: false, ProjectContext: false, RuntimeContext: true, ToolGuidance: false),
        });
        StringAssert.Contains(rooted.DeveloperInstructions!, $"- Scratchpad file: `{Path.Combine(Path.GetFullPath(projectRoot), "scratchpad", $"<yyyy-mm-dd>-<short-name>-{hash}.md")}`");
        // Another session has another hash, whatever its id is made of: two agents never write the same file.
        var other = builder.Build(new SystemPromptBuildRequest
        {
            ProviderKey = "codex", ProviderType = "codex", ProtocolFamily = "codex", UserCodeAltaRoot = projectRoot,
            Session = new SessionViewDescriptor { SessionId = "../other", ProviderId = "codex", ProviderKey = "codex", WorkingDirectory = workingDirectory, Kind = SessionViewKind.ProjectSession },
            PartOptionsOverride = new PartialSystemPromptPartOptions(Skills: false, ProjectContext: false, RuntimeContext: true, ToolGuidance: false),
        });
        // An application that ships the user guide names it, and says what it is for; one that ships none says nothing.
        Assert.IsFalse(developerInstructions.Contains("CodeAlta user guide", StringComparison.Ordinal));
        var guide = Directory.CreateDirectory(Path.Combine(appBase, "content", "user-guide")).FullName;
        File.WriteAllText(Path.Combine(guide, "readme.md"), "# User Guide");
        StringAssert.Contains(other.DeveloperInstructions!, "- Scratchpad file:");
        var guided = builder.Build(new SystemPromptBuildRequest
        {
            ProviderKey = "codex", ProviderType = "codex", ProtocolFamily = "codex",
            Session = new SessionViewDescriptor { SessionId = "session-1", ProviderId = "codex", ProviderKey = "codex", WorkingDirectory = workingDirectory, Kind = SessionViewKind.ProjectSession },
            PartOptionsOverride = new PartialSystemPromptPartOptions(Skills: false, ProjectContext: false, RuntimeContext: true, ToolGuidance: false),
        }).DeveloperInstructions!;
        StringAssert.Contains(guided, $"- CodeAlta user guide: `{guide}` (start with `readme.md`; the pictures its pages name are in `img/`).");
        StringAssert.Contains(guided, "For a question about CodeAlta itself");
        var otherHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData("../other"u8), 0, 3);
        Assert.AreNotEqual(hash, otherHash);
        StringAssert.Contains(other.DeveloperInstructions!, $"- Scratchpad file: `{Path.Combine(Path.GetFullPath(projectRoot), "scratchpad", $"<yyyy-mm-dd>-<short-name>-{otherHash}.md")}`");
    }

    [TestMethod]
    public void SystemPromptBuilder_NamesTheWorktreeASessionWorksIn_AndReadsItsInstructionFiles()
    {
        using var temp = TempDirectory.Create();
        var appBase = Path.Combine(temp.Path, "app");
        // The project is a folder below the root of its repository; a file above the repository applies to both checkouts.
        var repository = Path.Combine(temp.Path, "work", "repository");
        var projectRoot = Path.Combine(repository, "src", "app");
        var worktreeRoot = Path.Combine(temp.Path, "trees", "quiet-heron");
        var worktree = Path.Combine(worktreeRoot, "src", "app");
        Directory.CreateDirectory(Path.Combine(repository, ".git"));
        Directory.CreateDirectory(projectRoot);
        Directory.CreateDirectory(worktree);
        File.WriteAllText(Path.Combine(worktreeRoot, ".git"), "gitdir: " + Path.Combine(repository, ".git", "worktrees", "quiet-heron"));
        File.WriteAllText(Path.Combine(temp.Path, "work", "AGENTS.md"), "Above the repository.");
        File.WriteAllText(Path.Combine(repository, "AGENTS.md"), "Repository, main checkout.");
        File.WriteAllText(Path.Combine(worktreeRoot, "AGENTS.md"), "Repository, worktree.");
        // Not tracked by git: only the main checkout has it.
        File.WriteAllText(Path.Combine(projectRoot, "AGENTS.md"), "Project folder, main checkout only.");
        WriteSystem(appBase, "default", "Built-in default system.");
        WritePrompt(appBase, "default", "Default", "default", "Built-in default prompt.");
        var builder = new SystemPromptBuilder(new FileSystemPromptContentLocator(appBase));

        SystemPromptBundle Build(string? worktreeDirectory) => builder.Build(new SystemPromptBuildRequest
        {
            ProviderKey = "codex",
            ProviderType = "codex",
            ProtocolFamily = "codex",
            Session = new SessionViewDescriptor
            {
                SessionId = "session-1",
                ProviderId = "codex",
                ProviderKey = "codex",
                WorkingDirectory = projectRoot,
                WorktreeDirectory = worktreeDirectory,
                Kind = SessionViewKind.ProjectSession,
            },
            Project = new ProjectDescriptor { Id = "project-1", Slug = "project-1", DisplayName = "Project 1", ProjectPath = projectRoot },
            PartOptionsOverride = new PartialSystemPromptPartOptions(Skills: false, ProjectContext: true, RuntimeContext: true, ToolGuidance: false),
        });

        var inWorktree = Build(worktree).DeveloperInstructions!;
        StringAssert.Contains(inWorktree, $"- Current working directory: `{worktree}`");
        StringAssert.Contains(inWorktree, $"- Project root: `{worktree}`");
        StringAssert.Contains(inWorktree, $"- Git worktree: the working directory is a git worktree of the project, a checkout of its own with its own branch. The main checkout of the project is `{projectRoot}`");
        // The files of the repository come from the worktree; what only the main checkout has, and what is above the repository, stay.
        StringAssert.Contains(inWorktree, "Above the repository.");
        StringAssert.Contains(inWorktree, "Repository, worktree.");
        StringAssert.Contains(inWorktree, "Project folder, main checkout only.");
        Assert.IsFalse(inWorktree.Contains("Repository, main checkout.", StringComparison.Ordinal));

        var inProject = Build(null).DeveloperInstructions!;
        StringAssert.Contains(inProject, $"- Current working directory: `{projectRoot}`");
        StringAssert.Contains(inProject, "Repository, main checkout.");
        Assert.IsFalse(inProject.Contains("Git worktree", StringComparison.Ordinal));
        Assert.IsFalse(inProject.Contains("Repository, worktree.", StringComparison.Ordinal));

        // A worktree whose folder is gone is not where the session works.
        var gone = Build(Path.Combine(temp.Path, "trees", "gone", "src", "app")).DeveloperInstructions!;
        StringAssert.Contains(gone, $"- Current working directory: `{projectRoot}`");
        Assert.IsFalse(gone.Contains("Git worktree", StringComparison.Ordinal));
    }

    [TestMethod]
    public void SystemPromptBuilder_AppendsSystemAndAgentPromptResources()
    {
        using var temp = TempDirectory.Create();
        var appBase = Path.Combine(temp.Path, "app");
        var globalRoot = Path.Combine(temp.Path, "global");
        var projectRoot = Path.Combine(temp.Path, "project");
        Directory.CreateDirectory(projectRoot);
        WriteSystem(appBase, "default", "Built-in default system.");
        WriteSystem(globalRoot, "default", "Global system addition.", ["mode: append"]);
        WriteSystem(projectRoot, "default", "Project system addition.", ["append: true"]);
        WritePrompt(appBase, "default", "Default", "default", "Built-in default prompt.");
        WritePrompt(
            globalRoot,
            "default",
            name: null,
            system: "default",
            body: "Global agent addition.",
            frontmatterLines: ["mode: append", "skills: false"]);
        WritePrompt(
            projectRoot,
            "default",
            name: null,
            system: "default",
            body: "Project agent addition.",
            frontmatterLines: ["append: true", "tool_guidance: false"]);

        var builder = new SystemPromptBuilder(new FileSystemPromptContentLocator(appBase));
        var bundle = builder.Build(new SystemPromptBuildRequest
        {
            ProviderKey = "codex",
            ProviderType = "codex",
            ProtocolFamily = "codex",
            Session = new SessionViewDescriptor
            {
                SessionId = "session-1",
                ProviderId = "codex",
                ProviderKey = "codex",
                WorkingDirectory = projectRoot,
                Kind = SessionViewKind.ProjectSession,
            },
            Project = new ProjectDescriptor
            {
                Id = "project-1",
                Slug = "project-1",
                DisplayName = "Project 1",
                ProjectPath = projectRoot,
            },
            UserCodeAltaRoot = globalRoot,
            SelectedPromptName = "default",
            AvailableSkillsMarkdown = "Available skill guidance.",
        });

        var newline = Environment.NewLine;
        Assert.AreEqual($"Built-in default system.{newline}{newline}Global system addition.{newline}{newline}Project system addition.", bundle.SystemMessage);
        StringAssert.Contains(bundle.DeveloperInstructions!, $"Built-in default prompt.{newline}{newline}Global agent addition.{newline}{newline}Project agent addition.");
        Assert.IsFalse(bundle.DeveloperInstructions!.Contains("# Available Skills", StringComparison.Ordinal));
        Assert.IsFalse(bundle.DeveloperInstructions!.Contains("# Tool Guidance", StringComparison.Ordinal));
        Assert.IsFalse(bundle.Manifest.Composition.PartOptions.Skills);
        Assert.IsFalse(bundle.Manifest.Composition.PartOptions.ToolGuidance);
        Assert.AreEqual(1, bundle.Manifest.Parts.Count(part => part.Key == "system/default" && part.Status == "selected"));
        Assert.AreEqual(2, bundle.Manifest.Parts.Count(part => part.Key == "system/default" && part.Status == "appended"));
        Assert.AreEqual(1, bundle.Manifest.Parts.Count(part => part.Key == "agents/default" && part.Status == "selected"));
        Assert.AreEqual(2, bundle.Manifest.Parts.Count(part => part.Key == "agents/default" && part.Status == "appended"));
    }

    [TestMethod]
    public void SystemPromptBuilder_ListsEffectiveAgentPromptsWithoutSensitiveMetadata()
    {
        using var temp = TempDirectory.Create();
        var appBase = Path.Combine(temp.Path, "app");
        var globalRoot = Path.Combine(temp.Path, "global");
        var projectRoot = Path.Combine(temp.Path, "project");
        Directory.CreateDirectory(projectRoot);
        WriteSystem(appBase, "default", "Built-in default system.");
        WritePrompt(appBase, "default", "Default Built-in", "default", "Built-in default body.", "Built-in default description.");
        WritePrompt(globalRoot, "default", "Default Global", "default", "Global default body.", "Global default description.");
        WritePrompt(globalRoot, "global-extra", "Global Extra", "default", "Global extra body.", "Global extra description.");
        WriteSystem(projectRoot, "project-system", "Project system.");
        WritePrompt(projectRoot, "default", "Project Default", "project-system", "Project default body.", "Project default description.");

        var builder = new SystemPromptBuilder(new FileSystemPromptContentLocator(appBase));
        var bundle = builder.Build(new SystemPromptBuildRequest
        {
            ProviderKey = "codex",
            ProviderType = "codex",
            ProtocolFamily = "codex",
            Session = new SessionViewDescriptor
            {
                SessionId = "session-1",
                ProviderId = "codex",
                ProviderKey = "codex",
                WorkingDirectory = projectRoot,
                Kind = SessionViewKind.ProjectSession,
            },
            Project = new ProjectDescriptor
            {
                Id = "project-1",
                Slug = "project-1",
                DisplayName = "Project 1",
                ProjectPath = projectRoot,
            },
            UserCodeAltaRoot = globalRoot,
            SelectedPromptName = "global-extra",
            PartOptionsOverride = new PartialSystemPromptPartOptions(
                Skills: false,
                ProjectContext: false,
                RuntimeContext: false,
                ToolGuidance: true),
        });

        var developerInstructions = bundle.DeveloperInstructions!;
        var newline = Environment.NewLine;
        StringAssert.Contains(developerInstructions, "# Agent Prompts");
        StringAssert.Contains(developerInstructions, "Agent prompt profiles available for this session:");
        StringAssert.Contains(developerInstructions, $"- current: `global-extra` — Global Extra{newline}  - Source: user-global; system: `default`{newline}  - Description: Global extra description.");
        StringAssert.Contains(developerInstructions, $"- `default` — Project Default{newline}  - Source: project; system: `project-system`{newline}  - Description: Project default description.");
        StringAssert.Contains(developerInstructions, "alta session set_agent --prompt-id <id>");
        StringAssert.Contains(developerInstructions, "alta session send <session-id> --prompt-id <id> --stdin");
        Assert.IsFalse(developerInstructions.Contains("Default Built-in", StringComparison.Ordinal));
        Assert.IsFalse(developerInstructions.Contains("Default Global", StringComparison.Ordinal));
        Assert.IsFalse(developerInstructions.Contains("Project default body.", StringComparison.Ordinal));
        Assert.IsFalse(developerInstructions.Contains(projectRoot, StringComparison.Ordinal));
        Assert.IsFalse(developerInstructions.Contains("sha256:", StringComparison.Ordinal));
        Assert.IsTrue(bundle.Manifest.Parts.Any(static part => part.Key == "prompt.discovery" && part.Kind == "agent_prompts" && part.Status == "selected"));
    }

    [TestMethod]
    public void AgentPromptCatalog_ListsTheCustomAgentsOfGitHubCopilot_AfterThePromptsOfCodeAlta()
    {
        using var temp = TempDirectory.Create();
        var appBase = Path.Combine(temp.Path, "app");
        var globalRoot = Path.Combine(temp.Path, "global");
        var projectRoot = Path.Combine(temp.Path, "project");
        var profile = Path.Combine(temp.Path, "home");
        WritePrompt(appBase, "default", "Default", "default", "Built-in body.");
        WritePrompt(projectRoot, "docs", "Docs of CodeAlta", "default", "The docs prompt of CodeAlta.");
        var projectAgents = Directory.CreateDirectory(Path.Combine(projectRoot, ".github", "agents")).FullName;
        var userAgents = Directory.CreateDirectory(Path.Combine(profile, ".copilot", "agents")).FullName;
        // The header of an agent file is YAML: only its top-level scalars present the agent.
        File.WriteAllText(Path.Combine(projectAgents, "reviewer.agent.md"), """
            ---
            name: 'Security reviewer'
            description: >
              Reviews a change
              for security problems
            tools:
              - read
              - search
            mcp-servers:
              scanner:
                name: not-the-agent
                command: scan
            handoffs:
              - label: Fix
                agent: default
            ---
            Review the change for security problems.
            """);
        File.WriteAllText(Path.Combine(projectAgents, "docs.agent.md"), "---\nname: Docs of Copilot\n---\nThe docs agent of Copilot.\n");
        File.WriteAllText(Path.Combine(projectAgents, "README.md"), "# The agents of this project\n");
        File.WriteAllText(Path.Combine(projectAgents, "empty.agent.md"), "---\nname: Empty\n---\n");
        File.WriteAllText(Path.Combine(projectAgents, "a b.agent.md"), "Not an id.\n");
        File.WriteAllText(Path.Combine(userAgents, "reviewer.agent.md"), "The reviewer of the user.\n");
        File.WriteAllText(Path.Combine(userAgents, "planner.md"), "Plan the work.\n");

        var catalog = new AgentPromptCatalog(new FileSystemPromptContentLocator(appBase));
        var query = new AgentPromptCatalogQuery { UserCodeAltaRoot = globalRoot, UserProfileRoot = profile, ProjectRoot = projectRoot, ProjectPromptResourcesTrusted = true };
        var all = catalog.ListPrompts(query);

        CollectionAssert.AreEqual(
            new[] { "BuiltIn:default", "Project:docs", "CopilotUser:planner", "CopilotUser:reviewer", "CopilotProject:docs", "CopilotProject:reviewer" },
            all.Select(static prompt => $"{prompt.SourceKind}:{prompt.PromptName}").ToArray());
        var reviewer = all.Single(static prompt => prompt is { SourceKind: AgentPromptSourceKind.CopilotProject, PromptName: "reviewer" });
        Assert.AreEqual("Security reviewer", reviewer.DisplayName);
        Assert.AreEqual("Reviews a change for security problems", reviewer.Description);
        Assert.AreEqual("Review the change for security problems.", reviewer.Body);
        Assert.IsTrue(reviewer.IsCopilot);
        Assert.IsFalse(reviewer.IsBuiltIn);
        Assert.IsFalse(reviewer.IsShadowed);
        // The agent of the project comes before the one of the user, and a prompt of CodeAlta before both.
        Assert.IsTrue(all.Single(static prompt => prompt is { SourceKind: AgentPromptSourceKind.CopilotUser, PromptName: "reviewer" }).IsShadowed);
        Assert.IsTrue(all.Single(static prompt => prompt is { SourceKind: AgentPromptSourceKind.CopilotProject, PromptName: "docs" }).IsShadowed);
        Assert.AreEqual("planner", all.Single(static prompt => prompt.PromptName == "planner").DisplayName);

        var effective = catalog.ListEffectivePrompts(query);
        CollectionAssert.AreEqual(new[] { "default", "docs", "planner", "reviewer" }, effective.Select(static prompt => prompt.PromptName).ToArray());
        Assert.AreEqual("The docs prompt of CodeAlta.", effective.Single(static prompt => prompt.PromptName == "docs").Body);
        Assert.AreEqual(AgentPromptSourceKind.CopilotProject, catalog.ResolvePrompt(query, "reviewer")!.SourceKind);

        // Without a profile and a project, there is no agent of Copilot.
        Assert.HasCount(1, catalog.ListPrompts(new AgentPromptCatalogQuery { UserCodeAltaRoot = globalRoot }));
    }

    [TestMethod]
    public void SystemPromptBuilder_UsesACustomAgentOfGitHubCopilotAsTheAgentPrompt()
    {
        using var temp = TempDirectory.Create();
        var appBase = Path.Combine(temp.Path, "app");
        var globalRoot = Path.Combine(temp.Path, "global");
        var projectRoot = Path.Combine(temp.Path, "project");
        var profile = Path.Combine(temp.Path, "home");
        WriteSystem(appBase, "default", "Built-in default system.");
        WritePrompt(appBase, "default", "Default", "default", "Built-in default prompt.");
        var projectAgents = Directory.CreateDirectory(Path.Combine(projectRoot, ".github", "agents")).FullName;
        File.WriteAllText(Path.Combine(projectAgents, "reviewer.agent.md"), "---\nname: Reviewer\ntools: ['read']\n---\nReview the change for security problems.\n");
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(profile, ".copilot", "agents")).FullName, "writer.agent.md"), "Write the documentation.\n");

        SystemPromptBundle Build(string prompt) => new SystemPromptBuilder(new FileSystemPromptContentLocator(appBase)).Build(new SystemPromptBuildRequest
        {
            ProviderKey = "codex",
            ProviderType = "codex",
            ProtocolFamily = "codex",
            Session = new SessionViewDescriptor { SessionId = "session-1", ProviderId = "codex", ProviderKey = "codex", WorkingDirectory = projectRoot, Kind = SessionViewKind.ProjectSession },
            Project = new ProjectDescriptor { Id = "project-1", Slug = "project-1", DisplayName = "Project 1", ProjectPath = projectRoot },
            UserCodeAltaRoot = globalRoot,
            UserProfileRoot = profile,
            SelectedPromptName = prompt,
            PartOptionsOverride = new PartialSystemPromptPartOptions(Skills: false, ProjectContext: false, RuntimeContext: false, ToolGuidance: true),
        });

        var bundle = Build("reviewer");
        Assert.AreEqual("Built-in default system.", bundle.SystemMessage);
        Assert.AreEqual("reviewer", bundle.Manifest.Composition.AgentPromptName);
        StringAssert.Contains(bundle.DeveloperInstructions!, "Review the change for security problems.");
        Assert.IsFalse(bundle.DeveloperInstructions!.Contains("Built-in default prompt.", StringComparison.Ordinal));
        Assert.IsFalse(bundle.DeveloperInstructions.Contains("tools:", StringComparison.Ordinal), "The header of the file is not part of the prompt.");
        Assert.IsTrue(bundle.Manifest.Parts.Any(static part => part.Key == "agents/reviewer" && part.Status == "selected"));
        // The session is told which agents it can give to a sub-agent.
        StringAssert.Contains(bundle.DeveloperInstructions, "- current: `reviewer` — Reviewer");
        StringAssert.Contains(bundle.DeveloperInstructions, "Source: copilot-project");
        StringAssert.Contains(bundle.DeveloperInstructions, "- `writer` — writer");
        StringAssert.Contains(bundle.DeveloperInstructions, "Source: copilot-user");

        StringAssert.Contains(Build("writer").DeveloperInstructions!, "Write the documentation.");

        // A prompt of CodeAlta that is appended extends the agent.
        WritePrompt(globalRoot, "writer", null, "default", "In CodeAlta, write for CodeAlta Desktop first.", description: null, frontmatterLines: ["mode: append"]);
        var extended = Build("writer").DeveloperInstructions!;
        Assert.IsTrue(extended.IndexOf("Write the documentation.", StringComparison.Ordinal) is var first and >= 0
            && extended.IndexOf("In CodeAlta, write for CodeAlta Desktop first.", StringComparison.Ordinal) > first, extended);

        // A prompt of CodeAlta with the same id is the one that is used.
        WritePrompt(projectRoot, "reviewer", "Reviewer of CodeAlta", "default", "The reviewer prompt of CodeAlta.");
        var replaced = Build("reviewer");
        StringAssert.Contains(replaced.DeveloperInstructions!, "The reviewer prompt of CodeAlta.");
        Assert.IsFalse(replaced.DeveloperInstructions!.Contains("Review the change for security problems.", StringComparison.Ordinal));
    }

    private static void WriteSystem(string root, string id, string body, IReadOnlyList<string>? frontmatterLines = null)
    {
        var directory = root.EndsWith("app", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(root, "content", "prompts", "system")
            : root.EndsWith("project", StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(root, ".alta", "prompts", "system")
                : Path.Combine(root, "prompts", "system");
        Directory.CreateDirectory(directory);
        var builder = new StringWriter();
        builder.WriteLine("---");
        builder.WriteLine("description: Test system prompt.");
        if (frontmatterLines is not null)
        {
            foreach (var line in frontmatterLines)
            {
                builder.WriteLine(line);
            }
        }

        builder.WriteLine("---");
        builder.WriteLine(body);
        File.WriteAllText(Path.Combine(directory, id + ".system-prompt.md"), builder.ToString());
    }

    private static void WritePrompt(string root, string id, string? name, string system, string body, string? description = "Test agent prompt.", IReadOnlyList<string>? frontmatterLines = null)
    {
        var directory = root.EndsWith("app", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(root, "content", "prompts", "agents")
            : root.EndsWith("project", StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(root, ".alta", "prompts", "agents")
                : Path.Combine(root, "prompts", "agents");
        Directory.CreateDirectory(directory);
        var builder = new StringWriter();
        builder.WriteLine("---");
        if (name is not null)
        {
            builder.WriteLine("name: " + name);
        }

        if (!string.Equals(system, "default", StringComparison.OrdinalIgnoreCase))
        {
            builder.WriteLine("system: " + system);
        }

        if (description is not null)
        {
            builder.WriteLine("description: " + description);
        }

        if (frontmatterLines is not null)
        {
            foreach (var line in frontmatterLines)
            {
                builder.WriteLine(line);
            }
        }

        builder.WriteLine("---");
        builder.WriteLine(body);
        File.WriteAllText(Path.Combine(directory, id + ".prompt.md"), builder.ToString());
    }

    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TempDirectory Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "codealta-orchestration-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TempDirectory(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
