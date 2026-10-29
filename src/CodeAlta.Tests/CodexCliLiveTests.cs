using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Harness;
using CodeAlta.Agent.Runtime;
using CodeAlta.Orchestration.Runtime.SystemPrompts;
using CodeAlta.Plugin.CodexCli;

namespace CodeAlta.Tests;

/// <summary>
/// Runs the Codex CLI provider against a real <c>codex-app-server</c>. They run only when
/// <c>CODEALTA_TEST_CODEX_CLI</c> names the app-server (the pinned one) and <c>CODEALTA_TEST_CODEX_HOME</c> a Codex home
/// signed in through its sign-in: never <c>~/.codex</c>, whose <c>config.toml</c> CodeAlta would replace. The tests that
/// run turns, which cost a little of the plan, run only with <c>CODEALTA_TEST_CODEX_TURN=1</c>.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class CodexCliLiveTests
{
    private static readonly ModelProviderId ProviderId = new("codex-live");

    [TestMethod]
    public async Task Probe_ReadsTheAccountAndTheModels()
    {
        await using var fixture = LiveFixture.Create(turns: false);
        var probe = await fixture.Runtime.ProbeAsync();

        Assert.AreEqual(ModelProviderAvailability.Ready, probe.Availability, probe.StatusMessage);
        Assert.IsNotEmpty(probe.Models);
        Assert.IsTrue(probe.Models.All(static model => model.SupportedReasoningEfforts is { Count: > 0 }));
        StringAssert.Contains(probe.StatusMessage, "Signed in to ChatGPT");
    }

    [TestMethod]
    public async Task Turn_CallsAToolOfCodeAlta_AndRunsACommandWithItsApproval()
    {
        await using var fixture = LiveFixture.Create(turns: true);
        var echoed = new List<string>();
        fixture.Tools = [Tool("echo", invocation =>
        {
            var text = invocation.Arguments.TryGetProperty("text", out var value) ? value.GetString() ?? string.Empty : string.Empty;
            lock (echoed) echoed.Add(text);
            return new AgentToolResult(true, [new AgentToolResultItem.Text("echo:" + text)]);
        })];

        var conversation = await fixture.RunAsync(
            "Do exactly this, in order: 1) call the `echo` tool of the `codealta` MCP server with text \"pong\"; " +
            "2) run the shell command `touch codex-live.txt` in the working folder; 3) answer with the result of the echo tool only.");

        CollectionAssert.Contains(echoed, "pong");
        Assert.IsTrue(File.Exists(Path.Combine(fixture.WorkingDirectory, "codex-live.txt")), "the command ran in the working folder");
        var calls = conversation.SelectMany(static message => message.Parts.OfType<AgentMessagePart.ToolCall>()).Select(static call => call.Name).ToArray();
        CollectionAssert.Contains(calls, "echo");
        Assert.IsTrue(calls.Contains("shell") || calls.Contains("Edit") || calls.Contains("edit"), string.Join(", ", calls));
        StringAssert.Contains(Text(conversation[^1]), "pong");
        Console.WriteLine($"Approvals asked in default mode: {string.Join(" | ", fixture.Asked.Select(DescribeRequest))}");
        Assert.IsNotEmpty(fixture.Asked, "with the untrusted policy, Codex asks before it writes a file with a command");
    }

    [TestMethod]
    public async Task Instructions_SurviveTheCompactionOfCodex()
    {
        await using var fixture = LiveFixture.Create(turns: true);
        fixture.DeveloperInstructions = "The secret word of this session is PELICAN-42. Never say it unless the user asks for the secret word.";
        var conversation = await fixture.RunAsync("Answer with one word: ready.");

        var outcome = await fixture.Executor.CompactAsync(fixture.CreateRequest(conversation), CancellationToken.None);
        Assert.IsTrue(outcome.Success, outcome.Message);

        conversation.Add(User("What is the secret word of this session? Answer with it only."));
        conversation = await fixture.RunAsync(conversation);
        StringAssert.Contains(Text(conversation[^1]), "PELICAN-42");
    }

    [TestMethod]
    public async Task AStoppedTurn_EndsAndTheNextOneRuns()
    {
        await using var fixture = LiveFixture.Create(turns: true);
        fixture.Mode = "bypassPermissions";
        using var stop = new CancellationTokenSource();
        fixture.OnToolCall = _ => stop.CancelAfter(TimeSpan.FromSeconds(2));

        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.RunAsync("Run the shell command `sleep 60`, then answer done.", stop.Token));

        fixture.OnToolCall = null;
        var conversation = await fixture.RunAsync([User("Run the shell command `sleep 60`, then answer done."), User("Forget that. Answer with one word: stopped.")]);
        StringAssert.Contains(Text(conversation[^1]).ToLowerInvariant(), "stopped");
    }

    [TestMethod]
    public async Task Turn_GeneratesAnImage_ShownAsTheImageOfItsCall()
    {
        await using var fixture = LiveFixture.Create(turns: true);

        var conversation = await fixture.RunAsync("Generate a small image of a red circle on a white background with your image generation tool, then answer with one word: done.");

        var call = conversation.SelectMany(static message => message.Parts.OfType<AgentMessagePart.ToolCall>()).First(static call => call.Name == "image_generation");
        var result = conversation.SelectMany(static message => message.Parts.OfType<AgentMessagePart.ToolResult>()).Single(part => part.CallId == call.CallId).Result;
        Assert.IsTrue(result.Success, result.Error);
        var image = result.Items.OfType<AgentToolResultItem.Image>().Single();
        Assert.AreEqual("image/png", image.MediaType);
        Assert.IsGreaterThan(100, Convert.FromBase64String(image.Base64Data).Length);
        Console.WriteLine(((AgentToolResultItem.Text)result.Items[0]).Value);
    }

    [TestMethod]
    public async Task Turn_ACommit_EndsWithTheTrailerOfCodeAlta_BesideTheOneOfCodex()
    {
        await using var fixture = LiveFixture.Create(turns: true);
        fixture.Mode = "bypassPermissions";
        // The line the instructions of a session have while the co-author setting is on.
        fixture.DeveloperInstructions = $"Be brief.\n- Commit messages: end the message of every git commit you create with the trailer `{SystemPromptBuilder.CoAuthorTrailer}`, in its last paragraph with the other trailers. Add it beside the co-author or attribution lines your own tools ask for, never in place of them.";
        foreach (var arguments in new[] { "init -q", "config user.name Test", "config user.email test@example.invalid", "config commit.gpgsign false" })
        {
            await GitAsync(fixture.WorkingDirectory, arguments);
        }

        await fixture.RunAsync("Create the file note.txt with the text hello, then commit it with the subject \"Add the note\". Answer with one word: done.");

        var message = await GitAsync(fixture.WorkingDirectory, "log -1 --format=%B");
        Console.WriteLine(message);
        var trailers = message.TrimEnd().Split("\n\n")[^1];
        Assert.AreEqual(1, Count(trailers, SystemPromptBuilder.CoAuthorTrailer), message);
        // The trailer of Codex is there only when the ChatGPT workspace turns its attribution on: then once, beside it.
        Assert.IsLessThanOrEqualTo(1, Count(message, "Co-authored-by: Codex <noreply@openai.com>"), message);
        Assert.AreEqual(Count(message, "Co-authored-by: Codex <noreply@openai.com>"), Count(trailers, "Co-authored-by: Codex <noreply@openai.com>"), message);
    }

    private static int Count(string text, string value) => text.Split(value).Length - 1;

    private static async Task<string> GitAsync(string directory, string arguments)
    {
        using var git = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git", arguments) { WorkingDirectory = directory, RedirectStandardOutput = true, UseShellExecute = false })!;
        var output = await git.StandardOutput.ReadToEndAsync();
        await git.WaitForExitAsync();
        Assert.AreEqual(0, git.ExitCode, $"git {arguments}");
        return output.ReplaceLineEndings("\n");
    }

    private static string DescribeRequest(AgentPermissionRequest request) => request switch
    {
        AgentCommandPermissionRequest command => $"command: {command.Command}",
        AgentFileChangePermissionRequest file => $"file change: {file.Reason}",
        _ => request.GetType().Name,
    };

    private static AgentConversationMessage User(string text) => new(AgentConversationRole.User, [new AgentMessagePart.Text(text)]);

    private static string Text(AgentConversationMessage message) => string.Concat(message.Parts.OfType<AgentMessagePart.Text>().Select(static part => part.Value));

    private static AgentToolDefinition Tool(string name, Func<AgentToolInvocation, AgentToolResult> handler)
        => new(new AgentToolSpec(name, $"Echoes its text back. The tool {name} of CodeAlta.",
                JsonDocument.Parse("""{"type":"object","properties":{"text":{"type":"string"}},"required":["text"]}""").RootElement.Clone()),
            (invocation, _) => Task.FromResult(handler(invocation)));

    private sealed class LiveFixture : IAsyncDisposable
    {
        private readonly LoopbackHarnessToolEndpoints _endpoints = new();

        private LiveFixture(string command, string home)
        {
            WorkingDirectory = Path.Combine(Path.GetTempPath(), nameof(CodexCliLiveTests), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(WorkingDirectory);
            var options = new CodexCliRuntimeOptions
            {
                ProviderKey = ProviderId.Value,
                ProviderName = "Codex",
                Settings = new CodexCliSettings(command, home, new Dictionary<string, string>()),
                Home = home,
                ToolEndpoints = _endpoints,
                ProbeDirectory = Path.Combine(WorkingDirectory, ".probe"),
                ConnectionFactory = new CodexCliProcessConnectionFactory(null),
                RequestTimeout = TimeSpan.FromMinutes(2),
            };
            Runtime = new CodexCliModelProviderRuntime(new ModelProviderDescriptor(ProviderId, "Codex", "codex-cli"), options);
            Executor = (CodexCliTurnExecutor)Runtime.CreateTurnExecutor();
        }

        public static LiveFixture Create(bool turns)
        {
            var command = Environment.GetEnvironmentVariable("CODEALTA_TEST_CODEX_CLI");
            var home = Environment.GetEnvironmentVariable("CODEALTA_TEST_CODEX_HOME");
            if (string.IsNullOrWhiteSpace(command) || string.IsNullOrWhiteSpace(home))
            {
                Assert.Inconclusive("Set CODEALTA_TEST_CODEX_CLI and CODEALTA_TEST_CODEX_HOME to run the live tests of the Codex CLI provider.");
            }

            if (turns && Environment.GetEnvironmentVariable("CODEALTA_TEST_CODEX_TURN") != "1")
            {
                Assert.Inconclusive("Set CODEALTA_TEST_CODEX_TURN=1 to run turns with Codex.");
            }

            return new LiveFixture(command!, Path.GetFullPath(home!));
        }

        public CodexCliModelProviderRuntime Runtime { get; }

        public CodexCliTurnExecutor Executor { get; }

        public string WorkingDirectory { get; }

        public string DeveloperInstructions { get; set; } = "Be brief.";

        public string? Mode { get; set; }

        public IReadOnlyList<AgentToolDefinition> Tools { get; set; } = [];

        public List<AgentPermissionRequest> Asked { get; } = [];

        public Action<AgentMessagePart.ToolCall>? OnToolCall { get; set; }

        public AgentSessionState State { get; private set; } = new() { SessionId = "live-1", UpdatedAt = DateTimeOffset.UtcNow };

        public Task<List<AgentConversationMessage>> RunAsync(string prompt, CancellationToken cancellationToken = default) => RunAsync([User(prompt)], cancellationToken);

        public async Task<List<AgentConversationMessage>> RunAsync(IReadOnlyList<AgentConversationMessage> start, CancellationToken cancellationToken = default)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            var conversation = new List<AgentConversationMessage>(start);
            Executor.AttachRun(new AgentProviderRunContext("live-1", new AgentRunId("run-1"), (request, _) =>
            {
                lock (Asked) Asked.Add(request);
                return Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce));
            }, null));
            while (true)
            {
                var response = await Executor.ExecuteTurnAsync(CreateRequest(conversation), static (_, _) => ValueTask.CompletedTask, timeout.Token);
                conversation.Add(response.AssistantMessage);
                if (response.ProviderState is { } providerState)
                {
                    State = State with { ProviderState = providerState };
                }

                var calls = response.AssistantMessage.Parts.OfType<AgentMessagePart.ToolCall>().ToArray();
                foreach (var call in calls)
                {
                    OnToolCall?.Invoke(call);
                    var registered = Tools.FirstOrDefault(tool => tool.Spec.Name == call.Name);
                    var definition = Executor.ResolveTool("live-1", call, registered) ?? registered ?? throw new InvalidOperationException($"No tool {call.Name}.");
                    var result = await definition.Handler(new AgentToolInvocation(ProviderId, "live-1", call.CallId, call.Name, call.Arguments), timeout.Token);
                    conversation.Add(new AgentConversationMessage(AgentConversationRole.Tool, [new AgentMessagePart.ToolResult(call.CallId, result)]));
                }

                if (calls.Length == 0 && !response.RequiresProviderFollowUp)
                {
                    return conversation;
                }
            }
        }

        public AgentTurnRequest CreateRequest(IReadOnlyList<AgentConversationMessage> conversation)
            => new()
            {
                Provider = Runtime.RuntimeDescriptor,
                ProviderId = ProviderId,
                SessionId = "live-1",
                RunId = new AgentRunId("run-1"),
                WorkingDirectory = WorkingDirectory,
                DeveloperInstructions = DeveloperInstructions,
                PermissionMode = Mode,
                ReasoningEffort = AgentReasoningEffort.Low,
                Conversation = [.. conversation],
                Tools = Tools,
                State = State,
            };

        public async ValueTask DisposeAsync()
        {
            await Runtime.DisposeAsync();
            await _endpoints.DisposeAsync();
            try
            {
                // Git writes its objects read-only, which Windows does not delete.
                foreach (var file in Directory.EnumerateFiles(WorkingDirectory, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(WorkingDirectory, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
