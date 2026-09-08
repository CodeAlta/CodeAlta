using System.Runtime.CompilerServices;
using System.Text;
using Inverse = CodeAlta.Tests.RuntimeEventForwardingSourceInverse;
using Owner = CodeAlta.Tests.OwnedSessionCommandSourceInverse;
using Desktop = CodeAlta.Tests.DesktopOwnedSessionSourceInverse;

namespace CodeAlta.Tests;

/// <summary>Named checkout text only. No host/provider/fixture execution, assembly loading, or baseline-file reads.</summary>
[TestClass]
public sealed class RuntimeEventForwardingSourceTests
{
    private const string Helper = "CodeAlta.Orchestration/Runtime/OwnedProviderEventForwarding.cs";
    private const string Pure = "CodeAlta.Orchestration.Tests/OwnedProviderEventForwardingTests.cs";
    private const string Real = "CodeAlta.Orchestration.Tests/SessionRuntimeForwardingLifetimeTests.cs";
    private const string InverseSource = "CodeAlta.Tests/RuntimeEventForwardingSourceInverse.cs";
    private const string Self = "CodeAlta.Tests/RuntimeEventForwardingSourceTests.cs";
    private const string Notes = "../doc/runtime-provider-event-forwarding.md";
    private const string HistoricalFixture = "CodeAlta.Desktop.Tests/DesktopOwnedSessionSourceTests.cs";

    // Nine scoped inputs plus the untouched historical assertion/reader-map source. The sibling
    // Desktop fixture remains responsible for running its actual inherited readers separately.
    internal static IReadOnlyList<string> DirectContentPaths =>
    [
        Inverse.Runtime, Inverse.DesktopInverse, Inverse.DesktopProject,
        Helper, Pure, Real, InverseSource, Self, Notes, HistoricalFixture,
    ];
    internal static IReadOnlyList<string> TransitiveContentPaths => [];

    /// <summary>Checks genuine guard expressions and separate body/tail/attachment-use release points.</summary>
    [TestMethod]
    public void Runtime_OwnsBodiesTailsAndCapturedAttachmentUses()
    {
        var runtime = Read(Inverse.Runtime);
        foreach (var expression in new[]
        {
            "@event => _ = PostAgentEventToActorAsync(actor, session.SessionId, projector, @event)",
            "EnsureCoordinatorSessionCoreAsync(session, options, actorCancellationToken)",
            "var sessionHandleId = await _sessionActors.GetOrCreate(session.SessionId).QueryAsync(",
            "var runId = await _agentHub.RunAsync(sessionHandleId, sendOptions, cancellationToken).ConfigureAwait(false);",
            "return await _agentHub.SteerAsync(sessionHandleId, steerOptions, cancellationToken).ConfigureAwait(false);",
            "await MarkActiveRunIfStillInFlightAsync(session.SessionId, runId, runStartedAt, cancellationToken)",
            "var result = await _sessionActors.GetOrCreate(session.SessionId).ExecuteAsync(",
            "return entry.Projector.ProjectHistory(history);",
        }) StringAssert.Contains(runtime, expression);
        Assert.IsFalse(runtime.Contains("cancellationToken = execution.Token", StringComparison.Ordinal));
        var setup = Between(runtime, "    private async ValueTask<AgentSessionHandleId> CreateCoordinatorSessionAsync(", "    private async Task PublishRunSubmittedIfStillInFlightAsync(");
        Before(setup, "_forwarding.RegisterAttachment(", "await UpsertSessionMetadataAsync(");
        Before(setup, "projector.Entry = entry;", "await _agentHub.SubscribeSessionEventsAsync(");
        Before(setup, "attachment.InstallSubscription(subscription);", "attachment.CompleteSetup();");
        StringAssert.Contains(setup, "session.SessionId, sessionHandleId.ToString(),");
        StringAssert.Contains(setup, "await actor.QueryAsync(async actorCancellationToken =>\n        {\n            await UpdateSessionLocalStateAsync(session, actorCancellationToken).ConfigureAwait(false);");
        StringAssert.Contains(setup, "!ReferenceEquals(currentTicket, ticket)");
        StringAssert.Contains(setup, "entry.PendingAgentPromptId = previousEntry.PendingAgentPromptId;");

        var send = Between(runtime, "    private async Task<AgentRunId> SendOwnedBodyAsync(", "    private async Task<AgentRunId> RunCapturedAsync(");
        Before(send, "candidate.Matches(options, NormalizeOptionalText(candidate.PendingAgentPromptId)", "candidate.Attachment.TryAcquireHandleUse()");
        Before(send, "if (handleUse is null) return default(AgentSessionHandleId);", "candidate.PendingAgentPromptId = null;");
        StringAssert.Contains(send, "coordinationCancellationToken, candidate)");
        var compact = Between(runtime, "    private async Task CompactOwnedBodyAsync(", "    private static bool ShouldPublishHostCompactionOutcome(");
        RequireOnce(compact, "AgentSessionUpdateKind.CompactionStarted");
        Before(compact, "AgentSessionUpdateKind.CompactionStarted", "ResolveCoordinatorEntryAsync(");
        Before(compact, "entry.Matches(options, NormalizeOptionalText(entry.PendingAgentPromptId)", "entry.Attachment.TryAcquireHandleUse()");
        StringAssert.Contains(compact, "if (use is not null) entry.PendingAgentPromptId = null;");

        var post = Between(runtime, "    private async Task PostAgentEventToActorCoreAsync(", "    private static bool IsQueueDrainTrigger(");
        StringAssert.Contains(post, "projector.Entry!.TakeParentNotifications(sanitized)");
        Before(post, "projectionUse.Dispose();", "await DeliverParentNotificationAsync(");
        Before(post, "projectionUse.Dispose();", "await TryDrainNextQueuedPromptAsync(");
        var drain = Between(runtime, "    private async Task TryDrainNextQueuedPromptAsync(", "    private async Task<QueuedPromptDrainWork?> TryMarkNextQueuedPromptSubmittingAsync(");
        StringAssert.Contains(drain, "MarkQueuedPromptSubmittedAsync(work.Entry,");
        StringAssert.Contains(drain, "MarkQueuedPromptFailedAsync(work.Entry,");
        Before(drain, "finally { work.Use.Dispose(); }", "await TryDrainNextQueuedPromptAsync(sessionId)");
        var parent = Between(runtime, "    private async Task<SessionViewDescriptor?> TryResolveSessionForParentDeliveryAsync(", "    private async Task ApplyLocalSessionStateAsync(");
        StringAssert.Contains(parent, "session = await ResolveParentFromCachedStoreAsync(sessionId, cancellationToken)");
        StringAssert.Contains(parent, "_sessionViewCatalog.JournalStore.CreateSessionStore()");
        StringAssert.Contains(parent, ".GetSessionAsync(sessionId, cancellationToken)");
        StringAssert.Contains(parent, "await _projectCatalog.LoadAsync(cancellationToken)");
        foreach (var forbidden in new[] { "ListRecoverableSessionsAsync", "ResolveOwnedSessionAsync", "new FileSystemAgentSessionStore", "Task.Run(" })
            Assert.IsFalse(parent.Contains(forbidden, StringComparison.Ordinal), forbidden);

        var helper = Read(Helper);
        var forward = Between(helper, "    internal Task Forward(", "    internal Task RetireAsync(");
        Before(forward, "_active.Add(ordinal, (work, ObserveOwnedAsync(ordinal, work)));", "launch.TrySetResult();");
        StringAssert.Contains(forward, "identity: attachment.Identity");
        var retire = Between(helper, "    private async Task RetireCoreAsync(", "    private async Task WaitUsesAsync(");
        Before(retire, "cancelStage.Launch();", "await attachment.Setup.Task");
        Before(retire, "abortStage.Launch();", "await attachment.Setup.Task");
        Before(retire, "attachment.CallbackAdmission = false;", "subscription?.Dispose();");
        Before(retire, "await WaitUsesAsync(attachment, projection: true)", "var stopStage = new RetirementStage(attachment.Stop);");
        Before(retire, "if (!unsubscribe.IsCompletedSuccessfully)", "var stopStage = new RetirementStage(attachment.Stop);");
        StringAssert.Contains(retire, "throw new AggregateException(new[] { cancellation, abort, unsubscribe }");
        Assert.IsFalse(retire.Contains("_active", StringComparison.Ordinal));
        StringAssert.Contains(helper, "failure.Data[\"RetainedForwardingOwner\"] = this;");
        StringAssert.Contains(helper, "internal sealed record AttachmentIdentity(string SessionId, string HandleId);");
        StringAssert.Contains(helper, "internal AttachmentIdentity Identity { get; }");
        foreach (var forbidden in new[] { "Task.Run(", "ContinueWith(", "WaitAsync(TimeSpan", "Task.Delay(" })
            Assert.IsFalse(helper.Contains(forbidden, StringComparison.Ordinal), forbidden);
    }

    /// <summary>Checks controlled fixture ownership and the task-owned prompt/discovery setup without executing it.</summary>
    [TestMethod]
    public void Fixtures_KeepControlledOwnershipAndClosedRoots()
    {
        var pure = Read(Pure);
        Assert.AreEqual(9, Count(pure, "[TestMethod]"));
        Assert.AreEqual(4, Count(pure, "_ = f.Owner.Forward("));
        var retirement = Between(pure, "    public Task Retirement_JoinsProjectionUsesButNotWholeTails()", "    [TestMethod]\n    public Task Shutdown_JoinsCompleteTails()");
        Before(retirement, "var retirement = f.Track(f.Owner.RetireAsync(attachment));", "Assert.IsFalse(attachment.Stopped);");
        Before(retirement, "Assert.IsFalse(attachment.Stopped);", "releaseProjection.TrySetResult();");
        StringAssert.Contains(retirement, "await releaseProjection.Task;");
        StringAssert.Contains(retirement, "await f.Owner.RetireAsync(attachment);");
        StringAssert.Contains(pure, "FailedUnsubscribe_RetainsDependenciesAndFailureIdentity");
        StringAssert.Contains(pure, "Assert.AreSame(attachment.Identity, failure.Identity);");
        StringAssert.Contains(pure, "finally { await fixture.Cleanup(); }");
        StringAssert.Contains(pure, "failure.Data[\"RetainedFixture\"] = this;");
        StringAssert.Contains(pure, "_expected.Add(error)");
        var real = Read(Real);
        Assert.AreEqual(10, Count(real, "[TestMethod]"));
        StringAssert.Contains(real, "await f.Ready(f.Provider.ReplacementPreparationStarted.Task);");
        StringAssert.Contains(real, "\"during replacement\", \"send\", null");
        StringAssert.Contains(real, "Assert.AreEqual(2, state.QueuedPrompts.Count);");
        StringAssert.Contains(real, "Assert.AreNotEqual(await f.Wait(first), await f.Wait(second));");
        Assert.IsFalse(real.Contains("Assert.AreEqual(\"second-model\", f.Provider.Latest.Options.Model);", StringComparison.Ordinal));
        StringAssert.Contains(real, "excludesfile = fixture.ignore\\n");
        StringAssert.Contains(real, "File.WriteAllText(Path.Combine(git, \"fixture.ignore\"), \"\");");
        StringAssert.Contains(real, "Path.Combine(global, \"prompts\", \"agents\")");
        StringAssert.Contains(real, "\"plan.prompt.md\"");
        StringAssert.Contains(real, "new SessionDiscoveryScope(home, _root)");
        StringAssert.Contains(real, "BeforeSubscriptionReturn = () => closed.TrySetResult(f.Track(f.Runtime.DisposeAsync().AsTask()))");
        StringAssert.Contains(real, "await f.Ready(target.SendFinished.Task);");
        StringAssert.Contains(real, "await f.Ready(source.SendFinished.Task);");
        StringAssert.Contains(real, "failure.Data[\"RetainedFixture\"] = f;");
        foreach (var source in new[] { pure, real })
        {
            StringAssert.Contains(source, ".Select(task => task.WaitAsync(TimeSpan.FromSeconds(5))).ToArray()");
            Assert.IsFalse(source.Contains("Directory.Delete(", StringComparison.Ordinal));
        }
        StringAssert.Contains(Read(Notes), "not a completed shutdown or compatibility qualification");
    }

    /// <summary>Checks three whole originals, nine newline reconstructions, and every ordered tuple's missing/duplicate negatives.</summary>
    [TestMethod]
    public void Preservation_RestoresWholeOriginalsAndRejectsDrift()
    {
        Assert.AreEqual(3, Inverse.Originals.Count);
        Assert.AreEqual(95, Inverse.Originals.Sum(item => Inverse.Edits(item.Path).Count));
        Assert.AreEqual(96, Inverse.Originals.Sum(item => Inverse.Edits(item.Path).Sum(edit => edit.Count)));
        var reconstructions = 0;
        foreach (var (path, hash) in Inverse.Originals)
        {
            var current = Read(path);
            foreach (var representation in Representations(current))
            {
                Assert.AreEqual(hash, Owner.Hash(Inverse.Restore(path, representation)), path);
                reconstructions++;
            }
            // Tuples are ordered and some introduce the unchanged context for a later tuple.
            // Exercise the exact same occurrence checker at its actual application stage, not
            // a no-op Replace against raw current text that lacks an intermediate fragment.
            var intermediate = current;
            foreach (var edit in Inverse.Edits(path))
            {
                var after = SourceTestText.Canonicalize(edit.After);
                var before = SourceTestText.Canonicalize(edit.Before);
                Assert.IsTrue(SharedContextLines(before, after).Any(), path + ": missing unchanged context");
                Assert.ThrowsExactly<AssertFailedException>(() => Inverse.Apply(path, intermediate.Replace(after, "", StringComparison.Ordinal), edit), path);
                Assert.ThrowsExactly<AssertFailedException>(() => Inverse.Apply(path, intermediate + after + "\n", edit), path);
                intermediate = Inverse.Apply(path, intermediate, edit);
            }
            Assert.AreEqual(hash, Owner.Hash(intermediate), path);
            Assert.AreEqual(path switch
            {
                Inverse.Runtime => 131701,
                Inverse.DesktopInverse => 36681,
                Inverse.DesktopProject => 1632,
                _ => throw new AssertFailedException("Unexpected original path."),
            }, new UTF8Encoding(false, true).GetByteCount(intermediate), path);
            Assert.ThrowsExactly<AssertFailedException>(() => Inverse.Restore(path, "// unrelated drift\n" + current), path);
            Assert.ThrowsExactly<AssertFailedException>(() => Inverse.Restore(path, intermediate), path);
        }
        Assert.AreEqual(9, reconstructions);
        Assert.ThrowsExactly<AssertFailedException>(() => Inverse.Restore("unknown.cs", "// unknown\n"));
    }

    /// <summary>Checks disjoint newest gateways, old full-original anchors, unchanged historical checks, and zero-read inverse closure.</summary>
    [TestMethod]
    public void Preservation_ClosesReaderMapsAndInheritedGateways()
    {
        Assert.AreEqual(10, DirectContentPaths.Distinct(StringComparer.Ordinal).Count());
        Assert.AreEqual(0, TransitiveContentPaths.Count);
        Assert.AreEqual(0, Inverse.DirectContentPaths.Count);
        Assert.AreEqual(0, Inverse.TransitiveContentPaths.Count);
        Assert.AreEqual(16, Desktop.Originals.Count);
        Assert.AreEqual(42, Desktop.Originals.Sum(item => Desktop.Edits(item.Path).Count));
        Assert.AreEqual(42, Desktop.Originals.Sum(item => Desktop.Edits(item.Path).Sum(edit => edit.Count)));
        var runtime = Read(Inverse.Runtime);
        Assert.AreEqual(Inverse.Restore(Inverse.Runtime, runtime), Desktop.RestoreInput(Inverse.Runtime, runtime));
        Assert.AreEqual(Owner.Originals.Single(item => item.Path == Inverse.Runtime).Hash,
            Owner.Hash(Owner.RestoreCurrentInput(Inverse.Runtime, runtime)));
        SessionDiscoveryScopeSourceInverse.Restore(Inverse.Runtime, runtime);
        var project = Read(Inverse.DesktopProject);
        Assert.AreEqual(Desktop.Originals.Single(item => item.Path == Inverse.DesktopProject).Hash,
            Owner.Hash(Desktop.Restore(Inverse.DesktopProject, project)));
        Owner.RestoreCurrentInput(Inverse.DesktopProject, project);
        PluginNeutralContractSourceInverse.Restore(Inverse.DesktopProject, project);
        var desktopInverse = Read(Inverse.DesktopInverse);
        Assert.AreSame(desktopInverse, Desktop.RestoreInput(Inverse.DesktopInverse, desktopInverse));
        Inverse.Restore(Inverse.DesktopInverse, desktopInverse); // Deliberately direct; no recursive gateway.
        const string untouched = "unmapped input";
        Assert.AreSame(untouched, Desktop.RestoreInput("unmapped", untouched));

        var historical = Read(HistoricalFixture);
        foreach (var assertion in new[]
        {
            "Assert.AreEqual(16, Inverse.Originals.Count);",
            "Assert.AreEqual(42, Inverse.Originals.Sum(item => Inverse.Edits(item.Path).Count));",
            "Assert.AreEqual(42, Inverse.Originals.Sum(item => Inverse.Edits(item.Path).Sum(edit => edit.Count)));",
            "Assert.AreEqual(48, reconstructions);",
            "Assert.AreEqual(30, DirectContentPaths.Distinct(StringComparer.Ordinal).Count());",
            "Assert.AreEqual(19, TransitiveContentPaths.Distinct(StringComparer.Ordinal).Count());",
            "Assert.ThrowsExactly<AssertFailedException>(() => Inverse.Restore(\"unknown.cs\", \"// unknown\\n\"));",
            "new DesktopHistorySourceTests().Boundaries_ReconstructWholeSourcesAndPreserveHistoricalChains();",
        }) StringAssert.Contains(historical, assertion);
        var inverse = Read(InverseSource);
        foreach (var forbidden in new[] { "File.Read", "Directory.", "Assembly.Load", "Process.Start", "ReadAllText", "ReadAllBytes" })
            Assert.IsFalse(inverse.Contains(forbidden, StringComparison.Ordinal), forbidden);
        var self = Read(Self);
        RequireOnce(self, "return SourceTestText." + "DecodeSource(File.ReadAllBytes(");
    }

    private static IEnumerable<string> SharedContextLines(string before, string after)
        => before.Split('\n').Intersect(after.Split('\n'), StringComparer.Ordinal)
            .Where(line => line.Trim().Length > 1 && line.Trim() is not "}," and not "};" and not "try" and not "finally");

    private static IEnumerable<string> Representations(string source)
    {
        yield return source;
        yield return source.Replace("\n", "\r\n", StringComparison.Ordinal);
        var mixed = new StringBuilder();
        var crlf = false;
        foreach (var character in source)
        {
            if (character == '\n' && (crlf = !crlf)) mixed.Append('\r');
            mixed.Append(character);
        }
        yield return mixed.ToString();
    }

    private static string Read(string path, [CallerFilePath] string caller = "")
    {
        CollectionAssert.Contains(DirectContentPaths.ToArray(), path);
        return SourceTestText.DecodeSource(File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(caller)!, "..", path)));
    }

    private static int Count(string source, string fragment) => source.Split(fragment, StringSplitOptions.None).Length - 1;
    private static void RequireOnce(string source, string fragment) => Assert.AreEqual(1, Count(source, fragment), fragment);
    private static void Before(string source, string first, string second)
    {
        var start = source.IndexOf(first, StringComparison.Ordinal);
        Assert.IsTrue(start >= 0, first);
        Assert.IsTrue(source.IndexOf(second, StringComparison.Ordinal) > start, second);
    }
    private static string Between(string source, string start, string end)
    {
        RequireOnce(source, start);
        RequireOnce(source, end);
        var offset = source.IndexOf(start, StringComparison.Ordinal);
        var limit = source.IndexOf(end, offset + start.Length, StringComparison.Ordinal);
        Assert.IsTrue(limit > offset, end);
        return source[offset..limit];
    }
}
