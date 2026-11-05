using System.Collections.Concurrent;
using CodeAlta.Fleet.Client;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Fleet.Server;

namespace CodeAlta.Desktop.Tests;

/// <summary>
/// The budgets of a node and the prompts its node API queues or steers (<c>doc/fleet/observability.md</c>, budgets and alerts): a
/// queued prompt starts a turn later, and the node admits it to its budgets then, as a send; one a budget refuses never reaches the
/// model. A steer goes into the turn that runs, which its own admission let in: it is not admitted again.
/// </summary>
[TestClass]
public sealed class FleetNodeBudgetQueueTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task ASendHeldAtABudget_MakesItsSessionWait_AndIsSentOnceAdmittedAfterARestart()
    {
        var token = TestContext.CancellationToken;
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = await FleetNodeFixture.StartAsync(token, null, async (run, held, cancellation) =>
        {
            held();
            await never.Task.WaitAsync(cancellation).ConfigureAwait(false);
            return FleetBudgetAdmission.Admitted;
        });
        await using var client = await FleetNodeClient.ConnectAsync(await fixture.PairViewerAsync(token), fixture.ViewerIdentity, token);
        var project = await fixture.Host.ProjectCatalog.UpsertFromPathAsync(Directory.CreateDirectory(Path.Combine(fixture.Root, "held-project")).FullName);
        var created = await client.CreateSessionAsync(new FleetCreateSessionParams(Title: "Report", ProjectId: project.Id), "create-held", token);
        Assert.AreEqual(FleetBudgetCommandStatuses.Held, (await client.SendAsync(created.SessionId, "The held work.", retryKey: "send-held", cancellationToken: token)).Status);

        // The send waits for a person: its session says so, and does not move meanwhile.
        CollectionAssert.Contains((await fixture.Host.ListSessionsWaitingForPersonAsync()).ToList(), created.SessionId);
        Assert.AreEqual("waiting", (await fixture.Service.MoveSender.PreviewAsync(created.SessionId, token)).Busy, "a session whose send waits does not move");

        // The node restarts while it waits: the send is not lost, and goes once the budgets admit it.
        var restarted = new FleetNodeService(new FleetNodeServiceOptions
        {
            Host = fixture.Host, Identity = fixture.NodeIdentity, TrustedDevices = new CodeAlta.Fleet.Identity.TrustedDeviceStore(Path.Combine(fixture.Root, "restarted"),
                TimeProvider.System), Name = "test-node", Version = "1.0.0",
            AdmitBudget = (_, _, _) => Task.FromResult(FleetBudgetAdmission.Admitted),
        });
        try
        {
            var deadline = DateTime.UtcNow + Patience;
            while (!fixture.Provider.Prompts.Any(static prompt => prompt.Contains("The held work.", StringComparison.Ordinal)) && DateTime.UtcNow < deadline)
                await Task.Delay(50, token);
            Assert.IsTrue(fixture.Provider.Prompts.Any(static prompt => prompt.Contains("The held work.", StringComparison.Ordinal)), "the held send survives a restart");
            Assert.IsFalse((await fixture.Host.ListSessionsWaitingForPersonAsync()).Contains(created.SessionId), "sent, the session waits no more");
        }
        finally
        {
            await restarted.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task TheHeldFirstPromptOfANewSession_AsksTheSameHoldAfterARestart()
    {
        var token = TestContext.CancellationToken;
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new ConcurrentQueue<FleetNodeBudgetRun>();
        await using var fixture = await FleetNodeFixture.StartAsync(token, null, async (run, held, cancellation) =>
        {
            first.Enqueue(run);
            held();
            await never.Task.WaitAsync(cancellation).ConfigureAwait(false);
            return FleetBudgetAdmission.Admitted;
        });
        await using var client = await FleetNodeClient.ConnectAsync(await fixture.PairViewerAsync(token), fixture.ViewerIdentity, token);
        var created = await client.CreateSessionAsync(new FleetCreateSessionParams(Title: "Report", Prompt: "The first held work."), "create-first-held", token);
        Assert.AreEqual(FleetBudgetCommandStatuses.Held, created.Send?.Status);
        var asked = first.Single();
        Assert.AreEqual(created.SessionId, asked.RunId, "the first run of a session is admitted as the session");

        // After a restart the same run asks the same hold: the person who continues it continues this one, not another.
        var resumed = new ConcurrentQueue<FleetNodeBudgetRun>();
        var restarted = new FleetNodeService(new FleetNodeServiceOptions
        {
            Host = fixture.Host, Identity = fixture.NodeIdentity, TrustedDevices = new CodeAlta.Fleet.Identity.TrustedDeviceStore(Path.Combine(fixture.Root, "restarted"),
                TimeProvider.System), Name = "test-node", Version = "1.0.0",
            AdmitBudget = (run, _, _) => { resumed.Enqueue(run); return Task.FromResult(FleetBudgetAdmission.Admitted); },
        });
        try
        {
            await WaitUntilAsync(() => fixture.Provider.Prompts.Any(static prompt => prompt.Contains("The first held work.", StringComparison.Ordinal)), token);
            var again = resumed.Single();
            Assert.AreEqual((asked.RunId, asked.Nonce), (again.RunId, again.Nonce), "the restart asks the hold the first start asked");
        }
        finally
        {
            await restarted.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task TheFirstPromptOfASessionInAnEnvironment_IsAdmittedToTheBudgetsToo()
    {
        var token = TestContext.CancellationToken;
        var admitted = new ConcurrentQueue<FleetNodeBudgetRun>();
        await using var fixture = await FleetNodeFixture.StartAsync(token, null, (run, _, _) =>
        {
            admitted.Enqueue(run);
            return Task.FromResult(new FleetBudgetAdmission("The budget 'Home' is reached: the run does not start.", []));
        }, null, false, (environment, _, _, _) => Task.FromResult(new CodeAlta.Orchestration.Runtime.EnvironmentAdmissionResult(null,
            new CodeAlta.Orchestration.Runtime.EnvironmentAdmission(environment, "env-1", 1, null))));
        await using var client = await FleetNodeClient.ConnectAsync(await fixture.PairViewerAsync(token), fixture.ViewerIdentity, token);
        var created = await client.CreateSessionAsync(new FleetCreateSessionParams(Title: "Deploy", Prompt: "Deploy the release.") { Environment = "production" }, "create-env", token);

        // Admitted to its environment, the session's first prompt still goes through the budgets: one that stops it is never sent.
        var deadline = DateTime.UtcNow + Patience;
        while (admitted.IsEmpty && DateTime.UtcNow < deadline) await Task.Delay(50, token);
        Assert.AreEqual(created.SessionId, admitted.SingleOrDefault()?.RunId, "the budgets admit the first run of a session in an environment");
        await Task.Delay(TimeSpan.FromMilliseconds(300), token);
        Assert.IsFalse(fixture.Provider.Prompts.Any(static prompt => prompt.Contains("Deploy the release.", StringComparison.Ordinal)), "a prompt a budget stops never reaches the model");
    }

    [TestMethod]
    public async Task AQueuedPrompt_IsAdmittedToTheBudgetsWhenItsTurnStarts_AndASteerIsPartOfTheRunningTurn()
    {
        var token = TestContext.CancellationToken;
        var admitted = new ConcurrentQueue<FleetNodeBudgetRun>();
        var refuse = new ConcurrentDictionary<string, bool>(StringComparer.Ordinal);
        var hold = new ConcurrentDictionary<string, bool>(StringComparer.Ordinal);
        var letQueued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = await FleetNodeFixture.StartAsync(token, null, async (run, held, _) =>
        {
            admitted.Enqueue(run);
            if (refuse.ContainsKey(run.RunId)) return new FleetBudgetAdmission("The budget 'Home' is reached: the run does not start.", []);
            if (hold.ContainsKey(run.RunId))
            {
                held();
                await letQueued.Task.ConfigureAwait(false);
            }

            return FleetBudgetAdmission.Admitted;
        });
        await using var client = await FleetNodeClient.ConnectAsync(await fixture.PairViewerAsync(token), fixture.ViewerIdentity, token);
        var created = await client.CreateSessionAsync(new FleetCreateSessionParams(Title: "Report"), "create-1", token);

        // A turn that runs until the test lets it end.
        var running = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        fixture.Provider.Turns.Enqueue(ScriptedFleetProvider.WaitThenReply(running.Task, "First.", started));
        Assert.AreEqual("accepted", (await client.SendAsync(created.SessionId, "first", retryKey: "send-1", cancellationToken: token)).Status);
        await started.Task.WaitAsync(Patience, token);
        Assert.AreEqual(1, admitted.Count, "the send was admitted before its turn");

        // A steer into the running turn is part of it: no admission of its own.
        var state = await client.GetSessionStateAsync(created.SessionId, token);
        var steered = await client.SteerAsync(new FleetSteerParams(created.SessionId, "also this", state.RuntimeInstanceId, state.AttachmentGeneration!.Value, state.ActiveRunId!),
            "steer-1", token);
        Assert.AreEqual("accepted", steered.Status);
        Assert.AreEqual(1, admitted.Count, "a steer is not admitted again");

        // Two queued prompts: nothing is admitted while the turn runs; each is admitted when its turn would start.
        var refused = await client.QueueAsync(created.SessionId, "refused follow-up", "queue-1", token);
        var held = await client.QueueAsync(created.SessionId, "held follow-up", "queue-2", token);
        refuse[refused.QueueItemId!] = true;
        hold[held.QueueItemId!] = true;
        Assert.AreEqual(1, admitted.Count, "a prompt is admitted when its turn starts, not when it is queued");
        running.SetResult();

        async Task<string> Describe()
        {
            var history = await client.ReadHistoryAsync(new FleetHistoryParams(created.SessionId), token);
            return "admitted: " + string.Join(", ", admitted.Select(static run => run.RunId)) + " ;; prompts: " + string.Join(" | ", fixture.Provider.Prompts)
                + " ;; history: " + string.Join(" | ", history.Entries.Select(static entry => $"{entry.Type}/{entry.Kind}: {entry.Text}"));
        }

        await WaitUntilAsync(() => admitted.Any(run => run.RunId == held.QueueItemId), token, Describe);
        var runs = admitted.ToArray();
        Assert.AreEqual((refused.QueueItemId, held.QueueItemId), (runs[1].RunId, runs[2].RunId), "the queue is admitted in order, a refused prompt first");
        Assert.AreEqual("Report", runs[2].Title, "a queued run is named by its session");
        await Task.Delay(TimeSpan.FromMilliseconds(300), token);
        Assert.IsFalse(fixture.Provider.Prompts.Any(static prompt => prompt.Contains("refused follow-up", StringComparison.Ordinal)), "a refused prompt never reaches the model");
        Assert.IsFalse(fixture.Provider.Prompts.Any(static prompt => prompt.Contains("held follow-up", StringComparison.Ordinal)), "a held prompt waits");

        letQueued.SetResult();
        await WaitUntilAsync(() => fixture.Provider.Prompts.Any(static prompt => prompt.Contains("held follow-up", StringComparison.Ordinal)), token, Describe);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken token, Func<Task<string>>? describe = null)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("It did not get there in time. " + (describe is null ? string.Empty : await describe()));
            await Task.Delay(50, token);
        }
    }
}
