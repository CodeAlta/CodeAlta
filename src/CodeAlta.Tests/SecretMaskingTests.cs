using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog.Secrets;

namespace CodeAlta.Tests;

[TestClass]
public sealed class SecretMaskingTests
{
    private const string Secret = "sk-live-4f9a2c7e1b";

    [TestMethod]
    public void Text_IsMasked_LongestSecretFirst_AndShortValuesAreLeft()
    {
        var masker = new AgentSecretMasker(() => [Secret, Secret + "-extended", "abc"]);

        Assert.AreEqual("key=*** and ***", masker.MaskText($"key={Secret}-extended and {Secret}"));
        Assert.AreEqual("abc stays: shorter than four characters", masker.MaskText("abc stays: shorter than four characters"));
        var unchanged = "nothing secret";
        Assert.AreSame(unchanged, masker.MaskText(unchanged));
        Assert.IsTrue(masker.Contains($"x{Secret}x"));
    }

    [TestMethod]
    public void Json_IsMaskedInsideItsStringsOnly_AndStaysValid()
    {
        const string word = "type";
        var masker = new AgentSecretMasker(() => [word, "p+ss\"w0rd"]);
        var json = JsonSerializer.Serialize(new { type = "the type is here", other = "p+ss\"w0rd" });

        var masked = masker.MaskJson(json);

        using var document = JsonDocument.Parse(masked);
        Assert.AreEqual("the *** is here", document.RootElement.GetProperty("type").GetString(), "The name of a property is no value: it stays.");
        Assert.AreEqual("***", document.RootElement.GetProperty("other").GetString(), "A secret escaped in the JSON is masked as it is written there.");
    }

    [TestMethod]
    public void InPlace_ShortensTheText()
    {
        var masker = new AgentSecretMasker(() => [Secret]);
        var text = $"[INF] token {Secret} sent; again {Secret}.".ToCharArray();

        var length = masker.MaskInPlace(text);

        Assert.AreEqual("[INF] token *** sent; again ***.", new string(text, 0, length));
    }

    [TestMethod]
    public void AToolResult_IsMaskedInItsTextErrorDisplayAndValue()
    {
        var masker = new AgentSecretMasker(() => [Secret]);
        var result = new AgentToolResult(false, [new AgentToolResultItem.Text($"printed {Secret}")], $"failed with {Secret}")
        {
            Display = $"whole {Secret}",
            Structured = JsonSerializer.SerializeToElement(new { output = Secret }),
        };

        var masked = masker.MaskResult(result);

        Assert.AreEqual("printed ***", ((AgentToolResultItem.Text)masked.Items[0]).Value);
        Assert.AreEqual("failed with ***", masked.Error);
        Assert.AreEqual("whole ***", masked.Display);
        Assert.AreEqual("***", masked.Structured!.Value.GetProperty("output").GetString());
        var clean = new AgentToolResult(true, [new AgentToolResultItem.Text("fine")]);
        Assert.AreSame(clean, masker.MaskResult(clean));
    }

    [TestMethod]
    public void TheValues_AreReadAgain_WhenTheStoreChanges()
    {
        var values = new List<string>();
        var masker = new AgentSecretMasker(() => [.. values]);
        Assert.AreEqual(Secret, masker.MaskText(Secret));

        values.Add(Secret);
        masker.Refresh();

        Assert.AreEqual("***", masker.MaskText(Secret));
    }

    [TestMethod]
    public void AValueTheStoreNoLongerGives_StaysMasked_WhileTheProcessThatMayHoldItRuns()
    {
        // The store gives a value, then no longer does (its retired copies were evicted by many rotations, or their time passed),
        // while a process or a stream of this host that took it may still write it.
        var values = new List<string> { Secret };
        var masker = new AgentSecretMasker(() => [.. values]);
        Assert.AreEqual("***", masker.MaskText(Secret));
        values.Clear();
        values.Add("another-rotated-value");
        masker.Refresh();

        Assert.AreEqual("***", masker.MaskText(Secret), "a value masked once stays masked in this process");
        Assert.AreEqual("***", masker.MaskText("another-rotated-value"));
    }

    [TestMethod]
    public async Task ASecretATool_Prints_NeverLandsInTheJournal_NorReachesTheModelOrTheEvents()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-masking-" + Guid.NewGuid().ToString("N"));
        var store = new LocalSecretStore(root, new MasterKeySecretProtector(new FileSecretKeyStore(Path.Combine(root, "secrets", "key"))));
        store.SetSecret("GRAFANA_TOKEN", Secret, "tests");
        try
        {
            var executor = new EchoingTurnExecutor();
            var runtime = CreateRuntime(Path.Combine(root, "state"), executor, new AgentSecretMasker(store.GetSecretValues));
            var printed = new AgentToolDefinition(
                new AgentToolSpec("print_token", "Prints the token.", JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone()),
                static (_, _) => Task.FromResult(new AgentToolResult(true, [new AgentToolResultItem.Text($"GRAFANA_TOKEN={Secret}")])));
            var published = new List<string>();
            await using (var session = await runtime.CreateSessionAsync(new AgentSessionCreateOptions
            {
                ProviderKey = "fixture",
                Model = "model",
                WorkingDirectory = root,
                Tools = [printed],
                OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
            }))
            {
                using var subscription = session.Subscribe(@event => published.Add(@event.ToJson()));
                await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text($"Use the token {Secret} to check Grafana.") });
            }

            var journals = Directory.EnumerateFiles(Path.Combine(root, "state"), "*", SearchOption.AllDirectories).ToArray();
            Assert.IsTrue(journals.Length > 0);
            foreach (var journal in journals)
            {
                var text = File.ReadAllText(journal);
                Assert.IsFalse(text.Contains(Secret, StringComparison.Ordinal), $"{journal} holds the secret.");
            }

            Assert.IsTrue(journals.Any(journal => File.ReadAllText(journal).Contains("GRAFANA_TOKEN=***", StringComparison.Ordinal)), "The output of the tool is journaled, masked.");
            Assert.IsTrue(published.Count > 0);
            Assert.IsFalse(published.Any(static json => json.Contains(Secret, StringComparison.Ordinal)), "What the user is shown holds no secret.");
            Assert.IsFalse(executor.Seen.Any(static text => text.Contains("GRAFANA_TOKEN=" + Secret, StringComparison.Ordinal)), "The model is never sent the secret a tool printed.");
            Assert.IsTrue(executor.Seen.Any(static text => text.Contains("GRAFANA_TOKEN=***", StringComparison.Ordinal)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void AStream_MasksASecretSplitAtEveryPosition_AndHoldsBackLessThanTheLongestSecret()
    {
        var longer = Secret + "-extended";
        var masker = new AgentSecretMasker(() => [Secret, longer]);
        foreach (var (text, expected) in new[] { ($"a {longer} b", "a *** b"), ($"x {Secret}-ext", "x ***-ext"), ($"{Secret}{Secret}", "******"), ("no secret here", "no secret here") })
        {
            for (var cut = 0; cut <= text.Length; cut++)
            {
                var stream = masker.CreateStream();
                var shown = stream.Write(text[..cut]);
                Assert.IsLessThan(longer.Length, stream.HeldLength, $"cut {cut} of '{text}'");
                shown += stream.Write(text[cut..]);
                shown += stream.Flush();
                Assert.AreEqual(expected, shown, $"cut {cut} of '{text}'");
            }

            var byCharacter = masker.CreateStream();
            var whole = string.Concat(text.Select(character => byCharacter.Write(character.ToString()))) + byCharacter.Flush();
            Assert.AreEqual(expected, whole, $"'{text}' one character at a time");
        }
    }

    [TestMethod]
    public void AnException_IsMaskedInItsMessageStackAndInnerExceptions_OrLeftAsItIs()
    {
        var masker = new AgentSecretMasker(() => [Secret]);
        Exception thrown;
        try
        {
            throw new InvalidOperationException($"failed with {Secret}", new IOException($"inner {Secret}"));
        }
        catch (InvalidOperationException exception)
        {
            thrown = exception;
        }

        var masked = masker.MaskException(thrown);

        var replaced = Assert.IsInstanceOfType<AgentMaskedException>(masked);
        Assert.AreEqual("failed with ***", replaced.Message);
        Assert.AreEqual(typeof(InvalidOperationException).FullName, replaced.OriginalType);
        Assert.IsFalse(replaced.ToString().Contains(Secret, StringComparison.Ordinal));
        StringAssert.Contains(replaced.ToString(), "System.InvalidOperationException: failed with ***");
        StringAssert.Contains(replaced.ToString(), "inner ***");
        StringAssert.Contains(replaced.ToString(), nameof(AnException_IsMaskedInItsMessageStackAndInnerExceptions_OrLeftAsItIs), "The stack stays.");
        Assert.AreEqual("inner ***", replaced.InnerException!.Message);
        var clean = new InvalidOperationException("fine");
        Assert.AreSame(clean, masker.MaskException(clean));
    }

    [TestMethod]
    public async Task TheFilesOfABundle_AreCopiedMasked_TheirJsonStaysJson_AndAPictureIsCopiedAsItIs()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-bundle-masking-" + Guid.NewGuid().ToString("N"));
        var source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
        try
        {
            const string type = "type";
            var masker = new AgentSecretMasker(() => [Secret, "-----BEGIN KEY-----\nAAAA\n-----END KEY-----", type]);
            Directory.CreateDirectory(Path.Combine(source, "tool-output"));
            var output = string.Concat(Enumerable.Range(0, 20_000).Select(static line => $"line {line}\n")) + $"token {Secret}\n-----BEGIN KEY-----\nAAAA\n-----END KEY-----\n";
            File.WriteAllText(Path.Combine(source, "tool-output", "shell.txt"), "﻿" + output);
            File.WriteAllText(Path.Combine(source, "journal.jsonl"), $$"""{"type":"text","content":"the {{Secret}} and the type"}""" + "\n" + """{"broken":"line""" + "\n" + $$"""{"type":"x","v":"{{Secret}}"}""" + "\n");
            File.WriteAllText(Path.Combine(source, "state.json"), $$"""{ "type": "{{Secret}}" }""");
            var picture = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0, 0, 0, 13 }.Concat(System.Text.Encoding.UTF8.GetBytes(Secret)).ToArray();
            File.WriteAllBytes(Path.Combine(source, "picture.png"), picture);

            var target = Path.Combine(root, "bundle", "attachments");
            await CodeAlta.Catalog.SessionMoves.SessionBundle.CopyFolderAsync(source, target, masker, CancellationToken.None);
            var journal = await CodeAlta.Catalog.SessionMoves.SessionBundle.CopyIntoAsync(Path.Combine(source, "journal.jsonl"), Path.Combine(root, "bundle"), "journal.jsonl", masker, CancellationToken.None);

            var shell = File.ReadAllBytes(Path.Combine(target, "tool-output", "shell.txt"));
            var expected = "﻿" + output.Replace(Secret, "***", StringComparison.Ordinal).Replace("-----BEGIN KEY-----\nAAAA\n-----END KEY-----", "***", StringComparison.Ordinal);
            CollectionAssert.AreEqual(new System.Text.UTF8Encoding(false).GetBytes(expected), shell,
                "A text keeps its byte order mark and its lines, and a secret on several lines is masked whole.");
            var lines = File.ReadAllLines(Path.Combine(target, "journal.jsonl"));
            Assert.AreEqual("""{"type":"text","content":"the *** and the ***"}""", lines[0], "The names of a line of JSON stay, also one that is a secret.");
            Assert.AreEqual("""{"broken":"line""", lines[1]);
            Assert.AreEqual("""{"type":"x","v":"***"}""", lines[2], "A broken line does not change how the next one is read.");
            Assert.AreEqual("""{ "type": "***" }""", File.ReadAllText(Path.Combine(target, "state.json")));
            CollectionAssert.AreEqual(picture, File.ReadAllBytes(Path.Combine(target, "picture.png")), "A file that is no text is copied as it is.");
            Assert.AreEqual(new FileInfo(Path.Combine(root, "bundle", "journal.jsonl")).Length, journal.Length, "The hash is that of the masked copy.");
            Assert.IsFalse(File.ReadAllText(Path.Combine(root, "bundle", "journal.jsonl")).Contains(Secret, StringComparison.Ordinal));

            File.Copy(Path.Combine(source, "journal.jsonl"), Path.Combine(root, "provider.jsonl"));
            await CodeAlta.Catalog.SessionMoves.SessionBundle.MaskFolderAsync(root, masker, CancellationToken.None);
            Assert.IsFalse(File.ReadAllText(Path.Combine(root, "provider.jsonl")).Contains(Secret, StringComparison.Ordinal), "A folder written by a provider is masked where it is.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ASecretSplitBetweenDeltas_IsNeverShown_WhereverItIsSplit()
    {
        const string prefix = "token ";
        const string suffix = " end";
        var contents = new List<(string Id, string[] Deltas)>();
        for (var cut = 1; cut < Secret.Length; cut++)
        {
            contents.Add(($"answer-{cut}", [prefix + Secret[..cut], Secret[cut..] + suffix]));
        }

        var text = prefix + Secret + suffix;
        contents.Add(("answer-characters", [.. text.Select(static character => character.ToString())]));
        var executor = new ScriptedTurnExecutor(async (onUpdate, token) =>
        {
            foreach (var (id, deltas) in contents)
            {
                foreach (var delta in deltas)
                {
                    await onUpdate(new AgentTurnDelta { Kind = AgentContentKind.Assistant, ContentId = id, Text = delta }, token);
                }
            }

            return new AgentTurnResponse
            {
                AssistantMessage = new AgentConversationMessage(AgentConversationRole.Assistant, [.. contents.Select(_ => new AgentMessagePart.Text(text))]),
                AssistantPartContentIds = [.. contents.Select(static content => content.Id)],
            };
        });

        var run = await RunSessionAsync(executor, [], "Show the token.");

        foreach (var (id, _) in contents)
        {
            var shown = string.Concat(run.Published.OfType<AgentContentDeltaEvent>().Where(delta => delta.ContentId == id).Select(static delta => delta.Delta));
            Assert.AreEqual("token *** end", shown, $"The deltas of {id}, as a watch puts them together.");
        }

        // The summary of the session, its last answer, is listed and journaled masked too.
        AssertNoSecret(run);
    }

    [TestMethod]
    public async Task TheCallsOfAScript_NeverJournalASecret_InTheirArgumentsOrTheDiffOfTheFilesTheyChange()
    {
        var executor = new ScriptedTurnExecutor((_, _) => Task.FromResult(Call("run_script", new { })));
        var script = new AgentToolDefinition(
            new AgentToolSpec("run_script", "Runs a script.", JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone()) { Exposure = AgentToolExposure.Model },
            static async (invocation, token) =>
            {
                // A setting edited next to a stored token: the diff has the token as its context.
                var edited = await invocation.Tools!.InvokeAsync(new AgentNestedToolCall("replace_in_file",
                    JsonSerializer.SerializeToElement(new { path = "settings.env", old_string = "mode=1", new_string = "mode=2" })), token);
                var written = await invocation.Tools.InvokeAsync(new AgentNestedToolCall("write_file",
                    JsonSerializer.SerializeToElement(new { path = "notes.txt", content = $"the token is {Secret}\n" })), token);
                return new AgentToolResult(edited.Success && written.Success, [new AgentToolResultItem.Text(edited.Success && written.Success ? "done" : "failed")]);
            });

        var run = await RunSessionAsync(executor, [script], "Change the mode.", workspace =>
            File.WriteAllText(Path.Combine(workspace, "settings.env"), $"GRAFANA_TOKEN={Secret}\nmode=1\n"));

        Assert.IsTrue(run.Published.OfType<AgentActivityEvent>().Any(static activity => activity.ParentActivityId == "call-run_script" && activity.Phase == AgentActivityPhase.Completed),
            "The calls of the script are recorded.");
        Assert.IsTrue(run.Journals.Any(static text => text.Contains("GRAFANA_TOKEN=***", StringComparison.Ordinal)), "The diff of the call is journaled, masked.");
        AssertNoSecret(run);
    }

    [TestMethod]
    public async Task TheWholeOfALongOutput_IsSavedBesideTheJournal_Masked()
    {
        // The command builds the secret from two parts: what the model sent holds none.
        var command = OperatingSystem.IsWindows()
            ? "1..5000 | ForEach-Object { \"line-$_ \" + 'sk-live-' + '4f9a2c7e1b' }"
            : "seq -f 'line-%g sk-live-''4f9a2c7e1b' 1 5000";
        var executor = new ScriptedTurnExecutor((_, _) => Task.FromResult(Call("shell_command", new { command, timeoutMs = 60_000 })));

        var run = await RunSessionAsync(executor, [], "List the lines.");

        var saved = run.Files.Where(static file => file.Path.Contains("tool-output", StringComparison.Ordinal)).ToArray();
        Assert.HasCount(1, saved, "The whole output is kept with the session.");
        StringAssert.Contains(saved[0].Text, "line-2500 ***");
        AssertNoSecret(run);
    }

    [TestMethod]
    public async Task APermissionRequest_IsAskedMasked_AsTheCallIsShown()
    {
        // The person who answers reads the request, here or on another computer: the command holds the secret the model wrote.
        var executor = new ScriptedTurnExecutor((_, _) => Task.FromResult(Call("shell_command", new { command = $"echo the token is {Secret}" })));
        var asked = new List<AgentPermissionRequest>();

        var run = await RunSessionAsync(executor, [], "Print the token.", onPermission: (request, _) =>
        {
            lock (asked) asked.Add(request);
            return Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.Deny));
        });

        var command = asked.OfType<AgentCommandPermissionRequest>().Single();
        Assert.AreEqual("echo the token is ***", command.Command);
        AssertNoSecret(run);
    }

    [TestMethod]
    public async Task ASuggestedNextPrompt_IsToldMasked()
    {
        var executor = new ScriptedTurnExecutor((_, _) => Task.FromResult(new AgentTurnResponse
        {
            AssistantMessage = new AgentConversationMessage(AgentConversationRole.Assistant,
                [new AgentMessagePart.Text($"Done.\n<next_prompt>check with {Secret}</next_prompt>")]),
        }));

        var run = await RunSessionAsync(executor, [], "Check Grafana.", suggestions: true);

        Assert.AreEqual("check with ***", run.Published.OfType<AgentPromptSuggestionEvent>().Single().Suggestion, "A suggestion is told, never recorded: it is masked as it is told.");
        AssertNoSecret(run);
    }

    private static void AssertNoSecret(SessionRun run)
    {
        foreach (var (path, text) in run.Files)
        {
            Assert.IsFalse(text.Contains(Secret, StringComparison.Ordinal), $"{path} holds the secret.");
        }

        Assert.IsFalse(run.Published.Any(static @event => @event.ToJson().Contains(Secret, StringComparison.Ordinal)), "What the user is shown holds no secret.");
    }

    private static AgentTurnResponse Call(string name, object arguments)
        => new()
        {
            AssistantMessage = new AgentConversationMessage(AgentConversationRole.Assistant,
                [new AgentMessagePart.ToolCall("call-" + name, name, JsonSerializer.SerializeToElement(arguments))]),
        };

    // Runs one prompt in a session whose secret store holds the secret, and reads back the files of its state and what it published.
    private static async Task<SessionRun> RunSessionAsync(IModelProviderTurnExecutor executor, IReadOnlyList<AgentToolDefinition> tools, string prompt,
        Action<string>? prepare = null, bool suggestions = false, AgentPermissionRequestHandler? onPermission = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-masking-" + Guid.NewGuid().ToString("N"));
        var workspace = Directory.CreateDirectory(Path.Combine(root, "workspace")).FullName;
        prepare?.Invoke(workspace);
        var store = new LocalSecretStore(root, new MasterKeySecretProtector(new FileSecretKeyStore(Path.Combine(root, "secrets", "key"))));
        store.SetSecret("GRAFANA_TOKEN", Secret, "tests");
        try
        {
            var runtime = CreateRuntime(Path.Combine(root, "state"), executor, new AgentSecretMasker(store.GetSecretValues));
            var published = new List<AgentEvent>();
            await using (var session = await runtime.CreateSessionAsync(new AgentSessionCreateOptions
            {
                ProviderKey = "fixture",
                Model = "model",
                WorkingDirectory = workspace,
                Tools = tools,
                PromptSuggestions = suggestions,
                PromptSuggestionInstructions = suggestions ? "End with a <next_prompt> tag." : null,
                OnPermissionRequest = onPermission ?? (static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce))),
            }))
            {
                using var subscription = session.Subscribe(@event =>
                {
                    lock (published) published.Add(@event);
                });
                await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text(prompt) });
            }

            var files = Directory.EnumerateFiles(Path.Combine(root, "state"), "*", SearchOption.AllDirectories).Select(static path => (path, File.ReadAllText(path))).ToArray();
            Assert.IsTrue(files.Length > 0);
            lock (published) return new SessionRun(files, [.. published]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed record SessionRun(IReadOnlyList<(string Path, string Text)> Files, IReadOnlyList<AgentEvent> Published)
    {
        public IEnumerable<string> Journals => Files.Select(static file => file.Text);
    }

    // Plays its turns in order, then answers; a turn is given the callback of the deltas.
    private sealed class ScriptedTurnExecutor(params Func<Func<AgentTurnDelta, CancellationToken, ValueTask>, CancellationToken, Task<AgentTurnResponse>>[] turns) : IModelProviderTurnExecutor
    {
        private int _turn;

        public Task<AgentTurnResponse> ExecuteTurnAsync(AgentTurnRequest request, Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate, CancellationToken cancellationToken = default)
            => _turn < turns.Length
                ? turns[_turn++](onUpdate, cancellationToken)
                : Task.FromResult(new AgentTurnResponse
                {
                    AssistantMessage = new AgentConversationMessage(AgentConversationRole.Assistant, [new AgentMessagePart.Text("Done.")]),
                });
    }

    private static AgentRuntime CreateRuntime(string stateRoot, IModelProviderTurnExecutor executor, AgentSecretMasker masker)
        => new(new ModelProviderId("fixture"), "Fixture", new AgentRuntimeOptions
        {
            StateRootPath = stateRoot,
            SecretMasker = masker,
            Providers =
            [
                new AgentRuntimeProviderRegistration
                {
                    Provider = new ModelProviderRuntimeDescriptor
                    {
                        ProtocolFamily = "openai-responses",
                        ProviderKey = "fixture",
                        DisplayName = "Fixture",
                        TransportKind = AgentTransportKind.OpenAIResponses,
                        BaseUri = new Uri("https://provider.invalid/v1"),
                        IsDefault = true,
                        Profile = new AgentProviderProfile { MaxTokensFieldName = "max_output_tokens" },
                    },
                    TurnExecutor = executor,
                },
            ],
        });

    // Calls the tool once, then answers; it records the text of every message it is sent after the first turn.
    private sealed class EchoingTurnExecutor : IModelProviderTurnExecutor
    {
        private int _turns;

        public List<string> Seen { get; } = [];

        public Task<AgentTurnResponse> ExecuteTurnAsync(AgentTurnRequest request, Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate, CancellationToken cancellationToken = default)
        {
            if (++_turns > 1)
            {
                Seen.AddRange(request.Conversation.Select(static message => message.ToJson()));
            }

            return Task.FromResult(new AgentTurnResponse
            {
                AssistantMessage = _turns == 1
                    ? new AgentConversationMessage(AgentConversationRole.Assistant, [new AgentMessagePart.ToolCall("call-1", "print_token", JsonSerializer.SerializeToElement(new { }))])
                    : new AgentConversationMessage(AgentConversationRole.Assistant, [new AgentMessagePart.Text("Grafana answered.")]),
            });
        }
    }
}
