using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Tui.Models;
using CodeAlta.Tui.Presentation.Prompting;

namespace CodeAlta.Tests;

[TestClass]
public sealed class PromptComposerProjectionTests
{
    [TestMethod]
    public void Build_UsesUnavailableStateForConnectingSession()
    {
        var session = CreateSession("Review startup");

        var projection = PromptComposerProjectionBuilder.Build(
            session,
            selectedProject: null,
            globalScopeSelected: false,
            providerDisplayName: "Codex",
            availability: ModelProviderAvailability.Probing,
            anyProviderReady: false,
            draftTabOpen: false,
            openTabCount: 1,
            selectedSessionId: session.SessionId,
            selectedSessionHasQueuedPrompts: false,
            selectedSessionCanAlwaysEnqueue: true,
            selectedSessionCanCompact: false,
            selectedSessionCanAbort: true);

        Assert.AreEqual("Waiting for Codex to reconnect...", projection.Placeholder);
        Assert.IsFalse(projection.IsEnabled);
        Assert.IsFalse(projection.CanSend);
        Assert.IsFalse(projection.CanSteer);
        Assert.IsTrue(projection.CanAbort);
        Assert.IsFalse(projection.CanCompact);
        Assert.IsFalse(projection.CanClearQueue);
        Assert.IsTrue(projection.CanAlwaysEnqueue);
        Assert.IsTrue(projection.HasUnavailableStatus);
        Assert.AreEqual("Reconnecting session 'Review startup' to Codex. Prompt sending is temporarily unavailable.", projection.UnavailableStatusMessage);
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
            selectedSession: null,
            selectedProject: project,
            globalScopeSelected: false,
            providerDisplayName: "Codex",
            availability: ModelProviderAvailability.Ready,
            anyProviderReady: true,
            draftTabOpen: true,
            openTabCount: 1,
            selectedSessionId: null,
            selectedSessionHasQueuedPrompts: false,
            selectedSessionCanAlwaysEnqueue: false,
            selectedSessionCanCompact: false,
            selectedSessionCanAbort: false);

        Assert.AreEqual(
            "Start a session. [/] commands, [?] help, [@] to reference a project file, [ENTER] to send, [SHIFT+ENTER] for new line, [CTRL+ENTER] to steer.",
            projection.Placeholder);
        Assert.IsTrue(projection.IsEnabled);
        Assert.IsTrue(projection.CanSend);
        Assert.IsFalse(projection.CanSteer);
        Assert.IsFalse(projection.CanAbort);
        Assert.IsFalse(projection.CanCompact);
        Assert.IsFalse(projection.CanCloseTab);
        Assert.IsFalse(projection.CanClearQueue);
        Assert.IsFalse(projection.CanAlwaysEnqueue);
        Assert.IsFalse(projection.HasUnavailableStatus);
    }

    [TestMethod]
    public void Build_IncludesPluginPlaceholderContributionsInReadyPrompts()
    {
        var project = new ProjectDescriptor
        {
            Id = "project-1",
            DisplayName = "CodeAlta",
            ProjectPath = @"C:\code\CodeAlta",
            Slug = "codealta",
        };
        var placeholderContributions = new[] { "[#] to reference a GitHub issue" };

        var draftProjection = PromptComposerProjectionBuilder.Build(
            selectedSession: null,
            selectedProject: project,
            globalScopeSelected: false,
            providerDisplayName: "Codex",
            availability: ModelProviderAvailability.Ready,
            anyProviderReady: true,
            draftTabOpen: true,
            openTabCount: 1,
            selectedSessionId: null,
            selectedSessionHasQueuedPrompts: false,
            selectedSessionCanAlwaysEnqueue: false,
            selectedSessionCanCompact: false,
            selectedSessionCanAbort: false,
            promptPlaceholderContributions: placeholderContributions);
        var sessionProjection = PromptComposerProjectionBuilder.Build(
            CreateSession("Review startup"),
            selectedProject: null,
            globalScopeSelected: false,
            providerDisplayName: "Codex",
            availability: ModelProviderAvailability.Ready,
            anyProviderReady: true,
            draftTabOpen: false,
            openTabCount: 1,
            selectedSessionId: "session-1",
            selectedSessionHasQueuedPrompts: false,
            selectedSessionCanAlwaysEnqueue: true,
            selectedSessionCanCompact: true,
            selectedSessionCanAbort: false,
            promptPlaceholderContributions: placeholderContributions);

        Assert.AreEqual(
            "Start a session. [/] commands, [?] help, [@] to reference a project file, [#] to reference a GitHub issue, [ENTER] to send, [SHIFT+ENTER] for new line, [CTRL+ENTER] to steer.",
            draftProjection.Placeholder);
        Assert.AreEqual(
            "Continue the selected session. [/] commands, [?] help, [@] to reference a project file, [#] to reference a GitHub issue, [ENTER] to send, [SHIFT+ENTER] for new line, [CTRL+ENTER] to steer.",
            sessionProjection.Placeholder);
    }

    [TestMethod]
    public void Build_UsesMissingProviderMessagingWhenNoProviderIsReady()
    {
        var projection = PromptComposerProjectionBuilder.Build(
            selectedSession: null,
            selectedProject: null,
            globalScopeSelected: true,
            providerDisplayName: "Codex",
            availability: ModelProviderAvailability.Unsupported,
            anyProviderReady: false,
            draftTabOpen: true,
            openTabCount: 1,
            selectedSessionId: null,
            selectedSessionHasQueuedPrompts: false,
            selectedSessionCanAlwaysEnqueue: false,
            selectedSessionCanCompact: false,
            selectedSessionCanAbort: false);

        Assert.AreEqual("Configure model providers (Ctrl+G Ctrl+R) to start a session...", projection.Placeholder);
        Assert.IsTrue(projection.HasUnavailableStatus);
        Assert.AreEqual("No model provider is ready. Open Model Providers (Ctrl+G Ctrl+R) to configure one.", projection.UnavailableStatusMessage);
        Assert.AreEqual(StatusTone.Warning, projection.UnavailableStatusTone);
    }

    [TestMethod]
    public void Build_EnablesClearQueueForQueuedSelectedSession()
    {
        var session = CreateSession("Review startup");

        var projection = PromptComposerProjectionBuilder.Build(
            session,
            selectedProject: null,
            globalScopeSelected: false,
            providerDisplayName: "Codex",
            availability: ModelProviderAvailability.Ready,
            anyProviderReady: true,
            draftTabOpen: false,
            openTabCount: 1,
            selectedSessionId: session.SessionId,
            selectedSessionHasQueuedPrompts: true,
            selectedSessionCanAlwaysEnqueue: true,
            selectedSessionCanCompact: true,
            selectedSessionCanAbort: false);

        Assert.AreEqual(
            "Continue the selected session. [/] commands, [?] help, [@] to reference a project file, [ENTER] to send, [SHIFT+ENTER] for new line, [CTRL+ENTER] to steer.",
            projection.Placeholder);
        Assert.IsTrue(projection.CanSend);
        Assert.IsTrue(projection.CanSteer);
        Assert.IsFalse(projection.CanAbort);
        Assert.IsTrue(projection.CanCompact);
        Assert.IsTrue(projection.CanClearQueue);
        Assert.IsTrue(projection.CanAlwaysEnqueue);
    }

    [TestMethod]
    public void Build_DisablesCompactWhenSelectedSessionCannotCompact()
    {
        var session = CreateSession("Review startup");

        var projection = PromptComposerProjectionBuilder.Build(
            session,
            selectedProject: null,
            globalScopeSelected: false,
            providerDisplayName: "Codex",
            availability: ModelProviderAvailability.Ready,
            anyProviderReady: true,
            draftTabOpen: false,
            openTabCount: 1,
            selectedSessionId: session.SessionId,
            selectedSessionHasQueuedPrompts: false,
            selectedSessionCanAlwaysEnqueue: true,
            selectedSessionCanCompact: false,
            selectedSessionCanAbort: false);

        Assert.IsFalse(projection.CanCompact);
    }

    [TestMethod]
    public void Build_EnablesAbortOnlyWhenSelectedSessionIsRunning()
    {
        var session = CreateSession("Review startup");

        var idleProjection = PromptComposerProjectionBuilder.Build(
            session,
            selectedProject: null,
            globalScopeSelected: false,
            providerDisplayName: "Codex",
            availability: ModelProviderAvailability.Ready,
            anyProviderReady: true,
            draftTabOpen: false,
            openTabCount: 1,
            selectedSessionId: session.SessionId,
            selectedSessionHasQueuedPrompts: false,
            selectedSessionCanAlwaysEnqueue: true,
            selectedSessionCanCompact: true,
            selectedSessionCanAbort: false);
        var runningProjection = PromptComposerProjectionBuilder.Build(
            session,
            selectedProject: null,
            globalScopeSelected: false,
            providerDisplayName: "Codex",
            availability: ModelProviderAvailability.Ready,
            anyProviderReady: true,
            draftTabOpen: false,
            openTabCount: 1,
            selectedSessionId: session.SessionId,
            selectedSessionHasQueuedPrompts: false,
            selectedSessionCanAlwaysEnqueue: true,
            selectedSessionCanCompact: false,
            selectedSessionCanAbort: true);

        Assert.IsFalse(idleProjection.CanAbort);
        Assert.IsTrue(runningProjection.CanAbort);
    }

    [TestMethod]
    public void Build_EnablesDraftCloseWhenAnotherTabIsOpen()
    {
        var projection = PromptComposerProjectionBuilder.Build(
            selectedSession: null,
            selectedProject: null,
            globalScopeSelected: true,
            providerDisplayName: "Codex",
            availability: ModelProviderAvailability.Ready,
            anyProviderReady: true,
            draftTabOpen: true,
            openTabCount: 2,
            selectedSessionId: null,
            selectedSessionHasQueuedPrompts: false,
            selectedSessionCanAlwaysEnqueue: false,
            selectedSessionCanCompact: false,
            selectedSessionCanAbort: false);

        Assert.AreEqual(
            "Start a session. [/] commands, [?] help, [ENTER] to send, [SHIFT+ENTER] for new line, [CTRL+ENTER] to steer.",
            projection.Placeholder);
        Assert.IsTrue(projection.CanCloseTab);
    }

    private static SessionViewDescriptor CreateSession(string title)
    {
        return new SessionViewDescriptor
        {
            SessionId = "session-1",
            Kind = SessionViewKind.ProjectSession,
            ProviderId = ModelProviderIds.Codex.Value,
            ProjectRef = "project-1",
            WorkingDirectory = @"C:\code\CodeAlta",
            Title = title,
            Status = SessionViewStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            LastActiveAt = DateTimeOffset.UtcNow,
        };
    }
}
