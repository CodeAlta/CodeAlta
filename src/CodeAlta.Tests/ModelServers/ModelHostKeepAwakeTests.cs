using System.Net;
using CodeAlta.ModelServers;

namespace CodeAlta.Tests.ModelServers;

[TestClass]
public sealed class ModelHostKeepAwakeTests
{
    [TestMethod]
    public async Task TheMachineIsKeptAwakeWhileItsModelHostWorks_AndFiveMinutesAfter()
    {
        using var temp = TestTempDirectory.Create();
        var time = new SteppedTime(new DateTimeOffset(2026, 11, 9, 10, 0, 0, TimeSpan.Zero));
        var answer = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = 0;
        var released = 0;
        await using var host = new ModelHost(new ModelHostOptions
        {
            SharedRoot = Path.Combine(temp.Path, "shared"), InstanceRoot = Path.Combine(temp.Path, "instance"), Time = time,
            // The install asks GitHub for its release, and waits for the answer: its task runs meanwhile.
            NetworkHandler = new WaitingHandler(answer.Task),
            KeepAwake = reason =>
            {
                StringAssert.Contains(reason, "model servers");
                held++;
                return new Release(() => released++);
            },
        });

        host.HoldAwake();
        Assert.AreEqual(0, held, "an idle machine sleeps as its system decides");

        host.InstallBuilds(["cpu"], null, "test");
        host.HoldAwake();
        host.HoldAwake();
        Assert.AreEqual(1, held, "one assertion while it works");
        Assert.IsTrue(host.IsKeptAwake);

        answer.SetResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        for (var i = 0; i < 200 && host.GetStatus().Tasks.Any(static task => task.EndedAt is null); i++) await Task.Delay(25);
        Assert.IsFalse(host.GetStatus().Tasks.Any(static task => task.EndedAt is null), "the install ended");
        host.HoldAwake();
        time.Advance(TimeSpan.FromMinutes(4));
        host.HoldAwake();
        Assert.AreEqual(0, released, "a request that comes soon after finds the machine awake");
        time.Advance(TimeSpan.FromMinutes(2));
        host.HoldAwake();
        Assert.AreEqual(1, released);
        Assert.IsFalse(host.IsKeptAwake);
    }

    [TestMethod]
    public async Task ASystemThatRefusesIsNotAskedAgain()
    {
        using var temp = TestTempDirectory.Create();
        var asked = 0;
        var answer = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = new ModelHost(new ModelHostOptions
        {
            SharedRoot = Path.Combine(temp.Path, "shared"), InstanceRoot = Path.Combine(temp.Path, "instance"), NetworkHandler = new WaitingHandler(answer.Task),
            KeepAwake = _ =>
            {
                asked++;
                throw new InvalidOperationException("systemd-inhibit is unavailable.");
            },
        });

        host.InstallBuilds(["cpu"], null, "test");
        host.HoldAwake();
        host.HoldAwake();
        Assert.AreEqual(1, asked);
        Assert.IsFalse(host.IsKeptAwake);
        answer.SetResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private sealed class SteppedTime(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class WaitingHandler(Task<HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => await answer.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed class Release(Action released) : IDisposable
    {
        public void Dispose() => released();
    }
}
