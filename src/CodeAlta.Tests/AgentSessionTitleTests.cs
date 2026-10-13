using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Agent.Runtime.Compaction;

namespace CodeAlta.Tests;

[TestClass]
public sealed class AgentSessionTitleTests
{
    private const string Guidance = "Session title: write a <session_title> tag.";

    [TestMethod]
    public void FromPrompt_TakesTheOpeningSentenceOnOneLineWithinTheLimit()
    {
        Assert.AreEqual("Fix the login error", AgentSessionTitles.FromPrompt("  Fix the login error. Then add a regression test.  "));
        Assert.AreEqual("Review the parser", AgentSessionTitles.FromPrompt("## Review the parser\n\nCheck error handling."));
        Assert.AreEqual("Review the parser", AgentSessionTitles.FromPrompt("- Review the parser\r\n\r\n- Then the lexer"));
        Assert.AreEqual("Review the parser", AgentSessionTitles.FromPrompt("1. Review the parser\n \nMore."));
        Assert.AreEqual("Fix the login error", AgentSessionTitles.FromPrompt("Fix\t the\r\n login error"));
        Assert.AreEqual("Update src/parser.cs and its tests", AgentSessionTitles.FromPrompt("Update src/parser.cs and its tests! Keep changes focused."));
        Assert.AreEqual("Is it broken", AgentSessionTitles.FromPrompt("Is it broken?! Check."));
        Assert.AreEqual("#108 needs a look", AgentSessionTitles.FromPrompt("#108 needs a look"));
        Assert.AreEqual("...", AgentSessionTitles.FromPrompt("..."));
        Assert.IsNull(AgentSessionTitles.FromPrompt(""));
        Assert.IsNull(AgentSessionTitles.FromPrompt(" \n\t "));
        Assert.IsNull(AgentSessionTitles.FromPrompt(null));

        var cut = AgentSessionTitles.FromPrompt("Review the parser " + string.Concat(Enumerable.Repeat("and its tests ", 20)));
        // Cut at a word, with an ellipsis in the place of what is left out.
        Assert.AreEqual("Review the parser and its tests and its tests and its tests and its tests and…", cut);
        Assert.IsTrue(cut!.Length <= AgentSessionTitles.MaxDerivedLength, cut);
        // An emoji at the limit is left out whole, never cut in half.
        Assert.AreEqual(new string('x', 78) + "…", AgentSessionTitles.FromPrompt(new string('x', 78) + "😀more"));
        Assert.AreEqual(new string('x', 79) + "…", AgentSessionTitles.FromPrompt(new string('x', 90)));
    }

    [TestMethod]
    public void FromMessage_TakesTheFirstLine()
    {
        Assert.AreEqual("First line", AgentSessionTitles.FromMessage("\n  First line  \nSecond"));
        Assert.AreEqual(new string('a', 80), AgentSessionTitles.FromMessage(new string('a', 120)));
        Assert.AreEqual(new string('a', 79), AgentSessionTitles.FromMessage(new string('a', 79) + "😀 more"));
        Assert.IsNull(AgentSessionTitles.FromMessage("  \n "));
    }

    [TestMethod]
    public void PromptNames_OnlyWhatAPersonOrAnAgentWrote()
    {
        Assert.IsTrue(AgentSessionTitles.PromptNames("Fix the parser", providerInitiated: false));
        Assert.IsFalse(AgentSessionTitles.PromptNames("Fix the parser", providerInitiated: true));
        Assert.IsFalse(AgentSessionTitles.PromptNames("[CodeAlta background job]\nJob: job-1", providerInitiated: false));
        Assert.IsFalse(AgentSessionTitles.PromptNames("  [CodeAlta delegated-agent message]\nSource session: a", providerInitiated: false));
        Assert.IsTrue(AgentSessionTitles.PromptNames("[Draft] rename the tool", providerInitiated: false));
        Assert.IsFalse(AgentSessionTitles.PromptNames(null, providerInitiated: false));
    }

    [TestMethod]
    public void ModeNames_RoundTrip()
    {
        foreach (var mode in Enum.GetValues<AgentSessionTitleMode>())
        {
            Assert.AreEqual(mode, AgentSessionTitles.ParseMode(" " + AgentSessionTitles.NameOf(mode).ToUpperInvariant() + " "));
        }

        Assert.IsNull(AgentSessionTitles.ParseMode("default"));
        Assert.IsNull(AgentSessionTitles.ParseMode(null));
        Assert.AreEqual("unset", AgentSessionTitles.NameOf((AgentSessionTitleSource?)null));
        Assert.AreEqual("auto", AgentSessionTitles.NameOf(AgentSessionTitleSource.Auto));
        Assert.AreEqual("user", AgentSessionTitles.NameOf(AgentSessionTitleSource.User));
    }

    [TestMethod]
    public void TitleTag_Strip_RemovesTheTagAndItsLine()
    {
        Assert.AreEqual("Answer.", AgentSessionTitleTag.Strip("<session_title>Parser rewrite</session_title>\n\nAnswer.", out var title));
        Assert.AreEqual("Parser rewrite", title);
        Assert.AreEqual("Answer.\nMore.", AgentSessionTitleTag.Strip("Answer.\n<session_title> Other subject </session_title>\r\nMore.", out title));
        Assert.AreEqual("Other subject", title);
        Assert.AreEqual("Before  after", AgentSessionTitleTag.Strip("Before <session_title>Inline</session_title> after", out title));
        Assert.AreEqual("Inline", title);
        // The last tag that has a title wins; an empty one is removed and gives none.
        Assert.AreEqual("A\nB\n", AgentSessionTitleTag.Strip("<session_title>One</session_title>\nA\n<session_title>Two</session_title>\nB\n<session_title> </session_title>", out title));
        Assert.AreEqual("Two", title);
        Assert.AreEqual(string.Empty, AgentSessionTitleTag.Strip("<session_title>Only a title</session_title>\n", out title));
        Assert.AreEqual("Only a title", title);
    }

    [TestMethod]
    public void TitleTag_Strip_LeavesWhatIsNotATag()
    {
        var plain = "Compare a < b and <session_ids> here.";
        Assert.AreSame(plain, AgentSessionTitleTag.Strip(plain, out var title));
        Assert.IsNull(title);
        foreach (var text in new[]
                 {
                     "An open <session_title>tag without an end",
                     "A tag <session_title>over\ntwo lines</session_title>",
                     "Too long <session_title>" + new string('x', AgentSessionTitleTag.MaxContentLength + 1) + "</session_title>",
                     "Quoted `<session_title>Title in a few words</session_title>` inline",
                     "```xml\n<session_title>Quoted</session_title>\n```\nAfter the block.",
                     "~~~\n<session_title>Quoted</session_title>\n~~~",
                 })
        {
            Assert.AreSame(text, AgentSessionTitleTag.Strip(text, out title), text);
            Assert.IsNull(title, text);
        }

        // After a code block the text is read again.
        Assert.AreEqual("```\n<session_title>Quoted</session_title>\n```\nDone.",
            AgentSessionTitleTag.Strip("```\n<session_title>Quoted</session_title>\n```\n<session_title>Real</session_title>\nDone.", out title));
        Assert.AreEqual("Real", title);
        Assert.IsNull(AgentSessionTitleTag.Strip(null, out title));
    }

    [TestMethod]
    public void TitleTag_Filter_NeverShowsAPartOfATagWhateverTheChunks()
    {
        foreach (var (text, expected, expectedTitle) in new[]
                 {
                     ("<session_title>Parser rewrite</session_title>\nThe answer < that > follows.", "The answer < that > follows.", "Parser rewrite"),
                     ("Intro.\n<session_title>One</session_title>\nMiddle <session_title>Two</session_title> end.", "Intro.\nMiddle  end.", "Two"),
                     ("The answer ends with its title.\n<session_title>At the end</session_title>", "The answer ends with its title.\n", "At the end"),
                     ("A false start <session_ids> and a <b>tag</b>, then <session_title", "A false start <session_ids> and a <b>tag</b>, then <session_title", null),
                     ("Unfinished <session_title>never closed", "Unfinished <session_title>never closed", null),
                     ("a << b <<session_title>T</session_title> c", "a << b < c", "T"),
                 })
        {
            // Every split of the text in two, and the text one character at a time.
            for (var split = 0; split <= text.Length; split++)
            {
                AssertFiltered([text[..split], text[split..]], expected, expectedTitle);
            }

            AssertFiltered([.. text.Select(static character => character.ToString())], expected, expectedTitle);
        }
    }

    private static void AssertFiltered(IReadOnlyList<string> chunks, string expected, string? expectedTitle)
    {
        var filter = new AgentSessionTitleTag.Filter();
        var shown = new System.Text.StringBuilder();
        foreach (var chunk in chunks)
        {
            shown.Append(filter.Append(chunk));
            // Nothing that is shown while the text arrives is ever taken back: it is a prefix of the final text.
            Assert.IsTrue(expected.StartsWith(shown.ToString(), StringComparison.Ordinal), $"'{shown}' is not the start of '{expected}' for [{string.Join("|", chunks)}]");
            Assert.IsFalse(shown.ToString().Contains("<session_title>", StringComparison.Ordinal) && expectedTitle is not null, shown.ToString());
        }

        shown.Append(filter.Flush());
        Assert.AreEqual(expected, shown.ToString(), string.Join("|", chunks));
        Assert.AreEqual(expectedTitle, filter.Title, string.Join("|", chunks));
    }

    [TestMethod]
    public async Task FirstPrompt_NamesTheSessionOnceAndNothingElseDoes()
    {
        await using var fixture = await Fixture.CreateAsync("title-first-prompt", AgentSessionTitleMode.FirstPrompt, AgentSessionTitleSource.Auto,
            Answer("<session_title>From the model</session_title>\nFirst answer."), Answer("Second answer."));

        await fixture.Session.SendAsync(new() { Input = AgentInput.Text("[CodeAlta background job]\nJob: job-1") });
        Assert.AreEqual("CodeAlta", (await fixture.SummaryAsync()).Title, "A message of the host names nothing.");
        await fixture.Session.SendAsync(new() { Input = AgentInput.Text("Fix the login error. Then add a test.") });

        var summary = await fixture.SummaryAsync();
        Assert.AreEqual("Fix the login error", summary.Title);
        Assert.AreEqual(AgentSessionTitleSource.Auto, summary.TitleSource);
        Assert.AreEqual("Second answer.", summary.Summary);
        Assert.IsFalse(fixture.DeveloperInstructions[0]!.Contains(Guidance, StringComparison.Ordinal), "Only a session titled by its model is asked for a title.");
        // The tag is hidden in every mode: a model may go on writing it after the mode changed.
        CollectionAssert.AreEqual(new[] { "First answer.", "Second answer." }, fixture.ShownAssistantTexts());
    }

    [TestMethod]
    public async Task LastMessage_FollowsTheFirstLineOfEachAnswer()
    {
        await using var fixture = await Fixture.CreateAsync("title-last-message", AgentSessionTitleMode.LastMessage, AgentSessionTitleSource.Auto,
            Answer("Looking at the login error.\nDetails follow."), Answer("The test is added."));

        await fixture.Session.SendAsync(new() { Input = AgentInput.Text("Fix the login error.") });
        Assert.AreEqual("Looking at the login error.", (await fixture.SummaryAsync()).Title);
        await fixture.Session.SendAsync(new() { Input = AgentInput.Text("Add a test.") });

        var summary = await fixture.SummaryAsync();
        Assert.AreEqual("The test is added.", summary.Title);
        Assert.AreEqual(AgentSessionTitleSource.Auto, summary.TitleSource);
    }

    [TestMethod]
    public async Task Model_TakesTheTagAndShowsNoneOfIt()
    {
        const string Raw = "<session_title>Login error fix</session_title>\n\nI fixed the login error.";
        await using var fixture = await Fixture.CreateAsync("title-model", AgentSessionTitleMode.Model, AgentSessionTitleSource.Auto,
            async (_, onDelta, token) =>
            {
                foreach (var chunk in new[] { "<session", "_title>Login error", " fix</session_title>\n", "\nI fixed ", "the login error." })
                {
                    await onDelta(new AgentTurnDelta { Kind = AgentContentKind.Assistant, ContentId = "answer-1", Text = chunk }, token);
                }

                return new AgentTurnResponse { AssistantMessage = new(AgentConversationRole.Assistant, [new AgentMessagePart.Text(Raw)]), AssistantPartContentIds = ["answer-1"] };
            },
            Answer("No title this time."));

        await fixture.Session.SendAsync(new() { Input = AgentInput.Text("Fix the login error.") });

        var summary = await fixture.SummaryAsync();
        Assert.AreEqual("Login error fix", summary.Title);
        Assert.AreEqual(AgentSessionTitleSource.Auto, summary.TitleSource);
        Assert.AreEqual("I fixed the login error.", summary.Summary);
        CollectionAssert.AreEqual(new[] { "I fixed the login error." }, fixture.ShownAssistantTexts());
        Assert.AreEqual("I fixed the login error.", string.Concat(fixture.Live.OfType<AgentContentDeltaEvent>().Select(static delta => delta.Delta)));
        StringAssert.Contains(fixture.DeveloperInstructions[0], Guidance);

        // The model is sent what it wrote, and the journal keeps it: only what is shown has no tag.
        await fixture.Session.SendAsync(new() { Input = AgentInput.Text("Thanks.") });
        var replayed = fixture.Requests[1].Conversation.Single(static message => message.Role == AgentConversationRole.Assistant);
        Assert.AreEqual(Raw, replayed.Parts.OfType<AgentMessagePart.Text>().Single().Value);
        var persisted = await fixture.Store.ReadEventsAsync(fixture.Summary.SessionId);
        Assert.IsTrue(persisted.OfType<AgentRawEvent>().Any(static raw => raw.BackendEventType == "local.assistantMessage" && raw.Raw.GetRawText().Contains("session_title", StringComparison.Ordinal)));
        Assert.IsFalse(persisted.OfType<AgentContentCompletedEvent>().Any(static content => content.Kind == AgentContentKind.Assistant && content.Content.Contains("session_title", StringComparison.Ordinal)));
        Assert.AreEqual("Login error fix", (await fixture.SummaryAsync()).Title, "An answer without a tag keeps the title.");
    }

    [TestMethod]
    public async Task Model_KeepsTheFirstPromptTitleUntilTheModelGivesOne()
    {
        await using var fixture = await Fixture.CreateAsync("title-model-waits", AgentSessionTitleMode.Model, AgentSessionTitleSource.Auto,
            Answer("An answer without a title."), Answer("<session_title>  </session_title>\nStill none."), Answer("<session_title>Now named</session_title>\nDone."));

        await fixture.Session.SendAsync(new() { Input = AgentInput.Text("Review the parser.") });
        Assert.AreEqual("Review the parser", (await fixture.SummaryAsync()).Title);
        await fixture.Session.SendAsync(new() { Input = AgentInput.Text("Go on.") });
        Assert.AreEqual("Review the parser", (await fixture.SummaryAsync()).Title, "An empty tag gives no title.");
        await fixture.Session.SendAsync(new() { Input = AgentInput.Text("And now?") });
        Assert.AreEqual("Now named", (await fixture.SummaryAsync()).Title);
    }

    [TestMethod]
    public async Task ATitleSomebodyChoseSurvivesEveryMode()
    {
        foreach (var mode in Enum.GetValues<AgentSessionTitleMode>())
        {
            await using var fixture = await Fixture.CreateAsync("title-user-" + mode, mode, AgentSessionTitleSource.User,
                Answer("<session_title>From the model</session_title>\nAn answer."));
            await fixture.Session.SendAsync(new() { Input = AgentInput.Text("Fix the login error.") });

            var summary = await fixture.SummaryAsync();
            Assert.AreEqual("CodeAlta", summary.Title, mode.ToString());
            Assert.AreEqual(AgentSessionTitleSource.User, summary.TitleSource, mode.ToString());
            CollectionAssert.AreEqual(new[] { "An answer." }, fixture.ShownAssistantTexts(), mode.ToString());
            Assert.IsFalse(fixture.DeveloperInstructions[0]?.Contains(Guidance, StringComparison.Ordinal) == true, "A session somebody named is not asked for a title.");
        }
    }

    [TestMethod]
    public async Task ARenameDuringARunWins()
    {
        var answering = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = await Fixture.CreateAsync("title-rename-wins", AgentSessionTitleMode.Model, AgentSessionTitleSource.Auto,
            async (_, _, _) =>
            {
                answering.TrySetResult();
                await release.Task;
                return new AgentTurnResponse { AssistantMessage = new(AgentConversationRole.Assistant, [new AgentMessagePart.Text("<session_title>From the model</session_title>\nDone.")]) };
            },
            Answer("<session_title>Again from the model</session_title>\nDone again."));

        var send = fixture.Session.SendAsync(new() { Input = AgentInput.Text("Fix the login error.") });
        await answering.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Session.RenameAsync("My own title").WaitAsync(TimeSpan.FromSeconds(5));
        release.TrySetResult();
        await send.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Session.SendAsync(new() { Input = AgentInput.Text("Go on.") });

        var summary = await fixture.SummaryAsync();
        Assert.AreEqual("My own title", summary.Title);
        Assert.AreEqual(AgentSessionTitleSource.User, summary.TitleSource);
        Assert.IsFalse(fixture.DeveloperInstructions[1]?.Contains(Guidance, StringComparison.Ordinal) == true, "Once named, the session is no longer asked for a title.");
    }

    [TestMethod]
    public async Task AModeChangeActsFromTheNextRunAndReleasingTheTitleAppliesIt()
    {
        await using var fixture = await Fixture.CreateAsync("title-mode-change", AgentSessionTitleMode.FirstPrompt, AgentSessionTitleSource.Auto,
            Answer("The first answer."), Answer("<session_title>Named by the model</session_title>\nThe second answer."), Answer("The third answer."));

        await fixture.Session.SendAsync(new() { Input = AgentInput.Text("Fix the login error.") });
        fixture.Session.SetTitleMode(AgentSessionTitleMode.Model);
        Assert.AreEqual(AgentSessionTitleMode.Model, fixture.Session.TitleMode);
        await fixture.Session.SendAsync(new() { Input = AgentInput.Text("Go on.") });
        Assert.AreEqual("Named by the model", (await fixture.SummaryAsync()).Title);
        Assert.IsFalse(fixture.DeveloperInstructions[0]!.Contains(Guidance, StringComparison.Ordinal));
        StringAssert.Contains(fixture.DeveloperInstructions[1], Guidance);

        // A title somebody chose is handed back: the mode says what it is.
        await fixture.Session.RenameAsync("My own title");
        fixture.Session.SetTitleMode(AgentSessionTitleMode.LastMessage);
        await fixture.Session.ReleaseTitleAsync();
        var summary = await fixture.SummaryAsync();
        Assert.AreEqual("The second answer.", summary.Title);
        Assert.AreEqual(AgentSessionTitleSource.Auto, summary.TitleSource);

        fixture.Session.SetTitleMode(AgentSessionTitleMode.FirstPrompt);
        await fixture.Session.ReleaseTitleAsync();
        Assert.AreEqual("Fix the login error", (await fixture.SummaryAsync()).Title);
        await fixture.Session.SendAsync(new() { Input = AgentInput.Text("One more.") });
        Assert.AreEqual("Fix the login error", (await fixture.SummaryAsync()).Title);
        Assert.IsFalse(fixture.DeveloperInstructions[2]!.Contains(Guidance, StringComparison.Ordinal), "The instruction leaves with the mode.");
    }

    [TestMethod]
    public async Task AResumedSessionIsNotNamedAgainByALaterPrompt()
    {
        using var temp = TestTempDirectory.Create();
        var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(Path.Combine(temp.Path, "machine", "agents")));
        var provider = CreateProvider();
        var summary = CreateSummary("title-resumed") with { Title = "CodeAlta", TitleSource = AgentSessionTitleSource.Auto };
        var state = CreateState(summary.SessionId);
        await store.UpsertSessionAsync(summary);
        await store.UpsertStateAsync(state);
        await using (var first = new AgentSession(ModelProviderIds.OpenAIResponses, provider, summary, state, [], store,
                         new Executor([Answer("An answer.")]), Options(provider, temp.Path, AgentSessionTitleMode.FirstPrompt)))
        {
            await first.SendAsync(new() { Input = AgentInput.Text("Fix the login error.") });
        }

        var saved = (await store.GetSessionSummaryAsync(summary.SessionId))!;
        Assert.AreEqual("Fix the login error", saved.Title);
        var history = await store.ReadEventsAsync(summary.SessionId);
        await using var resumed = new AgentSession(ModelProviderIds.OpenAIResponses, provider, saved, (await store.GetStateAsync(summary.SessionId))!, history, store,
            new Executor([Answer("Another answer.")]), Options(provider, temp.Path, AgentSessionTitleMode.FirstPrompt));
        await resumed.SendAsync(new() { Input = AgentInput.Text("Now rewrite the parser.") });

        Assert.AreEqual("Fix the login error", (await store.GetSessionSummaryAsync(summary.SessionId))?.Title);
    }

    [TestMethod]
    public async Task ASessionWithoutARecordedSourceKeepsItsTitle()
    {
        await using var fixture = await Fixture.CreateAsync("title-unset", AgentSessionTitleMode.LastMessage, source: null,
            Answer("<session_title>From the model</session_title>\nAn answer."));
        await fixture.Session.SendAsync(new() { Input = AgentInput.Text("Fix the login error.") });

        var summary = await fixture.SummaryAsync();
        Assert.AreEqual("CodeAlta", summary.Title);
        Assert.IsNull(summary.TitleSource);
    }

    private static Func<AgentTurnRequest, Func<AgentTurnDelta, CancellationToken, ValueTask>, CancellationToken, Task<AgentTurnResponse>> Answer(string text)
        => (_, _, _) => Task.FromResult(new AgentTurnResponse { AssistantMessage = new(AgentConversationRole.Assistant, [new AgentMessagePart.Text(text)]) });

    private static AgentSessionCreateOptions Options(ModelProviderRuntimeDescriptor provider, string workingDirectory, AgentSessionTitleMode mode)
        => new()
        {
            ProviderKey = provider.ProviderKey,
            Model = "gpt-5.4",
            WorkingDirectory = workingDirectory,
            DeveloperInstructions = "Be brief.",
            TitleMode = mode,
            TitleInstructions = Guidance,
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
        };

    private static ModelProviderRuntimeDescriptor CreateProvider()
        => new()
        {
            ProtocolFamily = "openai-responses",
            ProviderKey = "openai",
            DisplayName = "OpenAI",
            TransportKind = AgentTransportKind.OpenAIResponses,
            BaseUri = new Uri("https://api.openai.com/v1"),
            Profile = new AgentProviderProfile { SupportsDeveloperRole = true, SupportsStore = true, MaxTokensFieldName = "max_output_tokens" },
            Compaction = AgentCompactionSettings.Default,
        };

    private static AgentSessionSummary CreateSummary(string sessionId)
        => new()
        {
            SessionId = sessionId,
            ProviderId = ModelProviderIds.OpenAIResponses,
            ProtocolFamily = "openai-responses",
            ProviderKey = "openai",
            ModelId = "gpt-5.4",
            WorkingDirectory = "C:\\repo",
            CreatedAt = DateTimeOffset.Parse("2026-04-06T10:00:00+00:00"),
            UpdatedAt = DateTimeOffset.Parse("2026-04-06T10:00:00+00:00"),
        };

    private static AgentSessionState CreateState(string sessionId)
        => new() { SessionId = sessionId, ProtocolFamily = "openai-responses", ProviderKey = "openai", UpdatedAt = DateTimeOffset.Parse("2026-04-06T10:00:00+00:00") };

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly TestTempDirectory _temp;
        private readonly IDisposable _subscription;

        private Fixture(TestTempDirectory temp, FileSystemAgentSessionStore store, AgentSessionSummary summary, AgentSession session, Executor executor)
        {
            _temp = temp;
            Store = store;
            Summary = summary;
            Session = session;
            Requests = executor.Requests;
            _subscription = session.Subscribe(@event => { lock (Live) Live.Add(@event); });
        }

        public FileSystemAgentSessionStore Store { get; }

        public AgentSessionSummary Summary { get; }

        public AgentSession Session { get; }

        public List<AgentTurnRequest> Requests { get; }

        public List<AgentEvent> Live { get; } = [];

        public IReadOnlyList<string?> DeveloperInstructions => [.. Requests.Select(static request => request.DeveloperInstructions)];

        /// <summary>A session titled "CodeAlta", as one created without a title in the project of that name.</summary>
        public static async Task<Fixture> CreateAsync(string sessionId, AgentSessionTitleMode mode, AgentSessionTitleSource? source,
            params Func<AgentTurnRequest, Func<AgentTurnDelta, CancellationToken, ValueTask>, CancellationToken, Task<AgentTurnResponse>>[] steps)
        {
            var temp = TestTempDirectory.Create();
            var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(Path.Combine(temp.Path, "machine", "agents")));
            var provider = CreateProvider();
            var summary = CreateSummary(sessionId) with { Title = "CodeAlta", TitleSource = source };
            var state = CreateState(sessionId);
            await store.UpsertSessionAsync(summary);
            await store.UpsertStateAsync(state);
            var executor = new Executor(steps);
            return new Fixture(temp, store, summary, new AgentSession(ModelProviderIds.OpenAIResponses, provider, summary, state, [], store, executor,
                Options(provider, temp.Path, mode)), executor);
        }

        public async Task<AgentSessionSummary> SummaryAsync() => (await Store.GetSessionSummaryAsync(Summary.SessionId))!;

        public string[] ShownAssistantTexts()
        {
            lock (Live) return [.. Live.OfType<AgentContentCompletedEvent>().Where(static content => content.Kind == AgentContentKind.Assistant).Select(static content => content.Content)];
        }

        public async ValueTask DisposeAsync()
        {
            _subscription.Dispose();
            await Session.DisposeAsync();
            _temp.Dispose();
        }
    }

    private sealed class Executor(IEnumerable<Func<AgentTurnRequest, Func<AgentTurnDelta, CancellationToken, ValueTask>, CancellationToken, Task<AgentTurnResponse>>> steps)
        : IModelProviderTurnExecutor, IModelProviderModelCatalog
    {
        private readonly Queue<Func<AgentTurnRequest, Func<AgentTurnDelta, CancellationToken, ValueTask>, CancellationToken, Task<AgentTurnResponse>>> _steps = new(steps);

        public List<AgentTurnRequest> Requests { get; } = [];

        public Task<IReadOnlyList<AgentModelInfo>> ListModelsAsync(ModelProviderRuntimeDescriptor provider, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AgentModelInfo>>([new AgentModelInfo("gpt-5.4", "GPT-5.4")]);

        public Task<AgentTurnResponse> ExecuteTurnAsync(AgentTurnRequest request, Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return _steps.Dequeue()(request, onUpdate, cancellationToken);
        }
    }
}
