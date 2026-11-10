using System.Text.Json;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Server;

namespace CodeAlta.Fleet.Tests;

/// <summary>
/// The directory a connector follows, against a server of the test that holds the answer of <c>directory.watch</c>
/// while changes arrive: the snapshot, read before them, never undoes them.
/// </summary>
[TestClass]
public sealed class FleetDirectoryWatchTests
{
    private static readonly DateTimeOffset Time = new(2026, 11, 3, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Hang = TestTime.Seconds(30);

    [TestMethod]
    public async Task ChangesSentWhileTheSnapshotWaitsApplyOverIt()
    {
        // The snapshot was read with s1 at version 1 and s2 present; meanwhile s1 moved to version 2 and s2 was withdrawn.
        var directory = await WatchAsync(
            [Change(Session("s1", 1, 2)), Removed(Session("s2", 1, 1))],
            Snapshot(Session("s1", 1, 1), Session("s2", 1, 1)));

        Assert.AreEqual(2L, directory.Sessions["s1"].Fact.Version, "The update sent before the snapshot stays.");
        Assert.IsFalse(directory.Sessions.ContainsKey("s2"), "The withdrawal sent before the snapshot stays.");
    }

    [TestMethod]
    public void ARemovalNeverRemovesNewerFactsOrThoseOfAnotherMachine()
    {
        // The withdrawal of a machine that lost the session reaches the watcher after the facts of the new owner.
        var directory = FleetDirectoryView.From(Snapshot(Session("s1", 2, 1), Session("s2", 1, 1, machine: "m-new"), Session("s3", 1, 1)))
            .Apply(Removed(Session("s1", 1, 3)))
            .Apply(Removed(Session("s2", 1, 1, machine: "m-old")));

        Assert.AreEqual(2L, directory.Sessions["s1"].Fact.Epoch, "A removal of an older epoch keeps the facts of the new one.");
        Assert.AreEqual("m-new", directory.Sessions["s2"].MachineId, "A removal by the machine that lost the session keeps its new owner's facts.");

        // A removal of the facts held, or of older ones of the same machine, removes them.
        var view = directory.Apply(Removed(Session("s3", 1, 1)));
        Assert.IsFalse(view.Sessions.ContainsKey("s3"));
        view = view.Apply(Removed(Session("s1", 2, 4)));
        Assert.IsFalse(view.Sessions.ContainsKey("s1"));
    }

    // Starts a connector that watches the directory of a server of the test: on directory.watch, the server sends the
    // changes first, then answers the snapshot, read before them.
    private static async Task<FleetDirectoryView> WatchAsync(IReadOnlyList<FleetDirectoryChange> changes, FleetDirectorySnapshot snapshot)
    {
        using var folder = new TestFolder();
        using var identity = DeviceIdentity.LoadOrCreate(Path.Combine(folder.Path, "desktop"));
        var (clientSide, serverSide) = FleetInMemoryTransport.CreatePair();
        var answered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        FleetConnection? server = null;
        var serving = Task.Run(async () =>
        {
            var accepted = await FleetHandshake.AcceptAsync(serverSide, new FleetHandshakeServerOptions { Protocol = new FleetProtocolRange(1, 1) }, new AcceptAll(),
                CancellationToken.None);
            server = new FleetConnection(serverSide, FleetConnectionOptions.FromPolicy(accepted.Welcome.Policy));
            server.RegisterRequestHandler(FleetServerMethods.PresenceUpdate, static (_, _) => ValueTask.FromResult<JsonElement?>(null));
            server.RegisterRequestHandler(FleetServerMethods.DirectoryWatch, async (_, token) =>
            {
                long sequence = 0;
                foreach (var change in changes)
                {
                    await server.SendEventAsync(new FleetEvent(FleetServerMethods.DirectoryChanged,
                        JsonSerializer.SerializeToElement(change, FleetServerJsonContext.Default.FleetDirectoryChange), null, ++sequence), token);
                }

                answered.TrySetResult();
                return JsonSerializer.SerializeToElement(snapshot, FleetServerJsonContext.Default.FleetDirectorySnapshot);
            });
            server.Start();
        });

        var snapshotSet = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var connector = new FleetConnector(new FleetConnectorOptions
        {
            ServerAddress = new Uri("http://server.test/"), Identity = identity, OutboxPath = Path.Combine(folder.Path, "outbox.json"),
            Client = new FleetClientInfo("desktop", "4.0.0", "windows"), WatchDirectory = true,
            ConnectTransport = (_, _) => Task.FromResult<IFleetTransport>(clientSide),
        });
        connector.DirectoryChanged += self =>
        {
            if (self.Directory.Sessions.Count > 0 && answered.Task.IsCompleted) snapshotSet.TrySetResult();
        };
        connector.Start();
        await serving.WithinAsync(Hang);
        await answered.Task.WithinAsync(Hang);
        await snapshotSet.Task.WithinAsync(Hang);
        // The events, sent before the answer, were all read by the time the answer is: what the directory holds now is final.
        await Task.Delay(100);
        var directory = connector.Directory;
        await server!.DisposeAsync();
        return directory;
    }

    private static FleetDirectorySession Session(string id, long epoch, long version, string machine = "m1")
        => new(machine, new FleetSessionFact(id, epoch, version, FleetSessionStates.Idle, Time, Time));

    private static FleetDirectoryChange Change(FleetDirectorySession session) => new(FleetServerMethods.ChangeKinds.Session, session.Fact.SessionId, Session: session);

    private static FleetDirectoryChange Removed(FleetDirectorySession session) => new(FleetServerMethods.ChangeKinds.SessionRemoved, session.Fact.SessionId, Session: session);

    private static FleetDirectorySnapshot Snapshot(params FleetDirectorySession[] sessions) => new([], sessions, []);

    private sealed class AcceptAll : IFleetHandshakeVerifier
    {
        public ValueTask<FleetProofVerification> VerifyAsync(FleetHello hello, FleetProofContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult(FleetProofVerification.Accept());
    }
}
