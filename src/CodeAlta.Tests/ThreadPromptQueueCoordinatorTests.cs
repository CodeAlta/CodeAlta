using CodeAlta.Agent;
using CodeAlta.App;
using CodeAlta.App.Context;
using CodeAlta.App.State;
using CodeAlta.Catalog;
using CodeAlta.Models;
using CodeAlta.Presentation.Prompting;
using CodeAlta.Presentation.Timeline;
using CodeAlta.Threading;
using CodeAlta.ViewModels;

namespace CodeAlta.Tests;

[TestClass]
public sealed class ThreadPromptQueueCoordinatorTests
{
    [TestMethod]
    public async Task DrainNextQueuedPromptAsync_RemovesPromptBeforeDispatchCompletes()
    {
        using var temp = TempDirectory.Create();
        var tab = CreateOpenThreadState();
        var dispatchStarted = new TaskCompletionSource<PromptSubmission>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondDispatchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDispatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatchCount = 0;
        var coordinator = CreateCoordinator(
            temp.Path,
            async (_, prompt, _) =>
            {
                var count = Interlocked.Increment(ref dispatchCount);
                if (count == 1)
                {
                    dispatchStarted.SetResult(prompt.Copy());
                }
                else
                {
                    secondDispatchStarted.SetResult();
                }

                await releaseDispatch.Task.ConfigureAwait(false);
            });
        coordinator.EnqueuePrompt(tab, "queued once");

        var firstDrain = coordinator.DrainNextQueuedPromptAsync(tab);
        var dispatchedPrompt = await dispatchStarted.Task.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        var secondDrain = coordinator.DrainNextQueuedPromptAsync(tab);
        var secondDispatchResult = await Task.WhenAny(secondDispatchStarted.Task, Task.Delay(TimeSpan.FromMilliseconds(100))).ConfigureAwait(false);

        Assert.AreEqual("queued once", dispatchedPrompt.Text);
        Assert.AreEqual(0, tab.QueuedPrompts.Count);
        Assert.AreEqual(1, dispatchCount);
        Assert.AreNotSame(secondDispatchStarted.Task, secondDispatchResult);

        releaseDispatch.SetResult();
        await Task.WhenAll(firstDrain, secondDrain).WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        Assert.AreEqual(0, tab.QueuedPrompts.Count);
        Assert.AreEqual(1, dispatchCount);
    }

    [TestMethod]
    public async Task DrainNextQueuedPromptAsync_RestoresPromptWhenDispatchThrows()
    {
        using var temp = TempDirectory.Create();
        var tab = CreateOpenThreadState();
        var coordinator = CreateCoordinator(
            temp.Path,
            static (_, _, _) => throw new InvalidOperationException("dispatch failed"));
        coordinator.EnqueuePrompt(tab, "retry me");

        await coordinator.DrainNextQueuedPromptAsync(tab).WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);

        Assert.AreEqual(1, tab.QueuedPrompts.Count);
        Assert.AreEqual("retry me", tab.QueuedPrompts[0].Text);
    }

    private static ThreadPromptQueueCoordinator CreateCoordinator(
        string rootPath,
        Func<OpenThreadState, PromptSubmission, CancellationToken, Task> dispatchQueuedPromptAsync)
    {
        var threadSelection = CreateThreadSelectionContext(rootPath);
        return new ThreadPromptQueueCoordinator(
            new ThreadWorkspaceViewModel(),
            threadSelection,
            static () => { },
            static action => action(),
            static () => { },
            dispatchQueuedPromptAsync,
            static (_, _, _) => Task.CompletedTask);
    }

    private static ThreadSelectionContext CreateThreadSelectionContext(string rootPath)
    {
        var catalogOptions = new CatalogOptions { GlobalRoot = rootPath };
        var threadState = new ShellThreadStateCoordinator(
            new ProjectCatalog(catalogOptions),
            new WorkThreadCatalog(catalogOptions),
            new InlineUiDispatcher(),
            new ShellStateStore(new InlineUiDispatcher()),
            static () => null,
            static _ => true,
            static _ => null,
            static _ => { },
            static _ => { },
            static (_, _, _, _) => { },
            static (_, _) => Task.CompletedTask,
            static () => { },
            static _ => { },
            static (_, _, _) => { });
        threadState.ViewState = new WorkThreadViewState();

        return new ThreadSelectionContext(
            threadState,
            static (_, _) => Task.CompletedTask,
            static _ => false);
    }

    private static OpenThreadState CreateOpenThreadState()
    {
        var thread = new WorkThreadDescriptor
        {
            ThreadId = "thread-1",
            Kind = WorkThreadKind.ProjectThread,
            BackendId = AgentBackendIds.Codex.Value,
            BackendSessionId = "backend-thread-1",
            ProjectRef = "project-1",
            WorkingDirectory = @"C:\code\CodeAlta",
            Title = "Review startup",
            Status = WorkThreadStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            LastActiveAt = DateTimeOffset.UtcNow,
        };

        var timeline = new ThreadTimelinePresenter(new InlineUiDispatcher(), static () => null);
        return new OpenThreadState(thread, timeline);
    }

    private sealed class InlineUiDispatcher : IUiDispatcher
    {
        public bool CheckAccess()
            => true;

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
