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
        StringAssert.Contains(plan.Body, "--model-ref");
        // Sub-agents are the planner's own call, by purpose and size: large independent questions whose conclusion is
        // enough go to children; what the plan will change is read by the planner itself.
        StringAssert.Contains(plan.Body, "explore them in parallel with sub-agents");
        StringAssert.Contains(plan.Body, "read directly, in batches, what the plan will change");
        StringAssert.Contains(plan.Body, "It serves two purposes");
        StringAssert.Contains(plan.Body, "dozens, not a few");
        StringAssert.Contains(plan.Body, "Explore yourself what the plan will change");
        StringAssert.Contains(plan.Body, "Never delegate the question you need answered next");
        StringAssert.Contains(plan.Body, "When unsure, explore yourself");
        StringAssert.Contains(plan.Body, "delegate only a piece of your question that is itself large and independent");
        StringAssert.Contains(plan.Body, "--reasoning low");
        Assert.IsFalse(plan.Body.Contains("without being asked", StringComparison.Ordinal));
        Assert.IsFalse(plan.Body.Contains("two, five or more", StringComparison.Ordinal));
        Assert.IsFalse(plan.Body.Contains("only when the user explicitly asks", StringComparison.Ordinal));
        StringAssert.Contains(plan.Body, "what the children found, in your own words");
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
        // Sub-agents are the agent's own call, by purpose and size: large independent parts go to children; the details of
        // its own change stay with the agent, and when unsure it does the work itself.
        StringAssert.Contains(defaultPrompt.Body, "It serves two purposes");
        StringAssert.Contains(defaultPrompt.Body, "dozens, not a few");
        StringAssert.Contains(defaultPrompt.Body, "Do it yourself when you will need the details");
        StringAssert.Contains(defaultPrompt.Body, "the review of your own diff");
        StringAssert.Contains(defaultPrompt.Body, "Never delegate the step you need next");
        StringAssert.Contains(defaultPrompt.Body, "Length alone is not a reason");
        StringAssert.Contains(defaultPrompt.Body, "When unsure, do it yourself");
        StringAssert.Contains(defaultPrompt.Body, "delegate only a piece of it that is itself large and independent");
        StringAssert.Contains(defaultPrompt.Body, "--reasoning low");
        Assert.IsFalse(defaultPrompt.Body.Contains("decide it yourself, without being asked", StringComparison.Ordinal));
        Assert.IsFalse(defaultPrompt.Body.Contains("two, five or more", StringComparison.Ordinal));
        Assert.IsFalse(defaultPrompt.Body.Contains("only when the user explicitly asks for delegation", StringComparison.Ordinal));
        StringAssert.Contains(defaultPrompt.Body, "Children that write files run one at a time unless each has a worktree of its own");
        StringAssert.Contains(defaultPrompt.Body, "what the children found or changed, in your own words");
        StringAssert.Contains(defaultPrompt.Body, "--model-ref");
        StringAssert.Contains(defaultPrompt.Body, "alta reminder create --duration 00:05:00");
        StringAssert.Contains(defaultPrompt.Body, "alta session set_agent --prompt-id plan");
        // Waiting for something outside the session: a reminder brings the agent back, a promise does not.
        StringAssert.Contains(defaultPrompt.Body, "nothing brings you back when it ends");
        StringAssert.Contains(defaultPrompt.Body, "alta reminder create --duration <delay>");
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
        StringAssert.Contains(defaultPrompt.Body, "in the scratchpad file named in your session context");
        StringAssert.Contains(defaultPrompt.Body, "reread it when you resume after a compaction");
        StringAssert.Contains(defaultPrompt.Body, "never commit it");
        // Git: a branch for work that is committed on the default branch, no branch from a branch, and nothing lands unasked.
        StringAssert.Contains(defaultPrompt.Body, "## Git branches");
        StringAssert.Contains(defaultPrompt.Body, "unless the user (for example \"no branch\", \"commit on main\") or the repository guidance says otherwise");
        StringAssert.Contains(defaultPrompt.Body, "Before your first commit on the default branch");
        StringAssert.Contains(defaultPrompt.Body, "Work that ends without a commit gets no branch");
        StringAssert.Contains(defaultPrompt.Body, "stay on it: never branch from it");
        StringAssert.Contains(defaultPrompt.Body, "As a sub-agent, never create or switch branches");
        StringAssert.Contains(defaultPrompt.Body, "Never merge or rebase your branch into the default branch");
        // Once merged and unused, a worktree and its branch are removed without being asked, safely, unless the user keeps them.
        StringAssert.Contains(defaultPrompt.Body, "is merged into the default branch and nothing uses it any more, clean up without being asked, unless the user asked to keep it");
        StringAssert.Contains(defaultPrompt.Body, "`git worktree remove <path>`");
        StringAssert.Contains(defaultPrompt.Body, "`git branch -d <branch>`");
        StringAssert.Contains(defaultPrompt.Body, "never force with `-D` or `--force`");
        StringAssert.Contains(defaultPrompt.Body, "A session cannot remove the worktree it works in");
        // A command that can do the waiting is a background job, whose result comes back; not for what ends in seconds.
        StringAssert.Contains(defaultPrompt.Body, "alta job start --command");
        StringAssert.Contains(defaultPrompt.Body, "its result (exit code and output) is sent to you as a prompt when it ends, whether it succeeded or failed");
        StringAssert.Contains(defaultPrompt.Body, "--timeout <seconds or hh:mm:ss>");
        StringAssert.Contains(defaultPrompt.Body, "no help to read first");
        StringAssert.Contains(defaultPrompt.Body, "Do not poll a job and do not wait for it with a sleep");
        StringAssert.Contains(defaultPrompt.Body, "Do not start a job for a command that ends in a few seconds");
    }

    [TestMethod]
    public void BuiltInSystemPrompt_SaysWhatBackgroundJobsAreFor()
    {
        using var root = TempDirectory.Create();
        var system = new AgentPromptCatalog().ListSystemPrompts(new AgentPromptCatalogQuery { UserCodeAltaRoot = root.Path })
            .Single(static prompt => prompt.IsBuiltIn && prompt.PromptName == "default");

        // A job is started without waiting and its result is sent later, whatever its exit code; it is for what takes long.
        StringAssert.Contains(system.Body, "Background jobs");
        StringAssert.Contains(system.Body, "`alta job start`");
        StringAssert.Contains(system.Body, "the host sends you its result (exit code and output) as a prompt when it ends, whether it succeeded or failed");
        StringAssert.Contains(system.Body, "Use them wisely");
        StringAssert.Contains(system.Body, "not for a command that completes quickly");
        StringAssert.Contains(system.Body, "Give a job a timeout when its command could hang");
        StringAssert.Contains(PromptFeatureConditions.Apply(system.Body, null), "unless the host explicitly supports it (a background job, a reminder)");
        StringAssert.Contains(PromptFeatureConditions.Apply(system.Body, new HashSet<string> { PromptFeatures.Reminders }), "unless the host explicitly supports it (a background job).");
    }

    [TestMethod]
    public void BuiltInSystemPrompt_AsksForIndependentCallsTogether_AndSmallOutput()
    {
        using var root = TempDirectory.Create();
        var system = new AgentPromptCatalog().ListSystemPrompts(new AgentPromptCatalogQuery { UserCodeAltaRoot = root.Path })
            .Single(static prompt => prompt.IsBuiltIn && prompt.PromptName == "default");

        // Independent reads and searches go in one response, decided edits in one step; output is filtered, not dumped.
        StringAssert.Contains(system.Body, "Make independent tool calls together");
        StringAssert.Contains(system.Body, "make all the calls in the same response");
        StringAssert.Contains(system.Body, "When you have decided several edits, make them in one step too");
        StringAssert.Contains(system.Body, "do not read again what is still in your context");
        StringAssert.Contains(system.Body, "Keep command output small");
        Assert.IsFalse(system.Body.Contains("Parallelize independent reads/searches when supported", StringComparison.Ordinal));
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
