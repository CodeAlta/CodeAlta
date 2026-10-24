using CodeAlta.Desktop;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class DesktopStartupGateTests
{
    [TestMethod]
    public void Shown_OpensOnceThePageHadTimeToDrawItsFirstWorkspace()
    {
        var time = new ManualTime();
        var gate = new DesktopStartupGate(time);
        gate.ApplicationLoading();

        time.Advance(TimeSpan.FromSeconds(5));
        Assert.IsFalse(gate.Shown.IsCompleted, "Nothing opens the gate before the page is served its workspace.");

        gate.WorkspaceServed();
        time.Advance(DesktopStartupGate.DrawDelay - TimeSpan.FromMilliseconds(1));
        Assert.IsFalse(gate.Shown.IsCompleted, "The page is given the time to draw the workspace.");

        // A later snapshot does not push the moment back.
        gate.WorkspaceServed();
        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.IsTrue(gate.Shown.Wait(TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    public void Shown_OpensAfterTheFallback_WhenThePageNeverAsks()
    {
        var time = new ManualTime();
        var gate = new DesktopStartupGate(time);
        gate.ApplicationLoading();

        time.Advance(DesktopStartupGate.Fallback - TimeSpan.FromMilliseconds(1));
        Assert.IsFalse(gate.Shown.IsCompleted);
        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.IsTrue(gate.Shown.Wait(TimeSpan.FromSeconds(5)));
    }

    /// <summary>A clock that moves only when the test advances it.</summary>
    private sealed class ManualTime : TimeProvider
    {
        private readonly List<Timer> _timers = [];
        private TimeSpan _now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new Timer(this, callback, state);
            timer.Change(dueTime, period);
            return timer;
        }

        public void Advance(TimeSpan by)
        {
            _now += by;
            Timer[] due;
            lock (_timers) due = _timers.Where(timer => timer.Due <= _now).ToArray();
            foreach (var timer in due) timer.Fire();
        }

        private sealed class Timer(ManualTime time, TimerCallback callback, object? state) : ITimer
        {
            public TimeSpan Due { get; private set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (time._timers)
                {
                    time._timers.Remove(this);
                    if (dueTime == Timeout.InfiniteTimeSpan) return true;
                    Due = time._now + dueTime;
                    time._timers.Add(this);
                }
                return true;
            }

            public void Fire()
            {
                lock (time._timers) time._timers.Remove(this);
                callback(state);
            }

            public void Dispose()
            {
                lock (time._timers) time._timers.Remove(this);
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
