using CodeAlta.LiveTool;

namespace CodeAlta.Tests;

public sealed partial class AltaLiveToolTests
{
    [TestMethod]
    public async Task SessionMove_ListsTheMachines_MovesAnotherSessionNow_AndTheCallerWhenItsTurnEnds()
    {
        var moves = new RecordingMachineMoves();
        var dispatcher = CreateDispatcher(new AltaServiceCollection().Add<IAltaSessionMachineMoves>(moves));

        var list = await dispatcher.InvokeAsync(["session", "move", "--list"], caller: AltaCallerIdentity.Cli).ConfigureAwait(false);
        Assert.AreEqual(AltaExitCodes.Success, list.ExitCode, list.Stderr);
        var machines = ReadJsonLines(list.Stdout).Where(static line => line.GetProperty("type").GetString() == "alta.session.move_machine").ToArray();
        CollectionAssert.AreEqual(new[] { "pc", "mac-mini" }, machines.Select(static line => line.GetProperty("name").GetString()).ToArray());
        Assert.IsTrue(machines[0].GetProperty("thisComputer").GetBoolean());

        var missing = await dispatcher.InvokeAsync(["session", "move", "session-a"], caller: AltaCallerIdentity.Cli).ConfigureAwait(false);
        Assert.AreEqual(AltaExitCodes.Usage, missing.ExitCode);
        StringAssert.Contains(missing.Stdout + missing.Stderr, "usage.missingMachine");

        var moved = await dispatcher.InvokeAsync(["session", "move", "session-a", "--machine", "mac-mini", "--clone"], caller: AltaCallerIdentity.Cli).ConfigureAwait(false);
        Assert.AreEqual(AltaExitCodes.Success, moved.ExitCode, moved.Stderr);
        Assert.AreEqual(("now", "session-a", "mac-mini", true), moves.Calls.Single());
        Assert.AreEqual("finished", ReadJsonLines(moved.Stdout).Single(static line => line.GetProperty("type").GetString() == "alta.session.moved").GetProperty("status").GetString());

        // An agent that moves its own session: it moves when its turn ends, never in the middle of it.
        var caller = new AltaCallerIdentity { Kind = "agent", SourceSessionId = "session-b" };
        var scheduled = await dispatcher.InvokeAsync(["session", "move", "--machine", "mac-mini"], caller: caller).ConfigureAwait(false);
        Assert.AreEqual(AltaExitCodes.Success, scheduled.ExitCode, scheduled.Stderr);
        Assert.AreEqual(("after-turn", "session-b", "mac-mini", false), moves.Calls[^1]);
        StringAssert.Contains(scheduled.Stdout, "end your turn");

        moves.Refusal = "forbidden";
        var refused = await dispatcher.InvokeAsync(["session", "move", "session-a", "--machine", "mac-mini"], caller: AltaCallerIdentity.Cli).ConfigureAwait(false);
        Assert.AreEqual(AltaExitCodes.PolicyDenied, refused.ExitCode);
        StringAssert.Contains(refused.Stdout + refused.Stderr, "session.moveForbidden");

        // Without paired machines the command does not exist.
        var bare = await CreateDispatcher(new AltaServiceCollection()).InvokeAsync(["session", "move", "--list"], caller: AltaCallerIdentity.Cli).ConfigureAwait(false);
        Assert.AreNotEqual(AltaExitCodes.Success, bare.ExitCode);
    }

    private sealed class RecordingMachineMoves : IAltaSessionMachineMoves
    {
        public List<(string When, string SessionId, string Machine, bool Clone)> Calls { get; } = [];

        public string? Refusal { get; set; }

        public IReadOnlyList<AltaMoveMachine> ListMachines() => [new("", "pc", true, "online"), new("m1", "mac-mini", false, "online")];

        public Task<AltaSessionMoveResult> MoveAsync(string sessionId, string machine, bool cloneRepository, CancellationToken cancellationToken)
        {
            Calls.Add(("now", sessionId, machine, cloneRepository));
            return Task.FromResult(Refusal is { } code ? new AltaSessionMoveResult("refused", code, "Not allowed.", machine, null, [])
                : new AltaSessionMoveResult("finished", null, null, machine, "/work/x", []));
        }

        public AltaSessionMoveResult Schedule(string sessionId, string machine, bool cloneRepository)
        {
            Calls.Add(("after-turn", sessionId, machine, cloneRepository));
            return new AltaSessionMoveResult("scheduled", null, null, machine, null, []);
        }
    }
}
