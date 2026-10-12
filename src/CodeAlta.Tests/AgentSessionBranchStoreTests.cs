using System.Text;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Tests;

/// <summary>The turns of a journal, and the fork and the rewind of a session at one of them.</summary>
[TestClass]
public sealed class AgentSessionBranchStoreTests
{
    private const string Source = "0199a000-0000-7000-8000-000000000001";
    private const string Target = "0199a000-0000-7000-8000-000000000002";
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-04-06T10:00:00+00:00");

    [TestMethod]
    public async Task ReadTurns_NamesEachRun_WithItsPromptAndWhatItDid()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(temp.Path));
        var session = await CreateSessionAsync(store, Source).ConfigureAwait(false);
        await AppendTurnAsync(store, session, "run-1", "First prompt", tools: 2).ConfigureAwait(false);
        await AppendTurnAsync(store, session, "run-2", "Second \"quoted\" prompt\nwith two lines \u00e9\U0001F600", steer: "and one more thing").ConfigureAwait(false);
        await AppendTurnAsync(store, session, "run-3", "Third prompt", end: TurnEnd.Error, sourceSessionId: "agent-session").ConfigureAwait(false);
        await AppendTurnAsync(store, session, "run-4", "Fourth prompt", end: TurnEnd.None).ConfigureAwait(false);

        var turns = await store.ReadTurnsAsync(Source).ConfigureAwait(false);

        Assert.AreEqual(4, turns.Turns.Count);
        CollectionAssert.AreEqual(new[] { 1, 2, 3, 4 }, turns.Turns.Select(static turn => turn.Number).ToArray());
        CollectionAssert.AreEqual(new[] { "run-1", "run-2", "run-3", "run-4" }, turns.Turns.Select(static turn => turn.RunId).ToArray());
        var first = turns.Turns[0];
        Assert.AreEqual("First prompt", first.PromptPreview);
        Assert.IsFalse(first.PromptTruncated);
        Assert.AreEqual(AgentSessionTurnStatus.Completed, first.Status);
        Assert.AreEqual(2, first.ToolCallCount);
        Assert.AreEqual(1, first.AssistantMessageCount);
        Assert.IsNotNull(first.EndedAt);
        Assert.IsNotNull(first.PromptContentId);
        StringAssert.StartsWith(first.PromptContentId, "user:");
        var second = turns.Turns[1];
        Assert.AreEqual("Second \"quoted\" prompt\nwith two lines \u00e9\U0001F600", second.PromptPreview);
        Assert.AreEqual(1, second.SteerCount);
        Assert.AreEqual(first.EndOffset, second.StartOffset, "A turn starts where the one before it ends.");
        Assert.AreEqual(AgentSessionTurnStatus.Failed, turns.Turns[2].Status);
        Assert.AreEqual("agent-session", turns.Turns[2].SourceSessionId);
        Assert.AreEqual(AgentSessionTurnStatus.Unfinished, turns.Turns[3].Status);
        Assert.IsNull(turns.Turns[3].EndedAt);
        var journal = new FileInfo(JournalPath(temp.Path, Source));
        Assert.AreEqual(journal.Length, turns.EndOffset);
        Assert.AreEqual(journal.Length, turns.Revision.Length);

        // The record of a prompt is where the timeline reads it: the offset names the same event.
        var record = await store.ReadHistoryRecordAsync(Source, second.PromptOffset!.Value, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(second.PromptContentId, ((AgentContentCompletedEvent)record!).ContentId);
    }

    [TestMethod]
    public async Task ReadTurns_CutsALongPrompt_AndReadsARecordLargerThanItsBuffer()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(temp.Path));
        var session = await CreateSessionAsync(store, Source).ConfigureAwait(false);
        var prompt = string.Concat(Enumerable.Repeat("\u00e9\"\\\n", 2000));
        await AppendTurnAsync(store, session, "run-1", prompt, toolOutput: new string('x', 700 * 1024)).ConfigureAwait(false);
        await AppendTurnAsync(store, session, "run-2", "After the large record").ConfigureAwait(false);

        var turns = await store.ReadTurnsAsync(Source).ConfigureAwait(false);

        Assert.AreEqual(2, turns.Turns.Count);
        Assert.AreEqual(prompt[..AgentSessionTurn.PromptPreviewLength], turns.Turns[0].PromptPreview);
        Assert.IsTrue(turns.Turns[0].PromptTruncated);
        Assert.AreEqual(AgentSessionTurnStatus.Completed, turns.Turns[0].Status);
        Assert.AreEqual("After the large record", turns.Turns[1].PromptPreview);
    }

    [TestMethod]
    public async Task ReadTurns_ReadsAJournalWrittenWithWindowsLineEnds()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(temp.Path));
        var session = await CreateSessionAsync(store, Source).ConfigureAwait(false);
        await AppendTurnAsync(store, session, "run-1", "First prompt").ConfigureAwait(false);
        await AppendTurnAsync(store, session, "run-2", "Second prompt").ConfigureAwait(false);
        var path = JournalPath(temp.Path, Source);
        var text = await File.ReadAllTextAsync(path).ConfigureAwait(false);
        await File.WriteAllTextAsync(path, text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal)).ConfigureAwait(false);

        var turns = await store.ReadTurnsAsync(Source).ConfigureAwait(false);

        Assert.AreEqual(2, turns.Turns.Count);
        Assert.AreEqual("Second prompt", turns.Turns[1].PromptPreview);
        Assert.AreEqual(AgentSessionTurnStatus.Completed, turns.Turns[1].Status);
        var record = await store.ReadHistoryRecordAsync(Source, turns.Turns[1].PromptOffset!.Value, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("Second prompt", ((AgentContentCompletedEvent)record!).Content);
    }

    [TestMethod]
    public async Task Fork_BeforeATurn_CopiesWhatPrecedesIt_UnderTheNewIdentifier()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(temp.Path));
        var session = await CreateSessionAsync(store, Source).ConfigureAwait(false);
        await AppendTurnAsync(store, session, "run-1", "First prompt", providerSessionId: "resp_1").ConfigureAwait(false);
        await AppendTurnAsync(store, session, "run-2", "Second prompt", providerSessionId: "resp_2").ConfigureAwait(false);
        await AppendTurnAsync(store, session, "run-3", "Third prompt", providerSessionId: "resp_3").ConfigureAwait(false);
        var before = await File.ReadAllBytesAsync(JournalPath(temp.Path, Source)).ConfigureAwait(false);

        var point = await store.ReadBranchPointAsync(Source, "run-3", activeRunId: null, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(2, point.KeptTurnCount);
        Assert.AreEqual("run-3", point.NextTurn!.RunId);
        Assert.AreEqual("resp_2", point.State.ProviderSessionId, "The state of the point is the last one written before it.");
        var created = Start.AddDays(3);
        var summary = point.Summary with { SessionId = Target, Title = "Sample session (fork)", ParentSessionId = null, CreatedAt = created, UpdatedAt = created };
        var state = point.State with { SessionId = Target, ProviderSessionId = null, UpdatedAt = created };
        var records = await store.CommitForkAsync(point, summary, state, header: null, [], includeRemainder: false, CancellationToken.None).ConfigureAwait(false);

        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(JournalPath(temp.Path, Source)).ConfigureAwait(false), "The source is not written.");
        var forked = await store.ReadEventsAsync(Target).ConfigureAwait(false);
        Assert.AreEqual(records, forked.Count);
        Assert.IsTrue(forked.All(static value => value.SessionId == Target));
        CollectionAssert.AreEqual(new[] { "First prompt", "Second prompt" }, Prompts(forked));
        // The conversation the model is sent again is the one of the source, record for record.
        var original = await store.ReadEventsAsync(Source).ConfigureAwait(false);
        CollectionAssert.AreEqual(
            original.Take(forked.Count).Select(static value => Normalize(value.ToJson())).ToArray(),
            forked.Select(static value => Normalize(value.ToJson())).ToArray());

        var forkedTurns = await store.ReadTurnsAsync(Target).ConfigureAwait(false);
        CollectionAssert.AreEqual(new[] { "run-1", "run-2" }, forkedTurns.Turns.Select(static turn => turn.RunId).ToArray());
        var listed = await store.ListSessionSummariesAsync().ToArrayAsync().ConfigureAwait(false);
        CollectionAssert.AreEquivalent(new[] { Source, Target }, listed.Select(static value => value.SessionId).ToArray());
        Assert.AreEqual("Sample session (fork)", (await store.GetSessionSummaryAsync(Target).ConfigureAwait(false))!.Title);
        Assert.IsNull((await store.GetStateAsync(Target).ConfigureAwait(false))!.ProviderSessionId);
        // The strict reader, which a provider change uses, accepts the fork: no record names the source.
        var strict = await store.ReadTransferSnapshotAsync(Target, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(forked.Count, strict.History.Count);
        Assert.AreEqual(JournalPath(temp.Path, Target, created), Directory.EnumerateFiles(temp.Path, Target + ".jsonl", SearchOption.AllDirectories).Single());

        static string Normalize(string json) => json.Replace(Source, Target, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Fork_AtTheEnd_LeavesOutTheTurnThatRuns()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(temp.Path));
        var session = await CreateSessionAsync(store, Source).ConfigureAwait(false);
        await AppendTurnAsync(store, session, "run-1", "First prompt").ConfigureAwait(false);
        await AppendTurnAsync(store, session, "run-2", "Second prompt", end: TurnEnd.None).ConfigureAwait(false);

        var whole = await store.ReadBranchPointAsync(Source, null, activeRunId: null, CancellationToken.None).ConfigureAwait(false);
        var running = await store.ReadBranchPointAsync(Source, null, activeRunId: "run-2", CancellationToken.None).ConfigureAwait(false);
        var unseen = await store.ReadBranchPointAsync(Source, null, activeRunId: "run-not-written-yet", CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(2, whole.KeptTurnCount);
        Assert.AreEqual(1, running.KeptTurnCount);
        Assert.AreEqual(running.Turns[1].StartOffset, running.CutOffset);
        Assert.AreEqual(2, unseen.KeptTurnCount);
        var refused = await Assert.ThrowsExactlyAsync<AgentSessionHistoryException>(
            () => store.ReadBranchPointAsync(Source, "run-9", null, CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual("turn_not_found", refused.Code);
        await AppendTurnAsync(store, session, "run-3", "Third prompt").ConfigureAwait(false);
        refused = await Assert.ThrowsExactlyAsync<AgentSessionHistoryException>(
            () => store.ReadBranchPointAsync(Source, "run-3", "run-2", CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual("turn_running", refused.Code);

        // The source grew since the point was read: what precedes the point did not change, and the fork is written.
        var summary = running.Summary with { SessionId = Target, CreatedAt = Start, UpdatedAt = Start };
        await store.CommitForkAsync(running, summary, running.State with { SessionId = Target }, null, [], false, CancellationToken.None).ConfigureAwait(false);
        CollectionAssert.AreEqual(new[] { "First prompt" }, Prompts(await store.ReadEventsAsync(Target).ConfigureAwait(false)));
    }

    [TestMethod]
    public async Task Fork_WithTheRemainder_HoldsEveryTurn()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(temp.Path));
        var session = await CreateSessionAsync(store, Source).ConfigureAwait(false);
        await AppendTurnAsync(store, session, "run-1", "First prompt").ConfigureAwait(false);
        await AppendTurnAsync(store, session, "run-2", "Second prompt").ConfigureAwait(false);

        var point = await store.ReadBranchPointAsync(Source, "run-2", null, CancellationToken.None).ConfigureAwait(false);
        var summary = point.Summary with { SessionId = Target, CreatedAt = Start, UpdatedAt = Start };
        await store.CommitForkAsync(point, summary, point.State with { SessionId = Target }, null, [], includeRemainder: true, CancellationToken.None).ConfigureAwait(false);

        CollectionAssert.AreEqual(new[] { "First prompt", "Second prompt" }, Prompts(await store.ReadEventsAsync(Target).ConfigureAwait(false)));
    }

    [TestMethod]
    public async Task Fork_WritesTheHeaderFirst_AndTheSnapshotsOfTheSourceAreNotCopied()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(temp.Path));
        var session = await CreateSessionAsync(store, Source, parentSessionId: "parent-of-the-source").ConfigureAwait(false);
        await AppendTurnAsync(store, session, "run-1", "First prompt").ConfigureAwait(false);

        var point = await store.ReadBranchPointAsync(Source, null, null, CancellationToken.None).ConfigureAwait(false);
        var summary = point.Summary with { SessionId = Target, ParentSessionId = null, CreatedBySessionId = null, CreatedAt = Start, UpdatedAt = Start };
        var header = new AgentRawEvent(summary.ProviderId, Target, Start, "codealta.sessionHeader", JsonSerializer.SerializeToElement(new { session_id = Target })).ToJson();
        var extra = new AgentRawEvent(summary.ProviderId, Target, Start, "codealta.sessionState", JsonSerializer.SerializeToElement(new { archived = false })).ToJson();
        await store.CommitForkAsync(point, summary, point.State with { SessionId = Target }, header, [extra], false, CancellationToken.None).ConfigureAwait(false);

        var lines = await File.ReadAllLinesAsync(JournalPath(temp.Path, Target)).ConfigureAwait(false);
        Assert.AreEqual(header, lines[0]);
        Assert.AreEqual(extra, lines[^1]);
        Assert.AreEqual(1, lines.Count(static line => line.Contains("local.sessionSummary", StringComparison.Ordinal)));
        Assert.AreEqual(1, lines.Count(static line => line.Contains("local.sessionState", StringComparison.Ordinal)));
        Assert.IsFalse(lines.Any(static line => line.Contains(Source, StringComparison.Ordinal)), "No record of the fork names the source.");
        // A summary inherits its lineage from the ones before it: those of the source must not be there.
        Assert.IsNull((await store.GetSessionSummaryAsync(Target).ConfigureAwait(false))!.ParentSessionId);
        Assert.IsNull((await store.GetSessionAsync(Target).ConfigureAwait(false))!.ParentSessionId);
    }

    [TestMethod]
    public async Task Fork_MovesThePathsOfAttachments_AndCopiesTheFilesItsRecordsName()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(temp.Path));
        var session = await CreateSessionAsync(store, Source).ConfigureAwait(false);
        var attachments = (await store.GetAttachmentDirectoryAsync(Source, CancellationToken.None).ConfigureAwait(false))!;
        Directory.CreateDirectory(attachments);
        await File.WriteAllTextAsync(Path.Combine(attachments, "kept-1.png"), "kept").ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(attachments, "later-2.png"), "later").ConfigureAwait(false);
        await AppendTurnAsync(store, session, "run-1", "With an image", imagePath: Path.Combine(attachments, "kept-1.png")).ConfigureAwait(false);
        await AppendTurnAsync(store, session, "run-2", "With another image", imagePath: Path.Combine(attachments, "later-2.png")).ConfigureAwait(false);

        var point = await store.ReadBranchPointAsync(Source, "run-2", null, CancellationToken.None).ConfigureAwait(false);
        var created = Start.AddMonths(2).AddDays(11);
        var summary = point.Summary with { SessionId = Target, CreatedAt = created, UpdatedAt = created };
        await store.CommitForkAsync(point, summary, point.State with { SessionId = Target }, null, [], false, CancellationToken.None).ConfigureAwait(false);

        var forkAttachments = (await store.GetAttachmentDirectoryAsync(Target, CancellationToken.None).ConfigureAwait(false))!;
        Assert.AreNotEqual(Path.GetDirectoryName(attachments), Path.GetDirectoryName(forkAttachments), "The fork is in the folder of its own day.");
        CollectionAssert.AreEqual(new[] { "kept-1.png" }, Directory.EnumerateFiles(forkAttachments).Select(Path.GetFileName).ToArray());
        var prompt = (await store.ReadEventsAsync(Target).ConfigureAwait(false)).OfType<AgentContentCompletedEvent>().First(static value => value.Kind == AgentContentKind.User);
        Assert.AreEqual(Path.Combine(forkAttachments, "kept-1.png"), prompt.Details!.Value.GetProperty("image").GetString());
        Assert.IsTrue(File.Exists(Path.Combine(attachments, "kept-1.png")), "The source keeps its files.");
    }

    [TestMethod]
    public async Task Rewind_RemovesWhatFollowsThePoint_AndRestoresTheStateWrittenBeforeIt()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(temp.Path));
        var session = await CreateSessionAsync(store, Source).ConfigureAwait(false);
        await AppendTurnAsync(store, session, "run-1", "First prompt", providerSessionId: "resp_1").ConfigureAwait(false);
        await store.AppendEventsAsync("openai", "openai", Source,
            [new AgentNotesEvent(ModelProviderIds.OpenAIResponses, Source, Start.AddMinutes(1), null, AgentNotesUpdateKind.Set, "Notes after the first turn")]).ConfigureAwait(false);
        await AppendTurnAsync(store, session, "run-2", "Second prompt", providerSessionId: "resp_2").ConfigureAwait(false);
        await store.AppendEventsAsync("openai", "openai", Source,
            [new AgentNotesEvent(ModelProviderIds.OpenAIResponses, Source, Start.AddMinutes(2), null, AgentNotesUpdateKind.Set, "Notes after the second turn")]).ConfigureAwait(false);
        Assert.AreEqual("Notes after the second turn", (await store.ReadLatestNotesAsync(Source).ConfigureAwait(false))!.Markdown);
        var lengthBefore = new FileInfo(JournalPath(temp.Path, Source)).Length;

        var point = await store.ReadBranchPointAsync(Source, "run-2", null, CancellationToken.None).ConfigureAwait(false);
        var now = Start.AddHours(1);
        await store.CommitRewindAsync(point, point.Summary with { UpdatedAt = now }, point.State with { UpdatedAt = now }, null, [], CancellationToken.None).ConfigureAwait(false);

        var events = await store.ReadEventsAsync(Source).ConfigureAwait(false);
        CollectionAssert.AreEqual(new[] { "First prompt" }, Prompts(events));
        Assert.IsTrue(new FileInfo(JournalPath(temp.Path, Source)).Length < lengthBefore);
        Assert.AreEqual("resp_1", (await store.GetStateAsync(Source).ConfigureAwait(false))!.ProviderSessionId);
        Assert.AreEqual(now, (await store.GetSessionSummaryAsync(Source).ConfigureAwait(false))!.UpdatedAt);
        Assert.AreEqual("Notes after the first turn", (await store.ReadLatestNotesAsync(Source).ConfigureAwait(false))!.Markdown);
        var turns = await store.ReadTurnsAsync(Source).ConfigureAwait(false);
        CollectionAssert.AreEqual(new[] { "run-1" }, turns.Turns.Select(static turn => turn.RunId).ToArray());
        Assert.AreEqual(0, Directory.EnumerateFiles(temp.Path, "*.rewind-*", SearchOption.AllDirectories).Count());
        await store.ReadTransferSnapshotAsync(Source, CancellationToken.None).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task Rewind_ReplacesTheFirstLine_SoThatAReaderOfOffsetsStartsAgain()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(temp.Path));
        var session = await CreateSessionAsync(store, Source).ConfigureAwait(false);
        var path = JournalPath(temp.Path, Source);
        var oldHeader = new AgentRawEvent(session.ProviderId, Source, Start, "codealta.sessionHeader", JsonSerializer.SerializeToElement(new { session_id = Source })).ToJson();
        await File.WriteAllTextAsync(path, oldHeader + "\n" + await File.ReadAllTextAsync(path).ConfigureAwait(false)).ConfigureAwait(false);
        await AppendTurnAsync(store, session, "run-1", "First prompt").ConfigureAwait(false);
        await AppendTurnAsync(store, session, "run-2", "Second prompt").ConfigureAwait(false);

        var point = await store.ReadBranchPointAsync(Source, "run-2", null, CancellationToken.None).ConfigureAwait(false);
        var newHeader = new AgentRawEvent(session.ProviderId, Source, Start, "codealta.sessionHeader", JsonSerializer.SerializeToElement(new { session_id = Source, rewind_count = 1 })).ToJson();
        await store.CommitRewindAsync(point, point.Summary, point.State, newHeader, [], CancellationToken.None).ConfigureAwait(false);

        var lines = await File.ReadAllLinesAsync(path).ConfigureAwait(false);
        Assert.AreEqual(newHeader, lines[0]);
        Assert.AreEqual(1, lines.Count(static line => line.Contains("codealta.sessionHeader", StringComparison.Ordinal)));
        CollectionAssert.AreEqual(new[] { "First prompt" }, Prompts(await store.ReadEventsAsync(Source).ConfigureAwait(false)));
        CollectionAssert.AreEqual(new[] { "run-1" }, (await store.ReadTurnsAsync(Source).ConfigureAwait(false)).Turns.Select(static turn => turn.RunId).ToArray());
    }

    [TestMethod]
    public async Task Rewind_ToBeforeTheFirstTurn_LeavesAnEmptyConversation()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(temp.Path));
        var session = await CreateSessionAsync(store, Source).ConfigureAwait(false);
        await AppendTurnAsync(store, session, "run-1", "First prompt", providerSessionId: "resp_1").ConfigureAwait(false);

        var point = await store.ReadBranchPointAsync(Source, "run-1", null, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(0, point.KeptTurnCount);
        Assert.IsNull(point.State.ProviderSessionId, "No turn ran yet at the point.");
        await store.CommitRewindAsync(point, point.Summary, point.State, null, [], CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(0, (await store.ReadEventsAsync(Source).ConfigureAwait(false)).Count);
        Assert.AreEqual("Sample session", (await store.GetSessionSummaryAsync(Source).ConfigureAwait(false))!.Title);
    }

    [TestMethod]
    public async Task Rewind_RefusesAJournalThatChangedSinceThePointWasRead()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(temp.Path));
        var session = await CreateSessionAsync(store, Source).ConfigureAwait(false);
        await AppendTurnAsync(store, session, "run-1", "First prompt").ConfigureAwait(false);
        await AppendTurnAsync(store, session, "run-2", "Second prompt").ConfigureAwait(false);
        var point = await store.ReadBranchPointAsync(Source, "run-2", null, CancellationToken.None).ConfigureAwait(false);
        await AppendTurnAsync(store, session, "run-3", "Third prompt").ConfigureAwait(false);
        var before = await File.ReadAllBytesAsync(JournalPath(temp.Path, Source)).ConfigureAwait(false);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => store.CommitRewindAsync(point, point.Summary, point.State, null, [], CancellationToken.None)).ConfigureAwait(false);

        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(JournalPath(temp.Path, Source)).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task BranchWriter_PassesThroughARecordLargerThanWhatItEdits()
    {
        var large = new string('y', AgentJournalBranchWriter.MaxEditedRecordBytes + 4096);
        var records = new[]
        {
            Record("contentCompleted", $"\"kind\":\"Assistant\",\"contentId\":\"a\",\"content\":\"{large}\"", "run-1"),
            Record("raw", $"\"backendEventType\":\"local.sessionState\",\"raw\":{{\"sessionId\":\"{Source}\",\"filler\":\"{large}\"}}", null),
            Record("contentCompleted", "\"kind\":\"Assistant\",\"contentId\":\"b\",\"content\":\"small\"", "run-1"),
        };
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(string.Join("\n", records) + "\n"));
        using var output = new MemoryStream();

        var result = await AgentJournalBranchWriter.CopyAsync(source, 0, source.Length, output, Source, Target, null, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(2, result.Records);
        Assert.AreEqual(1, result.Snapshots);
        var written = Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.AreEqual(2, written.Length);
        Assert.AreEqual(records[0].Replace(Source, Target, StringComparison.Ordinal), written[0]);
        Assert.AreEqual(records[2].Replace(Source, Target, StringComparison.Ordinal), written[1]);
        Assert.AreEqual(output.Length, result.Bytes);
    }

    [TestMethod]
    public async Task BranchWriter_KeepsLineEnds_ChangesOnlyTheSessionOfTheRecord_AndReadsARecordWrittenInAnotherOrder()
    {
        var mention = $"the session {Source} is named in a text";
        var first = Record("contentCompleted", $"\"kind\":\"Assistant\",\"contentId\":\"a\",\"content\":\"{mention}\"", "run-1");
        var foreign = Record("contentCompleted", "\"kind\":\"Assistant\",\"contentId\":\"b\",\"content\":\"of another session\"", "run-1")
            .Replace(Source, "another-session", StringComparison.Ordinal);
        var reordered = $"{{\"$type\":\"contentCompleted\",\"sessionId\":\"{Source}\",\"kind\":\"Assistant\",\"contentId\":\"c\",\"content\":\"old order\",\"backendId\":\"openai\",\"timestamp\":\"2026-04-06T10:00:00+00:00\"}}";
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(first + "\r\n\r\n" + foreign + "\n" + reordered));
        using var output = new MemoryStream();

        var result = await AgentJournalBranchWriter.CopyAsync(source, 0, source.Length, output, Source, Target, null, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(3, result.Records);
        var text = Encoding.UTF8.GetString(output.ToArray());
        var expectedFirst = first[..first.LastIndexOf(Source, StringComparison.Ordinal)] + Target + first[(first.LastIndexOf(Source, StringComparison.Ordinal) + Source.Length)..];
        Assert.AreEqual(expectedFirst + "\r\n" + foreign + "\n" + reordered.Replace(Source, Target, StringComparison.Ordinal) + "\n", text);
        StringAssert.Contains(text, mention, "What a record says is not changed, also when it names the session.");

        using var broken = new MemoryStream("{\"$type\":\"contentCompleted\",\"kind\":\"Assistant\""u8.ToArray());
        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => AgentJournalBranchWriter.CopyAsync(broken, 0, broken.Length, new MemoryStream(), Source, Target, null, CancellationToken.None)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task BranchWriter_MovesAnAttachmentPath_WhateverSeparatorJsonWroteItWith()
    {
        var folder = Source + ".attachments";
        var paths = new[]
        {
            $"/home/u/.alta/sessions/2026/04/06/{folder}/a-1.png",
            $"C:\\\\Users\\\\u\\\\.alta\\\\sessions\\\\2026\\\\04\\\\06\\\\{folder}\\\\b_2.png",
            $"\\/home\\/u\\/sessions\\/2026\\/04\\/06\\/{folder}\\/c.3.png",
            // Not in the folder of the day of the journal, and not a path: both stay.
            $"/home/u/.alta/sessions/2025/04/06/{folder}/other.png",
            $"the folder {folder} is named in a text",
        };
        var records = paths.Select(path => Record("contentCompleted", $"\"kind\":\"User\",\"contentId\":\"u\",\"content\":\"x\",\"details\":{{\"image\":\"{path}\"}}", "run-1")).ToArray();
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(string.Join("\n", records) + "\n"));
        using var output = new MemoryStream();

        var result = await AgentJournalBranchWriter.CopyAsync(source, 0, source.Length, output, Source, Target,
            new(("2026", "04", "06"), ("2027", "11", "30")), CancellationToken.None).ConfigureAwait(false);

        var moved = Target + ".attachments";
        var text = Encoding.UTF8.GetString(output.ToArray());
        StringAssert.Contains(text, $"/home/u/.alta/sessions/2027/11/30/{moved}/a-1.png");
        StringAssert.Contains(text, $"C:\\\\Users\\\\u\\\\.alta\\\\sessions\\\\2027\\\\11\\\\30\\\\{moved}\\\\b_2.png");
        StringAssert.Contains(text, $"\\/home\\/u\\/sessions\\/2027\\/11\\/30\\/{moved}\\/c.3.png");
        StringAssert.Contains(text, paths[3]);
        StringAssert.Contains(text, paths[4]);
        CollectionAssert.AreEquivalent(new[] { "a-1.png", "b_2.png", "c.3.png" }, result.AttachmentFiles.ToArray());
    }

    private static string Record(string type, string body, string? runId)
        => $"{{\"$type\":\"{type}\",{body},\"backendId\":\"openai\",\"sessionId\":\"{Source}\",\"timestamp\":\"2026-04-06T10:00:00+00:00\""
            + (runId is null ? "}" : $",\"runId\":\"{runId}\"}}");

    private static string[] Prompts(IReadOnlyList<AgentEvent> events)
        => events.OfType<AgentContentCompletedEvent>().Where(static value => value.Kind == AgentContentKind.User).Select(static value => value.Content).ToArray();

    private static string JournalPath(string root, string sessionId, DateTimeOffset? createdAt = null)
        => new AgentRuntimePathLayout(root).GetSessionFilePath(sessionId, createdAt ?? Start);

    private static async Task<AgentSessionSummary> CreateSessionAsync(FileSystemAgentSessionStore store, string sessionId, string? parentSessionId = null)
    {
        var session = new AgentSessionSummary
        {
            SessionId = sessionId,
            ProviderId = ModelProviderIds.OpenAIResponses,
            ProtocolFamily = "openai",
            ProviderKey = "openai",
            ModelId = "gpt-5.4",
            WorkingDirectory = "/repo/sample",
            Title = "Sample session",
            ParentSessionId = parentSessionId,
            CreatedBySessionId = parentSessionId,
            CreatedAt = Start,
            UpdatedAt = Start,
        };
        await store.UpsertSessionAsync(session).ConfigureAwait(false);
        await store.UpsertStateAsync(new AgentSessionState
        {
            SessionId = sessionId,
            ProtocolFamily = session.ProtocolFamily,
            ProviderKey = session.ProviderKey,
            UpdatedAt = Start,
        }).ConfigureAwait(false);
        return session;
    }

    private enum TurnEnd
    {
        Idle,
        Error,
        None,
    }

    // Writes what a run writes, in the order a session writes it: the system prompt of the run, the prompt (as the
    // message the model is sent and as the content the timeline shows), a state, the answer with its tools, the end.
    private static async Task AppendTurnAsync(FileSystemAgentSessionStore store, AgentSessionSummary session, string run, string prompt,
        int tools = 0, string? steer = null, TurnEnd end = TurnEnd.Idle, string? sourceSessionId = null, string? providerSessionId = null,
        string? toolOutput = null, string? imagePath = null)
    {
        var provider = session.ProviderId;
        var id = session.SessionId;
        var runId = new AgentRunId(run);
        var time = DateTimeOffset.UtcNow;
        var input = JsonSerializer.SerializeToElement(new Dictionary<string, string?> { ["text"] = prompt, ["image"] = imagePath });
        var events = new List<AgentEvent>
        {
            new AgentSessionUpdateEvent(provider, id, time, runId, AgentSessionUpdateKind.ModelChanged, "Model selected"),
            new AgentRawEvent(provider, id, time, "local.userMessage", input, runId),
            new AgentContentCompletedEvent(provider, id, time, runId, AgentContentKind.User, $"user:{Guid.CreateVersion7()}", null, prompt, input,
                SourceSessionId: sourceSessionId),
        };
        await store.AppendEventsAsync(session.ProtocolFamily, session.ProviderKey, id, events).ConfigureAwait(false);
        await store.UpsertStateAsync(new AgentSessionState
        {
            SessionId = id,
            ProtocolFamily = session.ProtocolFamily,
            ProviderKey = session.ProviderKey,
            ProviderSessionId = providerSessionId,
            UpdatedAt = time,
        }).ConfigureAwait(false);
        events.Clear();
        for (var index = 0; index < tools; index++)
        {
            events.Add(new AgentActivityEvent(provider, id, time, runId, AgentActivityKind.ToolCall, AgentActivityPhase.Started, $"{run}-tool-{index}", null, "read_file", null));
            events.Add(new AgentActivityEvent(provider, id, time, runId, AgentActivityKind.ToolCall, AgentActivityPhase.Completed, $"{run}-tool-{index}", null, "read_file", null));
        }

        if (toolOutput is not null)
        {
            events.Add(new AgentRawEvent(provider, id, time, "local.toolMessage", JsonSerializer.SerializeToElement(new { output = toolOutput }), runId));
        }

        if (steer is not null)
        {
            events.Add(new AgentContentCompletedEvent(provider, id, time, runId, AgentContentKind.User, $"user:{Guid.CreateVersion7()}", null, steer));
        }

        events.Add(new AgentRawEvent(provider, id, time, "local.assistantMessage", JsonSerializer.SerializeToElement(new { text = "Answer to " + run }), runId));
        events.Add(new AgentContentCompletedEvent(provider, id, time, runId, AgentContentKind.Assistant, $"assistant:{run}", null, "Answer to " + run));
        switch (end)
        {
            case TurnEnd.Idle:
                events.Add(new AgentSessionUpdateEvent(provider, id, time, runId, AgentSessionUpdateKind.Idle, null));
                break;
            case TurnEnd.Error:
                events.Add(new AgentErrorEvent(provider, id, time, "The run failed.", (Exception?)null, runId));
                break;
        }

        await store.AppendEventsAsync(session.ProtocolFamily, session.ProviderKey, id, events).ConfigureAwait(false);
    }
}
