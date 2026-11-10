using CodeAlta.Fleet.NodeApi;

namespace CodeAlta.Fleet.Tests;

[TestClass]
public sealed class FleetSessionSeatsTests
{
    private static FleetSeatRequest Run(string id, string? root = null) => new(id, root ?? id, root ?? id, "a run", "send");

    // Seats whose sessions at work are the set the test changes, decided only when the test asks (no loop in between).
    private static (FleetSessionSeats Seats, HashSet<string> Busy) Create(int? limit)
    {
        var busy = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seats = new FleetSessionSeats(() => limit, _ => Task.FromResult<IReadOnlyCollection<string>>([.. busy])) { Poll = TimeSpan.FromHours(1) };
        return (seats, busy);
    }

    [TestMethod]
    public async Task ALimitOfTwo_StartsTwo_AndQueuesTheOthersInOrder()
    {
        var (seats, busy) = Create(2);
        await using var _ = seats;
        var token = TestContext.CancellationToken;

        var first = await seats.RequestAsync(Run("a"), token);
        var second = await seats.RequestAsync(Run("b"), token);
        var third = await seats.RequestAsync(Run("c"), token);
        var fourth = await seats.RequestAsync(Run("d"), token);

        Assert.IsTrue(first.IsAdmitted && second.IsAdmitted);
        Assert.AreEqual((false, 1, false, 2), (third.IsAdmitted, third.Position, fourth.IsAdmitted, fourth.Position));
        var snapshot = seats.Snapshot();
        Assert.AreEqual((2, 2), (snapshot.Limit, snapshot.Working));
        CollectionAssert.AreEqual(new[] { "c", "d" }, snapshot.Queue.Select(static item => item.Id).ToArray());

        // The first starts its session, then ends it: its seat goes to the next one, not to the last one.
        busy.Add("a");
        busy.Add("b");
        await seats.EvaluateAsync(token);
        Assert.IsFalse(third.Admitted.IsCompleted);
        busy.Remove("a");
        await seats.EvaluateAsync(token);
        Assert.IsTrue(third.Admitted.IsCompletedSuccessfully);
        Assert.IsFalse(fourth.Admitted.IsCompleted);
        Assert.AreEqual(1, seats.Snapshot().Queue.Single().Position);
    }

    [TestMethod]
    public async Task ASubSession_TakesNoSecondSeat()
    {
        var (seats, busy) = Create(1);
        await using var _ = seats;
        var token = TestContext.CancellationToken;
        busy.Add("parent");
        await seats.EvaluateAsync(token);

        // A prompt of the parent's child shares its seat; another tree waits.
        var child = await seats.RequestAsync(Run("child-run", root: "parent"), token);
        var other = await seats.RequestAsync(Run("other"), token);

        Assert.IsTrue(child.IsAdmitted);
        Assert.AreEqual((false, 1), (other.IsAdmitted, other.Position));
    }

    [TestMethod]
    public async Task ASessionThatWaitsForAPerson_FreesItsSeat()
    {
        // The node reads the trees at work without the sessions that wait for a person: one that asks a question holds none.
        var (seats, busy) = Create(1);
        await using var _ = seats;
        var token = TestContext.CancellationToken;
        busy.Add("asking");
        await seats.EvaluateAsync(token);
        var waiting = await seats.RequestAsync(Run("next"), token);
        Assert.IsFalse(waiting.IsAdmitted);

        busy.Remove("asking");
        await seats.EvaluateAsync(token);
        Assert.IsTrue(waiting.Admitted.IsCompletedSuccessfully);
    }

    [TestMethod]
    public async Task ACancelledRun_LeavesTheQueue_AndStartNow_StartsOnePastTheLimit()
    {
        var (seats, busy) = Create(1);
        await using var _ = seats;
        var token = TestContext.CancellationToken;
        busy.Add("working");
        await seats.EvaluateAsync(token);
        var cancelled = await seats.RequestAsync(Run("cancelled"), token);
        var started = await seats.RequestAsync(Run("started"), token);
        var last = await seats.RequestAsync(Run("last"), token);

        Assert.IsTrue(seats.Cancel("cancelled"));
        Assert.IsTrue(cancelled.Admitted.IsCanceled);
        Assert.AreEqual(1, seats.Snapshot().Queue.First().Position);

        Assert.IsTrue(seats.StartNow("started"));
        Assert.IsTrue(started.Admitted.IsCompletedSuccessfully);
        var snapshot = seats.Snapshot();
        Assert.AreEqual(2, snapshot.Working, "one over the limit, as the person asked");
        CollectionAssert.AreEqual(new[] { "last" }, snapshot.Queue.Select(static item => item.Id).ToArray());
        Assert.IsFalse(seats.Cancel("cancelled"), "a run no longer queued is not found");
        Assert.IsFalse(last.Admitted.IsCompleted);
    }

    [TestMethod]
    public async Task ARunAdmitted_HoldsItsSeat_UntilReleasedOrSeenAtWork()
    {
        var (seats, busy) = Create(1);
        await using var _ = seats;
        var token = TestContext.CancellationToken;
        var admitted = await seats.RequestAsync(Run("automation-run"), token);
        Assert.IsTrue(admitted.IsAdmitted);
        var next = await seats.RequestAsync(Run("next"), token);
        Assert.IsFalse(next.IsAdmitted, "the seat is reserved before the session of the run exists");

        // Its session exists, then works: the tree holds the seat; a refused run would give it back with Release.
        seats.Bind("automation-run", "session-1");
        busy.Add("session-1");
        await seats.EvaluateAsync(token);
        Assert.IsFalse(next.Admitted.IsCompleted);
        busy.Clear();
        await seats.EvaluateAsync(token);
        Assert.IsTrue(next.Admitted.IsCompletedSuccessfully);

        var again = await seats.RequestAsync(Run("again"), token);
        Assert.IsFalse(again.IsAdmitted);
        seats.Release("next");
        await seats.EvaluateAsync(token);
        Assert.IsTrue(again.Admitted.IsCompletedSuccessfully);
    }

    [TestMethod]
    public async Task NoLimit_AdmitsEveryRun_AndALimitRaised_StartsThoseThatWait()
    {
        int? limit = 1;
        var busy = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "working" };
        await using var seats = new FleetSessionSeats(() => limit, _ => Task.FromResult<IReadOnlyCollection<string>>([.. busy])) { Poll = TimeSpan.FromHours(1) };
        var token = TestContext.CancellationToken;
        var waiting = await seats.RequestAsync(Run("waiting"), token);
        Assert.IsFalse(waiting.IsAdmitted);

        limit = null;
        await seats.EvaluateAsync(token);
        Assert.IsTrue(waiting.Admitted.IsCompletedSuccessfully);
        Assert.IsNull(seats.Snapshot().Limit);
    }

    [TestMethod]
    public async Task TheLoop_DecidesAgain_WhenPoked()
    {
        var busy = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "working" };
        var gate = new Lock();
        await using var seats = new FleetSessionSeats(() => 1, _ =>
        {
            lock (gate) return Task.FromResult<IReadOnlyCollection<string>>([.. busy]);
        }) { Poll = TimeSpan.FromHours(1) };
        var token = TestContext.CancellationToken;
        var waiting = await seats.RequestAsync(Run("waiting"), token);
        Assert.IsFalse(waiting.IsAdmitted);

        lock (gate) busy.Clear();
        seats.Poke();
        await waiting.Admitted.WithinAsync(TestTime.Seconds(10), token);
    }

    [TestMethod]
    public async Task ARequestCancelledBeforeItIsAsked_LeavesNothingInTheQueue()
    {
        var (seats, busy) = Create(1);
        await using var _ = seats;
        busy.Add("working");
        await seats.EvaluateAsync(TestContext.CancellationToken);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => seats.RequestAsync(Run("gone"), cancelled.Token));

        Assert.IsEmpty(seats.Snapshot().Queue);
    }

    [TestMethod]
    public async Task Disposing_CancelsTheRunsThatWait()
    {
        var (seats, busy) = Create(1);
        busy.Add("working");
        var token = TestContext.CancellationToken;
        await seats.EvaluateAsync(token);
        var waiting = await seats.RequestAsync(Run("waiting"), token);

        await seats.DisposeAsync();

        Assert.IsTrue(waiting.Admitted.IsCanceled);
    }

    public TestContext TestContext { get; set; } = null!;
}
