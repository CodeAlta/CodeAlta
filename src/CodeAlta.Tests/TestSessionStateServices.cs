using CodeAlta.Agent;
using CodeAlta.Tui.App;
using CodeAlta.Tui.App.Events;
using CodeAlta.Tui.App.State;
using CodeAlta.Catalog;
using CodeAlta.Tui.Models;
using CodeAlta.Tui.Threading;
using XenoAtom.Terminal.UI.Geometry;

namespace CodeAlta.Tests;

internal static class TestSessionStateServices
{
    public static ShellSessionStateCoordinator CreateCoordinator(
        ProjectCatalog projectCatalog,
        SessionViewCatalog sessionCatalog,
        IUiDispatcher uiDispatcher,
        ShellStateStore stateStore,
        Func<Rectangle?>? getTimelineBounds = null,
        Func<SessionViewDescriptor, bool>? isModelProviderReady = null,
        Func<string, string?>? loadPromptDraft = null,
        Action<string>? deletePromptDraft = null,
        Action<OpenSessionState>? applySessionPreference = null,
        Action<string, string?, AgentReasoningEffort?, bool>? rememberSessionPreference = null,
        Func<SessionViewDescriptor, CancellationToken, Task>? ensureSessionHistoryLoadedAsync = null,
        Func<IReadOnlyList<string>>? getOpenSessionTabIds = null,
        Action? resetPendingSessionTabSelection = null,
        Action<string>? replaceDraftTabWithSession = null,
        Action<string, ShellTabCloseReason>? removeSessionTabPage = null,
        FrontendEventPublisher? frontendEvents = null,
        ProjectDescriptor? currentProject = null)
        => new(
            projectCatalog,
            sessionCatalog,
            uiDispatcher,
            stateStore,
            new SessionTimelineSurface(getTimelineBounds ?? (static () => null)),
            new SessionPromptDraftService(loadPromptDraft ?? (static _ => null), deletePromptDraft ?? (static _ => { })),
            new SessionModelProviderPreferenceService(
                applySessionPreference ?? (static _ => { }),
                rememberSessionPreference ?? (static (_, _, _, _) => { })),
            new SessionModelProviderReadinessService(isModelProviderReady ?? (static _ => true)),
            new SessionHistoryLoaderService(ensureSessionHistoryLoadedAsync ?? (static (_, _) => Task.CompletedTask)),
            new SessionStateTabLifecycleService(
                getOpenSessionTabIds ?? (static () => []),
                resetPendingSessionTabSelection ?? (static () => { }),
                replaceDraftTabWithSession ?? (static _ => { }),
                removeSessionTabPage ?? (static (_, _) => { })),
            frontendEvents,
            currentProject);
}
