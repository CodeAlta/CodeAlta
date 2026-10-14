using System.Text.Json.Nodes;
using CodeAlta.Agent.Claude;

namespace CodeAlta.Tests;

/// <summary>
/// The registry of live sessions of Claude Code (<c>sessions/&lt;pid&gt;.json</c>), read to tell whether another Claude
/// process holds a conversation, with entries written as Claude Code 2.1.296 writes them and a scripted view of the
/// processes of the system.
/// </summary>
[TestClass]
public sealed class ClaudeCodeLiveSessionsTests
{
    private const string Conversation = "0a1b2c3d-4e5f-6789-abcd-ef0123456789";
    private const string Domain = "win32:test-host";
    private static readonly DateTime Started = new(2026, 10, 14, 9, 30, 15, 123, DateTimeKind.Local);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "CodeAlta-claude-live-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private readonly FakeProbe _probe = new();

    private string Sessions => Path.Combine(_root, "sessions");

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
        else if (File.Exists(_root))
        {
            File.Delete(_root);
        }
    }

    [TestMethod]
    public void LiveEntry_HoldsTheConversation_AndSaysWhere()
    {
        _probe.Run(4100, Started);
        WriteEntry(4100, Entry(4100, entrypoint: "claude-vscode", token: Started.ToFileTimeUtc().ToString()));

        var hold = CreateReader().FindHolder(Conversation);

        Assert.AreEqual(ClaudeCodeSessionHoldStatus.Held, hold.Status);
        var holder = hold.Holder!;
        Assert.AreEqual(4100, holder.Pid);
        Assert.AreEqual("claude-vscode", holder.Entrypoint);
        Assert.AreEqual("interactive", holder.Kind);
        Assert.AreEqual("idle", holder.Status);
        Assert.AreEqual(@"C:\work\project", holder.WorkingDirectory);
        Assert.AreEqual("my-session", holder.Name);
        Assert.AreEqual(ClaudeCodeHolderOrigin.VsCode, holder.Origin);
        Assert.AreEqual("VS Code", holder.OriginText);
    }

    [TestMethod]
    [DataRow("cli", (int)ClaudeCodeHolderOrigin.Terminal, "a terminal")]
    [DataRow("sdk-cli", (int)ClaudeCodeHolderOrigin.OtherApplication, "another application (sdk-cli)")]
    [DataRow(null, (int)ClaudeCodeHolderOrigin.OtherApplication, "another application")]
    public void Origin_IsNamedFromTheEntrypoint(string? entrypoint, int origin, string text)
    {
        _probe.Run(4100, Started);
        WriteEntry(4100, Entry(4100, entrypoint: entrypoint, token: Started.ToFileTimeUtc().ToString()));

        var holder = CreateReader().FindHolder(Conversation).Holder!;

        Assert.AreEqual((ClaudeCodeHolderOrigin)origin, holder.Origin);
        Assert.AreEqual(text, holder.OriginText);
    }

    [TestMethod]
    public void OtherConversation_IsNotHeld()
    {
        _probe.Run(4100, Started);
        WriteEntry(4100, Entry(4100, sessionId: "ffffffff-0000-0000-0000-000000000000", token: Started.ToFileTimeUtc().ToString()));

        Assert.AreEqual(ClaudeCodeSessionHoldStatus.None, CreateReader().FindHolder(Conversation).Status);
    }

    [TestMethod]
    public void EntryOfAProcessThatIsGone_IsStale()
    {
        // A killed process leaves its entry behind.
        WriteEntry(4100, Entry(4100, token: Started.ToFileTimeUtc().ToString()));

        Assert.AreEqual(ClaudeCodeSessionHoldStatus.None, CreateReader().FindHolder(Conversation).Status);
    }

    [TestMethod]
    public void IdentifierGivenToAnotherProcess_IsNotTheHolder()
    {
        // The process that wrote the entry is gone, and another one started later with its identifier.
        _probe.Run(4100, Started.AddMinutes(5));
        WriteEntry(4100, Entry(4100, token: Started.ToFileTimeUtc().ToString()));

        Assert.AreEqual(ClaudeCodeSessionHoldStatus.None, CreateReader().FindHolder(Conversation).Status);
    }

    [TestMethod]
    public void ProcessOfCodeAlta_IsNotAnotherHolder()
    {
        _probe.Run(4100, Started);
        WriteEntry(4100, Entry(4100, entrypoint: "sdk-cli", token: Started.ToFileTimeUtc().ToString()));
        var own = new ClaudeCodeOwnProcesses(_probe);
        var reader = CreateReader(own);

        using (own.Track(4100))
        {
            Assert.AreEqual(ClaudeCodeSessionHoldStatus.None, reader.FindHolder(Conversation).Status);
            CollectionAssert.AreEqual(new[] { 4100 }, own.Snapshot().ToArray());
        }

        // Once its connection closed, a process that still runs with the entry is another one's.
        Assert.AreEqual(ClaudeCodeSessionHoldStatus.Held, reader.FindHolder(Conversation).Status);
        Assert.AreEqual(0, own.Snapshot().Count);
    }

    [TestMethod]
    public void ProcessOfCodeAlta_WhoseIdentifierWasReused_IsAnotherHolder()
    {
        _probe.Run(4100, Started);
        var own = new ClaudeCodeOwnProcesses(_probe);
        using var registration = own.Track(4100);

        // CodeAlta's process exited without its connection being closed yet, and another CLI got its identifier.
        _probe.Run(4100, Started.AddMinutes(1));
        WriteEntry(4100, Entry(4100, token: Started.AddMinutes(1).ToFileTimeUtc().ToString()));

        Assert.AreEqual(ClaudeCodeSessionHoldStatus.Held, CreateReader(own).FindHolder(Conversation).Status);
    }

    [TestMethod]
    public void OwnProcesses_RecordNothingWithoutAProcess()
    {
        var own = new ClaudeCodeOwnProcesses(_probe);

        Assert.IsNull(own.Track(null));
        Assert.IsNull(own.Track(0));
        Assert.AreEqual(0, own.Snapshot().Count);
    }

    [TestMethod]
    public void EntryOfAnotherPidDomain_IsNotOfThisSystem()
    {
        _probe.Run(4100, Started);
        WriteEntry(4100, Entry(4100, token: Started.ToFileTimeUtc().ToString(), pidDomain: "linux:abcdef:pid:[4026531836]"));

        Assert.AreEqual(ClaudeCodeSessionHoldStatus.None, CreateReader().FindHolder(Conversation).Status);
    }

    [TestMethod]
    public void EntryWithoutPidDomain_IsOfThisSystem()
    {
        _probe.Run(4100, Started);
        WriteEntry(4100, Entry(4100, token: Started.ToFileTimeUtc().ToString(), pidDomain: null));

        Assert.AreEqual(ClaudeCodeSessionHoldStatus.Held, CreateReader().FindHolder(Conversation).Status);
    }

    [TestMethod]
    public void PidDomainOfThisSystemUnknown_IsUnknown()
    {
        _probe.PidDomain = null;
        _probe.Run(4100, Started);
        WriteEntry(4100, Entry(4100, token: Started.ToFileTimeUtc().ToString()));

        var hold = CreateReader().FindHolder(Conversation);

        Assert.AreEqual(ClaudeCodeSessionHoldStatus.Unknown, hold.Status);
        StringAssert.Contains(hold.Reason, Domain);
    }

    [TestMethod]
    public void TicksInsteadOfFileTime_AreComparedAsTicks()
    {
        // A fallback of the CLI on Windows writes .NET ticks, under procStartFt, which wins over procStart.
        _probe.Run(4100, Started);
        var entry = Entry(4100, token: "134000000000000000");
        entry["procStartFt"] = Started.Ticks.ToString();
        WriteEntry(4100, entry);

        Assert.AreEqual(ClaudeCodeSessionHoldStatus.Held, CreateReader().FindHolder(Conversation).Status);

        _probe.Run(4100, Started.AddSeconds(1));
        Assert.AreEqual(ClaudeCodeSessionHoldStatus.None, CreateReader().FindHolder(Conversation).Status);
    }

    [TestMethod]
    public void TokenOfLinuxOrMacOs_IsComparedAsText()
    {
        _probe.Run(4100, token: "Wed Oct 14 09:30:15 2026");
        WriteEntry(4100, Entry(4100, token: "Wed Oct 14 09:30:15 2026"));
        Assert.AreEqual(ClaudeCodeSessionHoldStatus.Held, CreateReader().FindHolder(Conversation).Status);

        _probe.Run(4100, token: "Wed Oct 14 09:31:00 2026");
        Assert.AreEqual(ClaudeCodeSessionHoldStatus.None, CreateReader().FindHolder(Conversation).Status);
    }

    [TestMethod]
    public void EntryWithoutToken_NeedsOnlyItsProcessToRun()
    {
        _probe.Run(4100, Started.AddDays(-1));
        WriteEntry(4100, Entry(4100, token: null));

        Assert.AreEqual(ClaudeCodeSessionHoldStatus.Held, CreateReader().FindHolder(Conversation).Status);
    }

    [TestMethod]
    public void StartThatCannotBeRead_DoesNotMakeTheEntryStale()
    {
        // A process of another user or an elevated one: it runs, its start time is not given.
        _probe.Run(4100, startTime: null);
        WriteEntry(4100, Entry(4100, token: Started.ToFileTimeUtc().ToString()));

        Assert.AreEqual(ClaudeCodeSessionHoldStatus.Held, CreateReader().FindHolder(Conversation).Status);
    }

    [TestMethod]
    public void UnreadableFreshEntry_OfALiveProcess_IsHeldByAnUnknownProcess()
    {
        // A process that is writing its entry: the conversation it holds is not known.
        _probe.Run(4100, Started);
        WriteRaw(4100, """{"pid":4100,"sessionId":"0a1b""", age: TimeSpan.FromSeconds(5));

        var hold = CreateReader().FindHolder(Conversation);

        Assert.AreEqual(ClaudeCodeSessionHoldStatus.Held, hold.Status);
        Assert.AreEqual(4100, hold.Holder!.Pid);
        Assert.IsFalse(hold.Holder.IsDescribed);
        Assert.AreEqual(ClaudeCodeHolderOrigin.Unknown, hold.Holder.Origin);
        Assert.AreEqual("another Claude process", hold.Holder.OriginText);
    }

    [TestMethod]
    public void UnreadableOldEntry_IsNotAHolder()
    {
        _probe.Run(4100, Started);
        WriteRaw(4100, "not json", age: TimeSpan.FromMinutes(1));

        Assert.AreEqual(ClaudeCodeSessionHoldStatus.None, CreateReader().FindHolder(Conversation).Status);
    }

    [TestMethod]
    public void UnreadableFreshEntry_OfAProcessThatIsGone_IsNotAHolder()
    {
        WriteRaw(4100, "not json", age: TimeSpan.Zero);

        Assert.AreEqual(ClaudeCodeSessionHoldStatus.None, CreateReader().FindHolder(Conversation).Status);
    }

    [TestMethod]
    public void EntryLargerThanTheLimit_IsUnreadable()
    {
        _probe.Run(4100, Started);
        var entry = Entry(4100, token: Started.ToFileTimeUtc().ToString());
        entry["padding"] = new string('x', ClaudeCodeLiveSessions.MaxEntryBytes);
        WriteRaw(4100, entry.ToJsonString(), age: TimeSpan.FromSeconds(1));

        var hold = CreateReader().FindHolder(Conversation);

        Assert.AreEqual(ClaudeCodeSessionHoldStatus.Held, hold.Status);
        Assert.IsFalse(hold.Holder!.IsDescribed, "A fresh entry too large to read counts as one being written.");
    }

    [TestMethod]
    [DataRow("""[1, 2, 3]""")]
    [DataRow("""{"pid":4100,"sessionId":42}""")]
    [DataRow("""{"pid":4100,"sessionId":"0a1b2c3d-4e5f-6789-abcd-ef0123456789","procStart":{"ft":1}}""")]
    [DataRow("""{"pid":4100,"sessionId":"0a1b2c3d-4e5f-6789-abcd-ef0123456789","pidDomain":7}""")]
    public void EntryOfAnUnknownShape_IsUnknown(string json)
    {
        _probe.Run(4100, Started);
        WriteRaw(4100, json, age: TimeSpan.FromMinutes(5));

        var hold = CreateReader().FindHolder(Conversation);

        Assert.AreEqual(ClaudeCodeSessionHoldStatus.Unknown, hold.Status);
        StringAssert.Contains(hold.Reason, "4100");
    }

    [TestMethod]
    public void EntryOfAnUnknownShape_OfAProcessThatIsGone_IsIgnored()
    {
        WriteRaw(4100, """{"pid":4100,"sessionId":42}""", age: TimeSpan.FromMinutes(5));

        Assert.AreEqual(ClaudeCodeSessionHoldStatus.None, CreateReader().FindHolder(Conversation).Status);
    }

    [TestMethod]
    public void RegistryThatIsNotAFolder_IsUnknown()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Sessions, "a file");

        var hold = CreateReader().FindHolder(Conversation);

        Assert.AreEqual(ClaudeCodeSessionHoldStatus.Unknown, hold.Status);
        StringAssert.Contains(hold.Reason, Sessions);
    }

    [TestMethod]
    public void MissingRegistry_HoldsNothing()
    {
        Assert.AreEqual(ClaudeCodeSessionHoldStatus.None, CreateReader().FindHolder(Conversation).Status);
    }

    [TestMethod]
    public void SeveralEntriesForTheConversation_TheLiveOneLastUpdatedHoldsIt()
    {
        _probe.Run(4100, Started);
        _probe.Run(4200, Started);
        WriteEntry(4000, Entry(4000, entrypoint: "cli", token: Started.ToFileTimeUtc().ToString(), updatedAt: 3_000));
        WriteEntry(4100, Entry(4100, entrypoint: "cli", token: Started.ToFileTimeUtc().ToString(), updatedAt: 1_000));
        WriteEntry(4200, Entry(4200, entrypoint: "claude-vscode", token: Started.ToFileTimeUtc().ToString(), updatedAt: 2_000));
        _probe.Run(4300, Started);
        WriteRaw(4300, "{", age: TimeSpan.Zero);

        var hold = CreateReader().FindHolder(Conversation);

        Assert.AreEqual(ClaudeCodeSessionHoldStatus.Held, hold.Status);
        Assert.AreEqual(4200, hold.Holder!.Pid, "A described holder wins over an unreadable entry, and the last updated over an older one.");
    }

    [TestMethod]
    public void EntryHeldWins_OverAnEntryOfAnUnknownShape()
    {
        _probe.Run(4100, Started);
        _probe.Run(4200, Started);
        WriteRaw(4100, """{"sessionId":42}""", age: TimeSpan.FromMinutes(5));
        WriteEntry(4200, Entry(4200, token: Started.ToFileTimeUtc().ToString()));

        Assert.AreEqual(4200, CreateReader().FindHolder(Conversation).Holder!.Pid);
    }

    [TestMethod]
    [DataRow("4100.0123456789abcdef.key")]
    [DataRow("04100.json")]
    [DataRow("abc.json")]
    [DataRow("1.json")]
    [DataRow("4100.json.tmp")]
    public void FilesThatAreNotEntries_AreIgnored(string name)
    {
        _probe.Run(4100, Started);
        _probe.Run(1, Started);
        Directory.CreateDirectory(Sessions);
        File.WriteAllText(Path.Combine(Sessions, name), Entry(4100, token: Started.ToFileTimeUtc().ToString()).ToJsonString());

        Assert.AreEqual(ClaudeCodeSessionHoldStatus.None, CreateReader().FindHolder(Conversation).Status);
    }

    [TestMethod]
    public void Entries_AreNeitherWrittenNorDeleted()
    {
        WriteEntry(4100, Entry(4100, token: Started.ToFileTimeUtc().ToString()));
        WriteRaw(4200, "{", age: TimeSpan.FromHours(1));
        var before = Directory.GetFiles(Sessions).Select(static file => (file, File.GetLastWriteTimeUtc(file))).ToArray();

        _ = CreateReader().FindHolder(Conversation);

        CollectionAssert.AreEqual(before, Directory.GetFiles(Sessions).Select(static file => (file, File.GetLastWriteTimeUtc(file))).ToArray());
    }

    [TestMethod]
    public void StartToken_OfFileTimeOrTicks()
    {
        var start = new ClaudeCodeProcessStart(Started, null);

        Assert.IsTrue(start.Matches(Started.ToFileTimeUtc().ToString()));
        Assert.IsTrue(start.Matches(Started.Ticks.ToString()));
        Assert.IsFalse(start.Matches((Started.ToFileTimeUtc() + 1).ToString()));
        Assert.IsFalse(start.Matches("not a number"));
        Assert.IsFalse(new ClaudeCodeProcessStart(null, null).Matches("1"));
        Assert.IsTrue(new ClaudeCodeProcessStart(null, "123456").Matches(" 123456\n"));
    }

    [TestMethod]
    public void LinuxStat_GivesField22_AfterTheCommandName()
    {
        // The name of the command can hold spaces and parentheses.
        const string stat = "4100 (claude (x) y) S 1 4100 4100 0 -1 4194560 2000 0 0 0 10 5 0 0 20 0 12 0 987654 1234567 890 18446744073709551615";

        Assert.AreEqual("987654", ClaudeCodeProcessProbe.ParseLinuxStat(stat));
        Assert.IsNull(ClaudeCodeProcessProbe.ParseLinuxStat("4100 (claude) S 1"));
        Assert.IsNull(ClaudeCodeProcessProbe.ParseLinuxStat("garbage"));
    }

    [TestMethod]
    public void ProbeOfThisSystem_SeesThisProcess()
    {
        var probe = ClaudeCodeProcessProbe.Instance;

        Assert.IsTrue(probe.IsRunning(Environment.ProcessId));
        Assert.IsFalse(probe.IsRunning(1));
        var start = probe.GetStart(Environment.ProcessId);
        Assert.IsNotNull(start);
        if (OperatingSystem.IsWindows())
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            Assert.IsTrue(start.Matches(process.StartTime.ToFileTimeUtc().ToString()));
            StringAssert.StartsWith(probe.PidDomain, "win32:");
            Assert.AreEqual(probe.PidDomain, probe.PidDomain!.ToLowerInvariant());
        }
    }

    private ClaudeCodeLiveSessions CreateReader(ClaudeCodeOwnProcesses? own = null)
        => new(_root, own ?? new ClaudeCodeOwnProcesses(_probe), _probe, _time);

    // An entry as Claude Code 2.1.296 writes it on Windows.
    private static JsonObject Entry(int pid, string? sessionId = Conversation, string? entrypoint = "cli", string? token = null,
        string? pidDomain = Domain, long updatedAt = 1_791_616_422_304)
    {
        var entry = new JsonObject
        {
            ["pid"] = pid,
            ["sessionId"] = sessionId,
            ["cwd"] = @"C:\work\project",
            ["startedAt"] = 1_791_616_422_304,
            ["version"] = "2.1.296",
            ["kind"] = "interactive",
            ["entrypoint"] = entrypoint,
            ["name"] = "my-session",
            ["updatedAt"] = updatedAt,
            ["status"] = "idle",
        };
        if (token is not null)
        {
            entry["procStart"] = token;
        }

        if (pidDomain is not null)
        {
            entry["pidDomain"] = pidDomain;
        }

        return entry;
    }

    private void WriteEntry(int pid, JsonObject entry) => WriteRaw(pid, entry.ToJsonString(), TimeSpan.FromMinutes(10));

    private void WriteRaw(int pid, string content, TimeSpan age)
    {
        Directory.CreateDirectory(Sessions);
        var file = Path.Combine(Sessions, $"{pid}.json");
        File.WriteAllText(file, content);
        File.SetLastWriteTimeUtc(file, _time.GetUtcNow().UtcDateTime - age);
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeProbe : IClaudeCodeProcessProbe
    {
        private readonly Dictionary<int, ClaudeCodeProcessStart?> _running = [];

        public string? PidDomain { get; set; } = Domain;

        public void Run(int pid, DateTime? startTime = null, string? token = null)
            => _running[pid] = startTime is null && token is null ? null : new ClaudeCodeProcessStart(startTime, token);

        public bool IsRunning(int pid) => _running.ContainsKey(pid);

        public ClaudeCodeProcessStart? GetStart(int pid) => _running.GetValueOrDefault(pid);
    }
}
