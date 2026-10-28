using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Agent.Runtime.Tools;

namespace CodeAlta.Tests;

// A read_file of the model that asks again for lines it has, of a file unchanged since, gets a note instead of the
// content; a script's reads, a changed file and a compaction read the file again.
public sealed partial class AgentSessionTests
{
    private static Func<AgentTurnRequest, Func<AgentTurnDelta, CancellationToken, ValueTask>, CancellationToken, Task<AgentTurnResponse>> Calls(string callId, string tool, string arguments, Action? before = null)
        => (_, _, _) =>
        {
            before?.Invoke();
            return Task.FromResult(new AgentTurnResponse { AssistantMessage = new(AgentConversationRole.Assistant, [new AgentMessagePart.ToolCall(callId, tool, Json(arguments))]) });
        };

    private static string ResultText(IReadOnlyList<AgentConversationMessage> conversation, string callId)
        => string.Concat(conversation.SelectMany(static message => message.Parts).OfType<AgentMessagePart.ToolResult>().Single(part => part.CallId == callId)
            .Result.Items.OfType<AgentToolResultItem.Text>().Select(static item => item.Value));

    [TestMethod]
    public async Task AReadOfLinesTheModelHas_GetsANote_UntilTheFileChangesOrTheConversationIsCompacted()
    {
        using var temp = TestTempDirectory.Create();
        var workspace = Path.Combine(temp.Path, "workspace");
        Directory.CreateDirectory(workspace);
        var file = Path.Combine(workspace, "a.txt");
        await File.WriteAllTextAsync(file, "one\ntwo\n");
        var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(Path.Combine(temp.Path, "machine", "agents")));
        var provider = CreateProvider();
        var summary = CreateSummary("session-read-guard") with { WorkingDirectory = workspace };
        await store.UpsertSessionAsync(summary);
        await store.UpsertStateAsync(CreateState(summary.SessionId));

        var scriptReads = new List<string?>();
        var script = ScriptTool(async (tools, cancellationToken) =>
        {
            var read = await tools.InvokeAsync(new AgentNestedToolCall("read_file", Json("""{"path":"a.txt"}""")), cancellationToken);
            scriptReads.Add(read.Structured?.GetProperty("content").GetString());
            return new AgentToolResult(true, [new AgentToolResultItem.Text("script done")]);
        });
        IReadOnlyList<AgentConversationMessage> first = [], second = [];
        await using var session = new AgentSession(ModelProviderIds.OpenAIResponses, provider, summary, CreateState(summary.SessionId), [], store,
            new ScriptedTurnExecutor(
                [new AgentModelInfo("gpt-5.4", "GPT-5.4")],
                static (_, _, _) => Task.FromResult(new AgentTurnResponse { AssistantMessage = new(AgentConversationRole.Assistant, [new AgentMessagePart.Text("## Objective\nRead a.txt")]) }),
                // A script reads first: its read is not one the model has.
                Calls("call-1", "script", "{}"),
                Calls("call-2", "read_file", """{"path":"a.txt"}"""),
                // Lines within that read, of the same file.
                Calls("call-3", "read_file", """{"path":"a.txt","offset":2,"limit":1}"""),
                // A script still reads the file.
                Calls("call-4", "script", "{}"),
                // The file changes: the read gives it again.
                Calls("call-5", "read_file", """{"path":"a.txt"}""", () => File.WriteAllText(file, "one\ntwo\nthree\n")),
                (request, _, _) =>
                {
                    first = request.Conversation;
                    return Task.FromResult(new AgentTurnResponse { AssistantMessage = new(AgentConversationRole.Assistant, [new AgentMessagePart.Text("Read.")]) });
                },
                // After a compaction, the model no longer has what it read.
                Calls("call-6", "read_file", """{"path":"a.txt"}"""),
                (request, _, _) =>
                {
                    second = request.Conversation;
                    return Task.FromResult(new AgentTurnResponse { AssistantMessage = new(AgentConversationRole.Assistant, [new AgentMessagePart.Text("Read again.")]) });
                }),
            new AgentSessionCreateOptions
            {
                ProviderKey = provider.ProviderKey,
                Model = "gpt-5.4",
                WorkingDirectory = workspace,
                OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
            });

        await session.SendAsync(new() { Input = AgentInput.Text("Read"), AdditionalTools = [script] });

        Assert.AreEqual("1:one" + Environment.NewLine + "2:two", ResultText(first, "call-2"));
        var note = ResultText(first, "call-3");
        StringAssert.StartsWith(note, "[read_file: ");
        StringAssert.Contains(note, "has not changed since your read of its lines 1-2 (call call-2)");
        CollectionAssert.AreEqual(new[] { "one\ntwo", "one\ntwo" }, scriptReads, "a script receives the content, every time");
        StringAssert.Contains(ResultText(first, "call-5"), "3:three");

        await session.CompactAsync();
        await session.SendAsync(new() { Input = AgentInput.Text("Again") });

        StringAssert.Contains(ResultText(second, "call-6"), "3:three");
    }

    [TestMethod]
    public void TheReadGuard_KnowsTheLinesARecordedReadGave()
    {
        using var temp = TestTempDirectory.Create();
        var file = Path.Combine(temp.Path, "a.txt");
        File.WriteAllLines(file, Enumerable.Range(1, 100).Select(static index => $"line {index}"));
        var guard = new AgentReadFileGuard(AgentBuiltInToolOptions.DefaultReadFileLines, AgentBuiltInToolOptions.MaxReadFileLines);
        var kept = new HashSet<AgentMessagePart.ToolResult>(ReferenceEqualityComparer.Instance);
        string? Ask(string arguments) => guard.TryAnswer(Json(arguments), temp.Path, kept.Contains, out _);
        void Read(string arguments, string callId, int start, int end, bool more, string text = "lines")
        {
            Assert.IsNull(Ask(arguments));
            guard.TryAnswer(Json(arguments), temp.Path, kept.Contains, out var stamp);
            var part = new AgentMessagePart.ToolResult(callId, new AgentToolResult(true, [new AgentToolResultItem.Text(text)]));
            kept.Add(part);
            guard.Record(stamp!.Value, callId, Json($$"""{"path":"a.txt","startLine":{{start}},"endLine":{{end}},"more":{{(more ? "true" : "false")}}}"""), text, part);
        }

        // Lines 10 to 19, with more after them.
        Read("""{"path":"a.txt","offset":10,"limit":10}""", "call-1", 10, 19, more: true);
        Assert.IsNotNull(Ask("""{"path":"a.txt","offset":12,"limit":5}"""));
        Assert.IsNull(Ask("""{"path":"a.txt","offset":15,"limit":10}"""), "lines after the read");
        Assert.IsNull(Ask("""{"path":"a.txt","offset":-5}"""), "the end of the file is not known yet");

        // A read that stopped at its limit of characters gave the lines before the one it names.
        Read("""{"path":"a.txt","offset":30}""", "call-2", 30, 100, more: false, text: "30:line 30\n[read_file stopped at 40,000 characters: read on with offset=60.]");
        Assert.IsNotNull(Ask("""{"path":"a.txt","offset":40,"limit":20}"""));
        Assert.IsNull(Ask("""{"path":"a.txt","offset":40,"limit":21}"""));

        // A read to the end of the file: any start within it, and the end of the file counted back.
        Read("""{"path":"a.txt","offset":60}""", "call-3", 60, 100, more: false);
        StringAssert.Contains(Ask("""{"path":"a.txt","offset":90,"limit":2000}"""), "(call call-3)");
        Assert.IsNotNull(Ask("""{"path":"a.txt","offset":-5}"""));

        // A result the conversation no longer has is not known.
        kept.Clear();
        Assert.IsNull(Ask("""{"path":"a.txt","offset":12,"limit":5}"""));

        Read("""{"path":"a.txt"}""", "call-4", 1, 100, more: false);
        Assert.IsNotNull(Ask("""{"path":"a.txt"}"""));
        guard.Clear();
        Assert.IsNull(Ask("""{"path":"a.txt"}"""), "a compaction forgets the reads");

        Read("""{"path":"a.txt"}""", "call-5", 1, 100, more: false);
        File.AppendAllText(file, "line 101\n");
        Assert.IsNull(Ask("""{"path":"a.txt"}"""), "the file changed");
        Assert.IsNull(Ask("""{"path":"missing.txt"}"""));
    }
}
