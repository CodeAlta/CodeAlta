using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Presentation.Threads;

namespace CodeAlta.Tests;

[TestClass]
public sealed class ThreadInfoReportTests
{
    [TestMethod]
    public void Build_UsesHistoryStorageAndBackendFacts()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, "1234567890");
            var thread = new WorkThreadDescriptor
            {
                ThreadId = "codex:thread-1",
                BackendId = AgentBackendIds.Codex.Value,
                BackendSessionId = "thread-1",
                WorkingDirectory = @"C:\code\CodeAlta",
                Title = "Investigate startup",
                CreatedAt = DateTimeOffset.Parse("2026-03-20T10:00:00+00:00"),
                UpdatedAt = DateTimeOffset.Parse("2026-03-20T10:15:00+00:00"),
                LastActiveAt = DateTimeOffset.Parse("2026-03-20T10:15:00+00:00"),
                StartedAt = DateTimeOffset.Parse("2026-03-20T10:02:00+00:00"),
            };
            AgentEvent[] history =
            [
                new AgentContentCompletedEvent(AgentBackendIds.Codex, "thread-1", thread.StartedAt.Value, null, AgentContentKind.User, "user-1", null, "Check startup"),
                new AgentContentCompletedEvent(AgentBackendIds.Codex, "thread-1", thread.StartedAt.Value.AddMinutes(1), null, AgentContentKind.Assistant, "assistant-1", null, "I am checking."),
                new AgentContentCompletedEvent(AgentBackendIds.Codex, "thread-1", thread.StartedAt.Value.AddMinutes(2), null, AgentContentKind.User, "user-2", null, "Continue"),
                new AgentContentCompletedEvent(AgentBackendIds.Codex, "thread-1", thread.StartedAt.Value.AddMinutes(3), null, AgentContentKind.Assistant, "assistant-2", null, "Done."),
            ];
            var metadata = new AgentSessionMetadata(
                SessionId: "thread-1",
                CreatedAt: DateTimeOffset.Parse("2026-03-20T10:00:00+00:00"),
                UpdatedAt: DateTimeOffset.Parse("2026-03-20T10:15:00+00:00"),
                Summary: "Investigate startup",
                WorkspacePath: tempFile,
                Details: new CodexSessionMetadataDetails(
                    ModelProvider: "openai",
                    Source: "AppServerSessionSource",
                    Status: "Open",
                    IsEphemeral: false,
                    ThreadName: "Startup investigation"));

            var report = ThreadInfoReportBuilder.Build(
                thread,
                "Codex",
                "gpt-5-codex",
                AgentReasoningEffort.High,
                metadata,
                history,
                DateTimeOffset.Parse("2026-03-20T10:20:00+00:00"));

            Assert.AreEqual(2, report.UserMessageCount);
            Assert.AreEqual(2, report.AssistantMessageCount);
            Assert.IsNotNull(report.StorageLocation);
            Assert.AreEqual(ThreadInfoStorageKind.File, report.StorageLocation.Kind);
            Assert.AreEqual(10L, report.StorageLocation.SizeBytes);
            Assert.AreEqual(TimeSpan.FromMinutes(18), report.Elapsed);
            CollectionAssert.AreEqual(
                new[] { "Model provider", "Source", "Status", "Persistence", "Thread name" },
                report.BackendFacts.Select(static fact => fact.Label).ToArray());
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [TestMethod]
    public void BuildMarkdown_IncludesStorageConversationAndBackendSections()
    {
        var report = new ThreadInfoReport(
            ThreadTitle: "Investigate startup",
            BackendName: "Codex",
            BackendSessionId: "thread-1",
            WorkingDirectory: @"C:\code\CodeAlta",
            ModelName: "gpt-5-codex",
            ReasoningEffort: AgentReasoningEffort.High,
            CreatedAt: DateTimeOffset.Parse("2026-03-20T10:00:00+00:00"),
            StartedAt: DateTimeOffset.Parse("2026-03-20T10:02:00+00:00"),
            LastUpdatedAt: DateTimeOffset.Parse("2026-03-20T10:15:00+00:00"),
            Elapsed: TimeSpan.FromMinutes(18),
            UserMessageCount: 2,
            AssistantMessageCount: 2,
            StorageLocation: new ThreadInfoStorageLocation(@"C:\threads\thread-1.jsonl", ThreadInfoStorageKind.File, 2048),
            BackendFacts:
            [
                new ThreadInfoFact("Model provider", "openai"),
                new ThreadInfoFact("Status", "Open"),
            ]);

        var markdown = ThreadInfoFormatter.BuildMarkdown(report);

        StringAssert.Contains(markdown, "# Investigate startup thread info");
        StringAssert.Contains(markdown, "## Overview");
        StringAssert.Contains(markdown, "## Timing");
        StringAssert.Contains(markdown, "## Conversation");
        StringAssert.Contains(markdown, "## Storage");
        StringAssert.Contains(markdown, "## Backend-specific details");
        StringAssert.Contains(markdown, "Total messages: 4");
        StringAssert.Contains(markdown, "File size: 2.0 KiB");
    }
}
