using CodeAlta.Tui.Threading;

namespace CodeAlta.Tests;

[TestClass]
public sealed class UiDispatcherTests
{
    [TestMethod]
    public void Invoke_RunsInlineWhenDispatcherHasAccess()
    {
        IUiDispatcher dispatcher = new RecordingUiDispatcher(hasAccess: true);
        var recordingDispatcher = (RecordingUiDispatcher)dispatcher;
        var invoked = false;

        dispatcher.Invoke(() => invoked = true);
        var value = dispatcher.Invoke(static () => 42);

        Assert.IsTrue(invoked);
        Assert.AreEqual(42, value);
        Assert.AreEqual(0, recordingDispatcher.InvokeCount);
    }

    [TestMethod]
    public void Invoke_MarshalsThroughDispatcherWhenAccessIsMissing()
    {
        IUiDispatcher dispatcher = new RecordingUiDispatcher(hasAccess: false);
        var recordingDispatcher = (RecordingUiDispatcher)dispatcher;
        var invoked = false;

        dispatcher.Invoke(() => invoked = true);
        var value = dispatcher.Invoke(static () => 42);

        Assert.IsTrue(invoked);
        Assert.AreEqual(42, value);
        Assert.AreEqual(2, recordingDispatcher.InvokeCount);
    }

    [TestMethod]
    public void PostDeferred_AlwaysPostsInsteadOfRunningInline()
    {
        IUiDispatcher dispatcher = new RecordingUiDispatcher(hasAccess: true);
        var recordingDispatcher = (RecordingUiDispatcher)dispatcher;
        var invoked = false;

        dispatcher.PostDeferred(() => invoked = true);

        Assert.IsFalse(invoked);
        Assert.AreEqual(1, recordingDispatcher.Posted.Count);

        recordingDispatcher.DrainPosted();
        Assert.IsTrue(invoked);
    }

    [TestMethod]
    public void VerifyAccess_ThrowsWhenDispatcherAccessIsMissing()
    {
        IUiDispatcher dispatcher = new RecordingUiDispatcher(hasAccess: false);

        Assert.ThrowsExactly<InvalidOperationException>(dispatcher.VerifyAccess);
    }

    private sealed class RecordingUiDispatcher(bool hasAccess) : IUiDispatcher
    {
        public List<Action> Posted { get; } = [];

        public int InvokeCount { get; private set; }

        public bool CheckAccess()
            => hasAccess;

        public void Post(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);
            Posted.Add(action);
        }

        public Task InvokeAsync(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);
            InvokeCount++;
            action();
            return Task.CompletedTask;
        }

        public Task<T> InvokeAsync<T>(Func<T> action)
        {
            ArgumentNullException.ThrowIfNull(action);
            InvokeCount++;
            return Task.FromResult(action());
        }

        public void DrainPosted()
        {
            foreach (var action in Posted.ToArray())
            {
                action();
            }

            Posted.Clear();
        }
    }
}
