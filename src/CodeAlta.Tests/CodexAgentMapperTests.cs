using CodeAlta.Agent;
using CodeAlta.Agent.Codex;
using CodeAlta.CodexSdk;
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
            new AskForApproval.OnRequest());

        Assert.AreEqual("gpt-5-mini", parameters.Model);
        Assert.AreEqual(@"C:\repo", parameters.Cwd);
        Assert.IsNotNull(parameters.Config);
        Assert.IsTrue(parameters.Config.ContainsKey("model_reasoning_effort"));
        Assert.AreEqual("high", parameters.Config["model_reasoning_effort"].GetString());
    }

    [TestMethod]
    public void ToTurnStartParams_MapsReasoningEffortOverrides()
    {
        var parameters = CodexAgentMapper.ToTurnStartParams(
            "thread-1",
            AgentInput.Text("hello"),
            @"C:\repo",
            "gpt-5-mini",
            AgentReasoningEffort.Medium);

        Assert.AreEqual("thread-1", parameters.ThreadId);
        Assert.AreEqual(@"C:\repo", parameters.Cwd);
        Assert.AreEqual("gpt-5-mini", parameters.Model);
        Assert.AreEqual(ReasoningEffort.Medium, parameters.Effort);
        Assert.AreEqual(1, parameters.Input.Count);
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
                    Text = "final answer"
                }
            });

        var messageEvent = CodexAgentMapper.ToAgentEvent("thread-1", messageNotification, timestamp);
        Assert.IsInstanceOfType<AgentContentCompletedEvent>(messageEvent);
        var mappedMessage = (AgentContentCompletedEvent)messageEvent;
        Assert.AreEqual(AgentContentKind.Assistant, mappedMessage.Kind);
        Assert.AreEqual("final answer", mappedMessage.Content);
        Assert.AreEqual("turn-2", mappedMessage.RunId?.Value);

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
                    Command = "dotnet test",
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
        Assert.AreEqual("dotnet test", activity.Name);
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
}
