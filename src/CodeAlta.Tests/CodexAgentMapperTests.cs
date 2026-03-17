using CodeAlta.Agent;
using CodeAlta.Agent.Codex;
using CodeAlta.CodexSdk;
using System.Text.Json;
using V2ReasoningEffort = CodeAlta.CodexSdk.ReasoningEffort;
using V2UserInput = CodeAlta.CodexSdk.UserInput;

namespace CodeAlta.Tests;

[TestClass]
public sealed class CodexAgentMapperTests
{
    [TestMethod]
    public void ToAgentModelInfo_MapsDescriptionAndReasoningEfforts()
    {
        var model = new Model
        {
            Id = "codex-mini",
            DisplayName = "Codex Mini",
            Description = "Fast coding model",
            DefaultReasoningEffort = V2ReasoningEffort.Minimal,
            SupportedReasoningEfforts =
            [
                new ReasoningEffortOption { ReasoningEffort = V2ReasoningEffort.Low, Description = "Low effort" },
                new ReasoningEffortOption { ReasoningEffort = V2ReasoningEffort.Xhigh, Description = "Extra high effort" }
            ]
        };

        var mapped = CodexAgentMapper.ToAgentModelInfo(model);

        Assert.AreEqual("codex-mini", mapped.Id);
        Assert.AreEqual("Codex Mini", mapped.DisplayName);
        Assert.AreEqual("Fast coding model", mapped.Description);
        Assert.AreEqual(AgentReasoningEffort.Minimal, mapped.DefaultReasoningEffort);
        CollectionAssert.AreEqual(
            new[] { AgentReasoningEffort.Low, AgentReasoningEffort.XHigh },
            mapped.SupportedReasoningEfforts!.ToArray());
    }

    [TestMethod]
    public void ToAgentModelInfo_DropsUnsupportedDefaultReasoningEffort()
    {
        var model = new Model
        {
            Id = "codex-mini",
            DefaultReasoningEffort = V2ReasoningEffort.Xhigh,
            SupportedReasoningEfforts =
            [
                new ReasoningEffortOption { ReasoningEffort = V2ReasoningEffort.Low, Description = "Low effort" },
                new ReasoningEffortOption { ReasoningEffort = V2ReasoningEffort.Medium, Description = "Medium effort" },
                new ReasoningEffortOption { ReasoningEffort = V2ReasoningEffort.High, Description = "High effort" }
            ]
        };

        var mapped = CodexAgentMapper.ToAgentModelInfo(model);

        Assert.IsNull(mapped.DefaultReasoningEffort);
        CollectionAssert.AreEqual(
            new[] { AgentReasoningEffort.Low, AgentReasoningEffort.Medium, AgentReasoningEffort.High },
            mapped.SupportedReasoningEfforts!.ToArray());
    }

    [TestMethod]
    public void TryExtractRepository_ParsesHttpsAndSsh()
    {
        var httpsRepository = CodexAgentMapper.TryExtractRepository("https://github.com/octo-org/octo-repo.git");
        var sshRepository = CodexAgentMapper.TryExtractRepository("git@github.com:octo-org/octo-repo.git");

        Assert.AreEqual("octo-org/octo-repo", httpsRepository);
        Assert.AreEqual("octo-org/octo-repo", sshRepository);
    }

    [TestMethod]
    public void ToTurnInput_MapsSupportedItemsAndCreatesFallbackTextForAttachments()
    {
        var input = new AgentInput(
        [
            new AgentInputItem.Text("hello"),
            new AgentInputItem.ImageUrl("https://example.com/image.png"),
            new AgentInputItem.LocalImage(@"C:\images\local.png"),
            new AgentInputItem.Skill("reviewer", "/skills/reviewer"),
            new AgentInputItem.Mention("workspace", "/mentions/workspace"),
            new AgentInputItem.File("Program.cs", "Program.cs", new AgentLineRange(3, 9)),
            new AgentInputItem.Directory("src", "src"),
            new AgentInputItem.Selection(
                "App.cs",
                "Selected block",
                "Console.WriteLine(\"hi\");",
                new AgentSelectionRange(
                    new AgentPosition(10, 2),
                    new AgentPosition(12, 1)))
        ]);

        var mapped = CodexAgentMapper.ToTurnInput(input);

        Assert.AreEqual(6, mapped.Count);
        Assert.IsTrue(mapped.OfType<V2UserInput.TextUserInput>().Any(x => x.Text == "hello"));
        Assert.IsTrue(mapped.OfType<V2UserInput.ImageUserInput>().Any(x => x.Url == "https://example.com/image.png"));
        Assert.IsTrue(mapped.OfType<V2UserInput.LocalImageUserInput>().Any(x => x.Path == @"C:\images\local.png"));
        Assert.IsTrue(mapped.OfType<V2UserInput.SkillUserInput>().Any(x => x.Name == "reviewer"));
        Assert.IsTrue(mapped.OfType<V2UserInput.MentionUserInput>().Any(x => x.Name == "workspace"));

        var fallback = mapped.OfType<V2UserInput.TextUserInput>().Single(x => x.Text != "hello").Text;
        Assert.IsTrue(fallback.Contains("[file] Program.cs", StringComparison.Ordinal));
        Assert.IsTrue(fallback.Contains("[directory] src", StringComparison.Ordinal));
        Assert.IsTrue(fallback.Contains("[selection] Selected block", StringComparison.Ordinal));
        Assert.IsTrue(fallback.Contains("Console.WriteLine(\"hi\");", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ToThreadStartParams_MapsReasoningEffortIntoConfig()
    {
        var parameters = CodexAgentMapper.ToThreadStartParams(
            new AgentSessionCreateOptions
            {
                Model = "gpt-5-mini",
                WorkingDirectory = @"C:\repo",
                ReasoningEffort = AgentReasoningEffort.High,
                OnPermissionRequest = static (_, _) =>
                    Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
            },
            new AskForApproval.OnRequest(),
            SandboxMode.DangerFullAccess);

        Assert.AreEqual("gpt-5-mini", parameters.Model);
        Assert.AreEqual(@"C:\repo", parameters.Cwd);
        Assert.AreEqual(SandboxMode.DangerFullAccess, parameters.Sandbox);
        Assert.IsNotNull(parameters.Config);
        Assert.IsTrue(parameters.Config.ContainsKey("model_reasoning_effort"));
        Assert.AreEqual("high", parameters.Config["model_reasoning_effort"].GetString());
    }

    [TestMethod]
    public void ToThreadStartParams_MapsMcpServerConfigurationIntoConfig()
    {
        var parameters = CodexAgentMapper.ToThreadStartParams(
            new AgentSessionCreateOptions
            {
                Model = "gpt-5-mini",
                OnPermissionRequest = static (_, _) =>
                    Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
                McpServers = new Dictionary<string, AgentMcpServerConfig>(StringComparer.Ordinal)
                {
                    ["local"] = new AgentLocalMcpServerConfig("dotnet")
                    {
                        Arguments = ["run", "--server"],
                        EnvironmentVariables = new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["DOTNET_ENVIRONMENT"] = "Development"
                        },
                        WorkingDirectory = @"C:\repo\mcp",
                        EnabledTools = ["hello_world"],
                        ToolTimeout = TimeSpan.FromSeconds(15),
                        Required = true
                    },
                    ["remote"] = new AgentRemoteMcpServerConfig("https://example.com/mcp")
                    {
                        Headers = new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["X-Test"] = "42"
                        },
                        BearerTokenEnvironmentVariable = "MCP_TOKEN",
                        EnvironmentHeaders = new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["Authorization"] = "MCP_AUTH_HEADER"
                        }
                    }
                }
            },
            new AskForApproval.OnRequest(),
            SandboxMode.DangerFullAccess);

        Assert.IsNotNull(parameters.Config);
        Assert.AreEqual("dotnet", parameters.Config["mcp_servers.local.command"].GetString());
        Assert.AreEqual("run", parameters.Config["mcp_servers.local.args"][0].GetString());
        Assert.AreEqual("Development", parameters.Config["mcp_servers.local.env"].GetProperty("DOTNET_ENVIRONMENT").GetString());
        Assert.AreEqual(@"C:\repo\mcp", parameters.Config["mcp_servers.local.cwd"].GetString());
        Assert.AreEqual("hello_world", parameters.Config["mcp_servers.local.enabled_tools"][0].GetString());
        Assert.IsTrue(parameters.Config["mcp_servers.local.required"].GetBoolean());
        Assert.AreEqual("https://example.com/mcp", parameters.Config["mcp_servers.remote.url"].GetString());
        Assert.AreEqual("42", parameters.Config["mcp_servers.remote.http_headers"].GetProperty("X-Test").GetString());
        Assert.AreEqual("MCP_TOKEN", parameters.Config["mcp_servers.remote.bearer_token_env_var"].GetString());
        Assert.AreEqual("MCP_AUTH_HEADER", parameters.Config["mcp_servers.remote.env_http_headers"].GetProperty("Authorization").GetString());
    }

    [TestMethod]
    public void ToThreadStartParams_ThrowsForSseRemoteMcpServer()
    {
        Assert.ThrowsExactly<NotSupportedException>(() =>
            CodexAgentMapper.ToThreadStartParams(
                new AgentSessionCreateOptions
                {
                    OnPermissionRequest = static (_, _) =>
                        Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
                    McpServers = new Dictionary<string, AgentMcpServerConfig>(StringComparer.Ordinal)
                    {
                        ["remote"] = new AgentRemoteMcpServerConfig("https://example.com/sse")
                        {
                            Transport = AgentMcpRemoteTransport.Sse
                        }
                    }
                },
                new AskForApproval.OnRequest(),
                SandboxMode.DangerFullAccess));
    }

    [TestMethod]
    public void ToTurnStartParams_MapsReasoningEffortOverrides()
    {
        var parameters = CodexAgentMapper.ToTurnStartParams(
            "thread-1",
            AgentInput.Text("hello"),
            @"C:\repo",
            "gpt-5-mini",
            AgentReasoningEffort.Medium,
            SandboxMode.DangerFullAccess);

        Assert.AreEqual("thread-1", parameters.ThreadId);
        Assert.AreEqual(@"C:\repo", parameters.Cwd);
        Assert.AreEqual("gpt-5-mini", parameters.Model);
        Assert.AreEqual(ReasoningEffort.Medium, parameters.Effort);
        Assert.IsInstanceOfType<SandboxPolicy.DangerFullAccessSandboxPolicy>(parameters.SandboxPolicy);
        Assert.AreEqual(1, parameters.Input.Count);
    }

    [TestMethod]
    public void ToTurnSteerParams_MapsExpectedRunIdAndInput()
    {
        var parameters = CodexAgentMapper.ToTurnSteerParams(
            "thread-1",
            new AgentRunId("turn-1"),
            AgentInput.Text("continue"));

        Assert.AreEqual("thread-1", parameters.ThreadId);
        Assert.AreEqual("turn-1", parameters.ExpectedTurnId);
        Assert.AreEqual(1, parameters.Input.Count);
        Assert.AreEqual("continue", ((V2UserInput.TextUserInput)parameters.Input[0]).Text);
    }

    [TestMethod]
    public void ToTurnStartParams_MapsWorkspaceWriteSandboxPolicyWithFullReadAccess()
    {
        var parameters = CodexAgentMapper.ToTurnStartParams(
            "thread-1",
            AgentInput.Text("hello"),
            @"C:\repo",
            "gpt-5-mini",
            reasoningEffort: null,
            SandboxMode.WorkspaceWrite);

        Assert.IsInstanceOfType<SandboxPolicy.WorkspaceWriteSandboxPolicy>(parameters.SandboxPolicy);
        var sandboxPolicy = (SandboxPolicy.WorkspaceWriteSandboxPolicy)parameters.SandboxPolicy;
        Assert.IsInstanceOfType<ReadOnlyAccess.FullAccessReadOnlyAccess>(sandboxPolicy.ReadOnlyAccess);
        CollectionAssert.AreEqual(new[] { @"C:\repo" }, sandboxPolicy.WritableRoots!.Select(static x => x.Value).ToArray());
    }

    [TestMethod]
    public void ToAgentEvent_MapsDeltaMessageAndErrorNotifications()
    {
        var timestamp = DateTimeOffset.Parse("2026-02-25T10:00:00+00:00");

        var deltaNotification = new CodexNotification.AgentMessageDelta(
            new AgentMessageDeltaNotification
            {
                ThreadId = "thread-1",
                TurnId = "turn-1",
                ItemId = "item-1",
                Delta = "abc"
            });

        var deltaEvent = CodexAgentMapper.ToAgentEvent("thread-1", deltaNotification, timestamp);
        Assert.IsInstanceOfType<AgentContentDeltaEvent>(deltaEvent);
        var mappedDelta = (AgentContentDeltaEvent)deltaEvent;
        Assert.AreEqual(AgentContentKind.Assistant, mappedDelta.Kind);
        Assert.AreEqual("abc", mappedDelta.Delta);
        Assert.AreEqual("turn-1", mappedDelta.RunId?.Value);

        var messageNotification = new CodexNotification.ItemCompleted(
            new ItemCompletedNotification
            {
                ThreadId = "thread-1",
                TurnId = "turn-2",
                Item = new ThreadItem.AgentMessageThreadItem
                {
                    Id = "item-2",
                    Text = "final answer",
                    Phase = MessagePhase.FinalAnswer
                }
            });

        var messageEvent = CodexAgentMapper.ToAgentEvent("thread-1", messageNotification, timestamp);
        Assert.IsInstanceOfType<AgentContentCompletedEvent>(messageEvent);
        var mappedMessage = (AgentContentCompletedEvent)messageEvent;
        Assert.AreEqual(AgentContentKind.Assistant, mappedMessage.Kind);
        Assert.AreEqual("final answer", mappedMessage.Content);
        Assert.AreEqual("turn-2", mappedMessage.RunId?.Value);

        var commentaryNotification = new CodexNotification.ItemCompleted(
            new ItemCompletedNotification
            {
                ThreadId = "thread-1",
                TurnId = "turn-2",
                Item = new ThreadItem.AgentMessageThreadItem
                {
                    Id = "item-3",
                    Text = "I’m checking one more file before finalizing.",
                    Phase = MessagePhase.Commentary
                }
            });

        var commentaryEvent = CodexAgentMapper.ToAgentEvent("thread-1", commentaryNotification, timestamp);
        Assert.IsInstanceOfType<AgentContentCompletedEvent>(commentaryEvent);
        Assert.AreEqual(AgentContentKind.Reasoning, ((AgentContentCompletedEvent)commentaryEvent).Kind);

        var errorNotification = new CodexNotification.Error(
            new ErrorNotification
            {
                ThreadId = "thread-1",
                TurnId = "turn-3",
                Error = new TurnError
                {
                    Message = "boom"
                }
            });

        var errorEvent = CodexAgentMapper.ToAgentEvent("thread-1", errorNotification, timestamp);
        Assert.IsInstanceOfType<AgentErrorEvent>(errorEvent);
        var mappedError = (AgentErrorEvent)errorEvent;
        Assert.AreEqual("boom", mappedError.Message);
        Assert.AreEqual("turn-3", mappedError.RunId?.Value);
    }

    [TestMethod]
    public void CreateEmptyToolRequestUserInputResponse_ContainsAllQuestionIds()
    {
        var request = new ToolRequestUserInputParams
        {
            ThreadId = "thread-1",
            TurnId = "turn-1",
            ItemId = "item-1",
            Questions =
            [
                new ToolRequestUserInputQuestion { Id = "q1", Question = "Question 1" },
                new ToolRequestUserInputQuestion { Id = "q2", Question = "Question 2" }
            ]
        };

        var response = CodexAgentMapper.CreateEmptyToolRequestUserInputResponse(request);

        Assert.AreEqual(2, response.Answers.Count);
        Assert.IsTrue(response.Answers.ContainsKey("q1"));
        Assert.IsTrue(response.Answers.ContainsKey("q2"));
        Assert.AreEqual(string.Empty, response.Answers["q1"].Answers.Single());
        Assert.AreEqual(string.Empty, response.Answers["q2"].Answers.Single());
    }

    [TestMethod]
    public void TryGetThreadId_UsesGeneratedServerRequest()
    {
        var request = new ServerRequest.ItemFileChangeRequestApprovalRequest
        {
            Id = new RequestId.IntegerValue { Value = 7 },
            Params = new FileChangeRequestApprovalParams
            {
                ItemId = "item-1",
                ThreadId = "thread-1",
                TurnId = "turn-1"
            }
        };

        var result = CodexAgentMapper.TryGetThreadId(request, out var threadId);

        Assert.IsTrue(result);
        Assert.AreEqual("thread-1", threadId);
    }

    [TestMethod]
    public void ToAgentEvent_MapsReasoningPlanAndCommandLifecycle()
    {
        var timestamp = DateTimeOffset.Parse("2026-02-25T10:00:00+00:00");

        var reasoningNotification = new CodexNotification.ReasoningTextDelta(
            new ReasoningTextDeltaNotification
            {
                ThreadId = "thread-1",
                TurnId = "turn-1",
                ItemId = "reasoning-1",
                ContentIndex = 0,
                Delta = "thinking"
            });

        var reasoningEvent = CodexAgentMapper.ToAgentEvent("thread-1", reasoningNotification, timestamp);
        Assert.IsInstanceOfType<AgentContentDeltaEvent>(reasoningEvent);
        var reasoning = (AgentContentDeltaEvent)reasoningEvent;
        Assert.AreEqual(AgentContentKind.Reasoning, reasoning.Kind);
        Assert.AreEqual("reasoning-1", reasoning.ContentId);
        Assert.AreEqual("thinking", reasoning.Delta);

        var planNotification = new CodexNotification.TurnPlanUpdated(
            new TurnPlanUpdatedNotification
            {
                ThreadId = "thread-1",
                TurnId = "turn-2",
                Explanation = "Plan explanation",
                Plan =
                [
                    new TurnPlanStep { Step = "Inspect files", Status = TurnPlanStepStatus.InProgress },
                    new TurnPlanStep { Step = "Write tests", Status = TurnPlanStepStatus.Pending }
                ]
            });

        var planEvent = CodexAgentMapper.ToAgentEvent("thread-1", planNotification, timestamp);
        Assert.IsInstanceOfType<AgentPlanSnapshotEvent>(planEvent);
        var plan = (AgentPlanSnapshotEvent)planEvent;
        Assert.AreEqual(AgentPlanChangeKind.Updated, plan.Snapshot.ChangeKind);
        Assert.AreEqual("Plan explanation", plan.Snapshot.Explanation);
        Assert.AreEqual(2, plan.Snapshot.Steps!.Count);
        Assert.AreEqual(AgentPlanStepStatus.InProgress, plan.Snapshot.Steps[0].Status);

        var itemStartedNotification = new CodexNotification.ItemStarted(
            new ItemStartedNotification
            {
                ThreadId = "thread-1",
                TurnId = "turn-3",
                Item = new ThreadItem.CommandExecutionThreadItem
                {
                    Id = "cmd-1",
                    Command = @"C:\Program Files\PowerShell\7\pwsh.exe -Command git remote -v",
                    CommandActions =
                    [
                        new CommandAction.UnknownCommandAction
                        {
                            Command = "git remote -v"
                        }
                    ],
                    Cwd = @"C:\repo",
                    Status = CommandExecutionStatus.InProgress
                }
            });

        var commandEvent = CodexAgentMapper.ToAgentEvent("thread-1", itemStartedNotification, timestamp);
        Assert.IsInstanceOfType<AgentActivityEvent>(commandEvent);
        var activity = (AgentActivityEvent)commandEvent;
        Assert.AreEqual(AgentActivityKind.CommandExecution, activity.Kind);
        Assert.AreEqual(AgentActivityPhase.Started, activity.Phase);
        Assert.AreEqual("cmd-1", activity.ActivityId);
        Assert.AreEqual("git remote -v", activity.Name);
        Assert.IsTrue(activity.Details.HasValue);
        Assert.AreEqual("git remote -v", activity.Details.Value.GetProperty("command").GetString());
        Assert.AreEqual(
            @"C:\Program Files\PowerShell\7\pwsh.exe -Command git remote -v",
            activity.Details.Value.GetProperty("rawCommand").GetString());
    }

    [TestMethod]
    public void ToAgentEvent_MapsTurnDiffUpdated()
    {
        var timestamp = DateTimeOffset.Parse("2026-02-25T10:00:00+00:00");
        var notification = new CodexNotification.TurnDiffUpdated(
            new TurnDiffUpdatedNotification
            {
                ThreadId = "thread-1",
                TurnId = "turn-1",
                Diff = "--- a/file.txt"
            });

        var @event = CodexAgentMapper.ToAgentEvent("thread-1", notification, timestamp);

        Assert.IsInstanceOfType<AgentSessionUpdateEvent>(@event);
        var update = (AgentSessionUpdateEvent)@event;
        Assert.AreEqual(AgentSessionUpdateKind.DiffUpdated, update.Kind);
        Assert.AreEqual("--- a/file.txt", update.Details?.GetProperty("diff").GetString());
        Assert.AreEqual("turn-1", update.RunId?.Value);
    }

    [TestMethod]
    public void ToAgentEvent_MapsWebSearchItemLifecycle()
    {
        var timestamp = DateTimeOffset.Parse("2026-02-25T10:00:00+00:00");
        var startedNotification = new CodexNotification.ItemStarted(
            new ItemStartedNotification
            {
                ThreadId = "thread-1",
                TurnId = "turn-1",
                Item = new ThreadItem.WebSearchThreadItem
                {
                    Id = "search-1",
                    Query = "tomlyn project",
                    Action = new WebSearchAction.SearchWebSearchAction
                    {
                        Query = "tomlyn project"
                    }
                }
            });

        var startedEvent = CodexAgentMapper.ToAgentEvent("thread-1", startedNotification, timestamp);

        Assert.IsInstanceOfType<AgentActivityEvent>(startedEvent);
        var started = (AgentActivityEvent)startedEvent;
        Assert.AreEqual(AgentActivityKind.WebSearch, started.Kind);
        Assert.AreEqual(AgentActivityPhase.Started, started.Phase);
        Assert.AreEqual("tomlyn project", started.Name);

        var completedNotification = new CodexNotification.ItemCompleted(
            new ItemCompletedNotification
            {
                ThreadId = "thread-1",
                TurnId = "turn-1",
                Item = new ThreadItem.WebSearchThreadItem
                {
                    Id = "search-1",
                    Query = "tomlyn project",
                    Action = new WebSearchAction.OpenPageWebSearchAction
                    {
                        Url = "https://github.com/xoofx/Tomlyn"
                    }
                }
            });

        var completedEvent = CodexAgentMapper.ToAgentEvent("thread-1", completedNotification, timestamp);

        Assert.IsInstanceOfType<AgentActivityEvent>(completedEvent);
        var completed = (AgentActivityEvent)completedEvent;
        Assert.AreEqual(AgentActivityKind.WebSearch, completed.Kind);
        Assert.AreEqual(AgentActivityPhase.Completed, completed.Phase);
        Assert.AreEqual("https://github.com/xoofx/Tomlyn", completed.Name);
    }

    [TestMethod]
    public void ToAgentEvent_MapsRawResponseLocalShellCall()
    {
        var timestamp = DateTimeOffset.Parse("2026-02-25T10:00:00+00:00");
        using var actionDocument = JsonDocument.Parse("""{"command":"Get-ChildItem -Path C:\\code\\Tomlyn"}""");
        var notification = new CodexNotification.RawResponseItemCompleted(
            new RawResponseItemCompletedNotification
            {
                ThreadId = "thread-1",
                TurnId = "turn-1",
                Item = new ResponseItem.LocalShellCallResponseItem
                {
                    CallId = "call-1",
                    Status = LocalShellStatus.Completed,
                    Action = actionDocument.RootElement.Clone()
                }
            });

        var @event = CodexAgentMapper.ToAgentEvent("thread-1", notification, timestamp);

        Assert.IsInstanceOfType<AgentActivityEvent>(@event);
        var activity = (AgentActivityEvent)@event;
        Assert.AreEqual(AgentActivityKind.CommandExecution, activity.Kind);
        Assert.AreEqual(AgentActivityPhase.Completed, activity.Phase);
        Assert.AreEqual("call-1", activity.ActivityId);
        Assert.AreEqual("Get-ChildItem -Path C:\\code\\Tomlyn", activity.Name);
        Assert.AreEqual("completed", activity.Details?.GetProperty("status").GetString()?.ToLowerInvariant());
        Assert.AreEqual(
            "Get-ChildItem -Path C:\\code\\Tomlyn",
            activity.Details?.GetProperty("command").GetString());
    }

    [TestMethod]
    public void ToAgentEvent_MapsRawResponseAssistantMessage()
    {
        var timestamp = DateTimeOffset.Parse("2026-02-25T10:00:00+00:00");
        var notification = new CodexNotification.RawResponseItemCompleted(
            new RawResponseItemCompletedNotification
            {
                ThreadId = "thread-1",
                TurnId = "turn-1",
                Item = new ResponseItem.MessageResponseItem
                {
                    Id = "message-1",
                    Role = "assistant",
                    Content =
                    [
                        new ContentItem.OutputTextContentItem { Text = "Sure — here's the summary." },
                    ]
                }
            });

        var @event = CodexAgentMapper.ToAgentEvent("thread-1", notification, timestamp);

        Assert.IsInstanceOfType<AgentContentCompletedEvent>(@event);
        var content = (AgentContentCompletedEvent)@event;
        Assert.AreEqual(AgentContentKind.Assistant, content.Kind);
        Assert.AreEqual("message-1", content.ContentId);
        Assert.AreEqual("Sure — here's the summary.", content.Content);
    }

    [TestMethod]
    public void ToAgentEvent_MapsRawFunctionCallArgumentsAsStructuredJson()
    {
        var timestamp = DateTimeOffset.Parse("2026-03-14T21:01:19+00:00");
        var notification = new CodexNotification.RawResponseItemCompleted(
            new RawResponseItemCompletedNotification
            {
                ThreadId = "thread-1",
                TurnId = "turn-1",
                Item = new ResponseItem.FunctionCallResponseItem
                {
                    CallId = "call-1",
                    Name = "shell_command",
                    Arguments = """{"command":"Get-Content C:\\code\\Tomlyn\\readme.md -TotalCount 250","timeout_ms":20000}"""
                }
            });

        var @event = CodexAgentMapper.ToAgentEvent("thread-1", notification, timestamp);

        Assert.IsInstanceOfType<AgentActivityEvent>(@event);
        var activity = (AgentActivityEvent)@event;
        Assert.AreEqual(AgentActivityKind.ToolCall, activity.Kind);
        Assert.AreEqual(AgentActivityPhase.Requested, activity.Phase);
        Assert.AreEqual("shell_command", activity.Name);
        Assert.IsTrue(activity.Details.HasValue);
        var arguments = activity.Details.Value.GetProperty("arguments");
        Assert.AreEqual(JsonValueKind.Object, arguments.ValueKind);
        Assert.AreEqual(@"Get-Content C:\code\Tomlyn\readme.md -TotalCount 250", arguments.GetProperty("command").GetString());
        Assert.AreEqual(20000, arguments.GetProperty("timeout_ms").GetInt32());
    }

    [TestMethod]
    public void ToAgentEvent_MapsRawResponseEncryptedReasoningToPlaceholder()
    {
        var timestamp = DateTimeOffset.Parse("2026-02-25T10:00:00+00:00");
        var notification = new CodexNotification.RawResponseItemCompleted(
            new RawResponseItemCompletedNotification
            {
                ThreadId = "thread-1",
                TurnId = "turn-1",
                Item = new ResponseItem.ReasoningResponseItem
                {
                    Id = "reasoning-1",
                    EncryptedContent = "ciphertext"
                }
            });

        var @event = CodexAgentMapper.ToAgentEvent("thread-1", notification, timestamp);

        Assert.IsInstanceOfType<AgentContentCompletedEvent>(@event);
        var content = (AgentContentCompletedEvent)@event;
        Assert.AreEqual(AgentContentKind.Reasoning, content.Kind);
        StringAssert.Contains(content.Content, "encrypted");
    }

    [TestMethod]
    public void ToHistoryEvents_MapsUserMessageItemsToUserContent()
    {
        var thread = new CodeAlta.CodexSdk.Thread
        {
            UpdatedAt = DateTimeOffset.Parse("2026-02-25T10:00:00+00:00").ToUnixTimeSeconds(),
            Turns =
            [
                new Turn
                {
                    Id = "turn-1",
                    Status = TurnStatus.Completed,
                    Items =
                    [
                        new ThreadItem.UserMessageThreadItem
                        {
                            Id = "user-1",
                            Content =
                            [
                                new UserInput.TextUserInput { Text = "Could you summarize the repo?" },
                            ]
                        }
                    ]
                }
            ]
        };

        var events = CodexAgentMapper.ToHistoryEvents("thread-1", thread);

        Assert.IsInstanceOfType<AgentContentCompletedEvent>(events[0]);
        var content = (AgentContentCompletedEvent)events[0];
        Assert.AreEqual(AgentContentKind.User, content.Kind);
        Assert.AreEqual("Could you summarize the repo?", content.Content);
    }

    [TestMethod]
    public void ToHistoryEvents_SanitizesInlineImagesInUserMessages()
    {
        var thread = new CodeAlta.CodexSdk.Thread
        {
            UpdatedAt = DateTimeOffset.Parse("2026-03-15T14:39:32+00:00").ToUnixTimeSeconds(),
            Turns =
            [
                new Turn
                {
                    Id = "turn-1",
                    Status = TurnStatus.Completed,
                    Items =
                    [
                        new ThreadItem.UserMessageThreadItem
                        {
                            Id = "user-1",
                            Content =
                            [
                                new UserInput.TextUserInput { Text = "Please inspect this." },
                                new UserInput.ImageUserInput { Url = "data:image/png;base64,AAAA" },
                                new UserInput.LocalImageUserInput { Path = @"C:\images\test.png" }
                            ]
                        }
                    ]
                }
            ]
        };

        var events = CodexAgentMapper.ToHistoryEvents("thread-1", thread);

        var user = events.OfType<AgentContentCompletedEvent>().Single(x => x.Kind == AgentContentKind.User);
        Assert.AreEqual($"Please inspect this.{Environment.NewLine}Inline Image{Environment.NewLine}Inline Image", user.Content);
        Assert.IsFalse(user.Content.Contains("data:image", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void ToSessionLogHistoryEvents_MapsExecCommandEventsUsingTypedEventMessages()
    {
        var sessionLogPath = CreateSessionLogFile(
            """{"timestamp":"2026-03-17T10:00:00Z","type":"event_msg","payload":{"type":"task_started","turn_id":"turn-1","model_context_window":258400,"collaboration_mode_kind":"default"}}""",
            """{"timestamp":"2026-03-17T10:00:01Z","type":"event_msg","payload":{"type":"exec_command_begin","call_id":"call-1","command":["C:\\Program Files\\PowerShell\\7\\pwsh.exe","-Command","git remote -v"],"cwd":"C:\\code\\Tomlyn","parsed_cmd":[{"type":"unknown","cmd":"git remote -v"}],"process_id":"1234","source":"agent","turn_id":"turn-1"}}""",
            """{"timestamp":"2026-03-17T10:00:02Z","type":"event_msg","payload":{"type":"exec_command_output_delta","call_id":"call-1","chunk":"origin\thttps://example.com/repo.git (fetch)","stream":"stdout"}}""",
            """{"timestamp":"2026-03-17T10:00:03Z","type":"event_msg","payload":{"type":"exec_command_end","aggregated_output":"origin\thttps://example.com/repo.git (fetch)","call_id":"call-1","command":["C:\\Program Files\\PowerShell\\7\\pwsh.exe","-Command","git remote -v"],"cwd":"C:\\code\\Tomlyn","duration":{"secs":1,"nanos":500000000},"exit_code":0,"formatted_output":"origin\thttps://example.com/repo.git (fetch)","parsed_cmd":[{"type":"unknown","cmd":"git remote -v"}],"process_id":"1234","source":"agent","status":"completed","stderr":"","stdout":"origin\thttps://example.com/repo.git (fetch)","turn_id":"turn-1"}}""");

        try
        {
            var events = CodexAgentMapper.ToSessionLogHistoryEvents("thread-1", sessionLogPath);

            var started = events.OfType<AgentActivityEvent>().Single(x => x.ActivityId == "call-1" && x.Phase == AgentActivityPhase.Started);
            Assert.AreEqual(AgentActivityKind.CommandExecution, started.Kind);
            Assert.AreEqual("git remote -v", started.Name);
            Assert.IsTrue(started.Details.HasValue);
            Assert.AreEqual("git remote -v", started.Details.Value.GetProperty("command").GetString());
            Assert.AreEqual(
                @"C:\Program Files\PowerShell\7\pwsh.exe",
                started.Details.Value.GetProperty("rawCommand").EnumerateArray().First().GetString());
            Assert.AreEqual("1234", started.Details.Value.GetProperty("processId").GetString());

            var output = events.OfType<AgentContentDeltaEvent>().Single(x => x.ContentId == "call-1");
            Assert.AreEqual(AgentContentKind.CommandOutput, output.Kind);
            Assert.AreEqual("origin\thttps://example.com/repo.git (fetch)", output.Delta);

            var completed = events.OfType<AgentActivityEvent>().Single(x => x.ActivityId == "call-1" && x.Phase == AgentActivityPhase.Completed);
            Assert.AreEqual("git remote -v", completed.Name);
            Assert.AreEqual("origin\thttps://example.com/repo.git (fetch)", completed.Message);
            Assert.IsTrue(completed.Details.HasValue);
            Assert.AreEqual(0, completed.Details.Value.GetProperty("exitCode").GetInt32());
        }
        finally
        {
            File.Delete(sessionLogPath);
        }
    }

    [TestMethod]
    public void ToSessionLogHistoryEvents_MapsTypedResponseItemsWithoutJsonFieldProbing()
    {
        var sessionLogPath = CreateSessionLogFile(
            """{"timestamp":"2026-03-17T10:00:00Z","type":"event_msg","payload":{"type":"task_started","turn_id":"turn-1","model_context_window":258400,"collaboration_mode_kind":"default"}}""",
            """{"timestamp":"2026-03-17T10:00:01Z","type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"hello"}]}}""",
            """{"timestamp":"2026-03-17T10:00:02Z","type":"response_item","payload":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"world"}]}}""",
            """{"timestamp":"2026-03-17T10:00:03Z","type":"response_item","payload":{"type":"function_call","name":"shell_command","arguments":"{\"command\":\"git status\",\"cwd\":\"C:\\\\code\\\\CodeAlta\"}","call_id":"call-1"}}""",
            """{"timestamp":"2026-03-17T10:00:04Z","type":"response_item","payload":{"type":"function_call_output","call_id":"call-1","output":{"body":"Exit code: 0"}}}""");

        try
        {
            var events = CodexAgentMapper.ToSessionLogHistoryEvents("thread-1", sessionLogPath);

            var contentEvents = events.OfType<AgentContentCompletedEvent>().ToArray();
            Assert.AreEqual(2, contentEvents.Length);
            Assert.AreEqual("message:0", contentEvents[0].ContentId);
            Assert.AreEqual("hello", contentEvents[0].Content);
            Assert.AreEqual("message:1", contentEvents[1].ContentId);
            Assert.AreEqual("world", contentEvents[1].Content);

            var functionCall = events.OfType<AgentActivityEvent>().Single(x => x.ActivityId == "call-1" && x.Phase == AgentActivityPhase.Requested);
            Assert.AreEqual(AgentActivityKind.ToolCall, functionCall.Kind);
            Assert.AreEqual("shell_command", functionCall.Name);
            Assert.IsTrue(functionCall.Details.HasValue);
            Assert.AreEqual("git status", functionCall.Details.Value.GetProperty("arguments").GetProperty("command").GetString());

            var functionCallOutput = events.OfType<AgentActivityEvent>().Single(x => x.ActivityId == "call-1" && x.Phase == AgentActivityPhase.Completed);
            Assert.IsTrue(functionCallOutput.Details.HasValue);
            Assert.AreEqual("Exit code: 0", functionCallOutput.Details.Value.GetProperty("output").GetProperty("body").GetString());
        }
        finally
        {
            File.Delete(sessionLogPath);
        }
    }

    [TestMethod]
    public void ToSessionLogHistoryEvents_MapsPatchApplyEventsUsingTypedEventMessages()
    {
        var sessionLogPath = CreateSessionLogFile(
            """{"timestamp":"2026-03-17T10:00:00Z","type":"event_msg","payload":{"type":"task_started","turn_id":"turn-1","model_context_window":258400,"collaboration_mode_kind":"default"}}""",
            """{"timestamp":"2026-03-17T10:00:01Z","type":"event_msg","payload":{"type":"patch_apply_begin","auto_approved":true,"call_id":"patch-1","changes":{"readme.md":{"type":"update","unified_diff":"@@ -1 +1 @@\\n-old\\n+new"}},"turn_id":"turn-1"}}""",
            """{"timestamp":"2026-03-17T10:00:02Z","type":"event_msg","payload":{"type":"patch_apply_end","call_id":"patch-1","changes":{"readme.md":{"type":"update","unified_diff":"@@ -1 +1 @@\\n-old\\n+new"}},"status":"completed","stderr":"","stdout":"Applied patch successfully","success":true,"turn_id":"turn-1"}}""");

        try
        {
            var events = CodexAgentMapper.ToSessionLogHistoryEvents("thread-1", sessionLogPath);

            var started = events.OfType<AgentActivityEvent>().Single(x => x.ActivityId == "patch-1" && x.Phase == AgentActivityPhase.Started);
            Assert.AreEqual(AgentActivityKind.FileChange, started.Kind);
            Assert.IsTrue(started.Details.HasValue);
            Assert.AreEqual(1, started.Details.Value.GetProperty("changeCount").GetInt32());
            Assert.IsTrue(started.Details.Value.GetProperty("autoApproved").GetBoolean());

            var completed = events.OfType<AgentActivityEvent>().Single(x => x.ActivityId == "patch-1" && x.Phase == AgentActivityPhase.Completed);
            Assert.AreEqual("file change", completed.Name);
            Assert.IsTrue(completed.Details.HasValue);
            Assert.AreEqual("Completed", completed.Details.Value.GetProperty("status").GetString());
            Assert.AreEqual("Applied patch successfully", completed.Details.Value.GetProperty("output").GetProperty("body").GetString());
        }
        finally
        {
            File.Delete(sessionLogPath);
        }
    }

    [TestMethod]
    public void ToAgentUserInputRequest_PreservesHeadersDescriptionsAndSecretFlags()
    {
        var request = new ToolRequestUserInputParams
        {
            ThreadId = "thread-1",
            TurnId = "turn-1",
            ItemId = "item-1",
            Questions =
            [
                new ToolRequestUserInputQuestion
                {
                    Id = "q1",
                    Header = "Choose",
                    Question = "Pick one",
                    IsOther = false,
                    IsSecret = true,
                    Options =
                    [
                        new ToolRequestUserInputOption { Label = "A", Description = "Option A" }
                    ]
                }
            ]
        };

        var mapped = CodexAgentMapper.ToAgentUserInputRequest(request);

        Assert.AreEqual("item-1", mapped.InteractionId);
        Assert.AreEqual(1, mapped.Form.Prompts.Count);
        var prompt = mapped.Form.Prompts[0];
        Assert.AreEqual("Choose", prompt.Header);
        Assert.IsTrue(prompt.IsSecret);
        Assert.IsFalse(prompt.AllowFreeform);
        Assert.AreEqual("A", prompt.Options![0].Label);
        Assert.AreEqual("Option A", prompt.Options[0].Description);
    }

    [TestMethod]
    public void ToPermissionRequest_MapsCommandApprovalDetails()
    {
        var request = new CommandExecutionRequestApprovalParams
        {
            ItemId = "item-1",
            ThreadId = "thread-1",
            TurnId = "turn-1",
            ApprovalId = "approval-1",
            Command = "rg TODO",
            Cwd = @"C:\repo",
            Reason = "Needs search access",
            CommandActions =
            [
                new CommandAction.SearchCommandAction
                {
                    Command = "rg TODO",
                    Path = @"C:\repo",
                    Query = "TODO"
                }
            ],
            NetworkApprovalContext = new NetworkApprovalContext
            {
                Host = "api.example.com",
                Protocol = NetworkApprovalProtocol.Https
            },
            ProposedExecpolicyAmendment = ["rg"],
            ProposedNetworkPolicyAmendments =
            [
                new NetworkPolicyAmendment
                {
                    Action = NetworkPolicyRuleAction.Allow,
                    Host = "api.example.com"
                }
            ]
        };

        var mapped = CodexAgentMapper.ToPermissionRequest("thread-1", request);

        Assert.IsInstanceOfType<AgentCommandPermissionRequest>(mapped);
        var command = (AgentCommandPermissionRequest)mapped;
        Assert.AreEqual("approval-1", command.InteractionId);
        Assert.AreEqual("rg TODO", command.Command);
        Assert.AreEqual(@"C:\repo", command.WorkingDirectory);
        Assert.AreEqual("Needs search access", command.Reason);
        Assert.AreEqual("api.example.com", command.Network!.Host);
        Assert.AreEqual(AgentNetworkPolicyAction.Allow, command.ProposedNetworkPolicyAmendments![0].Action);
        Assert.AreEqual(AgentCommandPreviewKind.Search, command.Actions![0].Kind);
    }

    private static string CreateSessionLogFile(params string[] lines)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.jsonl");
        File.WriteAllLines(path, lines);
        return path;
    }
}
