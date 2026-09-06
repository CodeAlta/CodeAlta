using CodeAlta.Agent;
using CodeAlta.LiveTool;
using CodeAlta.Orchestration.Runtime.Prompts;
using CodeAlta.Tui.App;

namespace CodeAlta.Tests;

// Pure presentation decisions used by the real coordinator; no terminal, files, history, or runtime startup.
[TestClass]
public sealed class AskResponsePresentationPolicyTests
{
    [TestMethod]
    public async Task RejectedCallback_ReconcilesOnlyItsObsoleteActiveGeneration()
    {
        var service = new AltaAskService();
        await service.QueueAsync(new(), "session", AltaCallerIdentity.Host);
        var old = service.Peek("session")!.ResponseHandle!;
        await service.RespondAsync(old, () => Task.FromResult(SessionPromptResponseResult.NotAdmitted()));
        var fresh = service.Peek("session")!.ResponseHandle!;
        Assert.IsTrue(AskResponsePresentationPolicy.ShouldReconcileRejected(old, old, fresh));
        Assert.IsTrue(AskResponsePresentationPolicy.ShouldReconcileRejected(old, old, null));
        Assert.IsFalse(AskResponsePresentationPolicy.ShouldReconcileRejected(fresh, old, fresh));
        Assert.IsFalse(AskResponsePresentationPolicy.ShouldReconcileRejected(null, old, fresh));
    }

    [TestMethod]
    public async Task SameHandleSubmittingOrIndeterminate_DoesNotDetachOriginalCompletion()
    {
        var service = new AltaAskService();
        await service.QueueAsync(new(), "session", AltaCallerIdentity.Host);
        var handle = service.Peek("session")!.ResponseHandle!;
        var release = new TaskCompletionSource<SessionPromptResponseResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var submit = service.RespondAsync(handle, () => release.Task);
        Assert.IsFalse(AskResponsePresentationPolicy.ShouldReconcileRejected(handle, handle, service.Peek("session")!.ResponseHandle));
        release.SetResult(SessionPromptResponseResult.Indeterminate());
        await submit;
        Assert.IsFalse(AskResponsePresentationPolicy.ShouldReconcileRejected(handle, handle, service.Peek("session")!.ResponseHandle));
    }

    [TestMethod]
    [DataRow("selected", "selected", null, true)]
    [DataRow("background", "selected", null, false)]
    [DataRow("active", "selected", "active", false)]
    [DataRow("active", "active", "active", true)]
    [DataRow("active", null, "active", false)]
    [DataRow("selected", "selected", "active", false)]
    [DataRow("background", "selected", "active", false)]
    [DataRow("selected", null, null, false)]
    [DataRow("SELECTED", "selected", null, false)]
    public void BlockedStatus_OnlyTargetsForegroundSession(string target, string? selected, string? active, bool expected)
        => Assert.AreEqual(expected, AskResponsePresentationPolicy.ShouldReportBlockedState(target, selected, active));

    [TestMethod]
    public void OptimisticCleanup_RequiresOwnMarkerAndNoObservedRuntimeOrNewerDispatch()
    {
        var start = new DateTimeOffset(2026, 9, 6, 0, 0, 0, TimeSpan.Zero);
        Assert.IsTrue(AskResponsePresentationPolicy.ShouldClearOptimisticRun(4, 4, start, start, null));
        Assert.IsFalse(AskResponsePresentationPolicy.ShouldClearOptimisticRun(4, 5, start, start, null));
        Assert.IsFalse(AskResponsePresentationPolicy.ShouldClearOptimisticRun(4, 4, null, start, null));
        Assert.IsFalse(AskResponsePresentationPolicy.ShouldClearOptimisticRun(4, 4, start, null, null));
        Assert.IsFalse(AskResponsePresentationPolicy.ShouldClearOptimisticRun(4, 4, start, start.AddSeconds(1), null));
        Assert.IsFalse(AskResponsePresentationPolicy.ShouldClearOptimisticRun(4, 4, start, start, new AgentRunId("observed-runtime")));
    }
}
