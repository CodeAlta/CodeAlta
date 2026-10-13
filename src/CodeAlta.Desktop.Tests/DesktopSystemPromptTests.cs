using System.Globalization;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Desktop.Rpc;

namespace CodeAlta.Desktop.Tests;

/// <summary>
/// A system prompt record as its window asks for it: whole, with the prompt it replaced, over the journal of a
/// real session; and what the row of the record says in a history page.
/// </summary>
[TestClass]
public sealed class DesktopSystemPromptTests
{
    private const string Epoch = "epoch-1";
    private const string Session = "0199a000-0000-7000-8000-000000000031";
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-04-06T10:00:00+00:00", CultureInfo.InvariantCulture);
    private static readonly ModelProviderId Provider = ModelProviderIds.OpenAIResponses;

    [TestMethod]
    public async Task ReadPrompt_GivesThePromptWhole_WithTheOneItReplaced_FromTheJournalOfASession()
    {
        var root = Directory.CreateTempSubdirectory("codealta-prompt-").FullName;
        try
        {
            var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(root));
            await CreateSessionAsync(store);
            // A prompt as long as a real one: a history row cannot hold it.
            var system = string.Concat(Enumerable.Range(0, 1200).Select(static index => $"- Rule {index} of the system message.\n"));
            var developer = "# Agent Prompt\n\nYou are the Default agent.\n\n## Working loop\n- Inspect.\n- Implement.\n";
            var titled = developer + "\n## Session title\n- Name the session.\n";
            var huge = new string('x', 9 * 1024 * 1024);
            await store.AppendEventsAsync("openai", "openai", Session,
            [
                Prompt("hash-1", changed: false, system, developer), Content(AgentContentKind.User, "First prompt"),
                Prompt("hash-2", changed: true, system, titled),
                // Too large for the timeline to load: finding the prompt before the next one passes it.
                new AgentRawEvent(Provider, Session, Start, "local.toolMessage", JsonSerializer.SerializeToElement(new { output = huge }), new("run")),
                Prompt("hash-3", changed: true, system.Replace("Rule 7 of", "Rule seven of", StringComparison.Ordinal), developer),
                Content(AgentContentKind.Assistant, "Answer"),
            ]);
            var offsets = Offsets(new AgentRuntimePathLayout(root).GetSessionFilePath(Session, Start));
            var service = new ToolCallsService(store.ReadHistoryRecordAsync, null, Epoch) { Prompts = store.ReadSystemPromptAsync };

            var first = await service.ReadPromptAsync(new(Epoch, Session, offsets.Prompts[0], null, 0), CancellationToken.None);
            Assert.AreEqual("ok", first.Status);
            Assert.IsFalse(first.Prompt!.Changed);
            Assert.IsNull(first.Prompt.Previous, "The first prompt of the session replaced none.");
            Assert.IsNull(first.Prompt.Change);
            Assert.AreEqual(new ToolCallText(system, system.Length, false), first.Prompt.System, "A history row cuts this text; the record has all of it.");
            Assert.AreEqual(new ToolCallText(developer, developer.Length, false), first.Prompt.Developer);
            Assert.AreEqual(("hash-1", "Default", "prompts/default.prompt.md", Start), (first.Prompt.Hash, first.Prompt.Name, first.Prompt.SourcePath, first.Prompt.Timestamp));
            Assert.AreEqual((30, 10, 20, true, false), (first.Prompt.TotalTokens, first.Prompt.SystemTokens, first.Prompt.DeveloperTokens, first.Prompt.Applied, first.Prompt.Lossy));

            var second = (await service.ReadPromptAsync(new(Epoch, Session, offsets.Prompts[1], null, 0), CancellationToken.None)).Prompt!;
            Assert.IsTrue(second.Changed);
            Assert.AreEqual(titled, second.Developer!.Text);
            Assert.AreEqual((offsets.Prompts[0], developer, system), (second.Previous!.Offset, second.Previous.Developer!.Text, second.Previous.System!.Text));
            Assert.AreEqual(new SystemPromptChange(3, 0), second.Change, "A blank line, a heading and an item were added.");

            var third = (await service.ReadPromptAsync(new(Epoch, Session, offsets.Prompts[2], null, 0), CancellationToken.None)).Prompt!;
            Assert.AreEqual(offsets.Prompts[1], third.Previous!.Offset, "The prompt before, across the record the timeline cannot load.");
            Assert.AreEqual(titled, third.Previous.Developer!.Text);
            Assert.AreEqual(new SystemPromptChange(1, 4), third.Change);
            // The row asks for the size of the change alone.
            var change = await service.ReadPromptAsync(new(Epoch, Session, offsets.Prompts[2], "change", 0), CancellationToken.None);
            Assert.AreEqual(("ok", null, null, new SystemPromptChange(1, 4)), (change.Status, change.Prompt, change.Text, change.Change));
            Assert.IsNull((await service.ReadPromptAsync(new(Epoch, Session, offsets.Prompts[0], "change", 0), CancellationToken.None)).Change);

            // A record that is no prompt, the middle of one, and a session that does not exist.
            foreach (var offset in new[] { offsets.Other, (long.Parse(offsets.Prompts[1], CultureInfo.InvariantCulture) + 9).ToString(CultureInfo.InvariantCulture), "999999999999" })
            {
                foreach (var part in new[] { null, "change", "system" })
                {
                    var refused = await service.ReadPromptAsync(new(Epoch, Session, offset, part, 0), CancellationToken.None);
                    Assert.AreEqual(("missing_record", null, null, null), (refused.Status, refused.Prompt, refused.Text, refused.Change), $"Offset {offset}, part {part}.");
                }
            }

            Assert.AreEqual("missing_session", (await service.ReadPromptAsync(new(Epoch, "0199a000-0000-7000-8000-0000000000ff", offsets.Prompts[0], null, 0), CancellationToken.None)).Status);
            // The wire form serializes with the generated context.
            StringAssert.Contains(JsonSerializer.Serialize(await service.ReadPromptAsync(new(Epoch, Session, offsets.Prompts[1], null, 0), CancellationToken.None),
                DesktopJsonContext.Default.SystemPromptResponse), "\"change\":{\"added\":3,\"removed\":0}");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task ReadPrompt_ContinuesALongTextByPosition_OfThePromptAndOfTheOneBefore()
    {
        var system = string.Concat(Enumerable.Range(0, ToolCallsService.ChunkCharacters / 2 + 50).Select(static index => "😀"));
        var before = Prompt("hash-1", changed: false, system, "Developer");
        var after = Prompt("hash-2", changed: true, "System", system + "!");
        var service = Service(new() { [10] = before, [90] = after }, new() { [90] = new(after, before, 10) });

        var prompt = (await service.ReadPromptAsync(new(Epoch, Session, "90", null, 0), CancellationToken.None)).Prompt!;
        Assert.AreEqual((true, system.Length + 1, ToolCallsService.ChunkCharacters), (prompt.Developer!.More, prompt.Developer.Length, prompt.Developer.Text.Length));
        Assert.AreEqual(("10", true), (prompt.Previous!.Offset, prompt.Previous.System!.More));

        var rest = await service.ReadPromptAsync(new(Epoch, Session, "90", "developer", prompt.Developer.Text.Length), CancellationToken.None);
        Assert.AreEqual(("ok", null), (rest.Status, rest.Prompt));
        Assert.AreEqual(system + "!", prompt.Developer.Text + rest.Text!.Text);
        Assert.IsFalse(rest.Text.More);
        // The rest of a text of the prompt before is asked for at its own offset.
        var previous = await service.ReadPromptAsync(new(Epoch, Session, "10", "system", prompt.Previous.System.Text.Length), CancellationToken.None);
        Assert.AreEqual(system, prompt.Previous.System.Text + previous.Text!.Text);
        // A part never ends between the two halves of a pair.
        var odd = (await service.ReadPromptAsync(new(Epoch, Session, "10", "system", 1), CancellationToken.None)).Text!;
        Assert.AreEqual(ToolCallsService.ChunkCharacters - 1, odd.Text.Length);
        Assert.IsTrue(char.IsLowSurrogate(odd.Text[^1]));
        Assert.AreEqual("invalid", (await service.ReadPromptAsync(new(Epoch, Session, "10", "system", system.Length + 1), CancellationToken.None)).Status);
        // Two prompts too long to compare on a history page leave the size of the change unknown.
        Assert.IsNull(SystemPromptProjection.Change(Prompt("a", false, new string('a', SystemPromptProjection.MaximumComparedCharacters), ""), Prompt("b", true, "b", "")));
    }

    [TestMethod]
    public async Task ReadPrompt_RefusesWhatItCannotServe_WithoutData()
    {
        var prompt = Prompt("hash-1", changed: false, "System", "Developer");
        var service = Service(new() { [10] = prompt }, new() { [10] = new(prompt, null, null) });

        Assert.AreEqual("unavailable", (await new ToolCallsService().ReadPromptAsync(new(Epoch, Session, "10", null, 0), CancellationToken.None)).Status);
        Assert.AreEqual("unavailable", (await new ToolCallsService((_, _, _) => Task.FromResult<AgentEvent?>(prompt), null, Epoch)
            .ReadPromptAsync(new(Epoch, Session, "10", null, 0), CancellationToken.None)).Status, "A host without the read of prompts.");
        Assert.AreEqual("stale_epoch", (await service.ReadPromptAsync(new("other", Session, "10", null, 0), CancellationToken.None)).Status);
        foreach (var request in new SystemPromptRequest[]
        {
            new(Epoch, " ", "10", null, 0), new(Epoch, Session, "-1", null, 0), new(Epoch, Session, "1e3", null, 0), new(Epoch, Session, "10", "manifest", 0),
            new(Epoch, Session, "10", null, 4), new(Epoch, Session, "10", "change", 4), new(Epoch, Session, "10", "system", -1),
        })
        {
            Assert.AreEqual("invalid", (await service.ReadPromptAsync(request, CancellationToken.None)).Status, request.ToString());
        }

        await Assert.ThrowsExactlyAsync<ArgumentNullException>(() => service.ReadPromptAsync(null!, CancellationToken.None));
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.ReadPromptAsync(new(Epoch, Session, "10", null, 0), new CancellationToken(true)));
        // The gate of the shared reads refuses synchronously; a failed read never names a path.
        foreach (var (failure, status) in new (Exception, string)[]
        {
            (new InvalidOperationException("Owned workspace read capacity reached."), "capacity"), (new ObjectDisposedException("reads"), "closed"),
        })
        {
            var gated = new ToolCallsService((_, _, _) => Task.FromResult<AgentEvent?>(prompt), null, Epoch) { Prompts = (_, _, _) => throw failure };
            Assert.AreEqual(status, (await gated.ReadPromptAsync(new(Epoch, Session, "10", null, 0), CancellationToken.None)).Status);
        }

        foreach (var (failure, status) in new (Exception, string)[]
        {
            (new AgentSessionHistoryException("record_too_large"), "too_large"), (new AgentSessionHistoryException("corrupt_record"), "read_failed"),
            (new IOException(@"C:\Users\someone\.alta\sessions\journal.jsonl"), "read_failed"),
        })
        {
            var failing = new ToolCallsService((_, _, _) => Task.FromResult<AgentEvent?>(prompt), null, Epoch) { Prompts = (_, _, _) => Task.FromException<AgentSystemPromptRecord>(failure) };
            var refused = await failing.ReadPromptAsync(new(Epoch, Session, "10", null, 0), CancellationToken.None);
            Assert.AreEqual(status, refused.Status);
            Assert.IsFalse(JsonSerializer.Serialize(refused, DesktopJsonContext.Default.SystemPromptResponse).Contains("someone", StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public void HistoryRow_SaysWhatThePromptIs_AndCountsItsChange_WhenThePromptBeforeIsInThePage()
    {
        var system = string.Concat(Enumerable.Range(0, 2000).Select(static index => $"- Rule {index}\n"));
        var first = Prompt("hash-1", changed: false, system, "One\nTwo\nThree\n");
        var second = Prompt("hash-2", changed: true, system, "One\nTwo and a half\nThree\nFour\n") with
        {
            ProviderPayloadSummary = new("developer-only", false, true),
            AgentPromptUsage = new("reviewer", null, null),
        };
        var third = Prompt("hash-3", changed: true, system, "One\nThree\nFour\n") with { AgentPromptUsage = null, AgentPromptId = "plan" };
        AgentSessionHistoryEntry[] Entries(params AgentEvent[] events) => events.Select(static (value, index) => new AgentSessionHistoryEntry(index * 100, value)).ToArray();

        var page = WorkspaceService.ProjectHistory(new(Entries(first, Content(AgentContentKind.User, "Prompt"), second, third), null, false));

        Assert.AreEqual("ok", page.Status);
        var rows = page.Entries.Where(static entry => entry.EventType == "system_prompt").ToArray();
        Assert.AreEqual(new HistoryPromptSummary(false, "hash-1", "prompts/default.prompt.md", 30, 10, 20, true, false, null, null), rows[0].Prompt);
        Assert.AreEqual(new HistoryPromptSummary(true, "hash-2", null, 30, 10, 20, false, true, 2, 1), rows[1].Prompt);
        Assert.AreEqual(new HistoryPromptSummary(true, "hash-3", null, 30, 10, 20, true, false, 0, 1), rows[2].Prompt);
        CollectionAssert.AreEqual(new[] { "Default", "reviewer", "plan" }, rows.Select(static row => row.Name).ToArray());
        CollectionAssert.AreEqual(new[] { "session_start", "changed", "changed" }, rows.Select(static row => row.Kind).ToArray());
        // The texts of a prompt are not in the page: three long prompts cost it three small rows.
        Assert.IsTrue(rows.All(static row => row.Text is null && row.Details is null && !row.TextTruncated));
        Assert.IsTrue(JsonSerializer.SerializeToUtf8Bytes(page, DesktopJsonContext.Default.HistoryResponse).Length < 8192);

        // A page that starts after the prompt that was replaced leaves the count to the row itself.
        var later = WorkspaceService.ProjectHistory(new(Entries(second), null, false)).Entries.Single().Prompt!;
        Assert.AreEqual((true, null, null), (later.Changed, later.Added, later.Removed));
    }

    private static ToolCallsService Service(Dictionary<long, AgentEvent> records, Dictionary<long, AgentSystemPromptRecord> prompts)
        => new((_, offset, _) => records.TryGetValue(offset, out var record) ? Task.FromResult<AgentEvent?>(record)
            : Task.FromException<AgentEvent?>(new AgentSessionHistoryException("invalid_cursor")), null, Epoch)
        {
            Prompts = (_, offset, _) => prompts.TryGetValue(offset, out var prompt) ? Task.FromResult(prompt)
                : Task.FromException<AgentSystemPromptRecord>(new AgentSessionHistoryException("invalid_cursor")),
        };

    private static AgentSystemPromptEvent Prompt(string hash, bool changed, string system, string developer)
        => new(Provider, Session, Start, new AgentRunId("run"), changed ? "changed" : "session_start", hash, "default", system, developer,
            new("native-system-and-developer", true, false), JsonSerializer.SerializeToElement(new { tools = 3 }), new(10, 20, 30, system.Length, developer.Length),
            changed ? new("changed", [], [], ["effective_prompt"]) : new("initial", ["system", "developer"], [], []),
            new("default", "Default", "prompts/default.prompt.md"));

    private static AgentContentCompletedEvent Content(AgentContentKind kind, string text)
        => new(Provider, Session, Start, new AgentRunId("run"), kind, $"content:{Guid.CreateVersion7()}", null, text);

    // Where the prompt records of the journal start, and the record of a message.
    private static (string[] Prompts, string Other) Offsets(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var prompts = new List<string>();
        string? other = null;
        for (var start = 0; start < bytes.Length;)
        {
            var line = bytes.AsSpan(start);
            if (line.StartsWith("{\"$type\":\"system_prompt\""u8)) prompts.Add(start.ToString(CultureInfo.InvariantCulture));
            else if (line.StartsWith("{\"$type\":\"contentCompleted\""u8)) other ??= start.ToString(CultureInfo.InvariantCulture);
            var end = Array.IndexOf(bytes, (byte)'\n', start);
            start = end < 0 ? bytes.Length : end + 1;
        }

        return (prompts.ToArray(), other!);
    }

    private static async Task CreateSessionAsync(FileSystemAgentSessionStore store)
    {
        await store.UpsertSessionAsync(new AgentSessionSummary
        {
            SessionId = Session,
            ProviderId = Provider,
            ProtocolFamily = "openai",
            ProviderKey = "openai",
            ModelId = "gpt-5.4",
            WorkingDirectory = "/repo/sample",
            Title = "Sample session",
            CreatedAt = Start,
            UpdatedAt = Start,
        });
        await store.UpsertStateAsync(new AgentSessionState { SessionId = Session, ProtocolFamily = "openai", ProviderKey = "openai", UpdatedAt = Start });
    }
}
