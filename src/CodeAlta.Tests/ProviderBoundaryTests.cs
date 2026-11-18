using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Orchestration.Runtime.Plugins;
using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Tests;

[TestClass]
public sealed class ProviderBoundaryTests
{
    private const string Name = "Dupont";
    private const string Token = "[PERSON_001]";

    [TestMethod]
    public async Task AConfidentialSession_SendsOnlyTokens_AndKeepsTheRealValuesForTheToolsAndTheUser()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-boundary-" + Guid.NewGuid().ToString("N"));
        try
        {
            var executor = new RecordingTurnExecutor(
                _ => new AgentConversationMessage(AgentConversationRole.Assistant,
                    [new AgentMessagePart.ToolCall("call-1", "open_file", JsonSerializer.SerializeToElement(new { client = $"Maître {Token}" }))]),
                _ => new AgentConversationMessage(AgentConversationRole.Assistant, [new AgentMessagePart.Text($"Le dossier de {Token} est ouvert.")]));
            string? toolArgument = null;
            var tool = new AgentToolDefinition(
                new AgentToolSpec("open_file", "Opens a file.", JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone()),
                (invocation, _) =>
                {
                    toolArgument = invocation.Arguments.GetProperty("client").GetString();
                    return Task.FromResult(new AgentToolResult(true, [new AgentToolResultItem.Text($"Dossier {Name}, 12 pages.")]));
                });
            var published = new List<string>();
            var runtime = CreateRuntime(Path.Combine(root, "state"), executor);
            await using (var session = await runtime.CreateSessionAsync(new AgentSessionCreateOptions
            {
                ProviderKey = "fixture",
                Model = "model",
                WorkingDirectory = root,
                Tools = [tool],
                ProviderBoundary = new FixedSource(new ReplacingBoundary()),
                OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
            }))
            {
                using var subscription = session.Subscribe(@event => published.Add(@event.ToJson()));
                await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text($"Ouvre le dossier de M. {Name}.") });
            }

            Assert.AreEqual(2, executor.Requests.Count);
            Assert.IsFalse(executor.Requests.Any(static text => text.Contains(Name, StringComparison.Ordinal)), "The provider never sees the real value.");
            Assert.IsTrue(executor.Requests[1].Contains($"Dossier {Token}", StringComparison.Ordinal), "The output of the tool is pseudonymized before it is sent.");
            Assert.AreEqual($"Maître {Name}", toolArgument, "The tool receives the real value.");
            Assert.IsTrue(published.Any(static json => json.Contains($"Le dossier de {Name} est ouvert.", StringComparison.Ordinal)), "The user reads the real value.");
            Assert.IsFalse(published.Any(static json => json.Contains(Token, StringComparison.Ordinal)), "No token reaches the user.");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ASessionTheSourceRefuses_SendsNothing()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-boundary-" + Guid.NewGuid().ToString("N"));
        try
        {
            var executor = new RecordingTurnExecutor(_ => new AgentConversationMessage(AgentConversationRole.Assistant, [new AgentMessagePart.Text("Done.")]));
            var errors = new List<string>();
            var runtime = CreateRuntime(Path.Combine(root, "state"), executor);
            await using (var session = await runtime.CreateSessionAsync(new AgentSessionCreateOptions
            {
                ProviderKey = "fixture",
                Model = "model",
                WorkingDirectory = root,
                ProviderBoundary = new RefusingSource(),
                OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
            }))
            {
                using var subscription = session.Subscribe(@event =>
                {
                    if (@event is AgentErrorEvent error) errors.Add(error.Message);
                });
                try
                {
                    await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text($"Ouvre le dossier de M. {Name}.") });
                }
                catch (Exception exception) when (exception is not AssertFailedException)
                {
                    errors.Add(exception.Message);
                }
            }

            Assert.AreEqual(0, executor.Requests.Count, "Nothing reaches the provider.");
            Assert.IsTrue(errors.Any(static message => message.Contains("not ready", StringComparison.Ordinal)), string.Join(" | ", errors));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ATokenSplitBetweenDeltas_IsRestoredWhole()
    {
        foreach (var cut in Enumerable.Range(1, $"Bonjour {Token} !".Length - 1))
        {
            var text = $"Bonjour {Token} !";
            var inner = new StreamingTurnExecutor([text[..cut], text[cut..]]);
            var executor = new ProviderBoundaryTurnExecutor(inner, new FixedSource(new ReplacingBoundary()), providerRunsOwnTools: false);
            var shown = new List<string>();
            await executor.ExecuteTurnAsync(CreateRequest("Bonjour"), (delta, _) =>
            {
                shown.Add(delta.Text);
                return ValueTask.CompletedTask;
            });

            Assert.AreEqual($"Bonjour {Name} !", string.Concat(shown), $"cut at {cut}");
            Assert.IsFalse(shown.Any(static part => part.Contains('[', StringComparison.Ordinal)), $"cut at {cut}: no part of a token is shown");
        }
    }

    [TestMethod]
    public async Task WhatAnAttemptTheProviderStartedAgainHeldBack_IsNotShown()
    {
        // The first attempt ends in the middle of a token, which its stream holds back; the provider then starts again.
        var inner = new AttemptsTurnExecutor([("first", $"Bonjour {Token[..5]}"), ("second", $"Salut {Token[..5]}"), ("second", $"{Token[5..]} !")]);
        var executor = new ProviderBoundaryTurnExecutor(inner, new FixedSource(new ReplacingBoundary()), providerRunsOwnTools: false);
        var shown = new List<(string? Attempt, string Text)>();
        await executor.ExecuteTurnAsync(CreateRequest("Bonjour"), (delta, _) =>
        {
            shown.Add((delta.AttemptId, delta.Text));
            return ValueTask.CompletedTask;
        });

        Assert.AreEqual("Bonjour ", string.Concat(shown.Where(static part => part.Attempt == "first").Select(static part => part.Text)), "The held end of the attempt that was started again is dropped.");
        Assert.AreEqual($"Salut {Name} !", string.Concat(shown.Where(static part => part.Attempt == "second").Select(static part => part.Text)));
    }

    [TestMethod]
    public async Task ASourceThatSaysConfidential_AndGivesNoBoundary_SendsNothing()
    {
        var inner = new StreamingTurnExecutor(["Done."]);
        var executor = new ProviderBoundaryTurnExecutor(inner, new NoBoundarySource(), providerRunsOwnTools: false);

        await Assert.ThrowsExactlyAsync<AgentTurnExecutionException>(() => executor.ExecuteTurnAsync(CreateRequest("Bonjour"), static (_, _) => ValueTask.CompletedTask));
        Assert.AreEqual(0, inner.Calls);
    }

    [TestMethod]
    public async Task APluginOfAnotherProject_IsNotAskedTheBoundaryOfASession()
    {
        using var temp = TestTempDirectory.Create();
        var registry = new PluginContributionRegistry();
        var activation = await new PluginRuntimeActivator(registry).ActivateAsync(
            new DiscoveredPluginType { Type = typeof(BoundaryPlugin), Descriptor = PluginDescriptorFactory.FromType(typeof(BoundaryPlugin)) }, null, null,
            new PluginActivationOptions
            {
                HostInfo = new PluginHostInfo { ApplicationName = "CodeAlta.Tests", Version = "1.0.0", HostApiVersion = "1.0.0", UserDataDirectory = temp.Path, IsHeadless = true },
            });
        Assert.IsTrue(activation.Succeeded, string.Join(Environment.NewLine, activation.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        var global = activation.ActivePlugin!;
        // The same plugin as one of the project "project-a" only.
        using var lifetime = new PluginActivationLifetime(default);
        var scoped = new ActivePluginInstance(global.Instance!, global.Descriptor, null, null,
            new PluginRuntimeContext
            {
                Plugin = global.Descriptor,
                Services = global.RuntimeContext.Services,
                Logger = global.RuntimeContext.Logger,
                PackageDirectory = "",
                Host = global.RuntimeContext.Host,
                LifetimeCancellationToken = lifetime.Token,
                Scope = PluginScope.Project,
                ScopeProjectId = "project-a",
            },
            [], registry, new PluginRuntimeTaskService(lifetime.Token), lifetime);
        var bridge = new PluginOrchestrationBridge(new PluginContributionAdapterService(registry), () => [scoped]);

        Assert.IsNotNull(bridge.GetProviderBoundary(new SessionToolHookScope("session-a", "project-a", temp.Path, null)));
        Assert.IsNull(bridge.GetProviderBoundary(new SessionToolHookScope("session-b", "project-b", temp.Path, null)), "A plugin of another project does not answer for the session.");
        await global.DeactivateAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task AProviderThatRunsItsOwnTools_IsRefused_InAConfidentialSession()
    {
        var inner = new StreamingTurnExecutor(["Done."]);
        var executor = new ProviderBoundaryTurnExecutor(inner, new FixedSource(new ReplacingBoundary()), providerRunsOwnTools: true);

        await Assert.ThrowsExactlyAsync<AgentTurnExecutionException>(() => executor.ExecuteTurnAsync(CreateRequest("Bonjour"), static (_, _) => ValueTask.CompletedTask));
        Assert.AreEqual(0, inner.Calls);
    }

    [TestMethod]
    public async Task AnImage_IsReplacedByANote_AndTheRequestIsSent_InAConfidentialSession()
    {
        var inner = new StreamingTurnExecutor(["Done."]);
        var executor = new ProviderBoundaryTurnExecutor(inner, new FixedSource(new ReplacingBoundary()), providerRunsOwnTools: false);
        var screenshot = new AgentToolResult(true, [new AgentToolResultItem.Text("Captured."), new AgentToolResultItem.LocalImage(@"C:\attachments\screen.png", "image/png", "screen.png")]);
        var request = CreateRequest("Regarde") with
        {
            Conversation =
            [
                new AgentConversationMessage(AgentConversationRole.User, [new AgentMessagePart.Text("Regarde"), new AgentMessagePart.Data("AAAA", "image/png", $"scan {Name}.png")]),
                new AgentConversationMessage(AgentConversationRole.Tool, [new AgentMessagePart.ToolResult("call", screenshot)]),
            ],
        };

        await executor.ExecuteTurnAsync(request, static (_, _) => ValueTask.CompletedTask);

        Assert.AreEqual(1, inner.Calls, "An image no longer stops the session: every request after it would fail.");
        var sent = inner.LastRequest!;
        var attached = (AgentMessagePart.Text)sent.Conversation[0].Parts[1];
        StringAssert.Contains(attached.Value, "Image not sent");
        StringAssert.Contains(attached.Value, Token, "The note goes through the boundary: the name of the image is pseudonymized.");
        Assert.IsFalse(attached.Value.Contains(Name, StringComparison.Ordinal));
        var result = ((AgentMessagePart.ToolResult)sent.Conversation[1].Parts[0]).Result;
        Assert.IsTrue(result.Items.All(static item => item is AgentToolResultItem.Text), "The image of a tool is a note too.");
        StringAssert.Contains(((AgentToolResultItem.Text)result.Items[1]).Value, "screen.png");
    }

    [TestMethod]
    public async Task APdfThatWasNotConverted_IsRefused_InAConfidentialSession()
    {
        var inner = new StreamingTurnExecutor(["Done."]);
        var executor = new ProviderBoundaryTurnExecutor(inner, new FixedSource(new ReplacingBoundary()), providerRunsOwnTools: false);
        var request = CreateRequest("Lis") with
        {
            Conversation = [new AgentConversationMessage(AgentConversationRole.User, [new AgentMessagePart.Text("Lis"), new AgentMessagePart.Data("JVBERi0=", AgentPdfInputs.MediaType)])],
        };

        var refused = await Assert.ThrowsExactlyAsync<AgentTurnExecutionException>(() => executor.ExecuteTurnAsync(request, static (_, _) => ValueTask.CompletedTask));
        StringAssert.Contains(refused.Message, "PDF");
        Assert.AreEqual(0, inner.Calls);
    }

    [TestMethod]
    public async Task ASessionThatIsNotConfidential_SendsItsRequestsAsTheyAre()
    {
        var inner = new StreamingTurnExecutor(["Done."]);
        var executor = new ProviderBoundaryTurnExecutor(inner, new FixedSource(null), providerRunsOwnTools: true);

        await executor.ExecuteTurnAsync(CreateRequest($"Bonjour {Name}"), static (_, _) => ValueTask.CompletedTask);

        Assert.AreEqual(1, inner.Calls);
        StringAssert.Contains(inner.LastRequest!.Conversation[0].ToJson(), Name);
    }

    private static AgentTurnRequest CreateRequest(string text)
    {
        var provider = Descriptor();
        return new AgentTurnRequest
        {
            Provider = provider,
            ProviderId = new ModelProviderId("fixture"),
            SessionId = "session",
            RunId = new AgentRunId("run"),
            ModelId = "model",
            Conversation = [new AgentConversationMessage(AgentConversationRole.User, [new AgentMessagePart.Text(text)])],
            Tools = [],
            State = new AgentSessionState { SessionId = "session", ProtocolFamily = provider.ProtocolFamily, ProviderKey = provider.ProviderKey, UpdatedAt = DateTimeOffset.UtcNow },
        };
    }

    private static ModelProviderRuntimeDescriptor Descriptor() => new()
    {
        ProtocolFamily = "openai-responses",
        ProviderKey = "fixture",
        DisplayName = "Fixture",
        TransportKind = AgentTransportKind.OpenAIResponses,
        BaseUri = new Uri("https://provider.invalid/v1"),
        IsDefault = true,
        Profile = new AgentProviderProfile { MaxTokensFieldName = "max_output_tokens" },
    };

    private static AgentRuntime CreateRuntime(string stateRoot, IModelProviderTurnExecutor executor)
        => new(new ModelProviderId("fixture"), "Fixture", new AgentRuntimeOptions
        {
            StateRootPath = stateRoot,
            Providers = [new AgentRuntimeProviderRegistration { Provider = Descriptor(), TurnExecutor = executor }],
        });

    private sealed class FixedSource(IAgentProviderBoundary? boundary) : IAgentProviderBoundarySource
    {
        public bool IsConfidential => boundary is not null;

        public ValueTask<IAgentProviderBoundary?> GetBoundaryAsync(AgentTurnRequest request, CancellationToken cancellationToken) => ValueTask.FromResult(boundary);
    }

    // A plugin that gives a boundary to every session it is asked for.
    [Plugin("boundary-fixture", DisplayName = "Boundary fixture")]
    public sealed class BoundaryPlugin : PluginBase
    {
        public override IAgentProviderBoundary? GetProviderBoundary(PluginProviderBoundaryContext context) => new ReplacingBoundary();
    }

    // A source at fault: it says the session is confidential and gives no boundary.
    private sealed class NoBoundarySource : IAgentProviderBoundarySource
    {
        public bool IsConfidential => true;

        public ValueTask<IAgentProviderBoundary?> GetBoundaryAsync(AgentTurnRequest request, CancellationToken cancellationToken) => ValueTask.FromResult<IAgentProviderBoundary?>(null);
    }

    private sealed class RefusingSource : IAgentProviderBoundarySource
    {
        public bool IsConfidential => true;

        public ValueTask<IAgentProviderBoundary?> GetBoundaryAsync(AgentTurnRequest request, CancellationToken cancellationToken)
            => throw new AgentProviderBoundaryException("The pseudonymizer is not ready: nothing was sent.");
    }

    // Replaces one name by one token, the way a real boundary replaces what it detects.
    private sealed class ReplacingBoundary : IAgentProviderBoundary
    {
        public ValueTask<AgentTurnRequest> PrepareRequestAsync(AgentTurnRequest request, CancellationToken cancellationToken)
            => ValueTask.FromResult(request with { Conversation = [.. request.Conversation.Select(message => message with { Parts = [.. message.Parts.Select(Hide)] })] });

        public IAgentBoundaryTextStream CreateResponseStream(AgentContentKind kind, string contentId) => new Stream();

        public AgentTurnResponse RestoreResponse(AgentTurnRequest request, AgentTurnResponse response)
            => response with { AssistantMessage = response.AssistantMessage with { Parts = [.. response.AssistantMessage.Parts.Select(Show)] } };

        private static AgentMessagePart Hide(AgentMessagePart part) => part switch
        {
            AgentMessagePart.Text text => new AgentMessagePart.Text(text.Value.Replace(Name, Token, StringComparison.Ordinal)),
            AgentMessagePart.ToolResult result => result with
            {
                Result = result.Result with { Items = [.. result.Result.Items.Select(static item => item is AgentToolResultItem.Text text ? new AgentToolResultItem.Text(text.Value.Replace(Name, Token, StringComparison.Ordinal)) : item)] },
            },
            AgentMessagePart.ToolCall call => call with { Arguments = JsonDocument.Parse(call.Arguments.GetRawText().Replace(Name, Token, StringComparison.Ordinal)).RootElement.Clone() },
            _ => part,
        };

        private static AgentMessagePart Show(AgentMessagePart part) => part switch
        {
            AgentMessagePart.Text text => new AgentMessagePart.Text(text.Value.Replace(Token, Name, StringComparison.Ordinal)),
            AgentMessagePart.ToolCall call => call with { Arguments = JsonDocument.Parse(call.Arguments.GetRawText().Replace(Token, Name, StringComparison.Ordinal)).RootElement.Clone() },
            _ => part,
        };

        private sealed class Stream : IAgentBoundaryTextStream
        {
            private string _held = string.Empty;

            public string Write(string part)
            {
                var text = _held + part;
                var start = text.LastIndexOf('[');
                if (start >= 0 && Token.StartsWith(text[start..], StringComparison.Ordinal) && text.Length - start < Token.Length)
                {
                    _held = text[start..];
                    return text[..start].Replace(Token, Name, StringComparison.Ordinal);
                }

                _held = string.Empty;
                return text.Replace(Token, Name, StringComparison.Ordinal);
            }

            public string Flush()
            {
                var text = _held;
                _held = string.Empty;
                return text;
            }
        }
    }

    // Answers with one scripted message per request, and records the conversation it is sent.
    private sealed class RecordingTurnExecutor(params Func<AgentTurnRequest, AgentConversationMessage>[] answers) : IModelProviderTurnExecutor
    {
        public List<string> Requests { get; } = [];

        public Task<AgentTurnResponse> ExecuteTurnAsync(AgentTurnRequest request, Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate, CancellationToken cancellationToken = default)
        {
            Requests.Add(string.Join("\n", request.Conversation.Select(static message => message.ToJson())));
            var answer = answers[Math.Min(Requests.Count - 1, answers.Length - 1)](request);
            return Task.FromResult(new AgentTurnResponse { AssistantMessage = answer });
        }
    }

    // Streams scripted parts, each of an attempt: the answer is what the last attempt streamed.
    private sealed class AttemptsTurnExecutor(IReadOnlyList<(string Attempt, string Part)> parts) : IModelProviderTurnExecutor
    {
        public async Task<AgentTurnResponse> ExecuteTurnAsync(AgentTurnRequest request, Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate, CancellationToken cancellationToken = default)
        {
            foreach (var (attempt, part) in parts)
            {
                await onUpdate(new AgentTurnDelta { Kind = AgentContentKind.Assistant, ContentId = "text-1", Text = part, AttemptId = attempt }, cancellationToken);
            }

            var last = parts[^1].Attempt;
            var text = string.Concat(parts.Where(part => part.Attempt == last).Select(static part => part.Part));
            return new AgentTurnResponse { AssistantMessage = new AgentConversationMessage(AgentConversationRole.Assistant, [new AgentMessagePart.Text(text)]) };
        }
    }

    // Streams scripted parts of one answer.
    private sealed class StreamingTurnExecutor(IReadOnlyList<string> parts) : IModelProviderTurnExecutor
    {
        public int Calls { get; private set; }

        public AgentTurnRequest? LastRequest { get; private set; }

        public async Task<AgentTurnResponse> ExecuteTurnAsync(AgentTurnRequest request, Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastRequest = request;
            foreach (var part in parts)
            {
                await onUpdate(new AgentTurnDelta { Kind = AgentContentKind.Assistant, ContentId = "text-1", Text = part }, cancellationToken);
            }

            return new AgentTurnResponse { AssistantMessage = new AgentConversationMessage(AgentConversationRole.Assistant, [new AgentMessagePart.Text(string.Concat(parts))]) };
        }
    }
}
