using CodeAlta.Catalog;
using CodeAlta.Tui.App;
using CodeAlta.Tui.Models;
using CodeAlta.Tui.Presentation.Prompting;
using CodeAlta.Tui.Threading;

namespace CodeAlta.Tests;

[TestClass]
public sealed class LegacyPromptSessionPortTests
{
    [TestMethod]
    public void CaptureAndRestorePrompt_UseUiDispatcherAndImages()
    {
        var dispatcher = new RecordingUiDispatcher();
        var image = PromptImageAttachmentFactory.Create("Image-1", [1, 2, 3], "image/png", ".png");
        var restoredText = string.Empty;
        IReadOnlyList<PromptImageAttachment>? restoredImages = null;
        var port = new LegacyPromptSessionPort(
            dispatcher,
            static () => false,
            static () => { },
            text => restoredText = text,
            () => [image],
            images => restoredImages = images);
        var promptSessionId = new PromptSessionId("prompt-1");

        var submission = port.CapturePrompt(promptSessionId, "hello");
        port.RestorePrompt(promptSessionId, submission);

        Assert.AreEqual("hello", submission.Text);
        Assert.AreEqual(1, submission.Images.Count);
        Assert.AreEqual(2, dispatcher.InvokeCount);
        Assert.AreEqual("hello", restoredText);
        Assert.IsNotNull(restoredImages);
        Assert.AreEqual(1, restoredImages.Count);
    }

    [TestMethod]
    public void CapturePrompt_UsesCurrentPromptTextWhenSubmittedTextIsNull()
    {
        var port = new LegacyPromptSessionPort(
            new RecordingUiDispatcher(),
            static () => false,
            static () => { },
            static _ => { },
            static () => [],
            static _ => { },
            static () => "steer immediately");
        var promptSessionId = new PromptSessionId("prompt-1");

        var submission = port.CapturePrompt(promptSessionId, submittedText: null);

        Assert.AreEqual("steer immediately", submission.Text);
        Assert.IsTrue(submission.HasContent);
    }

    [TestMethod]
    public void PromptOperations_RejectDefaultPromptSessionId()
    {
        var port = new LegacyPromptSessionPort(
            new RecordingUiDispatcher(),
            static () => false,
            static () => { },
            static _ => { },
            static () => [],
            static _ => { });

        Assert.ThrowsExactly<ArgumentException>(() => port.CapturePrompt(default, "hello"));
        Assert.ThrowsExactly<ArgumentException>(() => port.ClearPrompt(default));
        Assert.ThrowsExactly<ArgumentException>(() => port.IsPromptEmpty(default));
    }

    private sealed class RecordingUiDispatcher : IUiDispatcher
    {
        public int InvokeCount { get; private set; }

        public bool CheckAccess() => true;

        public void VerifyAccess()
        {
        }

        public void Post(Action action)
            => action();

        public void PostDeferred(Action action)
            => action();

        public void Invoke(Action action)
        {
            InvokeCount++;
            action();
        }

        public T Invoke<T>(Func<T> action)
        {
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
