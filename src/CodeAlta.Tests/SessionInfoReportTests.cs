using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Presentation.Sessions;

namespace CodeAlta.Tests;

[TestClass]
public sealed class SessionInfoReportTests
{
    [TestMethod]
    public void Build_UsesHistoryStorageAndProviderFacts()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, "1234567890");
            var session = new SessionViewDescriptor
            {
                SessionId = "codex:session-1",
                ProviderId = ModelProviderIds.Codex.Value,
                WorkingDirectory = @"C:\code\CodeAlta",
                Title = "Investigate startup",
                CreatedAt = DateTimeOffset.Parse("2026-03-20T10:00:00+00:00"),
                UpdatedAt = DateTimeOffset.Parse("2026-03-20T10:15:00+00:00"),
                LastActiveAt = DateTimeOffset.Parse("2026-03-20T10:15:00+00:00"),
                StartedAt = DateTimeOffset.Parse("2026-03-20T10:02:00+00:00"),
            };
            AgentEvent[] history =
            [
                new AgentContentCompletedEvent(ModelProviderIds.Codex, "session-1", session.StartedAt.Value, null, AgentContentKind.User, "user-1", null, "Check startup"),
                new AgentContentCompletedEvent(ModelProviderIds.Codex, "session-1", session.StartedAt.Value.AddMinutes(1), null, AgentContentKind.Assistant, "assistant-1", null, "I am checking."),
                new AgentContentCompletedEvent(ModelProviderIds.Codex, "session-1", session.StartedAt.Value.AddMinutes(2), null, AgentContentKind.User, "user-2", null, "Continue"),
                new AgentContentCompletedEvent(ModelProviderIds.Codex, "session-1", session.StartedAt.Value.AddMinutes(3), null, AgentContentKind.Assistant, "assistant-2", null, "Done."),
                new AgentRawEvent(
                    ModelProviderIds.Codex,
                    "session-1",
                    session.StartedAt.Value.AddMinutes(4),
                    "local.skillActivation",
                    JsonSerializer.SerializeToElement(
                        new AgentLoadedSkillState
                        {
                            Name = "code-review",
                            SkillFilePath = @"C:\code\CodeAlta\.alta\skills\code-review\SKILL.md",
                            SkillRootPath = @"C:\code\CodeAlta\.alta\skills\code-review",
                            SourceKind = "ProjectAlta",
                            SourceId = "project-alta:C:\\code\\CodeAlta",
                            ActivatedAt = session.StartedAt.Value.AddMinutes(4),
                            ActivationMode = "model",
                            ActivationId = "call-skill",
                            Payload = "<skill_content name=\"code-review\"></skill_content>",
                            RestoredFromHistory = true,
                        },
                        new JsonSerializerOptions
                        {
                            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        })),
            ];
            var metadata = new AgentSessionMetadata(
                SessionId: "session-1",
                CreatedAt: DateTimeOffset.Parse("2026-03-20T10:00:00+00:00"),
                UpdatedAt: DateTimeOffset.Parse("2026-03-20T10:15:00+00:00"),
                Summary: "Investigate startup",
                WorkspacePath: tempFile,
                Details: new CodexSessionMetadataDetails(
                    ModelProvider: "openai",
                    Source: "AppServerSessionSource",
                    Status: "Open",
                    IsEphemeral: false,
                    SessionName: "Startup investigation"));

            var report = SessionInfoReportBuilder.Build(
                session,
                "Codex",
                "gpt-5-codex",
                AgentReasoningEffort.High,
                metadata,
                history,
                DateTimeOffset.Parse("2026-03-20T10:20:00+00:00"));

            Assert.AreEqual(2, report.UserMessageCount);
            Assert.AreEqual(2, report.AssistantMessageCount);
            Assert.IsNotNull(report.StorageLocation);
            Assert.AreEqual(SessionInfoStorageKind.File, report.StorageLocation.Kind);
            Assert.AreEqual(10L, report.StorageLocation.SizeBytes);
            Assert.AreEqual(TimeSpan.FromMinutes(18), report.Elapsed);
            Assert.AreEqual(1, report.LoadedSkills.Count);
            Assert.AreEqual("code-review", report.LoadedSkills[0].Name);
            CollectionAssert.AreEqual(
                new[] { "Model provider", "Source", "Status", "Persistence", "Session name" },
                report.ProviderFacts.Select(static fact => fact.Label).ToArray());
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [TestMethod]
    public void BuildMarkdown_IncludesStorageConversationAndProviderSections()
    {
        var report = new SessionInfoReport(
            SessionTitle: "Investigate startup",
            ProviderName: "Codex",
            SessionId: "session-1",
            WorkingDirectory: @"C:\code\CodeAlta",
            ModelName: "gpt-5-codex",
            ReasoningEffort: AgentReasoningEffort.High,
            CreatedAt: DateTimeOffset.Parse("2026-03-20T10:00:00+00:00"),
            StartedAt: DateTimeOffset.Parse("2026-03-20T10:02:00+00:00"),
            LastUpdatedAt: DateTimeOffset.Parse("2026-03-20T10:15:00+00:00"),
            Elapsed: TimeSpan.FromMinutes(18),
            UserMessageCount: 2,
            AssistantMessageCount: 2,
            StorageLocation: new SessionInfoStorageLocation(@"C:\sessions\session-1.jsonl", SessionInfoStorageKind.File, 2048),
            ProviderFacts:
            [
                new SessionInfoFact("Model provider", "openai"),
                new SessionInfoFact("Status", "Open"),
            ],
            LoadedSkills:
            [
                new AgentLoadedSkillState
                {
                    Name = "code-review",
                    SkillFilePath = @"C:\skills\code-review\SKILL.md",
                    SkillRootPath = @"C:\skills\code-review",
                    SourceKind = "ProjectAlta",
                    SourceId = "project-alta:C:\\repo",
                    ActivatedAt = DateTimeOffset.Parse("2026-03-20T10:05:00+00:00"),
                    ActivationMode = "model",
                    ActivationId = "call-skill",
                    Payload = "<skill_content name=\"code-review\"></skill_content>",
                    RestoredFromHistory = true,
                },
            ]);

        var markdown = SessionInfoFormatter.BuildMarkdown(report);

        StringAssert.Contains(markdown, "# Investigate startup session info");
        StringAssert.Contains(markdown, "## Overview");
        StringAssert.Contains(markdown, "## Timing");
        StringAssert.Contains(markdown, "## Conversation");
        StringAssert.Contains(markdown, "## Loaded skills");
        StringAssert.Contains(markdown, "## Storage");
        StringAssert.Contains(markdown, "## Provider-specific details");
        StringAssert.Contains(markdown, "Total messages: 4");
        StringAssert.Contains(markdown, "File size: 2.0 KiB");
        StringAssert.Contains(markdown, "code-review");
    }
}
