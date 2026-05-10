using CodeAlta.Agent;
using CodeAlta.App;
using CodeAlta.App.Context;
using CodeAlta.App.State;
using CodeAlta.Catalog;
using CodeAlta.Models;
using CodeAlta.Threading;
using CodeAlta.ViewModels;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;

namespace CodeAlta.Tests;

[TestClass]
public sealed class ShellWorkspaceCoordinatorTests
{
    [TestMethod]
    public void ApplySelectionProjection_FocusesPromptWhenDisplayingThread()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var thread = CreateThread("thread-1", "project-1");
        var threadStateCoordinator = CreateThreadStateCoordinator(options);
        threadStateCoordinator.ApplyRecoveredCatalogState([CreateProject("project-1", "CodeAlta")], [thread]);
        threadStateCoordinator.OpenThread(thread.ThreadId);

        var threadSelection = new ThreadSelectionContext(
            threadStateCoordinator,
            static (_, _) => Task.CompletedTask,
            threadId => string.Equals(threadId, threadStateCoordinator.SelectedThreadId, StringComparison.OrdinalIgnoreCase));
        var deferredActions = new Queue<Action>();
        var focusedPromptCount = 0;
        Visual? paneContent = null;
        var uiDispatcher = new QueueingUiDispatcher(deferredActions);
        var workspaceContext = new ShellWorkspaceContext(
            new DelegatingShellPromptAvailabilityPort(
                static () => AgentBackendIds.Codex,
                static () => (false, string.Empty, StatusTone.Info)),
            new ShellWorkspaceSurfacePort(
                static () => true,
                static () => null,
                static () => null,
                content => paneContent = content,
                () => focusedPromptCount++,
                static _ => { },
                static _ => { }),
            new DelegatingShellWorkspaceProjectionPort(
                threadStateCoordinator.EnsureSelectionDefaults,
                static () => { },
                static () => { },
                static () => { },
                static () => { },
                static _ => { },
                static _ => { },
                static () => { },
                static () => { },
                static () => { }),
            uiDispatcher);
        var workspace = new ShellWorkspaceCoordinator(
            new CodeAltaShellViewModel(),
            new ThreadWorkspaceViewModel(),
            new SessionUsageViewModel(),
            CreateChatBackendStates(),
            threadSelection,
            workspaceContext,
            new State<float>(0));

        workspace.ApplySelectionProjection();

        var tab = threadStateCoordinator.FindOpenThread(thread.ThreadId);
        Assert.IsNotNull(tab);
        Assert.IsNull(paneContent);
        Assert.AreEqual(0, focusedPromptCount);

        while (deferredActions.Count > 0)
        {
            deferredActions.Dequeue()();
        }

        Assert.AreEqual(1, focusedPromptCount);
    }

    [TestMethod]
    public void ApplySelectionProjection_DraftScopeWithoutConfiguredProviders_DoesNotThrow()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var threadStateCoordinator = CreateThreadStateCoordinator(options);
        threadStateCoordinator.ApplyRecoveredCatalogState([], []);

        var threadSelection = new ThreadSelectionContext(
            threadStateCoordinator,
            static (_, _) => Task.CompletedTask,
            threadId => string.Equals(threadId, threadStateCoordinator.SelectedThreadId, StringComparison.OrdinalIgnoreCase));

        var (workspace, sessionUsage) = CreateWorkspaceCoordinator(
            threadStateCoordinator,
            threadSelection,
            new Dictionary<string, ChatBackendState>(StringComparer.OrdinalIgnoreCase),
            static () => AgentBackendIds.Codex);

        workspace.ApplySelectionProjection();

        Assert.AreEqual("Codex", sessionUsage.BackendName);
        Assert.IsNull(sessionUsage.ModelName);
        Assert.IsNull(sessionUsage.Usage);
    }

    [TestMethod]
    public void ApplySelectionProjection_SelectedThreadWithMissingProvider_DoesNotThrow()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var thread = CreateThread("thread-1", "project-1");
        var threadStateCoordinator = CreateThreadStateCoordinator(options);
        threadStateCoordinator.ApplyRecoveredCatalogState([CreateProject("project-1", "CodeAlta")], [thread]);
        threadStateCoordinator.OpenThread(thread.ThreadId);

        var threadSelection = new ThreadSelectionContext(
            threadStateCoordinator,
            static (_, _) => Task.CompletedTask,
            threadId => string.Equals(threadId, threadStateCoordinator.SelectedThreadId, StringComparison.OrdinalIgnoreCase));
        var (workspace, sessionUsage) = CreateWorkspaceCoordinator(
            threadStateCoordinator,
            threadSelection,
            new Dictionary<string, ChatBackendState>(StringComparer.OrdinalIgnoreCase),
            static () => AgentBackendIds.Codex);

        workspace.ApplySelectionProjection();

        Assert.AreEqual("Codex", sessionUsage.BackendName);
        Assert.IsNull(sessionUsage.ModelName);
        Assert.IsNull(sessionUsage.Usage);
    }

    [TestMethod]
    public void ShellWorkspaceContext_DispatchToUi_RunsInlineWhenDispatcherHasAccess()
    {
        var deferredActions = new Queue<Action>();
        var workspaceContext = CreateMinimalWorkspaceContext(new QueueingUiDispatcher(deferredActions));
        var count = 0;

        workspaceContext.DispatchToUi(() => count++);

        Assert.AreEqual(1, count);
        Assert.AreEqual(0, deferredActions.Count);
    }

    [TestMethod]
    public void ShellWorkspaceContext_DispatchToUiDeferred_QueuesWhenDispatcherHasAccess()
    {
        var deferredActions = new Queue<Action>();
        var workspaceContext = CreateMinimalWorkspaceContext(new QueueingUiDispatcher(deferredActions));
        var count = 0;

        workspaceContext.DispatchToUiDeferred(() => count++);

        Assert.AreEqual(0, count);
        Assert.AreEqual(1, deferredActions.Count);
    }

    private static (ShellWorkspaceCoordinator Workspace, SessionUsageViewModel SessionUsage) CreateWorkspaceCoordinator(
        ShellThreadStateCoordinator threadStateCoordinator,
        ThreadSelectionContext threadSelection,
        Dictionary<string, ChatBackendState> chatBackendStates,
        Func<AgentBackendId> getPreferredBackendId)
    {
        var workspaceContext = new ShellWorkspaceContext(
            new DelegatingShellPromptAvailabilityPort(
                getPreferredBackendId,
                static () => (true, "No model provider is ready.", StatusTone.Warning)),
            new ShellWorkspaceSurfacePort(
                static () => true,
                static () => null,
                static () => null,
                static _ => { },
                static () => { },
                static _ => { },
                static _ => { }),
            new DelegatingShellWorkspaceProjectionPort(
                threadStateCoordinator.EnsureSelectionDefaults,
                static () => { },
                static () => { },
                static () => { },
                static () => { },
                static _ => { },
                static _ => { },
                static () => { },
                static () => { },
                static () => { }),
            new InlineUiDispatcher());
        var sessionUsage = new SessionUsageViewModel();
        var workspace = new ShellWorkspaceCoordinator(
            new CodeAltaShellViewModel(),
            new ThreadWorkspaceViewModel(),
            sessionUsage,
            chatBackendStates,
            threadSelection,
            workspaceContext,
            new State<float>(0));

        return (workspace, sessionUsage);
    }

    private static ShellWorkspaceContext CreateMinimalWorkspaceContext(IUiDispatcher uiDispatcher)
        => new(
            new DelegatingShellPromptAvailabilityPort(
                static () => AgentBackendIds.Codex,
                static () => (false, string.Empty, StatusTone.Info)),
            new ShellWorkspaceSurfacePort(
                static () => false,
                static () => null,
                static () => null,
                static _ => { },
                static () => { },
                static _ => { },
                static _ => { }),
            new DelegatingShellWorkspaceProjectionPort(
                static () => { },
                static () => { },
                static () => { },
                static () => { },
                static () => { },
                static _ => { },
                static _ => { },
                static () => { },
                static () => { },
                static () => { }),
            uiDispatcher);

    private static ShellThreadStateCoordinator CreateThreadStateCoordinator(CatalogOptions options)
        => TestThreadStateServices.CreateCoordinator(
            new ProjectCatalog(options),
            new WorkThreadCatalog(options),
            new InlineUiDispatcher(),
            new ShellStateStore(new InlineUiDispatcher()));

    private static Dictionary<string, ChatBackendState> CreateChatBackendStates()
        => new(StringComparer.Ordinal)
        {
            [AgentBackendIds.Codex.Value] = new ChatBackendState(AgentBackendIds.Codex, "Codex")
            {
                Availability = ChatBackendAvailability.Ready,
            },
        };

    private static WorkThreadDescriptor CreateThread(string threadId, string projectId)
    {
        var timestamp = DateTimeOffset.Parse("2026-03-29T12:00:00+00:00");
        return new WorkThreadDescriptor
        {
            ThreadId = threadId,
            Kind = WorkThreadKind.ProjectThread,
            BackendId = AgentBackendIds.Codex.Value,
            BackendSessionId = $"session-{threadId}",
            ProjectRef = projectId,
            WorkingDirectory = @"C:\repo",
            Title = "Test thread",
            Status = WorkThreadStatus.Active,
            CreatedAt = timestamp,
            UpdatedAt = timestamp,
            LastActiveAt = timestamp,
        };
    }

    private static ProjectDescriptor CreateProject(string id, string displayName)
        => new()
        {
            Id = id,
            Slug = displayName.ToLowerInvariant(),
            Name = displayName,
            DisplayName = displayName,
            ProjectPath = $@"C:\repo\{displayName}",
            DefaultBranch = "main",
        };

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

    private sealed class QueueingUiDispatcher(Queue<Action> deferredActions) : IUiDispatcher
    {
        public bool CheckAccess() => true;

        public void Post(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);
            deferredActions.Enqueue(action);
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
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
