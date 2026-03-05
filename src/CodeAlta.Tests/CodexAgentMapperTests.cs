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
        Assert.IsInstanceOfType<AgentAssistantMessageDeltaEvent>(deltaEvent);
        var mappedDelta = (AgentAssistantMessageDeltaEvent)deltaEvent;
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
        Assert.IsInstanceOfType<AgentAssistantMessageEvent>(messageEvent);
        var mappedMessage = (AgentAssistantMessageEvent)messageEvent;
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
}
