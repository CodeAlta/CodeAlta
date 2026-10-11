using System.Text;
using System.Runtime.InteropServices;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Orchestration.Jobs;

namespace CodeAlta.Tests;

[TestClass]
public sealed class SessionRetentionSafetyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
    private const string SessionId = "retention-fixture";

    [TestMethod]
    public async Task MeaningfulActivity_InspectionDoesNotRefreshAge()
    {
        var work = Now.AddMonths(-2);
        using var stream = Journal(
            Raw("codealta.sessionHeader", work.AddDays(-1)),
            new AgentContentCompletedEvent(new("test"), SessionId, work, null, AgentContentKind.User, "prompt", null, "hello"),
            Raw("local.sessionSummary", Now), Raw("codealta.sessionState", Now));
        var activity = await SessionMeaningfulActivity.ReadAsync(stream, SessionId, work.AddDays(-1), Now, default);
        Assert.IsNull(activity.BlockedReason);
        Assert.AreEqual(work, activity.LastMeaningfulActivityUtc);
    }

    [TestMethod]
    public async Task MeaningfulActivity_OnlyProvenEmptyHeaderUsesCreation()
    {
        var creation = Now.AddYears(-1);
        using var empty = Journal(Raw("codealta.sessionHeader", creation));
        Assert.AreEqual(creation, (await SessionMeaningfulActivity.ReadAsync(empty, SessionId, creation, Now, default)).LastMeaningfulActivityUtc);
        using var unknown = new MemoryStream(Encoding.UTF8.GetBytes("{\"$type\":\"future-event\"}\n"));
        Assert.IsNotNull((await SessionMeaningfulActivity.ReadAsync(unknown, SessionId, creation, Now, default)).BlockedReason);
        using var noHeader = new MemoryStream();
        Assert.IsNotNull((await SessionMeaningfulActivity.ReadAsync(noHeader, SessionId, creation, Now, default)).BlockedReason);
    }

    [TestMethod]
    public async Task MeaningfulActivity_RejectsFutureAndIncompleteTail()
    {
        using var future = Journal(Raw("codealta.sessionHeader", Now.AddDays(1)));
        Assert.IsNotNull((await SessionMeaningfulActivity.ReadAsync(future, SessionId, Now, Now, default)).BlockedReason);
        using var tail = new MemoryStream(Encoding.UTF8.GetBytes(Raw("codealta.sessionHeader", Now.AddYears(-1)).ToJson()));
        Assert.IsNotNull((await SessionMeaningfulActivity.ReadAsync(tail, SessionId, Now.AddYears(-1), Now, default)).BlockedReason);
    }

    [TestMethod]
    public async Task MeaningfulActivity_ProtectsQueuedAndUnfinishedWork()
    {
        var creation = Now.AddYears(-1);
        var queued = Raw("codealta.sessionState", Now.AddMonths(-2)) with
        {
            Raw = System.Text.Json.JsonSerializer.SerializeToElement(new { queued_prompts = new[] { new { state = "queued" } } }),
        };
        using var queue = Journal(Raw("codealta.sessionHeader", creation), queued);
        Assert.AreEqual("queued_work", (await SessionMeaningfulActivity.ReadAsync(queue, SessionId, creation, Now, default)).BlockedReason);
        using var run = Journal(Raw("codealta.sessionHeader", creation), new AgentActivityEvent(new("test"), SessionId,
            Now.AddMonths(-2), new("unfinished"), AgentActivityKind.Turn, AgentActivityPhase.Started, "turn", null, null, null));
        Assert.AreEqual("unfinished_run", (await SessionMeaningfulActivity.ReadAsync(run, SessionId, creation, Now, default)).BlockedReason);
    }

    [TestMethod]
    public async Task JobAdmissionCannotCrossRetirementReservation()
    {
        await using var jobs = new SessionJobService(start: (_, _, _) => throw new AssertFailedException("Must not start a process."));
        using var lease = jobs.TryReserveRetirement(SessionId);
        Assert.IsNotNull(lease);
        Assert.AreEqual("session_retiring", jobs.Start(new SessionJobRequest
            { SessionId = SessionId, Command = "echo never", Folder = Path.GetTempPath() }).Status);
    }

    [TestMethod]
    public void Protection_UnknownRestorationAndAllSpacesProtect()
    {
        var protection = new SessionRetentionProtection();
        Assert.IsNull(protection.TryReserveRetirement(SessionId));
        Assert.IsTrue(protection.Publish("window", ["active-space", SessionId], ready: true));
        Assert.IsTrue(protection.Publish("external-work", [], ready: true));
        protection.SealParticipants(["window", "external-work"]);
        Assert.IsNull(protection.TryReserveRetirement(SessionId));
        Assert.IsTrue(protection.Publish("window", ["active-space"], ready: true));
        using var retirement = protection.TryReserveRetirement(SessionId);
        Assert.IsNotNull(retirement);
        // Opening/draft creation must get an accepted claim before making the tab usable.
        Assert.IsFalse(protection.Publish("window", [SessionId], ready: true));
    }

    [TestMethod]
    public void Protection_MissingOrDisconnectedParticipantFailsClosed()
    {
        var protection = new SessionRetentionProtection();
        protection.SealParticipants(["window", "external-work"]);
        Assert.IsTrue(protection.Publish("window", [], true));
        Assert.IsNull(protection.TryReserveRetirement(SessionId));
        Assert.IsTrue(protection.Publish("external-work", [], true));
        using (var claim = protection.TryReserveRetirement(SessionId)) Assert.IsNotNull(claim);
        protection.MarkUnknown("window");
        Assert.IsNull(protection.TryReserveRetirement(SessionId));
    }

    [TestMethod]
    public void Protection_NoncanonicalIdsCannotBypassRetirementClaim()
    {
        var protection = new SessionRetentionProtection();
        protection.SealParticipants(["window"]);
        protection.Publish("window", [], true);
        using var retirement = protection.TryReserveRetirement(SessionId);
        Assert.IsNotNull(retirement);
        Assert.ThrowsExactly<ArgumentException>(() => protection.Publish("window", [" " + SessionId + " "], true));
    }

    [TestMethod]
    public void Protection_ReleasingOldLeaseTwiceDoesNotReleaseNewRetirement()
    {
        var protection = new SessionRetentionProtection();
        protection.SealParticipants(["window"]);
        protection.Publish("window", [], true);
        var first = protection.TryReserveRetirement(SessionId)!;
        first.Dispose();
        using var second = protection.TryReserveRetirement(SessionId);
        Assert.IsNotNull(second);
        first.Dispose();
        Assert.IsFalse(protection.Publish("window", [SessionId], true));
    }

    [TestMethod]
    public void ExactJournalLease_BlocksMutationAndRename_AndDeletesOnlyClaimedFile()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Exact handle retirement is Windows-only; other platforms fail closed.");
        var root = Path.Combine(Path.GetTempPath(), "alta-retention-" + Guid.NewGuid().ToString("N"));
        var layout = new AgentRuntimePathLayout(root);
        var path = layout.GetSessionFilePath(SessionId, Now);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "fixture\n");
        var sentinel = Path.Combine(root, "config.toml");
        File.WriteAllText(sentinel, "protected");
        try
        {
            using (var lease = RetentionJournalLease.Open(layout, SessionId, Now))
            {
                Assert.ThrowsExactly<IOException>(() => File.WriteAllText(path, "new generation"));
                Assert.ThrowsExactly<IOException>(() => File.Move(path, path + ".old"));
                Assert.ThrowsExactly<IOException>(() => Directory.Move(Path.GetDirectoryName(path)!, Path.GetDirectoryName(path)! + "-moved"));
                lease.Delete();
            }
            Assert.IsFalse(File.Exists(path));
            Assert.AreEqual("protected", File.ReadAllText(sentinel));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public void ExactJournalLease_BlocksAncestorRenameBeforeOpeningDescendant()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Windows exact-file retirement.");
        var root = Path.Combine(Path.GetTempPath(), "alta-retention-construction-" + Guid.NewGuid().ToString("N"));
        var layout = new AgentRuntimePathLayout(root);
        var path = layout.GetSessionFilePath(SessionId, Now);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "owned");
        var observed = 0;
        try
        {
            using (var preexistingWriter = OpenReparseWriter(root))
            {
                Assert.IsFalse(preexistingWriter.IsInvalid, "Fixture must be permitted to open a reparse-capable directory writer.");
                Assert.ThrowsExactly<IOException>(() => RetentionJournalLease.Open(layout, SessionId, Now));
            }
            using var lease = RetentionJournalLease.Open(layout, SessionId, Now, directory =>
            {
                if (!directory.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return;
                observed++;
                Assert.ThrowsExactly<IOException>(() => Directory.Move(directory, directory + "-replacement"),
                    "An ancestor must already be immutable before the next descendant is opened.");
                using var writer = OpenReparseWriter(directory);
                Assert.IsTrue(writer.IsInvalid, "A reparse-capable writer cannot be admitted while constructing the descendant claim.");
                Assert.AreEqual(32, Marshal.GetLastWin32Error(), "Must be rejected by sharing, not unrelated ACL failure.");
            });
            Assert.IsTrue(observed >= 5);
            using var reader = new StreamReader(lease.Stream, Encoding.UTF8, leaveOpen: true);
            Assert.AreEqual("owned", reader.ReadToEnd());
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void ExactJournalLease_DisposeWithoutAuthorizationPreservesJournal()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Exact handle retirement is Windows-only; other platforms fail closed.");
        var root = Path.Combine(Path.GetTempPath(), "alta-retention-" + Guid.NewGuid().ToString("N"));
        var layout = new AgentRuntimePathLayout(root);
        var path = layout.GetSessionFilePath(SessionId, Now);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "fixture\n");
        try
        {
            using (RetentionJournalLease.Open(layout, SessionId, Now)) { }
            Assert.AreEqual("fixture\n", File.ReadAllText(path));
            Assert.ThrowsExactly<ArgumentException>(() => RetentionJournalLease.Open(layout, "../other", Now));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public void ExactJournalLease_RejectsLinkedSessionRootWithoutTouchingTarget()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Exact handle retirement is Windows-only; other platforms fail closed.");
        var root = Path.Combine(Path.GetTempPath(), "alta-retention-link-" + Guid.NewGuid().ToString("N"));
        var owned = Path.Combine(root, "owned");
        var outside = Path.Combine(root, "outside");
        var layout = new AgentRuntimePathLayout(owned);
        Directory.CreateDirectory(owned);
        Directory.CreateDirectory(outside);
        try
        {
            try { Directory.CreateSymbolicLink(layout.SessionsRootPath, outside); }
            catch (UnauthorizedAccessException) { Assert.Inconclusive("Symbolic-link creation is not permitted on this test host."); }
            var path = layout.GetSessionFilePath(SessionId, Now);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "outside sentinel");
            Assert.ThrowsExactly<IOException>(() => RetentionJournalLease.Open(layout, SessionId, Now));
            Assert.AreEqual("outside sentinel", File.ReadAllText(path));
        }
        finally
        {
            if (Directory.Exists(layout.SessionsRootPath)) Directory.Delete(layout.SessionsRootPath);
            Directory.Delete(root, true);
        }
    }

    private static AgentRawEvent Raw(string type, DateTimeOffset at)
        => new(new("test"), SessionId, at, type, System.Text.Json.JsonSerializer.SerializeToElement(new { }), null);

    private static MemoryStream Journal(params AgentEvent[] events)
        => new(Encoding.UTF8.GetBytes(string.Join('\n', events.Select(item => item.ToJson())) + "\n"));

    private static Microsoft.Win32.SafeHandles.SafeFileHandle OpenReparseWriter(string directory)
        => CreateFileW(directory, 0x40000000, 7, 0, 3, 0x02200000, 0);

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string path, uint access, uint share, nint security, uint creation, uint flags, nint template);
}
