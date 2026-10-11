using System.Reflection;
using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Skills;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Orchestration.Tests;

public sealed partial class SessionRuntimeForwardingLifetimeTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task SkillRetention_PreparationOutlivesCanceledPublicWait(bool createOnly) => Fixture.Run(async f =>
    {
        var roots = await InstallRetentionSkillCatalog(f);
        var source = Guid.NewGuid().ToString("D");
        var parent = Guid.NewGuid().ToString("D");
        f.Session.ParentSessionId = parent;
        f.Session.CreatedBy = new AltaActorProvenance { Kind = "agent", SourceSessionId = source };
        using var cancellation = new CancellationTokenSource();
        var waiter = f.Track(createOnly
            ? (Task)f.Runtime.CreateSkillActivationAsync(f.Session, f.Options, "retention-fixture", cancellation.Token)
            : f.Runtime.ActivateSkillAsync(f.Session, f.Options, "retention-fixture", cancellation.Token));
        Task close = Task.CompletedTask;
        try
        {
            await f.Ready(roots.Entered.Task);
            AssertParentRetirementBlocked(f.Runtime, f.Session.SessionId);
            AssertParentRetirementBlocked(f.Runtime, parent);
            AssertParentRetirementBlocked(f.Runtime, source);
            cancellation.Cancel();
            await f.ExpectCancellation(waiter);
            // The outer activation and its preparation each own admission. Neither is the
            // canceled public CreateSkillActivationAsync observer that used to be awaited here.
            Assert.IsTrue(SkillRetentionCount(f.Runtime, source) >= (createOnly ? 1 : 2));
            Assert.AreEqual(0, f.Provider.Creates);
            close = f.Close();
            Assert.IsFalse(close.IsCompleted, "Shutdown must join the original asynchronous root discovery.");
            AssertParentRetirementBlocked(f.Runtime, source);
        }
        finally { roots.Release.TrySetResult(); }
        await f.Wait(close);
        using var released = TryClaimParentRetirement(f.Runtime, f.Session.SessionId);
        using var releasedParent = TryClaimParentRetirement(f.Runtime, parent);
        using var releasedSource = TryClaimParentRetirement(f.Runtime, source);
        Assert.IsNotNull(released);
        Assert.IsNotNull(releasedParent);
        Assert.IsNotNull(releasedSource);
        Assert.AreEqual(0, f.Provider.Creates);
    });

    [TestMethod]
    public Task SkillRetention_ActivationOwnsOriginalSendAfterCanceledPublicWait() => Fixture.Run(async f =>
    {
        var roots = await InstallRetentionSkillCatalog(f);
        roots.Release.TrySetResult();
        var source = Guid.NewGuid().ToString("D");
        f.Session.CreatedBy = new AltaActorProvenance { Kind = "agent", SourceSessionId = source };
        using var cancellation = new CancellationTokenSource();
        var waiter = f.Track(f.Runtime.ActivateSkillAsync(f.Session, f.Options, "retention-fixture", cancellation.Token));
        await f.Ready(f.Provider.SendStarted.Task);
        Assert.IsTrue(SkillRetentionCount(f.Runtime, source) >= 2, "Activation and its original send must both own admission.");
        cancellation.Cancel();
        await f.ExpectCancellation(waiter);
        Assert.IsTrue(SkillRetentionCount(f.Runtime, source) >= 2, "Cancellation of the public waiter cannot complete the activation original.");
        AssertParentRetirementBlocked(f.Runtime, source);
        f.Provider.HoldAbort = true;
        var close = f.Close();
        await f.Ready(f.Provider.AbortStarted.Task);
        Assert.IsFalse(close.IsCompleted);
        AssertParentRetirementBlocked(f.Runtime, source);
        f.Provider.ReleaseSend.TrySetResult();
        f.Provider.ReleaseAbort.TrySetResult();
        await f.Wait(close);
        using var released = TryClaimParentRetirement(f.Runtime, source);
        Assert.IsNotNull(released);
        Assert.AreEqual(0, f.Provider.EarlyDisposals);
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task SkillRetention_RefusesRetiredLineageBeforePreparation(bool createOnly) => Fixture.Run(async f =>
    {
        var roots = await InstallRetentionSkillCatalog(f);
        var source = Guid.NewGuid().ToString("D");
        f.Session.CreatedBy = new AltaActorProvenance { Kind = "agent", SourceSessionId = source };
        using var retired = TryClaimParentRetirement(f.Runtime, source);
        Assert.IsNotNull(retired);
        try
        {
            var original = f.Track(createOnly
                ? (Task)f.Runtime.CreateSkillActivationAsync(f.Session, f.Options, "retention-fixture")
                : f.Runtime.ActivateSkillAsync(f.Session, f.Options, "retention-fixture"));
            await f.Expect<InvalidOperationException>(original);
            Assert.IsFalse(roots.Entered.Task.IsCompleted);
            Assert.AreEqual(0, f.Provider.Creates);
        }
        finally { roots.Release.TrySetResult(); }
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task SkillRetention_MixedPreparationFailureRetainsOriginalLineage(bool createOnly) => Fixture.Run(async f =>
    {
        var roots = await InstallRetentionSkillCatalog(f);
        var source = Guid.NewGuid().ToString("D");
        f.Session.CreatedBy = new AltaActorProvenance { Kind = "agent", SourceSessionId = source };
        var ordinary = new IOException("ordinary first preparation fault");
        roots.Failure = new AggregateException(ordinary,
            new AgentDependencyRetentionException("synthetic skill preparation", "unconfirmed dependency", [new IOException("retained sibling")], roots));
        var original = f.Track(createOnly
            ? (Task)f.Runtime.CreateSkillActivationAsync(f.Session, f.Options, "retention-fixture")
            : f.Runtime.ActivateSkillAsync(f.Session, f.Options, "retention-fixture"));
        try
        {
            await f.Ready(roots.Entered.Task);
            AssertParentRetirementBlocked(f.Runtime, source);
        }
        finally { roots.Release.TrySetResult(); }
        var failure = await f.Expect<AggregateException>(original);
        Assert.IsTrue(AgentDependencyRetentionException.Contains(failure));
        Assert.IsTrue(failure.Flatten().InnerExceptions.Contains(ordinary));
        AssertParentRetirementBlocked(f.Runtime, source);
        var closeFailure = await Assert.ThrowsAsync<Exception>(() => f.Wait(f.Close()));
        f.Expected(closeFailure);
        Assert.IsTrue(AgentDependencyRetentionException.Contains(closeFailure));
        AssertParentRetirementBlocked(f.Runtime, source);
        Assert.AreEqual(0, f.Provider.Creates);
    });

    private static async Task<RetentionSkillRootProvider> InstallRetentionSkillCatalog(Fixture f)
    {
        var root = Path.Combine(f.ProjectPath, "synthetic-skills");
        var directory = Path.Combine(root, "retention-fixture");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "SKILL.md"), """
            ---
            name: retention-fixture
            description: Inert synthetic retention fixture.
            ---
            Inert fixture context; no external actions.
            """);
        var provider = new RetentionSkillRootProvider(root);
        // Substitute only the runtime's catalog, not host/global discovery or the provider's
        // instruction catalog. This fixture owns every root and never discovers real user skills.
        typeof(SessionRuntimeService).GetField("_skillCatalog", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(f.Runtime, new SkillCatalog([provider]));
        return provider;
    }

    private static int SkillRetentionCount(SessionRuntimeService runtime, string sessionId)
    {
        var service = typeof(SessionRuntimeService);
        var gate = service.GetField("_identityGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(runtime)!;
        lock (gate)
            return ((Dictionary<string, int>)service.GetField("_retentionWork", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(runtime)!).GetValueOrDefault(sessionId);
    }

    private sealed class RetentionSkillRootProvider(string root) : ISkillRootProvider
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Exception? Failure { get; set; }

        public async ValueTask<IReadOnlyList<SkillRootRegistration>> GetRootsAsync(
            SkillDiscoveryContext context, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            // Deliberately ignores cancellation until the controlled original really completes.
            await Release.Task;
            if (Failure is { } failure) throw failure;
            return [new SkillRootRegistration
            {
                RootPath = root, SourceKind = SkillSourceKind.ProjectAlta, SourceId = "retention-fixture",
                Scope = SkillScopeKind.Project, Precedence = 0,
            }];
        }
    }
}
