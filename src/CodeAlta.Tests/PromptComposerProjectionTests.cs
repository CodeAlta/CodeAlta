using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Models;
using CodeAlta.Presentation.Prompting;

namespace CodeAlta.Tests;

[TestClass]
public sealed class PromptComposerProjectionTests
{
    [TestMethod]
    public void Build_UsesUnavailableStateForConnectingThread()
    {
        var thread = CreateThread("Review startup");

        var projection = PromptComposerProjectionBuilder.Build(
            thread,
            selectedProject: null,
            globalScopeSelected: false,
            backendDisplayName: "Codex",
            availability: ChatBackendAvailability.Connecting,
            anyBackendReady: false,
            draftTabOpen: false,
            selectedThreadId: thread.ThreadId,
            selectedThreadHasQueuedPrompts: false);

        Assert.AreEqual("Waiting for Codex to reconnect...", projection.Placeholder);
        Assert.IsFalse(projection.IsEnabled);
        Assert.IsFalse(projection.CanSend);
        Assert.IsFalse(projection.CanSteer);
        Assert.IsFalse(projection.CanDelegate);
        Assert.IsTrue(projection.CanAbort);
        Assert.IsFalse(projection.CanClearQueue);
        Assert.IsTrue(projection.HasUnavailableStatus);
        Assert.AreEqual("Reconnecting 'Review startup' to Codex. Prompt sending is temporarily unavailable.", projection.UnavailableStatusMessage);
        Assert.AreEqual(StatusTone.Info, projection.UnavailableStatusTone);
    }

    [TestMethod]
    public void Build_UsesReadyDraftStateForConnectedProjectScope()
    {
        var project = new ProjectDescriptor
        {
            Id = "project-1",
            DisplayName = "CodeAlta",
            ProjectPath = @"C:\code\CodeAlta",
            Slug = "codealta",
        };

        var projection = PromptComposerProjectionBuilder.Build(
            selectedThread: null,
            selectedProject: project,
            globalScopeSelected: false,
            backendDisplayName: "Codex",
            availability: ChatBackendAvailability.Ready,
            anyBackendReady: true,
            draftTabOpen: true,
            selectedThreadId: null,
            selectedThreadHasQueuedPrompts: false);

        Assert.AreEqual("Start a thread for CodeAlta...", projection.Placeholder);
        Assert.IsTrue(projection.IsEnabled);
        Assert.IsTrue(projection.CanSend);
        Assert.IsFalse(projection.CanSteer);
        Assert.IsFalse(projection.CanDelegate);
        Assert.IsFalse(projection.CanAbort);
        Assert.IsTrue(projection.CanCloseTab);
        Assert.IsFalse(projection.CanClearQueue);
        Assert.IsFalse(projection.HasUnavailableStatus);
    }

    [TestMethod]
    public void Build_UsesMissingBackendMessagingWhenNoBackendIsReady()
    {
        var projection = PromptComposerProjectionBuilder.Build(
            selectedThread: null,
            selectedProject: null,
            globalScopeSelected: true,
            backendDisplayName: "Codex",
            availability: ChatBackendAvailability.Unsupported,
            anyBackendReady: false,
            draftTabOpen: true,
            selectedThreadId: null,
            selectedThreadHasQueuedPrompts: false);

        Assert.AreEqual("Install or connect Codex/Copilot to start a thread...", projection.Placeholder);
        Assert.IsTrue(projection.HasUnavailableStatus);
        Assert.AreEqual("No chat backend is connected. Browse threads and projects, but prompt sending is unavailable.", projection.UnavailableStatusMessage);
        Assert.AreEqual(StatusTone.Warning, projection.UnavailableStatusTone);
    }

    [TestMethod]
    public void Build_EnablesClearQueueForQueuedSelectedThread()
    {
        var thread = CreateThread("Review startup");

        var projection = PromptComposerProjectionBuilder.Build(
            thread,
            selectedProject: null,
            globalScopeSelected: false,
            backendDisplayName: "Codex",
            availability: ChatBackendAvailability.Ready,
            anyBackendReady: true,
            draftTabOpen: false,
            selectedThreadId: thread.ThreadId,
            selectedThreadHasQueuedPrompts: true);

        Assert.IsTrue(projection.CanSend);
        Assert.IsTrue(projection.CanSteer);
        Assert.IsTrue(projection.CanClearQueue);
    }

    private static WorkThreadDescriptor CreateThread(string title)
    {
        return new WorkThreadDescriptor
        {
            ThreadId = "thread-1",
            Kind = WorkThreadKind.ProjectThread,
            BackendId = AgentBackendIds.Codex.Value,
            BackendSessionId = "backend-thread-1",
            ProjectRef = "project-1",
            WorkingDirectory = @"C:\code\CodeAlta",
            Title = title,
            Status = WorkThreadStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            LastActiveAt = DateTimeOffset.UtcNow,
        };
    }
}
