using CodeAlta.Agent;
using CodeAlta.Tui.App;
using CodeAlta.Tui.App.Context;
using CodeAlta.Tui.App.State;
using CodeAlta.Catalog;
using CodeAlta.Tui.Models;
using CodeAlta.Tui.Presentation.Prompting;
using CodeAlta.Tui.Presentation.Timeline;
using CodeAlta.Tui.Threading;
using CodeAlta.Tui.ViewModels;

namespace CodeAlta.Tests;

[TestClass]
public sealed class SessionPromptQueueCoordinatorTests
{
    [TestMethod]
    public async Task ImageSubmission_QueueRetryAndComposerClearPreserveOwnedBytesAndSavedReferences()
    {
        using var temp = TempDirectory.Create();
        var tab = CreateOpenSessionState();
        var store = new PromptImageAttachmentStore(new CatalogOptions { GlobalRoot = temp.Path });
        var composer = new List<PromptImageAttachment> { PromptImageAttachmentFactory.Create("Screenshot", [1, 2, 3], "image/png", ".png") };
        var port = new LegacyPromptSessionPort(new InlineUiDispatcher(), () => composer.Count == 0,
            composer.Clear, static _ => { }, () => composer, images => composer.AddRange(images.Select(image => image.Copy())));
        var promptSessionId = new PromptSessionId("prompt-1");
        var submission = port.CapturePrompt(promptSessionId, "describe");
        IReadOnlyList<PromptImageAttachmentReference>? saved = null;
        var attempts = 0;
        var coordinator = CreateCoordinator(temp.Path, async (_, prompt, token) =>
        {
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, prompt.Images[0].Bytes);
            saved = await store.SaveAsync(tab.SessionView, prompt.Images, token);
            var input = prompt.AppendImageItems(AgentInput.Text(prompt.Text), saved);
            var image = input.Items.OfType<AgentInputItem.LocalImage>().Single();
            Assert.AreEqual(saved[0].Path, image.Path);
            if (++attempts == 1)
            {
                throw new IOException("dispatch failed after persistence");
            }
        });
        coordinator.EnqueuePrompt(tab, submission);
        composer[0].Bytes[0] = 99;
        port.ClearPrompt(promptSessionId);
        Assert.IsTrue(port.IsPromptEmpty(promptSessionId));
        submission.Images[0].Bytes[0] = 88;

        await coordinator.DrainNextQueuedPromptAsync(tab);
        Assert.AreEqual(1, tab.QueuedPrompts.Count);
        port.RestorePrompt(promptSessionId, tab.QueuedPrompts[0].Submission);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, composer[0].Bytes);
        port.ClearPrompt(promptSessionId);
        var firstPath = saved![0].Path;
        await coordinator.DrainNextQueuedPromptAsync(tab);

        Assert.AreEqual(0, tab.QueuedPrompts.Count);
        Assert.AreEqual(2, attempts);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(firstPath));
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(saved![0].Path));
    }

    [TestMethod]
    public async Task DrainNextQueuedPromptAsync_RemovesPromptBeforeDispatchCompletes()
    {
        using var temp = TempDirectory.Create();
        var tab = CreateOpenSessionState();
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
        var tab = CreateOpenSessionState();
        var coordinator = CreateCoordinator(
            temp.Path,
            static (_, _, _) => throw new InvalidOperationException("dispatch failed"));
        coordinator.EnqueuePrompt(tab, "retry me");

        await coordinator.DrainNextQueuedPromptAsync(tab).WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);

        Assert.AreEqual(1, tab.QueuedPrompts.Count);
        Assert.AreEqual("retry me", tab.QueuedPrompts[0].Text);
    }

    private static SessionPromptQueueCoordinator CreateCoordinator(
        string rootPath,
        Func<OpenSessionState, PromptSubmission, CancellationToken, Task> dispatchQueuedPromptAsync)
    {
        var sessionSelection = CreateSessionSelectionContext(rootPath);
        return new SessionPromptQueueCoordinator(
            new SessionWorkspaceViewModel(),
            sessionSelection,
            static action => action(),
            static () => { },
            dispatchQueuedPromptAsync,
            static (_, _, _) => Task.CompletedTask);
    }

    private static SessionSelectionContext CreateSessionSelectionContext(string rootPath)
    {
        var catalogOptions = new CatalogOptions { GlobalRoot = rootPath };
        var sessionState = TestSessionStateServices.CreateCoordinator(
            new ProjectCatalog(catalogOptions),
            new SessionViewCatalog(catalogOptions),
            new InlineUiDispatcher(),
            new ShellStateStore(new InlineUiDispatcher()));
        sessionState.ViewState = new SessionViewViewState();

        return new SessionSelectionContext(
            sessionState,
            static (_, _) => Task.CompletedTask,
            static _ => false);
    }

    private static OpenSessionState CreateOpenSessionState()
    {
        var session = new SessionViewDescriptor
        {
            SessionId = "session-1",
            Kind = SessionViewKind.ProjectSession,
            ProviderId = ModelProviderIds.Codex.Value,
            ProjectRef = "project-1",
            WorkingDirectory = @"C:\code\CodeAlta",
            Title = "Review startup",
            Status = SessionViewStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            LastActiveAt = DateTimeOffset.UtcNow,
        };

        var timeline = new SessionTimelinePresenter(new InlineUiDispatcher(), static () => null);
        return new OpenSessionState(session, timeline);
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
