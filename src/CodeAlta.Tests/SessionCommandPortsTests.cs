using CodeAlta.Tui.App;
using CodeAlta.Tui.App.State;
using CodeAlta.Catalog;
using CodeAlta.Tui.Models;
using CodeAlta.Tui.Presentation.Timeline;
using CodeAlta.Tui.Threading;

namespace CodeAlta.Tests;

[TestClass]
public sealed class SessionCommandPortsTests
{
    [TestMethod]
    public async Task LifecyclePort_ForwardsCreationPersistenceAndRekey()
    {
        var created = new SessionViewDescriptor { SessionId = "session-1", ProviderId = "codex", ProviderKey = "codex" };
        var persisted = false;
        (string OldSessionId, SessionViewDescriptor Session)? rekey = null;
        var port = new DelegatingSessionLifecycleCommandPort(
            title =>
            {
                Assert.AreEqual("hello", title);
                return Task.FromResult<SessionViewDescriptor?>(created);
            },
            static _ => Task.FromResult<SessionViewDescriptor?>(null),
            () =>
            {
                persisted = true;
                return Task.CompletedTask;
            },
            (oldSessionId, session) => rekey = (oldSessionId, session));

        var result = await port.CreateGlobalSessionAsync("hello");
        await port.PersistViewStateAsync();
        port.RekeySessionIdentity("old-session", created);

        Assert.AreSame(created, result);
        Assert.IsTrue(persisted);
        Assert.IsNotNull(rekey);
        Assert.AreEqual("old-session", rekey.Value.OldSessionId);
        Assert.AreSame(created, rekey.Value.Session);
    }

    [TestMethod]
    public void UiPort_MarshalsCallbacksThroughDispatcher()
    {
        var dispatcher = new RecordingUiDispatcher();
        var cleared = false;
        var rendered = false;
        var port = new SessionCommandUiPort(
            dispatcher,
            static () => true,
            static () => false,
            () => cleared = true,
            static () => { },
            static () => { },
            static () => { },
            (_, action, _) =>
            {
                rendered = true;
                action();
            });
        var actionRan = false;

        Assert.IsTrue(port.TrySetPromptUnavailableStatus());
        Assert.IsFalse(port.GetAutoApproveEnabled());
        port.ClearDraftInput();
        port.TryRenderInteraction(CreateSessionState(), () => actionRan = true, "test");

        Assert.IsTrue(cleared);
        Assert.IsTrue(rendered);
        Assert.IsTrue(actionRan);
        Assert.AreEqual(4, dispatcher.InvokeCount);
    }

    private static OpenSessionState CreateSessionState()
    {
        var session = new SessionViewDescriptor { SessionId = "session-1", ProviderId = "codex", ProviderKey = "codex" };
        return new OpenSessionState(session, new SessionTimelinePresenter(new ImmediateUiDispatcher(), static () => null));
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

    private sealed class ImmediateUiDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;

        public void Post(Action action)
            => action();

        public Task InvokeAsync(Action action)
        {
            action();
            return Task.CompletedTask;
        }

        public Task<T> InvokeAsync<T>(Func<T> action)
            => Task.FromResult(action());
    }
}
