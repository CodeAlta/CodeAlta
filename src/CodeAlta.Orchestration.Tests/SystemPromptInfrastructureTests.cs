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
