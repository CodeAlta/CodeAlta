using System.Text.Json;
using CodeNoesis.Agent;
using CodeNoesis.Agent.Copilot;
using GitHub.Copilot.SDK;

namespace CodeNoesis.Tests;

[TestClass]
public sealed class CopilotAgentMapperTests
{
    [TestMethod]
    public void ToSessionConfig_MapsCoreOptions()
    {
        var toolSchema = JsonDocument.Parse("""{"type":"object","properties":{"value":{"type":"string"}}}""").RootElement;
        var options = new AgentSessionCreateOptions
        {
            Model = "gpt-5",
            WorkingDirectory = @"C:\repo",
            Streaming = true,
            ReasoningEffort = AgentReasoningEffort.High,
            SystemMessage = "System guidance",
            DeveloperInstructions = "Developer guidance",
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
            Tools =
            [
                new AgentToolDefinition(
                    new AgentToolSpec("echo_tool", "Echoes input", toolSchema),
                    static (_, _) => Task.FromResult<AgentToolResult>(
                        new(
                            true,
                            [new AgentToolResultItem.Text("ok")])))
            ]
        };

        var config = CopilotAgentMapper.ToSessionConfig(options);

        Assert.AreEqual("gpt-5", config.Model);
        Assert.AreEqual(@"C:\repo", config.WorkingDirectory);
        Assert.IsTrue(config.Streaming);
        Assert.AreEqual("high", config.ReasoningEffort);
        Assert.IsNotNull(config.SystemMessage);
        Assert.AreEqual(SystemMessageMode.Append, config.SystemMessage.Mode);
        Assert.IsTrue(config.SystemMessage.Content!.Contains("System guidance", StringComparison.Ordinal));
        Assert.IsTrue(config.SystemMessage.Content.Contains("Developer guidance", StringComparison.Ordinal));
        Assert.IsNotNull(config.OnPermissionRequest);
        Assert.IsNotNull(config.Tools);
        Assert.AreEqual(1, config.Tools.Count);
    }

    [TestMethod]
    public void ToMessageOptions_MapsAttachmentsAndPromptFallbacks()
    {
        var options = new AgentSendOptions
        {
            Mode = "edit",
            Input = new AgentInput(
            [
                new AgentInputItem.Text("Review these files"),
                new AgentInputItem.File("Program.cs", "Program.cs", new AgentLineRange(2, 8)),
                new AgentInputItem.Directory("src", "source"),
                new AgentInputItem.Selection(
                    "App.cs",
                    "Selection",
                    "Console.WriteLine(\"hello\");",
                    new AgentSelectionRange(
                        new AgentPosition(1, 3),
                        new AgentPosition(4, 1))),
                new AgentInputItem.ImageUrl("https://example.com/image.png"),
                new AgentInputItem.LocalImage(@"C:\img\local.png"),
                new AgentInputItem.Skill("linter", "/skills/linter"),
                new AgentInputItem.Mention("workspace", "/mentions/workspace")
            ])
        };

        var mapped = CopilotAgentMapper.ToMessageOptions(options);

        Assert.AreEqual("edit", mapped.Mode);
        Assert.IsNotNull(mapped.Attachments);
        Assert.AreEqual(3, mapped.Attachments.Count);

        Assert.IsInstanceOfType<UserMessageDataAttachmentsItemFile>(mapped.Attachments[0]);
        Assert.IsInstanceOfType<UserMessageDataAttachmentsItemDirectory>(mapped.Attachments[1]);
        Assert.IsInstanceOfType<UserMessageDataAttachmentsItemSelection>(mapped.Attachments[2]);

        var fileAttachment = (UserMessageDataAttachmentsItemFile)mapped.Attachments[0];
        Assert.AreEqual("Program.cs", fileAttachment.Path);
        Assert.IsNotNull(fileAttachment.LineRange);
        Assert.AreEqual(2d, fileAttachment.LineRange.Start);
        Assert.AreEqual(8d, fileAttachment.LineRange.End);

        Assert.IsTrue(mapped.Prompt.Contains("Review these files", StringComparison.Ordinal));
        Assert.IsTrue(mapped.Prompt.Contains("[image-url] https://example.com/image.png", StringComparison.Ordinal));
        Assert.IsTrue(mapped.Prompt.Contains(@"[local-image] C:\img\local.png", StringComparison.Ordinal));
        Assert.IsTrue(mapped.Prompt.Contains("[skill] name=linter path=/skills/linter", StringComparison.Ordinal));
        Assert.IsTrue(mapped.Prompt.Contains("[mention] name=workspace path=/mentions/workspace", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ToAgentEvent_MapsKnownAndUnknownSessionEvents()
    {
        var timestamp = DateTimeOffset.Parse("2026-02-25T12:00:00+00:00");
        var deltaEvent = new AssistantMessageDeltaEvent
        {
            Timestamp = timestamp,
            Data = new AssistantMessageDeltaData
            {
                MessageId = "msg-1",
                DeltaContent = "delta"
            }
        };

        var mappedDelta = CopilotAgentMapper.ToAgentEvent("session-1", deltaEvent);
        Assert.IsInstanceOfType<AgentAssistantMessageDeltaEvent>(mappedDelta);
        var normalizedDelta = (AgentAssistantMessageDeltaEvent)mappedDelta;
        Assert.AreEqual("delta", normalizedDelta.Delta);
        Assert.AreEqual("msg-1", normalizedDelta.RunId?.Value);

        var messageEvent = new AssistantMessageEvent
        {
            Timestamp = timestamp,
            Data = new AssistantMessageData
            {
                MessageId = "msg-2",
                Content = "final message"
            }
        };

        var mappedMessage = CopilotAgentMapper.ToAgentEvent("session-1", messageEvent);
        Assert.IsInstanceOfType<AgentAssistantMessageEvent>(mappedMessage);
        var normalizedMessage = (AgentAssistantMessageEvent)mappedMessage;
        Assert.AreEqual("final message", normalizedMessage.Content);
        Assert.AreEqual("msg-2", normalizedMessage.RunId?.Value);

        var unknownEvent = new SessionInfoEvent
        {
            Timestamp = timestamp,
            Data = new SessionInfoData
            {
                InfoType = "note",
                Message = "raw event"
            }
        };

        var mappedUnknown = CopilotAgentMapper.ToAgentEvent("session-1", unknownEvent);
        Assert.IsInstanceOfType<AgentRawEvent>(mappedUnknown);
        var raw = (AgentRawEvent)mappedUnknown;
        Assert.AreEqual("session.info", raw.BackendEventType);
    }
}
