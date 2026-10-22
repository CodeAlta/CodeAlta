using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Tests;

/// <summary>The identifiers of the provenance of a prompt of the host, and which one a prompt the host sends is recorded with.</summary>
[TestClass]
public sealed class AgentPromptProvenanceTests
{
    [TestMethod]
    public void Identifiers_AreLowercaseWordsSeparatedByHyphens()
    {
        foreach (var id in AgentPromptProvenance.KnownIds) Assert.IsTrue(AgentPromptProvenance.IsValid(id), id);
        Assert.IsTrue(AgentPromptProvenance.IsValid("code-review2"));
        foreach (var id in new[] { null, "", "Reminder", "user review", "-reminder", "reminder-", "user--review", "user_review", new string('a', 65) })
            Assert.IsFalse(AgentPromptProvenance.IsValid(id), id ?? "null");

        Assert.AreEqual("reminder", AgentPromptProvenance.Normalize(" reminder "));
        Assert.IsNull(AgentPromptProvenance.Normalize("Not An Id"));
    }

    [TestMethod]
    public void DisplayName_OfAnIdentifierThisVersionDoesNotKnow_IsItsWords()
    {
        Assert.AreEqual("User Review", AgentPromptProvenance.GetDisplayName(AgentPromptProvenance.UserReview));
        Assert.AreEqual("Background Job", AgentPromptProvenance.GetDisplayName(AgentPromptProvenance.BackgroundJob));
        Assert.IsTrue(AgentPromptProvenance.IsKnown(AgentPromptProvenance.Automation));
        Assert.IsFalse(AgentPromptProvenance.IsKnown("code-review"));
        Assert.AreEqual("Code Review", AgentPromptProvenance.GetDisplayName("code-review"));
    }

    [TestMethod]
    public void PromptOfTheHost_IsRecordedWithWhatItsSenderNames_ElseWithWhatTheHostKnowsOfIt()
    {
        // What the sender names wins.
        Assert.AreEqual("user-review", SessionPromptProvenance.Of("user-review", "send", "host"));
        // The kind a prompt is queued or dispatched as.
        Assert.AreEqual(AgentPromptProvenance.BackgroundJob, SessionPromptProvenance.Of(null, SessionRuntimeService.JobPromptKind, "host"));
        Assert.AreEqual(AgentPromptProvenance.AgentSession, SessionPromptProvenance.Of(null, SessionPromptProvenance.ParentNotifyKind, "agent"));
        Assert.AreEqual(AgentPromptProvenance.Reminder, SessionPromptProvenance.Of(null, "reminder", null));
        // The kind of the actor that sent it.
        Assert.AreEqual(AgentPromptProvenance.AgentSession, SessionPromptProvenance.Of(null, "send", "agent"));
        Assert.AreEqual(AgentPromptProvenance.Reminder, SessionPromptProvenance.Of(null, "send", "reminder"));
        Assert.AreEqual(AgentPromptProvenance.Automation, SessionPromptProvenance.Of(null, "send", AltaActorProvenance.AutomationKind));
        Assert.AreEqual(AgentPromptProvenance.Plugin, SessionPromptProvenance.Of(null, "send", "plugin"));
        // A person, the command line, the host and a client of the MCP server name nothing: the prompt is the user's.
        foreach (var kind in new[] { null, "user", "cli", "host", "mcp" }) Assert.IsNull(SessionPromptProvenance.Of(null, "send", kind), kind ?? "null");
        Assert.AreEqual(AgentPromptProvenance.UserReview,
            SessionPromptProvenance.Of("send", new AltaActorProvenance { Kind = "host", PromptProvenance = AgentPromptProvenance.UserReview }));
    }
}
