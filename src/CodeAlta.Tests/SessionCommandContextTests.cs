using CodeAlta.Agent;
using CodeAlta.Tui.App;
using CodeAlta.Tui.App.Context;
using CodeAlta.Tui.App.State;
using CodeAlta.Catalog;
using CodeAlta.Tui.Models;
using CodeAlta.Tui.Presentation.Prompting;
using CodeAlta.Tui.Threading;

namespace CodeAlta.Tests;

[TestClass]
public sealed class ShellSessionCommandContextTests
{
    [TestMethod]
    public void ClearDraftInput_InvokesCallbackOnUiDispatcher()
    {
        var dispatcher = new RecordingUiDispatcher();
        var calledOnUiSession = false;
        var context = CreateContext(
            dispatcher,
            clearDraftInput: () => calledOnUiSession = dispatcher.CheckAccess());

        context.ClearDraftInput();

        Assert.IsTrue(calledOnUiSession);
        Assert.AreEqual(1, dispatcher.InvokeActionCount);
    }

    [TestMethod]
    public void IsSessionInputEmpty_InvokesCallbackOnUiDispatcher()
    {
        var dispatcher = new RecordingUiDispatcher();
        var callbackRanOnUi = false;
        var context = CreateContext(
            dispatcher,
            isSessionInputEmpty: () =>
            {
                callbackRanOnUi = dispatcher.CheckAccess();
                return true;
            });

        var isEmpty = context.IsSessionInputEmpty();

        Assert.IsTrue(isEmpty);
        Assert.IsTrue(callbackRanOnUi);
        Assert.AreEqual(1, dispatcher.InvokeFuncCount);
    }

    [TestMethod]
    public void CaptureSessionInput_IncludesClonedPromptImages()
    {
        var dispatcher = new RecordingUiDispatcher();
        var image = PromptImageAttachmentFactory.Create("Image-1", [1, 2, 3], "image/png", ".png");
        var context = CreateContext(
            dispatcher,
            snapshotPromptImages: () => [image]);

        var submission = context.CaptureSessionInput(string.Empty);

        Assert.AreEqual(string.Empty, submission.Text);
        Assert.AreEqual(1, submission.Images.Count);
        Assert.AreEqual("Image-1", submission.Images[0].Title);
        Assert.AreNotSame(image.Bytes, submission.Images[0].Bytes);
        image.Bytes[0] = 9;
        Assert.AreEqual(1, submission.Images[0].Bytes[0]);
    }

    [TestMethod]
    public void RestoreSessionInput_RestoresTextAndPromptImages()
    {
        var dispatcher = new RecordingUiDispatcher();
        var restoredText = string.Empty;
        IReadOnlyList<PromptImageAttachment>? restoredImages = null;
        var context = CreateContext(
            dispatcher,
            restoreSessionInput: text => restoredText = text,
            restorePromptImages: images => restoredImages = images);
        var image = PromptImageAttachmentFactory.Create("Image-1", [1, 2, 3], "image/png", ".png");

        context.RestoreSessionInput(PromptSubmission.Create("retry", [image]));

        Assert.AreEqual("retry", restoredText);
        Assert.IsNotNull(restoredImages);
        Assert.AreEqual(1, restoredImages.Count);
        Assert.AreEqual("Image-1", restoredImages[0].Title);
    }

    [TestMethod]
    public void SetShellStatus_InvokesStatusCallbackOnUiDispatcher()
    {
        var dispatcher = new RecordingUiDispatcher();
        var callbackRanOnUi = false;
        var context = CreateContext(
            dispatcher,
            setShellStatus: (_, _, _) => callbackRanOnUi = dispatcher.CheckAccess());

        context.SetShellStatus("Ready", false, StatusTone.Ready);

        Assert.IsTrue(callbackRanOnUi);
        Assert.AreEqual(1, dispatcher.InvokeActionCount);
    }

    private static ShellSessionCommandContext CreateContext(
        IUiDispatcher dispatcher,
        Action? clearDraftInput = null,
        Func<bool>? isSessionInputEmpty = null,
        Action<string>? restoreSessionInput = null,
        Func<IReadOnlyList<PromptImageAttachment>>? snapshotPromptImages = null,
        Action<IReadOnlyList<PromptImageAttachment>>? restorePromptImages = null,
        Action<string, bool, StatusTone>? setShellStatus = null)
    {
        clearDraftInput ??= static () => { };
        isSessionInputEmpty ??= static () => true;
        restoreSessionInput ??= static _ => { };
        snapshotPromptImages ??= static () => [];
        restorePromptImages ??= static _ => { };
        setShellStatus ??= static (_, _, _) => { };
        var promptSessionId = new PromptSessionId("prompt-1");
        var promptSessionPort = new PromptSessionPort(
            dispatcher,
            isSessionInputEmpty,
            static () => { },
            restoreSessionInput,
            snapshotPromptImages,
            restorePromptImages);
        promptSessionPort.BindPromptSession(new PromptSessionBinding(
            promptSessionId,
            ProjectId.NewVersion7(),
            new ShellSessionRef.Draft(new SessionDraftId("draft-1")),
            new ModelProviderId("provider-1")));

        return new ShellSessionCommandContext(
            new DelegatingSessionLifecycleCommandPort(
                static _ => Task.FromResult<SessionViewDescriptor?>(null),
                static _ => Task.FromResult<SessionViewDescriptor?>(null),
                static () => Task.CompletedTask),
            new SessionCommandUiPort(
                dispatcher,
                static () => false,
                static () => true,
                clearDraftInput,
                static () => { },
                static () => { },
                static () => { },
                static (_, _, _) => { }),
            promptSessionPort,
            () => promptSessionId,
            new ShellStatusPort(dispatcher, setShellStatus, static (_, _, _, _) => { }));
    }

    private sealed class RecordingUiDispatcher : IUiDispatcher
    {
        private int _depth;

        public int InvokeActionCount { get; private set; }

        public int InvokeFuncCount { get; private set; }

        public bool CheckAccess() => _depth > 0;

        public void Post(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);
            _depth++;
            try
            {
                action();
            }
            finally
            {
                _depth--;
            }
        }

        public Task InvokeAsync(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);
            InvokeActionCount++;
            _depth++;
            try
            {
                action();
                return Task.CompletedTask;
            }
            finally
            {
                _depth--;
            }
        }

        public Task<T> InvokeAsync<T>(Func<T> action)
        {
            ArgumentNullException.ThrowIfNull(action);
            InvokeFuncCount++;
            _depth++;
            try
            {
                return Task.FromResult(action());
            }
            finally
            {
                _depth--;
            }
        }
    }
}
