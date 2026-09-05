using CodeAlta.Agent;
using CodeAlta.Tui.App;
using CodeAlta.Catalog;
using CodeAlta.Tui.Models;
using CodeAlta.Tui.Presentation.Prompting;
using CodeAlta.Tui.Threading;

namespace CodeAlta.Tests;

[TestClass]
public sealed class PromptSessionPortTests
{
    [TestMethod]
    public void CapturePrompt_RequiresBoundPromptSessionAndClonesImages()
    {
        var image = PromptImageAttachment.Create("Image-1", [1, 2, 3], "image/png", ".png");
        var port = CreatePort(snapshotPromptImages: () => [image]);
        var promptSessionId = new PromptSessionId("prompt-1");
        port.BindPromptSession(CreateBinding(promptSessionId));

        var submission = port.CapturePrompt(promptSessionId, "hello");

        Assert.AreEqual("hello", submission.Text);
        Assert.AreEqual(1, submission.Images.Count);
        Assert.AreNotSame(image.Bytes, submission.Images[0].Bytes);
    }

    [TestMethod]
    public void RestorePrompt_RestoresTextAndImagesThroughDispatcher()
    {
        var dispatcher = new RecordingUiDispatcher();
        var restoredText = string.Empty;
        IReadOnlyList<PromptImageAttachment>? restoredImages = null;
        var port = CreatePort(
            dispatcher,
            restorePromptText: text => restoredText = text,
            restorePromptImages: images => restoredImages = images);
        var promptSessionId = new PromptSessionId("prompt-1");
        var image = PromptImageAttachment.Create("Image-1", [1], "image/png", ".png");
        port.BindPromptSession(CreateBinding(promptSessionId));

        port.RestorePrompt(promptSessionId, PromptSubmission.Create("retry", [image]));

        Assert.AreEqual("retry", restoredText);
        Assert.IsNotNull(restoredImages);
        Assert.AreEqual(1, restoredImages.Count);
        Assert.AreEqual(1, dispatcher.InvokeCount);
    }

    [TestMethod]
    public void GetPromptSession_ReturnsBoundSnapshotWithPromptEmptyState()
    {
        var port = CreatePort(isPromptEmpty: static () => true);
        var promptSessionId = new PromptSessionId("prompt-1");
        var binding = CreateBinding(promptSessionId);
        port.BindPromptSession(binding);

        var snapshot = port.GetPromptSession(promptSessionId);

        Assert.AreEqual(binding, snapshot.Binding);
        Assert.IsTrue(snapshot.IsPromptEmpty);
    }

    [TestMethod]
    public void ClearAndUpdateOperations_RequireBoundPromptSession()
    {
        var port = CreatePort();
        var promptSessionId = new PromptSessionId("missing");

        Assert.ThrowsExactly<KeyNotFoundException>(() => port.ClearPrompt(promptSessionId));
        Assert.ThrowsExactly<KeyNotFoundException>(() => port.UpdatePromptAvailability(promptSessionId));
        Assert.ThrowsExactly<KeyNotFoundException>(() => port.UpdatePromptAttachments(promptSessionId));
    }

    private static PromptSessionPort CreatePort(
        RecordingUiDispatcher? dispatcher = null,
        Func<bool>? isPromptEmpty = null,
        Action? clearPrompt = null,
        Action<string>? restorePromptText = null,
        Func<IReadOnlyList<PromptImageAttachment>>? snapshotPromptImages = null,
        Action<IReadOnlyList<PromptImageAttachment>>? restorePromptImages = null)
        => new(
            dispatcher ?? new RecordingUiDispatcher(),
            isPromptEmpty ?? (() => false),
            clearPrompt ?? (() => { }),
            restorePromptText ?? (_ => { }),
            snapshotPromptImages ?? (() => []),
            restorePromptImages ?? (_ => { }));

    private static PromptSessionBinding CreateBinding(PromptSessionId promptSessionId)
        => new(
            promptSessionId,
            ProjectId.NewVersion7(),
            new ShellSessionRef.Draft(new SessionDraftId("draft-1")),
            new ModelProviderId("provider-1"));

    private sealed class RecordingUiDispatcher : IUiDispatcher
    {
        public int InvokeCount { get; private set; }

        public bool CheckAccess() => true;

        public void VerifyAccess()
        {
        }

        public void Post(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);
            action();
        }

        public void PostDeferred(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);
            action();
        }

        public void Invoke(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);
            InvokeCount++;
            action();
        }

        public T Invoke<T>(Func<T> action)
        {
            ArgumentNullException.ThrowIfNull(action);
            InvokeCount++;
            return action();
        }

        public Task InvokeAsync(Action action)
        {
            Invoke(action);
            return Task.CompletedTask;
        }

        public Task<T> InvokeAsync<T>(Func<T> action)
            => Task.FromResult(Invoke(action));
    }

}
