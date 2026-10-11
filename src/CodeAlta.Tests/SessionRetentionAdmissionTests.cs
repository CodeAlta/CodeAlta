using CodeAlta.Agent;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Tests;

[TestClass]
public sealed class SessionRetentionAdmissionTests
{
    [TestMethod]
    public async Task InitialReadinessWaitRequiresEveryOwnerAndCancellationOnlyEndsItsWaiter()
    {
        var protection = new SessionRetentionProtection();
        protection.SealParticipants(["external", "desktop"]);
        using var cancellation = new CancellationTokenSource();
        var observer = protection.WaitForReadyAsync(cancellation.Token);
        var original = protection.WaitForReadyAsync(CancellationToken.None);
        Assert.IsTrue(protection.Publish("external", [], true));
        Assert.IsFalse(original.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => observer);
        Assert.IsFalse(original.IsCompleted);
        Assert.IsTrue(protection.Publish("desktop", ["open-session"], true));
        await original.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsNull(protection.TryReserveRetirement("open-session"));
        protection.MarkUnknown("desktop");
        Assert.IsNull(protection.TryReserveRetirement("other"), "First readiness does not override later readiness loss.");
    }

    [TestMethod]
    public async Task RefusalTellsWhy_AndTheEndOfWorkProtectingEverySessionCanBeAwaited()
    {
        var protection = Ready();
        Assert.IsTrue(protection.Publish("desktop", ["open"], true));
        Assert.IsNull(protection.TryReserveRetirement("open", out var refusal));
        Assert.AreEqual("session_protected", refusal);
        var all = protection.TryProtectAll()!;
        Assert.IsNull(protection.TryReserveRetirement("closed", out refusal));
        Assert.AreEqual("all_sessions_protected", refusal);
        Assert.IsFalse(await protection.WaitForAllSessionsReleasedAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None));
        var released = protection.WaitForAllSessionsReleasedAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        all.Dispose();
        Assert.IsTrue(await released);
        using (var retirement = protection.TryReserveRetirement("closed", out refusal)) Assert.IsNotNull(retirement, refusal);
        protection.MarkUnknown("desktop");
        Assert.IsNull(protection.TryReserveRetirement("closed", out refusal));
        Assert.AreEqual("protection_unavailable", refusal);
    }

    [TestMethod]
    public void SessionClaimsOverlapUntilEveryOriginalOwnerReleases()
    {
        var protection = Ready();
        ISessionRetentionAdmission admission = protection;
        var first = admission.TryProtectSession("session");
        var second = admission.TryProtectSession("SESSION");
        Assert.IsNotNull(first);
        Assert.IsNotNull(second);
        Assert.IsNull(protection.TryReserveRetirement("session"));
        first.Dispose();
        first.Dispose();
        Assert.IsNull(protection.TryReserveRetirement("session"));
        second.Dispose();
        using var retirement = protection.TryReserveRetirement("session");
        Assert.IsNotNull(retirement);
        Assert.IsNull(admission.TryProtectSession("session"));
        Assert.IsNull(admission.TryProtectAll());
        using var unrelated = admission.TryProtectSession("other");
        Assert.IsNotNull(unrelated);
    }

    [TestMethod]
    public void UnknownTargetTransferOverlapsSpecificClaims()
    {
        var protection = Ready();
        var preparation = protection.TryProtectAll();
        Assert.IsNotNull(preparation);
        Assert.IsNull(protection.TryReserveRetirement("session"));
        var target = protection.TryProtectSession("session");
        Assert.IsNotNull(target);
        preparation.Dispose();
        Assert.IsNull(protection.TryReserveRetirement("session"));
        using var unrelated = protection.TryReserveRetirement("other");
        Assert.IsNotNull(unrelated);
        target.Dispose();
        using var retirement = protection.TryReserveRetirement("session");
        Assert.IsNotNull(retirement);
    }

    [TestMethod]
    public void CommittedRetirementCannotBeResurrectedAfterReservationDisposal()
    {
        var protection = Ready();
        using (var retirement = protection.TryReserveRetirement("session"))
        {
            Assert.IsNotNull(retirement);
            Assert.IsTrue(protection.DeleteIfReady("session", () => true));
        }
        Assert.IsNull(protection.TryProtectSession("SESSION"));
        Assert.IsNull(protection.TryReserveRetirement("session"));
        Assert.IsFalse(protection.Publish("window", ["session"], true));
        using var unrelated = protection.TryProtectSession("new-session");
        Assert.IsNotNull(unrelated);
    }

    [TestMethod]
    public void RefusedDeletionDoesNotTombstoneAnUnchangedSource()
    {
        var protection = Ready();
        using (var retirement = protection.TryReserveRetirement("session"))
            Assert.IsFalse(protection.DeleteIfReady("session", () => false));
        using var claim = protection.TryProtectSession("session");
        Assert.IsNotNull(claim);
    }

    [TestMethod]
    public void UnknownParticipantsStayFailClosedEvenAfterClaimsRelease()
    {
        var protection = new SessionRetentionProtection();
        using (var claim = protection.TryProtectSession("session")) Assert.IsNotNull(claim);
        Assert.IsNull(protection.TryReserveRetirement("session"));
        Assert.ThrowsExactly<ArgumentException>(() => protection.TryProtectSession(" session "));
    }

    private static SessionRetentionProtection Ready()
    {
        var protection = new SessionRetentionProtection();
        protection.SealParticipants(["fixture"]);
        Assert.IsTrue(protection.Publish("fixture", [], true));
        return protection;
    }
}
