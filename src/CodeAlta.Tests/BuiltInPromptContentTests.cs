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
        StringAssert.Contains(plan.Body, "--same-model-as <session-id>");
        StringAssert.Contains(plan.Body, "--model-ref");
        // Sub-agents are the planner's own call: one for each independent question of a broad plan, none for a small one, no chain.
        StringAssert.Contains(plan.Body, "explore them in parallel with sub-agents");
        StringAssert.Contains(plan.Body, "a normal way to dig into a problem");
        StringAssert.Contains(plan.Body, "as many sub-agents as there are independent questions");
        StringAssert.Contains(plan.Body, "a small plan needs no child session");
        StringAssert.Contains(plan.Body, "without being asked");
        StringAssert.Contains(plan.Body, "never hand the exploration to a single child");
        StringAssert.Contains(plan.Body, "--reasoning low");
        Assert.IsFalse(plan.Body.Contains("only when the user explicitly asks", StringComparison.Ordinal));
        StringAssert.Contains(plan.Body, "all relevant child and descendant work");
        StringAssert.Contains(plan.Body, "alta session set_agent --prompt-id default");
        // The plan is a document for a reader: its front matter lists it, and its approval is told to CodeAlta, which shows it.
        StringAssert.Contains(plan.Body, "Who reads the plan");
        StringAssert.Contains(plan.Body, "status: draft");
        StringAssert.Contains(plan.Body, "summary: <one or two sentences");
        StringAssert.Contains(plan.Body, "```mermaid");
        StringAssert.Contains(plan.Body, "alta plan status <plan-id> approved");
        StringAssert.Contains(plan.Body, "Do not start the work and do not hand off by yourself");
        Assert.IsFalse(plan.Body.Contains("Switch to Default and execute", StringComparison.Ordinal), "The user chooses where an approved plan is carried out, on its card.");
        StringAssert.Contains(plan.Body, "- [ ] 1. <A step a builder can do and check on its own");
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
        // Sub-agents are the agent's own call: one for each independent part, direct work when the task is small, no chain of single children.
        StringAssert.Contains(defaultPrompt.Body, "Delegating is a normal way to work");
        StringAssert.Contains(defaultPrompt.Body, "without being asked");
        StringAssert.Contains(defaultPrompt.Body, "as many sub-agents as there are independent parts");
        StringAssert.Contains(defaultPrompt.Body, "Work directly when the task is small or does not split");
        StringAssert.Contains(defaultPrompt.Body, "never your task or most of it to a single child");
        StringAssert.Contains(defaultPrompt.Body, "--reasoning low");
        Assert.IsFalse(defaultPrompt.Body.Contains("only when the user explicitly asks for delegation", StringComparison.Ordinal));
        StringAssert.Contains(defaultPrompt.Body, "Implementation children that write files must run sequentially");
        StringAssert.Contains(defaultPrompt.Body, "all relevant child and descendant work");
        StringAssert.Contains(defaultPrompt.Body, "over-compression across nesting levels");
        StringAssert.Contains(defaultPrompt.Body, "--same-model-as");
        StringAssert.Contains(defaultPrompt.Body, "--model-ref");
        StringAssert.Contains(defaultPrompt.Body, "alta reminder create --duration 00:05:00");
        StringAssert.Contains(defaultPrompt.Body, "alta session set_agent --prompt-id plan");
        // Follow-up tasks: proposed for what was found and verified beside the request, never started by the session.
        StringAssert.Contains(defaultPrompt.Description!, "proposing follow-up tasks");
        StringAssert.Contains(defaultPrompt.Body, "unrelated to the current request");
        StringAssert.Contains(defaultPrompt.Body, "Most turns propose nothing");
        StringAssert.Contains(defaultPrompt.Body, "alta task list");
        StringAssert.Contains(defaultPrompt.Body, "alta task create --title");
        StringAssert.Contains(defaultPrompt.Body, "Do not start a task you proposed");
        StringAssert.Contains(defaultPrompt.Body, "alta task complete <id>");
        StringAssert.Contains(defaultPrompt.Body, "alta plan status <plan-id> done");
        // Long instructions are written down where a compaction of the context does not lose them.
        StringAssert.Contains(defaultPrompt.Body, "many instructions at once");
        StringAssert.Contains(defaultPrompt.Body, "scratchpad folder named in your runtime context");
        StringAssert.Contains(defaultPrompt.Body, "reread it when you resume after a compaction");
        StringAssert.Contains(defaultPrompt.Body, "never commit it");
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
