using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Orchestration.Tests;

[TestClass]
public sealed class PromptSkillHistoryTests
{
    [TestMethod]
    public void ReadSkills_NamesTheSkillNotesOfAMessage_InTheirOrder()
    {
        var input = new AgentInput(
        [
            new AgentInputItem.HostContext(AgentInputItem.HostContext.SkillKind, "The user activated the CodeAlta skill 'grill-me'.\n\n<skill_content name=\"grill-me\" source=\"user\">\nAsk.\n</skill_content>"),
            new AgentInputItem.HostContext("review", "<skill_content name=\"not-a-skill-note\">"),
            new AgentInputItem.HostContext(AgentInputItem.HostContext.SkillKind, "<skill_content name=\"tdd\">\nTest first.\n</skill_content>"),
            new AgentInputItem.HostContext(AgentInputItem.HostContext.SkillKind, "a note that names no skill"),
            new AgentInputItem.Text("/grill-me /tdd the parser"),
        ]);
        using var details = JsonDocument.Parse(input.ToJson());

        CollectionAssert.AreEqual(new[] { "grill-me", "tdd" }, PromptSkillHistory.ReadSkills(details.RootElement).ToArray());
    }

    [TestMethod]
    public void ReadSkills_FindsNothingInOtherDetails()
    {
        Assert.HasCount(0, PromptSkillHistory.ReadSkills(null));
        using var text = JsonDocument.Parse(AgentInput.Text("/tdd").ToJson());
        Assert.HasCount(0, PromptSkillHistory.ReadSkills(text.RootElement));
        using var array = JsonDocument.Parse("[1, 2]");
        Assert.HasCount(0, PromptSkillHistory.ReadSkills(array.RootElement));
    }
}
