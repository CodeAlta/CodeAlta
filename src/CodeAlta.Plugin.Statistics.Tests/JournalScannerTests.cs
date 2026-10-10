using CodeAlta.Plugin.Statistics.Journal;

namespace CodeAlta.Plugin.Statistics.Tests;

internal sealed class CollectingSink : IJournalRecordSink
{
    public List<JournalRecord> Records { get; } = [];

    public HashSet<ulong> Seen { get; } = [];

    public void OnRecord(JournalRecord record) => Records.Add(record);

    public bool IsPromptSeen(ulong idHash) => Seen.Contains(idHash);
}

[TestClass]
public sealed class JournalScannerTests
{
    private static readonly DateTimeOffset T0 = JournalBuilder.Time(0);

    private static (JournalScanResult Result, CollectingSink Sink) Scan(JournalBuilder builder, long offset = 0, JournalScanOptions? options = null, bool trailingNewline = true)
    {
        using var scanner = new JournalScanner(options);
        var sink = new CollectingSink();
        using var stream = builder.ToStream(trailingNewline);
        var result = scanner.Scan(stream, offset, null, sink, long.MaxValue, CancellationToken.None);
        return (result, sink);
    }

    [TestMethod]
    public void Scan_ParsesTheRecordsStatisticsNeed_AndPassesOverTheOthers()
    {
        var b = new JournalBuilder();
        b.Header(T0)
            .State(T0.AddSeconds(1))
            .LocalSnapshot(T0.AddSeconds(2), padding: 5000)
            .ModelChanged(T0.AddSeconds(3), "run-1", "codex", "gpt-6.1-sol")
            .User(T0.AddSeconds(4), "run-1", "hello there world")
            .Requested(T0.AddSeconds(5), "run-1", "t1", "read_file")
            .ToolStarted(T0.AddSeconds(5), "run-1", "t1", "read_file", "{\"path\":\"a.cs\"}")
            .ToolOutput(T0.AddSeconds(6), "run-1", "t1", new string('o', 20000))
            .ToolDone(T0.AddSeconds(7), "run-1", "t1", "read_file", readFiles: ["C:\\code\\a.cs"])
            .Usage(T0.AddSeconds(8), "run-1")
            .Idle(T0.AddSeconds(9), "run-1");

        var (result, sink) = Scan(b);

        Assert.AreEqual(11, result.Lines);
        Assert.AreEqual(0, result.MalformedLines);
        Assert.AreEqual(0, result.UnknownLines);
        Assert.IsTrue(result.ReachedEnd);
        Assert.AreEqual(b.ToBytes().Length, result.EndOffset);
        CollectionAssert.AreEqual(
            new[]
            {
                JournalRecordKind.Header, JournalRecordKind.State, JournalRecordKind.Touch, JournalRecordKind.ModelChanged, JournalRecordKind.UserContent,
                JournalRecordKind.Touch, JournalRecordKind.Tool, JournalRecordKind.Touch, JournalRecordKind.Tool, JournalRecordKind.Usage, JournalRecordKind.RunEnd,
            },
            sink.Records.Select(static record => record.Kind).ToArray());
        Assert.AreEqual(7, result.ParsedRecords);
        Assert.IsTrue(result.SkippedBytes > 25000, "the snapshot and the tool output are passed over, not parsed");

        var user = (UserContentRecord)sink.Records[4];
        Assert.AreEqual("run-1", user.RunId);
        Assert.AreEqual("codex", user.Provider);
        Assert.AreEqual(T0.AddSeconds(4), user.Timestamp);
        Assert.AreEqual(17, user.Chars);
        Assert.AreEqual(3, user.Words);

        var started = (ToolRecord)sink.Records[6];
        Assert.AreEqual(ToolPhase.Started, started.Phase);
        Assert.AreEqual("t1", started.ActivityId);
        Assert.AreEqual("read_file", started.Name);
        Assert.AreEqual("{\"path\":\"a.cs\"}".Length, started.ArgumentBytes);

        var completed = (ToolRecord)sink.Records[8];
        Assert.AreEqual(ToolPhase.Completed, completed.Phase);
        Assert.AreEqual(1, completed.FilesRead);
        Assert.IsTrue(completed.ResultBytes > 0);
        Assert.AreEqual(true, completed.Success);

        Assert.AreEqual(RunEndKind.Idle, ((RunEndRecord)sink.Records[10]).End);
    }

    [TestMethod]
    public void Scan_ResumesFromAnOffsetAtARecordBoundary()
    {
        var b = new JournalBuilder();
        b.Header(T0).User(T0.AddSeconds(1), "r", "one").Usage(T0.AddSeconds(2), "r").User(T0.AddSeconds(3), "r", "two").Idle(T0.AddSeconds(4), "r");

        var (first, firstSink) = Scan(b);
        var bytes = b.ToBytes();
        var half = new JournalBuilder();
        foreach (var line in b.Lines.Take(3))
        {
            half.Add(line);
        }

        var (partial, _) = Scan(half);
        var (rest, restSink) = Scan(b, partial.EndOffset);

        Assert.AreEqual(5, firstSink.Records.Count);
        Assert.AreEqual(half.ToBytes().Length, partial.EndOffset);
        Assert.AreEqual(2, restSink.Records.Count);
        Assert.AreEqual(bytes.Length, rest.EndOffset);
        Assert.AreEqual(first.EndOffset, rest.EndOffset);
    }

    [TestMethod]
    public void Scan_LeavesAHalfWrittenLastLineForLater()
    {
        var b = new JournalBuilder();
        b.Header(T0).User(T0.AddSeconds(1), "r", "one");
        var complete = b.ToBytes().Length;
        var all = b.ToBytes();
        var withPartial = all.Concat("{\"$type\":\"sessionUpdate\",\"kind\":\"UsageUpd"u8.ToArray()).ToArray();

        using var scanner = new JournalScanner();
        var sink = new CollectingSink();
        using var stream = new MemoryStream(withPartial);
        var result = scanner.Scan(stream, 0, null, sink, long.MaxValue, CancellationToken.None);

        Assert.AreEqual(2, sink.Records.Count);
        Assert.AreEqual(complete, result.EndOffset);
        Assert.IsTrue(result.PendingBytes > 0);
    }

    [TestMethod]
    public void Scan_DoesNotConsumeALastLineWithoutItsLineEnd()
    {
        var b = new JournalBuilder();
        b.Header(T0).User(T0.AddSeconds(1), "r", "one");

        var (result, sink) = Scan(b, trailingNewline: false);

        Assert.AreEqual(1, sink.Records.Count);
        Assert.AreEqual(b.Lines[0].Length + 1, result.EndOffset);
        Assert.IsTrue(result.PendingBytes > 0);
    }

    [TestMethod]
    public void Scan_CountsMalformedLines_AndGoesOn()
    {
        var b = new JournalBuilder();
        b.Header(T0)
            .Add("{\"$type\":\"sessionUpdate\",\"kind\":\"UsageUpdated\",\"usage\":{\"window\":{\"currentTokens\":}}}")
            .Add("not json at all")
            .Add("{\"$type\":\"activity\",\"kind\":\"ToolCall\",\"phase\":\"Started\",\"activityId\":\"x\"")
            .User(T0.AddSeconds(1), "r", "survivor");

        var (result, sink) = Scan(b);

        Assert.AreEqual(5, result.Lines);
        Assert.AreEqual(2, result.MalformedLines);
        Assert.AreEqual(1, result.UnknownLines);
        Assert.AreEqual(2, sink.Records.Count);
        Assert.IsInstanceOfType<UserContentRecord>(sink.Records[1]);
    }

    [TestMethod]
    public void Scan_ReadsLegacyRecords()
    {
        var b = new JournalBuilder();
        b.Header(T0, legacy: true, kind: "ProjectThread")
            .State(T0.AddSeconds(1), legacy: true)
            .Add($"{{\"$type\":\"activity\",\"kind\":\"Turn\",\"phase\":\"Started\",\"activityId\":\"turn-1\",{b.Envelope(T0.AddSeconds(2), "r")}")
            .Add($"{{\"$type\":\"sessionUpdate\",\"kind\":\"Shutdown\",{b.Envelope(T0.AddSeconds(3), null)}");

        var (result, sink) = Scan(b);

        Assert.AreEqual(0, result.MalformedLines);
        Assert.AreEqual(4, sink.Records.Count);
        var header = (HeaderRecord)sink.Records[0];
        Assert.IsTrue(header.IsLegacy);
        Assert.AreEqual("ProjectThread", header.SessionKind);
        Assert.IsInstanceOfType<StateRecord>(sink.Records[1]);
        Assert.AreEqual("r", sink.Records[2].RunId);
        Assert.IsNull(sink.Records[3].RunId);
    }

    [TestMethod]
    public void Scan_HeaderCarriesTheIdentityOfTheSession()
    {
        var b = new JournalBuilder("33333333-3333-3333-3333-333333333333", "claude-code");
        b.Header(T0, parent: "44444444-4444-4444-4444-444444444444", createdByKind: "agent", createdBySession: "44444444-4444-4444-4444-444444444444", title: "Fix the thing");

        var (_, sink) = Scan(b);

        var header = (HeaderRecord)sink.Records[0];
        Assert.AreEqual("ProjectSession", header.SessionKind);
        Assert.AreEqual("22222222-2222-2222-2222-222222222222", header.ProjectRef);
        Assert.AreEqual("44444444-4444-4444-4444-444444444444", header.ParentSessionId);
        Assert.AreEqual("agent", header.CreatedBy?.Kind);
        Assert.AreEqual("44444444-4444-4444-4444-444444444444", header.CreatedBy?.SourceSessionId);
        Assert.AreEqual("Fix the thing", header.Title);
        Assert.AreEqual("claude-code", header.ProviderKey);
        Assert.AreEqual("claude-code", header.Provider);
        Assert.AreEqual(T0, header.CreatedAt);
    }

    [TestMethod]
    public void Scan_ReadsTheUsageOfARequest()
    {
        var b = new JournalBuilder("33333333-3333-3333-3333-333333333333", "claude-code");
        b.Usage(T0, "r", model: "claude-opus-5-5", input: 2, output: 669, cacheRead: 14932, cacheWrite: 24624, cached: 14932, cost: 6.5957132, duration: 806888.0, window: 40227, limit: 1000000);

        var (_, sink) = Scan(b);

        var usage = (UsageRecord)sink.Records[0];
        Assert.AreEqual(40227, usage.WindowTokens);
        Assert.AreEqual(1000000, usage.WindowLimit);
        var op = usage.Operation!;
        Assert.AreEqual("claude-opus-5-5", op.Model);
        Assert.AreEqual(2, op.InputTokens);
        Assert.AreEqual(669, op.OutputTokens);
        Assert.AreEqual(14932, op.CacheReadTokens);
        Assert.AreEqual(24624, op.CacheWriteTokens);
        Assert.AreEqual(14932, op.CachedInputTokens);
        Assert.AreEqual(6.5957132, op.Cost);
        Assert.AreEqual(806888.0, op.DurationMs);
        Assert.IsNull(op.ReasoningTokens);
    }

    [TestMethod]
    public void Scan_ReadsToolDetails_ShellProgramAltaCommandDiffAndExtensions()
    {
        var b = new JournalBuilder();
        b.ToolDone(T0, "r", "t1", "shell_command", argsJson: "{\"command\":\"\\\"C:\\\\Program Files\\\\Git\\\\bin\\\\GIT.exe\\\" status --short\",\"workdir\":\"x\"}")
            .ToolDone(T0.AddSeconds(1), "r", "t2", "alta", argsJson: "{\"args\":[\"session\",\"create\",\"--project\",\"p\"],\"cwd\":null}")
            .ToolDone(T0.AddSeconds(2), "r", "t3", "apply_patch", modifiedFiles: ["C:\\code\\a.CS", "/home/x/b.md", "noext"], diff: "--- a/a.cs\n+++ b/a.cs\n@@ -1,2 +1,3 @@\n ctx\n-old\n+new\n+more\n\\ No newline")
            .ToolDone(T0.AddSeconds(3), "r", "t4", "Bash", argsJson: "{\"command\":\"cd x && dotnet build\"}")
            .ToolDone(T0.AddSeconds(4), "r", "t5", "codealta_skills_activate", argsJson: "{\"skillName\":\"ilspy\"}")
            .ToolDone(T0.AddSeconds(5), "r", "t6", "shell_command", phase: "Failed", argsJson: "{\"command\":\"\\\"quoted\\\" arg\"}");

        var (_, sink) = Scan(b);

        var shell = (ToolRecord)sink.Records[0];
        Assert.AreEqual("git", shell.ShellProgram);
        var alta = (ToolRecord)sink.Records[1];
        Assert.AreEqual("session create", alta.AltaCommand);
        var patch = (ToolRecord)sink.Records[2];
        Assert.AreEqual(2, patch.LinesAdded);
        Assert.AreEqual(1, patch.LinesRemoved);
        CollectionAssert.AreEqual(new[] { "cs", "md", string.Empty }, patch.ModifiedExtensions);
        Assert.AreEqual("cd", ((ToolRecord)sink.Records[3]).ShellProgram);
        Assert.AreEqual("ilspy", ((ToolRecord)sink.Records[4]).SkillName);
        var failed = (ToolRecord)sink.Records[5];
        Assert.AreEqual(ToolPhase.Failed, failed.Phase);
        Assert.AreEqual(false, failed.Success);
        Assert.AreEqual("quoted", failed.ShellProgram);
    }

    [TestMethod]
    public void Scan_ReadsPromptContent_AttachmentsAnswersAndSenders()
    {
        var b = new JournalBuilder();
        b.User(T0, "r", "answer to a question", ["file", "directory", "localImage", "localImage", "skill"], answer: true, sourceSession: "55555555-5555-5555-5555-555555555555");

        var (_, sink) = Scan(b);

        var user = (UserContentRecord)sink.Records[0];
        Assert.IsTrue(user.IsAnswer);
        Assert.AreEqual("55555555-5555-5555-5555-555555555555", user.SourceSessionId);
        Assert.AreEqual(1, user.Files);
        Assert.AreEqual(1, user.Directories);
        Assert.AreEqual(2, user.Images);
        Assert.AreEqual(1, user.Skills);
        Assert.AreEqual(4, user.Words);
    }

    [TestMethod]
    public void Scan_CountsCharactersLikeAString_ForEscapedAndNonAsciiText()
    {
        var text = "line one\nline \"two\"\t\u00e9\u4e2d\ud83d\ude00 end";
        var b = new JournalBuilder();
        b.User(T0, "r", text);

        var (_, sink) = Scan(b);

        var user = (UserContentRecord)sink.Records[0];
        Assert.AreEqual(text.Length, user.Chars);
        Assert.AreEqual(6, user.Words);
    }

    [TestMethod]
    public void Scan_TakesOnlyTheProvenanceEntriesItHasNotSeen()
    {
        var b = new JournalBuilder();
        var entries = new[]
        {
            JournalBuilder.Provenance("prompt-one", "send", T0, runId: "r1", submittedByKind: "agent", sourceSession: "s1"),
            JournalBuilder.Provenance("prompt-two", "parent-notify", T0.AddSeconds(5), queued: true, submittedByKind: "agent"),
            JournalBuilder.Provenance("prompt-three", "steer", T0.AddSeconds(9)),
        };
        b.State(T0, provenance: entries, queued: 2);

        using var scanner = new JournalScanner();
        var sink = new CollectingSink();
        sink.Seen.Add(JournalHash.Compute("prompt-one"u8));
        using var stream = b.ToStream();
        scanner.Scan(stream, 0, null, sink, long.MaxValue, CancellationToken.None);

        var state = (StateRecord)sink.Records[0];
        Assert.AreEqual(1, state.ProvenancePassedOver);
        Assert.AreEqual(2, state.Provenance.Count);
        Assert.AreEqual("parent-notify", state.Provenance[0].DispatchKind);
        Assert.IsTrue(state.Provenance[0].Queued);
        Assert.AreEqual("agent", state.Provenance[0].SubmittedBy?.Kind);
        Assert.AreEqual(T0.AddSeconds(5), state.Provenance[0].CreatedAt);
        Assert.AreEqual("steer", state.Provenance[1].DispatchKind);
        Assert.IsNull(state.Provenance[1].SubmittedBy);
        Assert.AreEqual("gpt-6.1-sol", state.ModelId);
        Assert.AreEqual("Low", state.ReasoningEffort);
    }

    [TestMethod]
    public void Scan_ReadsTheEnvelopeFromTheEnd_WhenTheTimeGoesBackwards()
    {
        var b = new JournalBuilder();
        b.Usage(T0.AddSeconds(10), "r").Usage(T0.AddSeconds(5), "r");

        var (_, sink) = Scan(b);

        Assert.AreEqual(T0.AddSeconds(10), sink.Records[0].Timestamp);
        Assert.AreEqual(T0.AddSeconds(5), sink.Records[1].Timestamp);
    }

    [TestMethod]
    public void Scan_PassesOverAHugeLineWithoutBufferingIt()
    {
        // A tool output of about 24 MB, a state snapshot of the same size and a needed record just over the bound.
        var huge = new string('x', 24 * 1024 * 1024);
        var b = new JournalBuilder();
        b.Header(T0)
            .ToolOutput(T0.AddSeconds(1), "r", "t1", huge)
            .LocalSnapshot(T0.AddSeconds(2), padding: huge.Length)
            .ToolDone(T0.AddSeconds(3), "r", "t1", "read_file", resultText: huge, readFiles: ["a.cs"])
            .User(T0.AddSeconds(4), "r", "after the huge lines");

        var options = new JournalScanOptions { MaxParsedRecordBytes = 1024 * 1024 };
        var (result, sink) = Scan(b, options: options);

        Assert.AreEqual(5, sink.Records.Count);
        Assert.AreEqual(1, result.OversizeRecords);
        Assert.AreEqual(0, result.MalformedLines);
        var tool = (ToolRecord)sink.Records[3];
        Assert.IsTrue(tool.Oversize);
        Assert.AreEqual(T0.AddSeconds(3), tool.Timestamp);
        Assert.AreEqual("r", tool.RunId);
        Assert.AreEqual("t1", tool.ActivityId);
        Assert.AreEqual(1, tool.FilesRead, "the start of an oversize record is parsed: the readFiles come before the result");
        Assert.IsTrue(tool.ResultBytes > huge.Length, "the size of the line stands for the size of the result");
        Assert.AreEqual(T0.AddSeconds(4), sink.Records[4].Timestamp);
        Assert.AreEqual(b.ToBytes().Length, result.EndOffset);
    }

    [TestMethod]
    public void Scan_AHugeUserPromptIsCountedBySize()
    {
        var huge = new string('y', 3 * 1024 * 1024);
        var b = new JournalBuilder();
        b.User(T0, "r", huge);

        var (result, sink) = Scan(b, options: new JournalScanOptions { MaxParsedRecordBytes = 512 * 1024 });

        var user = (UserContentRecord)sink.Records[0];
        Assert.IsTrue(user.Oversize);
        Assert.AreEqual(1, result.OversizeRecords);
        Assert.IsTrue(user.Chars >= huge.Length);
        Assert.AreEqual(T0, user.Timestamp);
    }

    [TestMethod]
    public void Scan_StopsAtALineEndAfterTheByteLimit_AndResumesFromThere()
    {
        var b = new JournalBuilder();
        for (var index = 0; index < 20; index++)
        {
            b.Usage(T0.AddSeconds(index), "r");
        }

        using var scanner = new JournalScanner();
        var all = 0;
        long offset = 0;
        var scans = 0;
        using var stream = b.ToStream();
        while (true)
        {
            var sink = new CollectingSink();
            var result = scanner.Scan(stream, offset, null, sink, 1500, CancellationToken.None);
            all += sink.Records.Count;
            offset = result.EndOffset;
            scans++;
            if (result.ReachedEnd)
            {
                break;
            }
        }

        Assert.AreEqual(20, all);
        Assert.IsTrue(scans > 3);
        Assert.AreEqual(b.ToBytes().Length, offset);
    }

    [TestMethod]
    public void Scan_DetectsAFileWhoseFirstLineChanged()
    {
        var b = new JournalBuilder();
        b.Header(T0).User(T0.AddSeconds(1), "r");
        using var scanner = new JournalScanner();
        using var original = b.ToStream();
        var mark = JournalScanner.ReadFirstLine(original);
        Assert.IsNotNull(mark);

        // A header prepended to an older file: the first line is not the one that was read.
        var rewritten = new JournalBuilder();
        rewritten.Header(T0.AddSeconds(-1), title: "Another").Add(b.Lines[0]).Add(b.Lines[1]);
        using var stream = rewritten.ToStream();
        var result = scanner.Scan(stream, original.Length, mark, new CollectingSink(), long.MaxValue, CancellationToken.None);

        Assert.IsTrue(result.RewriteDetected);
        Assert.AreEqual(original.Length, result.EndOffset);

        var again = scanner.Scan(original, 0, mark, new CollectingSink(), long.MaxValue, CancellationToken.None);
        Assert.IsFalse(again.RewriteDetected);
        Assert.AreEqual(mark, again.FirstLine);
    }

    [TestMethod]
    public void Scan_DetectsAFileShorterThanTheOffset()
    {
        var b = new JournalBuilder();
        b.Header(T0);
        using var scanner = new JournalScanner();
        using var stream = b.ToStream();

        var result = scanner.Scan(stream, stream.Length + 100, null, new CollectingSink(), long.MaxValue, CancellationToken.None);

        Assert.IsTrue(result.RewriteDetected);
    }

    [TestMethod]
    public void ReadFirstLine_IsNullUntilTheFirstLineIsComplete()
    {
        using var partial = new MemoryStream("{\"$type\":\"raw\""u8.ToArray());
        Assert.IsNull(JournalScanner.ReadFirstLine(partial));

        using var complete = new MemoryStream("{\"$type\":\"raw\"}\nrest"u8.ToArray());
        var mark = JournalScanner.ReadFirstLine(complete);
        Assert.AreEqual(15, mark!.Value.Length);
    }

    [TestMethod]
    public void Scan_SkipsAByteOrderMark_AndTrimsCarriageReturns()
    {
        var b = new JournalBuilder();
        b.Header(T0).User(T0.AddSeconds(1), "r", "crlf");
        var text = "\uFEFF" + string.Join("\r\n", b.Lines) + "\r\n";
        using var stream = new MemoryStream(new System.Text.UTF8Encoding(false).GetBytes(text));
        using var scanner = new JournalScanner();
        var sink = new CollectingSink();

        var result = scanner.Scan(stream, 0, null, sink, long.MaxValue, CancellationToken.None);

        Assert.AreEqual(2, sink.Records.Count);
        Assert.AreEqual(0, result.MalformedLines);
        Assert.AreEqual(stream.Length, result.EndOffset);
    }

    [TestMethod]
    public void Scan_StopsWhenCanceled_AtALineEnd()
    {
        var b = new JournalBuilder();
        for (var index = 0; index < 600; index++)
        {
            b.Usage(T0.AddSeconds(index), "r");
        }

        using var scanner = new JournalScanner();
        using var stream = b.ToStream();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var sink = new CollectingSink();

        var result = scanner.Scan(stream, 0, null, sink, long.MaxValue, cancellation.Token);

        Assert.IsTrue(result.Canceled);
        Assert.IsFalse(result.ReachedEnd);
        Assert.IsTrue(sink.Records.Count is > 0 and < 600);
        var consumed = b.Lines.Take(sink.Records.Count).Sum(static line => line.Length + 1L);
        Assert.AreEqual(consumed, result.EndOffset);
    }
}

[TestClass]
public sealed class JournalTimestampTests
{
    [TestMethod]
    [DataRow("2026-10-09T10:11:12.1234567+00:00", "2026-10-09T10:11:12.1234567Z")]
    [DataRow("2026-10-09T10:11:12.123456+00:00", "2026-10-09T10:11:12.1234560Z")]
    [DataRow("2026-10-09T10:11:12.5+00:00", "2026-10-09T10:11:12.5000000Z")]
    [DataRow("2026-10-09T10:11:12+00:00", "2026-10-09T10:11:12.0000000Z")]
    [DataRow("2026-10-09T10:11:12.1234567Z", "2026-10-09T10:11:12.1234567Z")]
    [DataRow("2026-10-09T12:11:12.25+02:00", "2026-10-09T10:11:12.2500000Z")]
    [DataRow("2026-10-09T05:41:12-04:30", "2026-10-09T10:11:12.0000000Z")]
    [DataRow("2026-10-09T23:59:59.99999999+00:00", "2026-10-09T23:59:59.9999999Z")]
    public void TryParseTimestamp_ReadsEveryFormOfTheJournal(string text, string expectedUtc)
    {
        Assert.IsTrue(JournalEnvelope.TryParseTimestamp(System.Text.Encoding.UTF8.GetBytes(text), out var value));
        Assert.AreEqual(DateTimeOffset.Parse(expectedUtc, System.Globalization.CultureInfo.InvariantCulture), value);
        Assert.AreEqual(TimeSpan.Zero, value.Offset);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("2026-10-09")]
    [DataRow("2026-13-09T10:11:12+00:00")]
    [DataRow("2026-10-32T10:11:12+00:00")]
    [DataRow("2026-10-09T25:11:12+00:00")]
    [DataRow("2026-10-09T10:11:12")]
    [DataRow("2026-10-09T10:11:12.+00:00")]
    [DataRow("2026-10-09T10:11:12+0000")]
    [DataRow("2026-10-09 10:11:12+00:00")]
    // A time no calendar has, written by a damaged line: it is refused, never an exception that would skip the whole session.
    [DataRow("0000-01-01T00:00:00+00:00")]
    [DataRow("0000-06-15T10:11:12Z")]
    [DataRow("0001-01-01T00:00:00+01:00")]
    [DataRow("9999-12-31T23:59:59-01:00")]
    public void TryParseTimestamp_RefusesWhatIsNotATime(string text)
    {
        Assert.IsFalse(JournalEnvelope.TryParseTimestamp(System.Text.Encoding.UTF8.GetBytes(text), out _));
    }
}
