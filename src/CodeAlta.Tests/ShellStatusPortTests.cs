using CodeAlta.Tui.App;
using CodeAlta.Tui.Models;
using CodeAlta.Tui.Threading;

namespace CodeAlta.Tests;

[TestClass]
public sealed class ShellStatusPortTests
{
    [TestMethod]
    public void SetShellStatus_InvokesCallbackThroughDispatcher()
    {
        var dispatcher = new RecordingUiDispatcher();
        var captured = default(ShellStatusUpdate);
        var port = new ShellStatusPort(
            dispatcher,
            (message, showSpinner, tone) => captured = new ShellStatusUpdate(message, showSpinner, tone),
            static (_, _, _, _) => { });

        port.SetShellStatus(new ShellStatusUpdate("Working", true, StatusTone.Info));

        Assert.AreEqual(new ShellStatusUpdate("Working", true, StatusTone.Info), captured);
        Assert.AreEqual(1, dispatcher.InvokeCount);
    }

    [TestMethod]
    public void SetProviderSessionLoadStatus_AllowsNullMessage()
    {
        var dispatcher = new RecordingUiDispatcher();
        string? captured = "unchanged";
        var port = new ShellStatusPort(
            dispatcher,
            static (_, _, _) => { },
            static (_, _, _, _) => { },
            setProviderSessionLoadStatus: message => captured = message);

        port.SetProviderSessionLoadStatus(null);

        Assert.IsNull(captured);
        Assert.AreEqual(1, dispatcher.InvokeCount);
    }

    [TestMethod]
    public void SetShellStatus_RejectsEmptyMessage()
    {
        var port = new ShellStatusPort(
            new RecordingUiDispatcher(),
            static (_, _, _) => { },
            static (_, _, _, _) => { });

        Assert.ThrowsExactly<ArgumentException>(() => port.SetShellStatus(new ShellStatusUpdate(string.Empty, false, StatusTone.Info)));
    }

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
