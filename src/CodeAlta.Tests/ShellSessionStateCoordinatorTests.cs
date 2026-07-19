using System.Globalization;
using CodeAlta.Agent;
using CodeAlta.App;
using CodeAlta.App.Events;
using CodeAlta.App.State;
using CodeAlta.Catalog;
using CodeAlta.Models;
using CodeAlta.Threading;
using CodeAlta.Views;

namespace CodeAlta.Tests;

[TestClass]
public sealed class ShellSessionStateCoordinatorTests
{
    [TestMethod]
    public async Task LoadCatalogStateAsync_IncludesTransientCurrentProjectWithoutSavingCatalogFile()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var project = new ProjectDescriptor
        {
            Id = ProjectId.NewVersion7().ToString(),
            Slug = "code",
            Name = "Code",
            DisplayName = "Code",
            ProjectPath = Path.Combine(temp.Path, "Code"),
            DefaultBranch = "main",
        };
        var coordinator = CreateCoordinator(options, currentProject: project);

        await coordinator.LoadCatalogStateAsync(CancellationToken.None);

        Assert.AreEqual(1, coordinator.Projects.Count);
        Assert.AreEqual(project.Id, coordinator.Projects[0].Id);
        Assert.IsFalse((await new ProjectCatalog(options).LoadAsync(CancellationToken.None).ConfigureAwait(false)).Any());
    }

    [TestMethod]
    public void RemoveDeletedProject_DoesNotReAddTransientCurrentProjectOnCatalogRefresh()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var project = new ProjectDescriptor
        {
            Id = ProjectId.NewVersion7().ToString(),
            Slug = "code",
            Name = "Code",
            DisplayName = "Code",
            ProjectPath = Path.Combine(temp.Path, "Code"),
            DefaultBranch = "main",
        };
        var coordinator = CreateCoordinator(options, currentProject: project);
        coordinator.ApplyRecoveredCatalogState([], []);

        coordinator.RemoveDeletedProject(project, []);
        coordinator.ApplyRecoveredCatalogState([], []);

        Assert.IsFalse(coordinator.Projects.Any(candidate => string.Equals(candidate.Id, project.Id, StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void ApplyRecoveredCatalogState_AppliesPersistedSessionLocalState()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var coordinator = CreateCoordinator(options);
        coordinator.ViewState = new SessionViewViewState
        {
            SessionStates = new Dictionary<string, SessionViewLocalState>(StringComparer.OrdinalIgnoreCase)
            {
                ["session-1"] = new SessionViewLocalState
                {
                    ProviderKey = "zai",
                    ModelId = "glm-5.1",
                    ReasoningEffort = AgentReasoningEffort.High,
                    AgentPromptId = "plan",
                    Archived = true,
                    MessageCount = 12,
                },
            },
        };

        coordinator.ApplyRecoveredCatalogState([], [CreateSession("session-1")]);

        var session = coordinator.Sessions.Single();
        Assert.AreEqual("zai", session.ProviderId);
        Assert.AreEqual("zai", session.ProviderKey);
        Assert.AreEqual("glm-5.1", session.ModelId);
        Assert.AreEqual(AgentReasoningEffort.High, session.ReasoningEffort);
        Assert.AreEqual("plan", session.AgentPromptId);
        Assert.AreEqual(SessionViewStatus.Archived, session.Status);
        Assert.AreEqual(12, session.MessageCount);
    }

    [TestMethod]
    public async Task PersistSessionLocalStateAsync_AppendsArchivedAndMessageCountToJournalState()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var sessionCatalog = new SessionViewCatalog(options);
        var coordinator = CreateCoordinator(options, sessionCatalog);
        coordinator.ViewState = new SessionViewViewState();

        var session = CreateSession("session-1");
        session.ProviderKey = "zai";
        session.ProviderId = "zai";
        session.ModelId = "glm-5.1";
        session.ReasoningEffort = AgentReasoningEffort.High;
        session.AgentPromptId = "plan";
        session.Status = SessionViewStatus.Archived;
        session.MessageCount = 6;
        await coordinator.PersistSessionLocalStateAsync(session).ConfigureAwait(false);

        var reloaded = await sessionCatalog.JournalStore.ReadLatestStateAsync(session.SessionId, session.CreatedAt).ConfigureAwait(false);
        Assert.IsNotNull(reloaded);
        Assert.AreEqual("zai", reloaded.ProviderKey);
        Assert.AreEqual("glm-5.1", reloaded.ModelId);
        Assert.AreEqual(AgentReasoningEffort.High, reloaded.ReasoningEffort);
        Assert.AreEqual("plan", reloaded.AgentPromptId);
        Assert.IsTrue(reloaded.Archived);
        Assert.AreEqual(6, reloaded.MessageCount);
    }

    [TestMethod]
    public async Task PersistSessionLocalStateAsync_PreservesRuntimePromptQueueState()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var sessionCatalog = new SessionViewCatalog(options);
        var coordinator = CreateCoordinator(options, sessionCatalog);
        coordinator.ViewState = new SessionViewViewState();
        var session = CreateSession("session-1");
        var timestamp = session.CreatedAt.AddMinutes(1);
        var submittedBy = new AltaActorProvenance
        {
            Kind = "agent",
            SourceSessionId = "child-session",
            CreatedAt = timestamp,
        };
        await sessionCatalog.JournalStore.AppendStateAsync(
                session,
                new SessionViewLocalState
                {
                    PromptProvenance =
                    [
                        new SessionViewPromptProvenance
                        {
                            PromptId = "queue-1",
                            Kind = "parent-notify",
                            Queued = true,
                            PromptPreview = "queued child result",
                            SubmittedBy = submittedBy,
                            CreatedAt = timestamp,
                        },
                    ],
                    QueuedPrompts =
                    [
                        new SessionViewQueuedPrompt
                        {
                            QueueItemId = "queue-1",
                            Kind = "parent-notify",
                            Prompt = "queued child result",
                            PromptPreview = "queued child result",
                            State = "queued",
                            SubmittedBy = submittedBy,
                            CreatedAt = timestamp,
                        },
                    ],
                })
            .ConfigureAwait(false);
        session.MessageCount = 7;

        await coordinator.PersistSessionLocalStateAsync(session).ConfigureAwait(false);

        var reloaded = await sessionCatalog.JournalStore.ReadLatestStateAsync(session.SessionId, session.CreatedAt).ConfigureAwait(false);
        Assert.IsNotNull(reloaded);
        Assert.AreEqual(7, reloaded.MessageCount);
        var provenance = reloaded.PromptProvenance.Single();
        Assert.AreEqual("queue-1", provenance.PromptId);
        Assert.AreEqual("parent-notify", provenance.Kind);
        Assert.IsTrue(provenance.Queued);
        Assert.AreEqual("child-session", provenance.SubmittedBy?.SourceSessionId);
        var queuedPrompt = reloaded.QueuedPrompts.Single();
        Assert.AreEqual("queue-1", queuedPrompt.QueueItemId);
        Assert.AreEqual("queued", queuedPrompt.State);
        Assert.AreEqual("queued child result", queuedPrompt.Prompt);
        Assert.AreEqual("child-session", queuedPrompt.SubmittedBy?.SourceSessionId);
    }

    [TestMethod]
    public async Task CloseSessionTabAsync_LastSelectedProjectSessionFallsBackToProjectScope()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var coordinator = CreateCoordinator(options);
        var project = CreateProject("project-1", "CodeAlta");
        coordinator.ApplyRecoveredCatalogState([project], [CreateSession("session-1", project.Id)]);
        coordinator.OpenSession("session-1");

        var closeResult = await coordinator.CloseSessionTabAsync("session-1").ConfigureAwait(false);

        Assert.AreEqual(TabCloseResult.Closed, closeResult);
        Assert.IsTrue(coordinator.DraftTabOpen);
        Assert.IsFalse(coordinator.GlobalScopeSelected);
        Assert.AreEqual(project.Id, coordinator.SelectedProjectId);
        Assert.IsNull(coordinator.SelectedSessionId);
        Assert.AreEqual(ShellSurface.DraftWorkspace, coordinator.Selection.Surface);
        Assert.IsInstanceOfType<WorkspaceTarget.Draft>(coordinator.Selection.Target);
    }

    [TestMethod]
    public async Task CloseSessionTabAsync_ReleasesSessionStateAndTimelineForReopen()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var coordinator = CreateCoordinator(options);
        var project = CreateProject("project-1", "CodeAlta");
        var session = CreateSession("session-1", project.Id);
        coordinator.ApplyRecoveredCatalogState([project], [session]);
        coordinator.OpenSession(session.SessionId);

        var tab = coordinator.FindOpenSession(session.SessionId);
        Assert.IsNotNull(tab);
        tab.Timeline.RenderOptimisticUserPrompt("Retained timeline content", DateTimeOffset.UtcNow);
        Assert.IsNotEmpty(tab.Timeline.Flow.Items);

        var closeResult = await coordinator.CloseSessionTabAsync(session.SessionId).ConfigureAwait(false);

        Assert.AreEqual(TabCloseResult.Closed, closeResult);
        Assert.IsNull(coordinator.FindOpenSession(session.SessionId));
        Assert.IsEmpty(tab.Timeline.Flow.Items);
        Assert.IsFalse(coordinator.ViewState.OpenSessionIds.Contains(session.SessionId, StringComparer.OrdinalIgnoreCase));

        Assert.AreEqual(OpenSessionResult.Opened, coordinator.OpenSession(session.SessionId));
        var reopenedTab = coordinator.FindOpenSession(session.SessionId);
        Assert.IsNotNull(reopenedTab);
        Assert.AreNotSame(tab, reopenedTab);
        Assert.AreNotSame(tab.Timeline, reopenedTab.Timeline);
        Assert.IsEmpty(reopenedTab.Timeline.Flow.Items);
    }

    [TestMethod]
    public async Task RegisterCreatedSessionAsync_ReplacesDraftTabWithCreatedSession()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var sessionCatalog = new SessionViewCatalog(options);
        var dispatcher = new InlineUiDispatcher();
        var publisher = new FrontendEventPublisher(dispatcher);
        var events = new List<ShellFrontendEvent>();
        publisher.Subscribe(events.Add);
        var replacedDraftSessionIds = new List<string>();
        var coordinator = CreateCoordinator(
            options,
            sessionCatalog,
            replaceDraftTabWithSession: replacedDraftSessionIds.Add,
            stateStore: new ShellStateStore(dispatcher),
            frontendEvents: publisher);
        var project = CreateProject("project-1", "CodeAlta");
        var session = CreateSession("session-1", project.Id);
        coordinator.ApplyRecoveredCatalogState([project], []);
        events.Clear();

        await coordinator.RegisterCreatedSessionAsync(session);

        CollectionAssert.AreEqual(new[] { "session-1" }, replacedDraftSessionIds.ToArray());
        Assert.AreEqual("session-1", coordinator.SelectedSessionId);
        CollectionAssert.Contains(coordinator.ViewState.OpenSessionIds, "session-1");
        Assert.IsTrue(events.OfType<CatalogChangedEvent>().Any(), "Created sessions must refresh the sidebar/catalog projection immediately.");
        Assert.IsTrue(events.OfType<SelectionChangedEvent>().Any(selection => selection.Snapshot?.Selection.SelectedSessionId == "session-1"));

        var persisted = await sessionCatalog.LoadViewStateAsync().ConfigureAwait(false);
        Assert.AreEqual("session-1", persisted.SelectedSessionId);
        Assert.AreEqual(SessionViewSelectionSurface.Session, persisted.Selection.Surface);
        Assert.AreEqual("session-1", persisted.Selection.SessionId);
        CollectionAssert.Contains(persisted.OpenSessionIds, "session-1");
    }

    [TestMethod]
    public async Task ApplyRecoveredCatalogState_StaleRecoveryPreservesLocallyCreatedSession()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var coordinator = CreateCoordinator(options);
        var project = CreateProject("project-1", "CodeAlta");
        var recoveredSession = CreateSession("recovered-session", project.Id);
        var createdSession = CreateSession("created-session", project.Id);
        coordinator.ApplyRecoveredCatalogState([project], [recoveredSession]);
        coordinator.SelectProjectScope(project.Id);

        await coordinator.RegisterCreatedSessionAsync(createdSession).ConfigureAwait(false);
        coordinator.ApplyRecoveredCatalogState([project], [recoveredSession], pruneMissingSessions: false);
        coordinator.ApplyRecoveredCatalogState([project], [recoveredSession]);

        Assert.IsNotNull(coordinator.FindSession(createdSession.SessionId));
        Assert.AreEqual(createdSession.SessionId, coordinator.SelectedSessionId);
        CollectionAssert.Contains(coordinator.ViewState.OpenSessionIds, createdSession.SessionId);
        Assert.AreEqual(SessionViewSelectionSurface.Session, coordinator.ViewState.Selection.Surface);
        Assert.AreEqual(createdSession.SessionId, coordinator.ViewState.Selection.SessionId);

        coordinator.ApplyRecoveredCatalogState([project], [recoveredSession, createdSession]);
        coordinator.ApplyRecoveredCatalogState([project], [recoveredSession]);

        Assert.IsNull(coordinator.FindSession(createdSession.SessionId));
    }

    [TestMethod]
    public async Task CloseSessionTabAsync_ClearsPendingStartupRestoreBeforeRecoveryCanReopenIt()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var sessionCatalog = new SessionViewCatalog(options);
        var coordinator = CreateCoordinator(options, sessionCatalog);
        var project = CreateProject("project-1", "CodeAlta");
        var session = CreateSession("session-1", project.Id);
        coordinator.ApplyRecoveredCatalogState([project], [session]);
        coordinator.OpenSession(session.SessionId);
        coordinator.PendingStartupSessionRestoreId = session.SessionId;

        await coordinator.CloseSessionTabAsync(session.SessionId).ConfigureAwait(false);
        coordinator.ApplyRecoveredCatalogState([project], [session], pruneMissingSessions: false);

        Assert.IsNull(coordinator.PendingStartupSessionRestoreId);
        Assert.IsNull(coordinator.SelectedSessionId);
        Assert.AreEqual(SessionViewSelectionSurface.Draft, coordinator.ViewState.Selection.Surface);
        Assert.AreEqual(SessionViewDraftScope.Project, coordinator.ViewState.Selection.DraftScope);
        Assert.AreEqual(project.Id, coordinator.ViewState.Selection.ProjectId);
        Assert.IsFalse(coordinator.ViewState.OpenSessionIds.Contains(session.SessionId, StringComparer.OrdinalIgnoreCase));

        var persisted = await sessionCatalog.LoadViewStateAsync().ConfigureAwait(false);
        Assert.IsNull(persisted.SelectedSessionId);
        Assert.AreEqual(SessionViewSelectionSurface.Draft, persisted.Selection.Surface);
        Assert.IsFalse(persisted.OpenSessionIds.Contains(session.SessionId, StringComparer.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void SelectProjectScope_ClearsPendingStartupRestoreBeforeRecoveryCanReopenIt()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var coordinator = CreateCoordinator(options);
        var project = CreateProject("project-1", "CodeAlta");
        var session = CreateSession("session-1", project.Id);
        coordinator.ApplyRecoveredCatalogState([project], [session]);
        coordinator.OpenSession(session.SessionId);
        coordinator.PendingStartupSessionRestoreId = session.SessionId;

        coordinator.SelectProjectScope(project.Id);
        coordinator.ApplyRecoveredCatalogState([project], [session], pruneMissingSessions: false);

        Assert.IsNull(coordinator.PendingStartupSessionRestoreId);
        Assert.IsNull(coordinator.SelectedSessionId);
        Assert.AreEqual(SessionViewSelectionSurface.Draft, coordinator.ViewState.Selection.Surface);
        Assert.AreEqual(SessionViewDraftScope.Project, coordinator.ViewState.Selection.DraftScope);
        Assert.AreEqual(project.Id, coordinator.ViewState.Selection.ProjectId);
        Assert.IsFalse(coordinator.ViewState.OpenSessionIds.Contains(session.SessionId, StringComparer.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void SelectProjectScope_PreservesOpenSessionIdsForOpenSessionTabs()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var project = CreateProject("project-1", "CodeAlta");
        var session = CreateSession("session-1", project.Id);
        var coordinator = CreateCoordinator(options, getOpenSessionTabIds: () => [session.SessionId]);
        coordinator.ApplyRecoveredCatalogState([project], [session]);
        coordinator.OpenSession(session.SessionId);

        coordinator.SelectProjectScope(project.Id);

        CollectionAssert.Contains(coordinator.ViewState.OpenSessionIds, session.SessionId);
        Assert.IsNull(coordinator.SelectedSessionId);
        Assert.AreEqual(SessionViewSelectionSurface.Draft, coordinator.ViewState.Selection.Surface);
    }

    [TestMethod]
    public void UpsertRuntimeSession_AddsAgentCreatedProjectSessionWithoutOpeningIt()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var dispatcher = new InlineUiDispatcher();
        var publisher = new FrontendEventPublisher(dispatcher);
        var events = new List<ShellFrontendEvent>();
        publisher.Subscribe(events.Add);
        var coordinator = CreateCoordinator(
            options,
            stateStore: new ShellStateStore(dispatcher),
            frontendEvents: publisher);
        var project = CreateProject("project-1", "CodeAlta");
        var session = CreateSession("agent-child", project.Id);
        session.CreatedBy = new AltaActorProvenance
        {
            Kind = "agent",
            SourceSessionId = "global-parent",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        coordinator.ApplyRecoveredCatalogState([project], []);

        coordinator.UpsertRuntimeSession(session);

        Assert.AreEqual(session.SessionId, coordinator.Sessions.Single().SessionId);
        Assert.IsFalse(coordinator.ViewState.OpenSessionIds.Contains(session.SessionId, StringComparer.OrdinalIgnoreCase));
        Assert.IsNull(coordinator.FindOpenSession(session.SessionId));
        Assert.IsTrue(events.OfType<CatalogChangedEvent>().Any());
    }

    [TestMethod]
    public void UpsertRuntimeSession_RemembersParentSessionIdForRecovery()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var coordinator = CreateCoordinator(options);
        var project = CreateProject("project-1", "CodeAlta");
        var parent = CreateSession("session-parent", project.Id);
        var child = CreateSession("session-child", project.Id);
        child.ParentSessionId = parent.SessionId;
        coordinator.ApplyRecoveredCatalogState([project], [parent]);

        coordinator.UpsertRuntimeSession(child);

        Assert.AreEqual(parent.SessionId, coordinator.ViewState.SessionStates[child.SessionId].ParentSessionId);

        var recoveredChild = CreateSession(child.SessionId, project.Id);
        coordinator.ApplyRecoveredCatalogState([project], [parent, recoveredChild]);

        Assert.AreEqual(parent.SessionId, coordinator.Sessions.Single(session => session.SessionId == child.SessionId).ParentSessionId);
    }

    [TestMethod]
    public void UpsertRuntimeSession_PreservesInMemoryProviderSelectionOverStaleLocalState()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var coordinator = CreateCoordinator(options);
        var project = CreateProject("project-1", "CodeAlta");
        var session = CreateSession("session-1", project.Id);
        session.ProviderId = "disabled-provider";
        session.ProviderKey = "disabled-provider";
        coordinator.ApplyRecoveredCatalogState([project], [session]);
        coordinator.ViewState.SessionStates[session.SessionId] = new SessionViewLocalState
        {
            ProviderKey = "openai",
            ModelId = "old-model",
            ReasoningEffort = AgentReasoningEffort.Low,
        };
        session.ProviderId = "anthropic";
        session.ProviderKey = "anthropic";
        session.ModelId = "claude-sonnet-4";
        session.ReasoningEffort = AgentReasoningEffort.High;

        coordinator.UpsertRuntimeSession(session);

        var updated = coordinator.Sessions.Single(candidate => candidate.SessionId == session.SessionId);
        Assert.AreSame(session, updated);
        Assert.AreEqual("anthropic", updated.ProviderId);
        Assert.AreEqual("anthropic", updated.ProviderKey);
        Assert.AreEqual("claude-sonnet-4", updated.ModelId);
        Assert.AreEqual(AgentReasoningEffort.High, updated.ReasoningEffort);
        Assert.AreEqual("anthropic", coordinator.ViewState.SessionStates[session.SessionId].ProviderKey);
        Assert.AreEqual("claude-sonnet-4", coordinator.ViewState.SessionStates[session.SessionId].ModelId);
        Assert.AreEqual(AgentReasoningEffort.High, coordinator.ViewState.SessionStates[session.SessionId].ReasoningEffort);
    }

    [TestMethod]
    public async Task ClosingSessionTab_DoesNotStopSession()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var coordinator = CreateCoordinator(options);
        var project = CreateProject("project-1", "CodeAlta");
        var session = CreateSession("session-1", project.Id);
        session.Status = SessionViewStatus.Active;
        coordinator.ApplyRecoveredCatalogState([project], [session]);
        coordinator.OpenSession(session.SessionId);

        var closeResult = await coordinator.CloseSessionTabAsync(session.SessionId).ConfigureAwait(false);

        Assert.AreEqual(TabCloseResult.Closed, closeResult);
        Assert.AreSame(session, coordinator.FindSession(session.SessionId));
        Assert.AreEqual(SessionViewStatus.Active, session.Status);
        Assert.IsFalse(coordinator.ViewState.OpenSessionIds.Contains(session.SessionId, StringComparer.OrdinalIgnoreCase));

        coordinator.OpenSession(session.SessionId);

        Assert.IsTrue(coordinator.ViewState.OpenSessionIds.Contains(session.SessionId, StringComparer.OrdinalIgnoreCase));
        Assert.IsNotNull(coordinator.FindOpenSession(session.SessionId));
        Assert.AreEqual(session.SessionId, coordinator.SelectedSessionId);
    }

    [TestMethod]
    public async Task CloseSessionTabAsync_DetachesSessionTabWithExplicitCloseReason()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var closeReasons = new List<(string SessionId, ShellTabCloseReason Reason)>();
        var coordinator = CreateCoordinator(options, removeSessionTabPage: (sessionId, reason) => closeReasons.Add((sessionId, reason)));
        var project = CreateProject("project-1", "CodeAlta");
        coordinator.ApplyRecoveredCatalogState([project], [CreateSession("session-1", project.Id)]);
        coordinator.OpenSession("session-1");

        await coordinator.CloseSessionTabAsync("session-1").ConfigureAwait(false);

        CollectionAssert.AreEqual(
            new[] { ("session-1", ShellTabCloseReason.UserDetached) },
            closeReasons.ToArray());
    }

    [TestMethod]
    public async Task CloseSessionTabAsync_SelectsFallbackSessionBeforeRemovingSelectedTabPage()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        string? selectedSessionDuringRemoval = null;
        ShellSessionStateCoordinator? coordinator = null;
        coordinator = CreateCoordinator(
            options,
            getOpenSessionTabIds: static () => ["session-1", "session-2"],
            removeSessionTabPage: (_, _) => selectedSessionDuringRemoval = coordinator!.SelectedSessionId);
        var project = CreateProject("project-1", "CodeAlta");
        coordinator.ApplyRecoveredCatalogState(
            [project],
            [CreateSession("session-1", project.Id), CreateSession("session-2", project.Id)]);
        coordinator.OpenSession("session-1");
        coordinator.OpenSession("session-2");

        await coordinator.CloseSessionTabAsync("session-2").ConfigureAwait(false);

        Assert.AreEqual("session-1", selectedSessionDuringRemoval);
        Assert.AreEqual("session-1", coordinator.SelectedSessionId);
    }

    [TestMethod]
    public async Task CloseSessionTabAsync_DoesNotMaterializePersistedSessionWhenClosingSelectedTab()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var loadedSessionIds = new List<string>();
        var project = CreateProject("project-1", "CodeAlta");
        var firstSession = CreateSession("session-1", project.Id);
        var selectedSession = CreateSession("session-2", project.Id);
        var coordinator = CreateCoordinator(
            options,
            getOpenSessionTabIds: () => [selectedSession.SessionId],
            ensureSessionHistoryLoadedAsync: (session, _) =>
            {
                loadedSessionIds.Add(session.SessionId);
                return Task.CompletedTask;
            });
        coordinator.ViewState = new SessionViewViewState
        {
            OpenSessionIds = [firstSession.SessionId, selectedSession.SessionId],
            SelectedSessionId = selectedSession.SessionId,
            Selection = SessionViewSelectionState.Session(selectedSession.SessionId, project.Id),
        };
        coordinator.ApplyRecoveredCatalogState([project], [firstSession, selectedSession]);
        Assert.IsNull(coordinator.FindOpenSession(firstSession.SessionId));

        await coordinator.CloseSessionTabAsync(selectedSession.SessionId).ConfigureAwait(false);

        Assert.IsNull(coordinator.SelectedSessionId);
        Assert.IsFalse(coordinator.ViewState.OpenSessionIds.Contains(firstSession.SessionId, StringComparer.OrdinalIgnoreCase));
        Assert.IsInstanceOfType<WorkspaceTarget.Draft>(coordinator.Selection.Target);
        Assert.IsFalse(coordinator.GlobalScopeSelected);
        Assert.AreEqual(project.Id, coordinator.SelectedProjectId);
        Assert.IsNull(coordinator.FindOpenSession(firstSession.SessionId));
        CollectionAssert.DoesNotContain(loadedSessionIds, firstSession.SessionId);
    }

    [TestMethod]
    public async Task CloseSessionTabAsync_SelectsDraftBeforeRemovingLastSelectedTabPage()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        string? selectedSessionDuringRemoval = "not-called";
        WorkspaceTarget? targetDuringRemoval = null;
        ShellSessionStateCoordinator? coordinator = null;
        coordinator = CreateCoordinator(
            options,
            removeSessionTabPage: (_, _) =>
            {
                selectedSessionDuringRemoval = coordinator!.SelectedSessionId;
                targetDuringRemoval = coordinator.Selection.Target;
            });
        var project = CreateProject("project-1", "CodeAlta");
        coordinator.ApplyRecoveredCatalogState([project], [CreateSession("session-1", project.Id)]);
        coordinator.OpenSession("session-1");

        await coordinator.CloseSessionTabAsync("session-1").ConfigureAwait(false);

        Assert.IsNull(selectedSessionDuringRemoval);
        Assert.IsInstanceOfType<WorkspaceTarget.Draft>(targetDuringRemoval);
        Assert.IsNull(coordinator.SelectedSessionId);
        Assert.IsInstanceOfType<WorkspaceTarget.Draft>(coordinator.Selection.Target);
    }

    [TestMethod]
    public void EnsureSessionTab_LoadsPersistedPromptDraft()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var coordinator = CreateCoordinator(options, loadPromptDraft: static sessionId => sessionId == "session-1" ? "saved prompt" : null);
        var project = CreateProject("project-1", "CodeAlta");
        var session = CreateSession("session-1", project.Id);
        coordinator.ApplyRecoveredCatalogState([project], [session]);

        var tab = coordinator.EnsureSessionTab(session);

        Assert.AreEqual("saved prompt", tab.Session.PromptDraftText);
    }

    [TestMethod]
    public void OpenSession_ReturnsTypedLifecycleResult()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var coordinator = CreateCoordinator(options);
        var project = CreateProject("project-1", "CodeAlta");
        var session = CreateSession("session-1", project.Id);
        coordinator.ApplyRecoveredCatalogState([project], [session]);

        var opened = coordinator.OpenSession(session.SessionId);
        var alreadyOpen = coordinator.OpenSession(session.SessionId);
        var missing = coordinator.OpenSession("missing-session");

        Assert.AreEqual(OpenSessionResult.Opened, opened);
        Assert.AreEqual(OpenSessionResult.AlreadyOpen, alreadyOpen);
        Assert.AreEqual(OpenSessionResult.NotFound, missing);
    }

    [TestMethod]
    public async Task CloseSessionTabAsync_ReturnsNotOpenForMissingOpenTab()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var coordinator = CreateCoordinator(options);

        var result = await coordinator.CloseSessionTabAsync("session-1").ConfigureAwait(false);

        Assert.AreEqual(TabCloseResult.NotOpen, result);
    }

    [TestMethod]
    public void RemoveDeletedProject_SelectedProjectScopeFallsBackToGlobal()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var coordinator = CreateCoordinator(options);
        var project = CreateProject("project-1", "CodeAlta");
        coordinator.ApplyRecoveredCatalogState([project], [CreateSession("session-1", project.Id)]);
        coordinator.SelectProjectScope(project.Id);

        coordinator.RemoveDeletedProject(project.Id, ["session-1"]);

        Assert.IsTrue(coordinator.DraftTabOpen);
        Assert.IsTrue(coordinator.GlobalScopeSelected);
        Assert.IsNull(coordinator.SelectedSessionId);
        Assert.IsNull(coordinator.GetProjectById(project.Id));
        Assert.IsFalse(coordinator.Sessions.Any(session => session.SessionId == "session-1"));
        Assert.AreEqual(ShellSurface.DraftWorkspace, coordinator.Selection.Surface);
        Assert.IsInstanceOfType<WorkspaceTarget.Draft>(coordinator.Selection.Target);
    }

    [TestMethod]
    public void DeleteSessionAndProject_CloseTabsWithExplicitReasons()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var closeReasons = new List<(string SessionId, ShellTabCloseReason Reason)>();
        var coordinator = CreateCoordinator(options, removeSessionTabPage: (sessionId, reason) => closeReasons.Add((sessionId, reason)));
        var project = CreateProject("project-1", "CodeAlta");
        coordinator.ApplyRecoveredCatalogState([project], [CreateSession("session-1", project.Id), CreateSession("session-2", project.Id)]);
        coordinator.OpenSession("session-1");
        coordinator.OpenSession("session-2");

        coordinator.RemoveDeletedSession("session-1", fallbackProjectId: project.Id);
        coordinator.RemoveDeletedProject(project.Id, ["session-2"]);

        CollectionAssert.AreEqual(
            new[]
            {
                ("session-1", ShellTabCloseReason.SessionDeleted),
                ("session-2", ShellTabCloseReason.ProjectClosed),
            },
            closeReasons.ToArray());
        Assert.IsNull(coordinator.GetProjectById(project.Id));
        Assert.IsFalse(coordinator.Sessions.Any());
    }

    [TestMethod]
    public void SelectProjectScope_ReturnsTypedSelectionChangeResult()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var coordinator = CreateCoordinator(options);
        var project = CreateProject("project-1", "CodeAlta");
        coordinator.ApplyRecoveredCatalogState([project], []);

        var changed = coordinator.SelectProjectScope(project.Id);
        var unchanged = coordinator.SelectProjectScope(project.Id);

        Assert.AreEqual(SelectionChangeResult.Changed, changed);
        Assert.AreEqual(SelectionChangeResult.Unchanged, unchanged);
    }

    [TestMethod]
    public void OpenSession_GlobalSessionDoesNotSelectFirstProject()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var coordinator = CreateCoordinator(options);
        var firstProject = CreateProject("project-1", ".azuredevops");
        var globalSession = CreateGlobalSession("global-session", options.GlobalRoot);
        coordinator.ApplyRecoveredCatalogState([firstProject], [globalSession]);

        coordinator.OpenSession(globalSession.SessionId);

        Assert.AreEqual(globalSession.SessionId, coordinator.SelectedSessionId);
        Assert.IsNull(coordinator.SelectedProjectId);
        Assert.IsNull(coordinator.GetSelectedProject());
    }

    [TestMethod]
    public void ApplyRecoveredCatalogState_PreservesPersistedDraftSelectionEvenWhenOpenSessionsExist()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var coordinator = CreateCoordinator(options);
        var project = CreateProject("project-1", "CodeAlta");
        coordinator.ViewState = new SessionViewViewState
        {
            OpenSessionIds = ["session-1"],
            Selection = SessionViewSelectionState.ProjectDraft(project.Id),
        };

        coordinator.ApplyRecoveredCatalogState([project], [CreateSession("session-1", project.Id)]);

        Assert.AreEqual(ShellSurface.DraftWorkspace, coordinator.Selection.Surface);
        Assert.IsInstanceOfType<WorkspaceTarget.Draft>(coordinator.Selection.Target);
        Assert.IsFalse(coordinator.GlobalScopeSelected);
        Assert.AreEqual(project.Id, coordinator.SelectedProjectId);
        Assert.IsNull(coordinator.SelectedSessionId);
    }

    [TestMethod]
    public void ApplyInitialCatalogState_PreservesPersistedDraftSelectionEvenWhenOpenSessionsExist()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var coordinator = CreateCoordinator(options);
        var project = CreateProject("project-1", "CodeAlta");
        var firstSession = CreateSession("session-1", project.Id);
        var lastSession = CreateSession("session-2", project.Id);

        coordinator.ApplyInitialCatalogState(new ShellSessionStateCoordinator.InitialCatalogState(
            [project],
            [firstSession, lastSession],
            new SessionViewViewState
            {
                OpenSessionIds = [firstSession.SessionId, lastSession.SessionId],
                Selection = SessionViewSelectionState.ProjectDraft(project.Id),
            }));

        Assert.AreEqual(ShellSurface.DraftWorkspace, coordinator.Selection.Surface);
        Assert.IsInstanceOfType<WorkspaceTarget.Draft>(coordinator.Selection.Target);
        Assert.IsFalse(coordinator.GlobalScopeSelected);
        Assert.AreEqual(project.Id, coordinator.SelectedProjectId);
        Assert.IsNull(coordinator.SelectedSessionId);
        Assert.IsNull(coordinator.PendingStartupSessionRestoreId);
        Assert.AreEqual(SessionViewSelectionSurface.Draft, coordinator.ViewState.Selection.Surface);
        Assert.AreEqual(SessionViewDraftScope.Project, coordinator.ViewState.Selection.DraftScope);
    }

    [TestMethod]
    public void ApplyRecoveredCatalogState_PartialRecoveryPreservesPendingStartupRestore()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var coordinator = CreateCoordinator(options);
        var project = CreateProject("project-1", "CodeAlta");
        var fastSession = CreateSession("session-fast", project.Id);
        var slowSession = CreateSession("session-slow", project.Id);
        coordinator.ViewState = new SessionViewViewState
        {
            OpenSessionIds = [slowSession.SessionId],
            SelectedSessionId = slowSession.SessionId,
            Selection = SessionViewSelectionState.Session(slowSession.SessionId, project.Id),
        };
        coordinator.PendingStartupSessionRestoreId = slowSession.SessionId;

        coordinator.ApplyRecoveredCatalogState([project], [fastSession], pruneMissingSessions: false);

        Assert.AreEqual(slowSession.SessionId, coordinator.PendingStartupSessionRestoreId);
        CollectionAssert.Contains(coordinator.ViewState.OpenSessionIds, slowSession.SessionId);

        coordinator.ApplyRecoveredCatalogState([project], [fastSession, slowSession], pruneMissingSessions: false);

        Assert.AreEqual(slowSession.SessionId, coordinator.SelectedSessionId);
        CollectionAssert.Contains(coordinator.ViewState.OpenSessionIds, slowSession.SessionId);
    }

    [TestMethod]
    public async Task SaveNavigatorSettingsAsync_PersistsUpdatedSettings()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var sessionCatalog = new SessionViewCatalog(options);
        var coordinator = CreateCoordinator(options, sessionCatalog);

        await coordinator.SaveNavigatorSettingsAsync(new NavigatorSettings
        {
            SortMode = NavigatorProjectSortMode.Date,
            RecentSessionsPerProject = 7,
            ThemeSchemeName = "Elderberry Dark Soft",
        }).ConfigureAwait(false);

        var viewState = await sessionCatalog.LoadViewStateAsync().ConfigureAwait(false);
        Assert.AreEqual(NavigatorProjectSortMode.Date, viewState.Navigator.SortMode);
        Assert.AreEqual(7, viewState.Navigator.RecentSessionsPerProject);
        Assert.AreEqual("Elderberry Dark Soft", viewState.Navigator.ThemeSchemeName);
    }

    [TestMethod]
    public async Task LoadNavigatorSettingsAsync_CanApplyPersistedLanguageBeforeCatalogRecovery()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var sessionCatalog = new SessionViewCatalog(options);
        await sessionCatalog.SaveViewStateAsync(new SessionViewViewState
        {
            Navigator = new NavigatorSettings
            {
                LanguageName = "zh-CN",
            },
        }).ConfigureAwait(false);
        var coordinator = CreateCoordinator(options, sessionCatalog);
        var previousUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");

            var settings = await coordinator.LoadNavigatorSettingsAsync(CancellationToken.None).ConfigureAwait(false);
            coordinator.ApplyNavigatorSettingsSnapshot(settings);
            UiTheme.ApplyLanguage(coordinator.NavigatorSettings);

            Assert.AreEqual("zh-CN", SR.Language);
            Assert.AreEqual("语言", SR.T("Language"));
        }
        finally
        {
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }

    [TestMethod]
    public async Task NavigatorThemePreview_ChangesEffectiveThemeWithoutPersisting()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var sessionCatalog = new SessionViewCatalog(options);
        var coordinator = CreateCoordinator(options, sessionCatalog);

        await coordinator.SaveNavigatorSettingsAsync(new NavigatorSettings
        {
            SortMode = NavigatorProjectSortMode.Date,
            RecentSessionsPerProject = 7,
            ThemeSchemeName = "Elderberry Dark Soft",
        }).ConfigureAwait(false);

        coordinator.PreviewNavigatorTheme("Blueberry Light");

        Assert.AreEqual("Blueberry Light", coordinator.EffectiveThemeSchemeName);
        var viewState = await sessionCatalog.LoadViewStateAsync().ConfigureAwait(false);
        Assert.AreEqual("Elderberry Dark Soft", viewState.Navigator.ThemeSchemeName);

        coordinator.ClearNavigatorThemePreview();

        Assert.AreEqual("Elderberry Dark Soft", coordinator.EffectiveThemeSchemeName);
    }

    [TestMethod]
    public void CatalogSelectionAndNavigatorMutations_UpdateShellStateStore()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var stateStore = new ShellStateStore(new InlineUiDispatcher());
        var coordinator = CreateCoordinator(options, stateStore: stateStore);
        var project = CreateProject("project-1", "Project 1");
        var session = CreateSession("session-1");

        coordinator.ApplyRecoveredCatalogState([project], [session]);
        coordinator.OpenSession("session-1");
        coordinator.SaveNavigatorSettingsAsync(new NavigatorSettings
        {
            SortMode = NavigatorProjectSortMode.Date,
            RecentSessionsPerProject = 5,
            ThemeSchemeName = "Blueberry Light",
        }).GetAwaiter().GetResult();

        var snapshot = stateStore.Snapshot;
        Assert.AreEqual(1, snapshot.Projects.Count);
        Assert.AreEqual(1, snapshot.Sessions.Count);
        Assert.AreEqual("session-1", snapshot.Selection.SelectedSessionId);
        CollectionAssert.AreEqual(new[] { "session-1" }, snapshot.OpenSessionIds.ToArray());
        Assert.AreEqual(NavigatorProjectSortMode.Date, snapshot.NavigatorSettings.SortMode);
        Assert.AreEqual(5, snapshot.NavigatorSettings.RecentSessionsPerProject);
        Assert.AreEqual("Blueberry Light", snapshot.NavigatorSettings.ThemeSchemeName);
    }

    [TestMethod]
    public void CatalogAndSelectionMutations_PublishTypedFrontendEvents()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var dispatcher = new InlineUiDispatcher();
        var publisher = new FrontendEventPublisher(dispatcher);
        var events = new List<ShellFrontendEvent>();
        publisher.Subscribe(events.Add);
        var coordinator = CreateCoordinator(
            options,
            stateStore: new ShellStateStore(dispatcher),
            frontendEvents: publisher);
        var project = CreateProject("project-1", "Project 1");
        var session = CreateSession("session-1");

        coordinator.ApplyRecoveredCatalogState([project], [session]);
        coordinator.OpenSession(session.SessionId);

        Assert.IsTrue(events.OfType<CatalogChangedEvent>().Any());
        Assert.IsTrue(events.OfType<SelectionChangedEvent>().Any(selection => selection.Snapshot?.Selection.SelectedSessionId == session.SessionId));
    }

    [TestMethod]
    public void EnsureSelectionDefaults_DoesNotPublishWhenSelectionIsUnchanged()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var dispatcher = new InlineUiDispatcher();
        var publisher = new FrontendEventPublisher(dispatcher);
        var events = new List<ShellFrontendEvent>();
        publisher.Subscribe(events.Add);
        var coordinator = CreateCoordinator(
            options,
            stateStore: new ShellStateStore(dispatcher),
            frontendEvents: publisher);
        var project = CreateProject("project-1", "Project 1");

        coordinator.ApplyRecoveredCatalogState([project], []);
        events.Clear();

        coordinator.EnsureSelectionDefaults();

        CollectionAssert.AreEqual(Array.Empty<ShellFrontendEvent>(), events);
    }

    [TestMethod]
    public async Task RemoveDeletedSessionArtifactsAsync_RemovesPersistedSessionStateAndPendingRestore()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var sessionCatalog = new SessionViewCatalog(options);
        var deletedPromptDrafts = new List<string>();
        var coordinator = CreateCoordinator(
            options,
            sessionCatalog,
            deletePromptDraft: deletedPromptDrafts.Add);
        coordinator.ViewState = new SessionViewViewState
        {
            SessionStates = new Dictionary<string, SessionViewLocalState>(StringComparer.OrdinalIgnoreCase)
            {
                ["session-1"] = new SessionViewLocalState
                {
                    Archived = true,
                    MessageCount = 4,
                },
                ["session-2"] = new SessionViewLocalState
                {
                    MessageCount = 1,
                },
            },
        };
        coordinator.PendingStartupSessionRestoreId = "session-1";

        await coordinator.RemoveDeletedSessionArtifactsAsync(["session-1"]).ConfigureAwait(false);

        Assert.IsNull(coordinator.PendingStartupSessionRestoreId);
        CollectionAssert.AreEqual(new[] { "session-1" }, deletedPromptDrafts);
        Assert.IsFalse(coordinator.ViewState.SessionStates.ContainsKey("session-1"));
        Assert.IsTrue(coordinator.ViewState.SessionStates.ContainsKey("session-2"));

        var persistedViewState = await sessionCatalog.LoadViewStateAsync().ConfigureAwait(false);
        Assert.IsFalse(persistedViewState.SessionStates.ContainsKey("session-1"));
        Assert.IsFalse(persistedViewState.SessionStates.ContainsKey("session-2"));
    }

    [TestMethod]
    public void RekeySessionIdentity_MovesOpenSessionSelectionAndPreferences()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var coordinator = CreateCoordinator(options);
        var project = CreateProject("project-1", "CodeAlta");
        var session = CreateSession("openai:session-1", project.Id);
        coordinator.ApplyRecoveredCatalogState([project], [session]);
        coordinator.ViewState.OpenSessionIds.Add(session.SessionId);
        coordinator.ViewState.SelectedSessionId = session.SessionId;
        coordinator.ViewState.Selection = SessionViewSelectionState.Session(session.SessionId, project.Id);
        coordinator.ViewState.SessionStates[session.SessionId] = new SessionViewLocalState { MessageCount = 9 };
        coordinator.ViewState.SessionPreferences[session.SessionId] = new SessionViewPreference
        {
            ModelId = "gpt-4.1",
            ReasoningEffort = AgentReasoningEffort.High,
        };
        coordinator.OpenSession(session.SessionId);
        coordinator.PendingStartupSessionRestoreId = session.SessionId;

        var tab = coordinator.FindOpenSession(session.SessionId);
        Assert.IsNotNull(tab);
        tab.Session.PromptDraftText = "preserve me";

        session.SessionId = "anthropic:session-1";
        session.ProviderId = "anthropic";
        session.ProviderKey = "anthropic";
        coordinator.RekeySessionIdentity("openai:session-1", session);

        Assert.AreEqual("anthropic:session-1", coordinator.SelectedSessionId);
        Assert.AreEqual("anthropic:session-1", coordinator.ViewState.SelectedSessionId);
        Assert.AreEqual("anthropic:session-1", coordinator.ViewState.Selection.SessionId);
        CollectionAssert.AreEqual(new[] { "anthropic:session-1" }, coordinator.ViewState.OpenSessionIds);
        Assert.IsFalse(coordinator.ViewState.SessionStates.ContainsKey("openai:session-1"));
        Assert.IsTrue(coordinator.ViewState.SessionStates.ContainsKey("anthropic:session-1"));
        Assert.IsFalse(coordinator.ViewState.SessionPreferences.ContainsKey("openai:session-1"));
        Assert.IsTrue(coordinator.ViewState.SessionPreferences.ContainsKey("anthropic:session-1"));
        Assert.AreEqual("anthropic:session-1", coordinator.PendingStartupSessionRestoreId);

        var reboundTab = coordinator.FindOpenSession("anthropic:session-1");
        Assert.IsNotNull(reboundTab);
        Assert.AreEqual("preserve me", reboundTab.Session.PromptDraftText);
        Assert.IsNull(coordinator.FindOpenSession("openai:session-1"));
    }

    private static ShellSessionStateCoordinator CreateCoordinator(
        CatalogOptions options,
        SessionViewCatalog? sessionCatalog = null,
        Func<string, string?>? loadPromptDraft = null,
        Action<string>? deletePromptDraft = null,
        Action<string>? replaceDraftTabWithSession = null,
        Action<string, ShellTabCloseReason>? removeSessionTabPage = null,
        Func<IReadOnlyList<string>>? getOpenSessionTabIds = null,
        ShellStateStore? stateStore = null,
        FrontendEventPublisher? frontendEvents = null,
        Func<SessionViewDescriptor, CancellationToken, Task>? ensureSessionHistoryLoadedAsync = null,
        ProjectDescriptor? currentProject = null)
    {
        sessionCatalog ??= new SessionViewCatalog(options);
        return TestSessionStateServices.CreateCoordinator(
            new ProjectCatalog(options),
            sessionCatalog,
            new InlineUiDispatcher(),
            stateStore ?? new ShellStateStore(new InlineUiDispatcher()),
            loadPromptDraft: loadPromptDraft,
            deletePromptDraft: deletePromptDraft,
            ensureSessionHistoryLoadedAsync: ensureSessionHistoryLoadedAsync,
            getOpenSessionTabIds: getOpenSessionTabIds,
            replaceDraftTabWithSession: replaceDraftTabWithSession,
            removeSessionTabPage: removeSessionTabPage,
            frontendEvents: frontendEvents,
            currentProject: currentProject);
    }

    private static SessionViewDescriptor CreateSession(string sessionId)
        => CreateSession(sessionId, "project-1");

    private static SessionViewDescriptor CreateSession(string sessionId, string projectId)
    {
        var timestamp = DateTimeOffset.Parse("2026-03-29T12:00:00+00:00");
        return new SessionViewDescriptor
        {
            SessionId = sessionId,
            Kind = SessionViewKind.ProjectSession,
            ProviderId = ModelProviderIds.Codex.Value,
            ProjectRef = "project-1",
            WorkingDirectory = @"C:\repo",
            Title = "Test session",
            Status = SessionViewStatus.Active,
            CreatedAt = timestamp,
            UpdatedAt = timestamp,
            LastActiveAt = timestamp,
        };
    }

    private static SessionViewDescriptor CreateGlobalSession(string sessionId, string globalRoot)
    {
        var timestamp = DateTimeOffset.Parse("2026-03-29T12:00:00+00:00");
        return new SessionViewDescriptor
        {
            SessionId = sessionId,
            Kind = SessionViewKind.GlobalSession,
            ProviderId = ModelProviderIds.Codex.Value,
            WorkingDirectory = globalRoot,
            Title = "Global Session",
            Status = SessionViewStatus.Active,
            CreatedAt = timestamp,
            UpdatedAt = timestamp,
            LastActiveAt = timestamp,
        };
    }

    private static ProjectDescriptor CreateProject(string id, string displayName)
    {
        return new ProjectDescriptor
        {
            Id = id,
            Slug = displayName.ToLowerInvariant(),
            Name = displayName,
            DisplayName = displayName,
            ProjectPath = $@"C:\repo\{displayName}",
            DefaultBranch = "main",
        };
    }

    private sealed class InlineUiDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;

        public void Post(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);
            action();
        }

        public Task InvokeAsync(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);
            action();
            return Task.CompletedTask;
        }

        public Task<T> InvokeAsync<T>(Func<T> action)
        {
            ArgumentNullException.ThrowIfNull(action);
            return Task.FromResult(action());
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TempDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"CodeAlta.Tests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new TempDirectory(path);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch
            {
            }
        }
    }
}
