using CodeAlta.Plugin.Statistics.Facts;

namespace CodeAlta.Plugin.Statistics.Tests;

/// <summary>Every use of a skill is counted by its name, and every call of an MCP tool by its server and tool.</summary>
[TestClass]
public sealed class SkillAndMcpUsageFactsTests
{
    private static readonly DateTimeOffset T0 = JournalBuilder.Time(0);
    private static readonly QuarterHour Q = QuarterHour.Of(T0);

    private static long Detail(FactBatch batch, DetailList list, string name)
        => batch.Details.TryGetValue(new DetailKey(Q, list, name), out var count) ? count : 0;

    [TestMethod]
    public void SkillActivations_AreNamed_WhateverTheToolThatActivatesThem()
    {
        var b = new JournalBuilder();
        b.ModelChanged(T0, "r1", "codex", "m", "Low")
            // an in-process provider: the activity kind is Skill and the name is the third word
            .ToolStarted(T0.AddSeconds(1), "r1", "a", "alta", "{\"args\":[\"skill\",\"activate\",\"release-notes\"]}", kind: "Skill")
            .ToolDone(T0.AddSeconds(2), "r1", "a", "alta", argsJson: "{\"args\":[\"skill\",\"activate\",\"release-notes\"]}", kind: "Skill")
            // Claude Code: an ordinary tool call of alta, with an option before the name
            .ToolStarted(T0.AddSeconds(3), "r1", "b", "alta", "{\"args\":[\"skill\",\"activate\",\"--session\",\"01a1\",\"dotnet-test-fix\"]}")
            .ToolDone(T0.AddSeconds(4), "r1", "b", "alta", argsJson: "{\"args\":[\"skill\",\"activate\",\"--session\",\"01a1\",\"dotnet-test-fix\"]}")
            // the alias of the old prompts, under the name the CLI gives the alta tool of the developer instance
            .ToolDone(T0.AddSeconds(5), "r1", "c", "mcp__codealta_dev__alta", argsJson: "{\"args\":[\"skills_activate\",\"ilspy-decompile\"]}")
            // the Skill tool of Claude Code, whose free text is never kept
            .ToolStarted(T0.AddSeconds(6), "r1", "d", "Skill", "{\"skill\":\"claude-api\",\"args\":\"how do I stream\"}")
            .ToolDone(T0.AddSeconds(7), "r1", "d", "Skill", argsJson: "{\"skill\":\"claude-api\",\"args\":\"how do I stream\"}", resultText: "Launching skill: claude-api")
            .Idle(T0.AddSeconds(8), "r1");

        var batch = SessionFactsReducerTests.Reduce(b);

        Assert.AreEqual(1, Detail(batch, DetailList.Skill, "release-notes"));
        Assert.AreEqual(1, Detail(batch, DetailList.Skill, "dotnet-test-fix"));
        Assert.AreEqual(1, Detail(batch, DetailList.Skill, "ilspy-decompile"));
        Assert.AreEqual(1, Detail(batch, DetailList.Skill, "claude-api"));
        Assert.AreEqual(4, batch.Details.Count(static pair => pair.Key.List == DetailList.Skill));
        // The alta command of Claude Code is still counted as a command; the free text of the Skill tool is no name.
        Assert.AreEqual(1, Detail(batch, DetailList.AltaCommand, "skill activate"));
    }

    [TestMethod]
    public void AnActivationThatFailsOrDoesNotEnd_UsesNoSkill()
    {
        var b = new JournalBuilder();
        b.ModelChanged(T0, "r1", "codex", "m", "Low")
            .ToolStarted(T0.AddSeconds(1), "r1", "a", "alta", "{\"args\":[\"skill\",\"activate\",\"not-a-skill\"]}", kind: "Skill")
            .ToolDone(T0.AddSeconds(2), "r1", "a", "alta", phase: "Failed", kind: "Skill")
            .ToolStarted(T0.AddSeconds(3), "r1", "b", "alta", "{\"args\":[\"skill\",\"activate\",\"never-ends\"]}", kind: "Skill")
            // a `skill` argument of another tool is not the Skill tool
            .ToolDone(T0.AddSeconds(4), "r1", "c", "inspect", argsJson: "{\"skill\":\"no-skill\"}")
            // a name that is not written as one
            .ToolDone(T0.AddSeconds(5), "r1", "d", "alta", argsJson: "{\"args\":[\"skill\",\"activate\",\"rm -rf /\"]}")
            .Idle(T0.AddSeconds(6), "r1");

        var batch = SessionFactsReducerTests.Reduce(b);

        Assert.AreEqual(0, batch.Details.Count(static pair => pair.Key.List == DetailList.Skill));
    }

    [TestMethod]
    public void SkillsAttachedToAPrompt_AreNamed_OncePerPrompt()
    {
        var b = new JournalBuilder();
        b.ModelChanged(T0, "r1", "codex", "m", "Low")
            .User(T0.AddSeconds(1), "r1", "release it", skills: ["release-notes", "release-notes", "site-screenshots"])
            .User(T0.AddSeconds(2), "r1", "and again", skills: ["release-notes"])
            .User(T0.AddSeconds(3), "r1", "an old prompt", ["skill"])
            .Idle(T0.AddSeconds(4), "r1");

        var batch = SessionFactsReducerTests.Reduce(b);

        Assert.AreEqual(2, Detail(batch, DetailList.SkillAttached, "release-notes"));
        Assert.AreEqual(1, Detail(batch, DetailList.SkillAttached, "site-screenshots"));
        // The old builder names its skill `s`: a name is a name, whatever it is.
        Assert.AreEqual(1, Detail(batch, DetailList.SkillAttached, "s"));
        Assert.AreEqual(0, batch.Details.Count(static pair => pair.Key.List == DetailList.Skill));
    }

    [TestMethod]
    public void ASkillMdThatIsRead_IsAUseOfItsSkill()
    {
        var b = new JournalBuilder();
        b.ModelChanged(T0, "r1", "codex", "m", "Low")
            .ToolDone(T0.AddSeconds(1), "r1", "a", "read_file", readFiles: ["/home/u/.alta/skills/dotnet-test-fix/SKILL.md", "/home/u/README.md"])
            .ToolDone(T0.AddSeconds(2), "r1", "b", "Read", readFiles: ["C:\\repo\\.agents\\skills\\win-skill\\skill.md"])
            .ToolDone(T0.AddSeconds(3), "r1", "c", "Read", readFiles: ["/x/skills/Not A Name/SKILL.md", "/SKILL.md", "/x/MYSKILL.md"])
            .ToolDone(T0.AddSeconds(4), "r1", "d", "Read", phase: "Failed", readFiles: ["/x/skills/failed-read/SKILL.md"])
            .Idle(T0.AddSeconds(5), "r1");

        var batch = SessionFactsReducerTests.Reduce(b);

        Assert.AreEqual(1, Detail(batch, DetailList.SkillRead, "dotnet-test-fix"));
        Assert.AreEqual(1, Detail(batch, DetailList.SkillRead, "win-skill"));
        Assert.AreEqual(2, batch.Details.Count(static pair => pair.Key.List == DetailList.SkillRead));
    }

    [TestMethod]
    public void McpTools_AreNamed_DirectOrThroughAlta()
    {
        var b = new JournalBuilder();
        b.ModelChanged(T0, "r1", "codex", "m", "Low")
            .ToolStarted(T0.AddSeconds(1), "r1", "a", "mcp__github__issue_read", "{\"n\":1}")
            .ToolDone(T0.AddSeconds(2), "r1", "a", "mcp__github__issue_read")
            .ToolStarted(T0.AddSeconds(3), "r1", "b", "alta", "{\"args\":[\"mcp\",\"tool\",\"call\",\"--server\",\"memory\",\"--tool\",\"echo\",\"--arguments\",\"{}\"]}")
            .ToolStarted(T0.AddSeconds(4), "r1", "c", "mcp__codealta__alta", "{\"args\":[\"mcp\",\"tool\",\"call\",\"--server=memory\",\"--tool=echo\"]}")
            .ToolStarted(T0.AddSeconds(5), "r1", "d", "alta", "{\"args\":[\"mcp\",\"tool\",\"search\",\"--server\",\"memory\"]}")
            .ToolStarted(T0.AddSeconds(6), "r1", "e", "alta", "{\"args\":[\"mcp\",\"tool\",\"call\",\"--server\",\"a/b\",\"--tool\",\"echo\"]}")
            .Idle(T0.AddSeconds(7), "r1");

        var batch = SessionFactsReducerTests.Reduce(b);

        Assert.AreEqual(1, Detail(batch, DetailList.McpTool, "github/issue_read"));
        Assert.AreEqual(2, Detail(batch, DetailList.McpTool, "memory/echo"));
        Assert.AreEqual(2, batch.Details.Count(static pair => pair.Key.List == DetailList.McpTool));
        Assert.AreEqual(4, Detail(batch, DetailList.AltaCommand, "mcp tool"));
    }
}
