using CodeAlta.Agent;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Orchestration.Runtime.Skills;

namespace CodeAlta.Orchestration.Tests;

[TestClass]
public sealed class SessionSkillActivationPlannerTests
{
    [TestMethod]
    public void Plan_ActivatesForReadyIdleCodeAltaManagedSession()
    {
        var decision = new SessionSkillActivationPlanner().Plan(
            CreateSession("local"),
            isModelProviderReady: true,
            isSessionBusy: false);

        Assert.AreEqual(SessionViewSkillActivationDecisionKind.Activate, decision.Kind);
    }

    [TestMethod]
    public void Plan_RejectsMissingSession()
    {
        var decision = new SessionSkillActivationPlanner().Plan(
            session: null,
            isModelProviderReady: true,
            isSessionBusy: false);

        Assert.AreEqual(SessionViewSkillActivationDecisionKind.RejectNoSession, decision.Kind);
        StringAssert.Contains(decision.Message, "Open");
    }

    [TestMethod]
    public void Plan_ActivatesForReadyIdleCodexSession()
    {
        var decision = new SessionSkillActivationPlanner().Plan(
            CreateSession(ModelProviderIds.Codex.Value),
            isModelProviderReady: true,
            isSessionBusy: false);

        Assert.AreEqual(SessionViewSkillActivationDecisionKind.Activate, decision.Kind);
    }

    [TestMethod]
    public void Plan_RejectsProviderNotReady()
    {
        var decision = new SessionSkillActivationPlanner().Plan(
            CreateSession("local"),
            isModelProviderReady: false,
            isSessionBusy: false);

        Assert.AreEqual(SessionViewSkillActivationDecisionKind.RejectProviderNotReady, decision.Kind);
    }

    [TestMethod]
    public void Plan_RejectsBusySession()
    {
        var decision = new SessionSkillActivationPlanner().Plan(
            CreateSession("local") with { Title = "Session" },
            isModelProviderReady: true,
            isSessionBusy: true);

        Assert.AreEqual(SessionViewSkillActivationDecisionKind.RejectSessionBusy, decision.Kind);
        StringAssert.Contains(decision.Message, "Session");
    }

    private static SessionViewDescriptorSnapshot CreateSession(string ProviderId)
        => new()
        {
            SessionId = "session-1",
            ProviderId = ProviderId,
            WorkingDirectory = "C:/project",
            Title = "Session",
        };
}
