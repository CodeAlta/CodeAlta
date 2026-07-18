using CodeAlta.Orchestration.Runtime.SystemPrompts;

namespace CodeAlta.Tests;

[TestClass]
public sealed class BuiltInPromptContentTests
{
    [TestMethod]
    public void BuiltInAgentPrompts_IncludePlanWorkflow()
    {
        using var root = TempDirectory.Create();
        var prompts = ListBuiltInPrompts(root.Path);

        var plan = prompts.Single(static prompt => prompt.IsBuiltIn && prompt.PromptName == "plan");

        Assert.AreEqual("Plan", plan.DisplayName);
        StringAssert.Contains(plan.Description!, "Read-only planning mode");
        StringAssert.Contains(plan.Description!, ".alta/plans/");
        StringAssert.Contains(plan.Description!, "Default");
        StringAssert.Contains(plan.Body, ".alta/plans/yyyy-mm-dd-{plan-name}.md");
        StringAssert.Contains(plan.Body, "Initial understanding");
        StringAssert.Contains(plan.Body, "Design and validation");
        StringAssert.Contains(plan.Body, "Plan file lifecycle");
        StringAssert.Contains(plan.Body, "versioned repository artifacts");
        StringAssert.Contains(plan.Body, "alta ask --stdin");
        StringAssert.Contains(plan.Body, "GitHub-style blockquotes");
        StringAssert.Contains(plan.Body, "use the exact `description` field on questions and choices");
        StringAssert.Contains(plan.Body, "alta session current");
        StringAssert.Contains(plan.Body, "alta session send <current-session-id> --queue-if-busy --stdin");
        StringAssert.Contains(plan.Body, "enqueue the execution turn with `--queue-if-busy`");
        StringAssert.Contains(plan.Body, "--same-model-as <session-id>");
        StringAssert.Contains(plan.Body, "--model-ref");
        StringAssert.Contains(plan.Body, "only when the user explicitly asks for delegation or sub-sessions");
        StringAssert.Contains(plan.Body, "all relevant child and descendant work");
        StringAssert.Contains(plan.Body, "over-compression across nesting levels");
        StringAssert.Contains(plan.Body, "\"description\": \"Review the attached plan file before CodeAlta starts implementation.\"");
        StringAssert.Contains(plan.Body, "\"description\": \"Approve the plan and hand off to Default/build mode for execution.\"");
        StringAssert.Contains(plan.Body, "alta session set_agent --prompt-id default");
        Assert.IsFalse(plan.Body.Contains("Do not call `alta session send` to the current/calling session", StringComparison.Ordinal));
        Assert.IsFalse(plan.Body.Contains("\"description\": \"Approve the plan; Plan mode must immediately run `alta session set_agent --prompt-id default` and stop.\"", StringComparison.Ordinal));
        StringAssert.Contains(plan.Body, "- [ ] <small, ordered implementation step");
    }

    [TestMethod]
    public void BuiltInDefaultPrompt_IncludesPlanExecutionAndDelegationGuidance()
    {
        using var root = TempDirectory.Create();
        var prompts = ListBuiltInPrompts(root.Path);

        var defaultPrompt = prompts.Single(static prompt => prompt.IsBuiltIn && prompt.PromptName == "default");

        StringAssert.Contains(defaultPrompt.Description!, "implementation/build mode");
        StringAssert.Contains(defaultPrompt.Description!, "approved plan files");
        StringAssert.Contains(defaultPrompt.Description!, "verification and commits");
        StringAssert.Contains(defaultPrompt.Body, "Default implementation agent");
        Assert.IsFalse(defaultPrompt.Body.Contains("prior Plan-mode read-only instructions", StringComparison.Ordinal));
        StringAssert.Contains(defaultPrompt.Body, "Executing plan files (if any)");
        StringAssert.Contains(defaultPrompt.Body, "alta notes clear");
        StringAssert.Contains(defaultPrompt.Body, "Do not ask questions or use `alta ask --stdin` by default");
        StringAssert.Contains(defaultPrompt.Body, "explicitly asks for interactive questions/approval");
        StringAssert.Contains(defaultPrompt.Body, "choose the narrowest safe interpretation and proceed");
        StringAssert.Contains(defaultPrompt.Body, "Never commit when the user asks not to commit");
        StringAssert.Contains(defaultPrompt.Body, "requests review or approval before committing");
        StringAssert.Contains(defaultPrompt.Body, "repository-local commit guidance");
        Assert.IsFalse(defaultPrompt.Body.Contains("ask a concise question in the normal chat and stop", StringComparison.Ordinal));
        StringAssert.Contains(defaultPrompt.Body, "GitHub-style blockquotes");
        StringAssert.Contains(defaultPrompt.Body, "commit the plan update with the implementation step it records");
        StringAssert.Contains(defaultPrompt.Body, "one writing child at a time");
        StringAssert.Contains(defaultPrompt.Body, "only when the user explicitly asks for delegation or sub-sessions");
        StringAssert.Contains(defaultPrompt.Body, "complexity alone is not permission");
        StringAssert.Contains(defaultPrompt.Body, "Implementation children that write files must run sequentially");
        StringAssert.Contains(defaultPrompt.Body, "all relevant child and descendant work");
        StringAssert.Contains(defaultPrompt.Body, "over-compression across nesting levels");
        StringAssert.Contains(defaultPrompt.Body, "--same-model-as");
        StringAssert.Contains(defaultPrompt.Body, "--model-ref");
        StringAssert.Contains(defaultPrompt.Body, "alta reminder create --duration 00:05:00");
        StringAssert.Contains(defaultPrompt.Body, "alta session set_agent --prompt-id plan");
    }

    private static IReadOnlyList<AgentPromptDescriptor> ListBuiltInPrompts(string userCodeAltaRoot)
        => new AgentPromptCatalog().ListPrompts(new AgentPromptCatalogQuery
        {
            UserCodeAltaRoot = userCodeAltaRoot,
        });

    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TempDirectory Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CodeAlta.Tests." + Guid.NewGuid().ToString("N"));
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
