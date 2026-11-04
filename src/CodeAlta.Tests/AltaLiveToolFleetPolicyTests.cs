using CodeAlta.LiveTool;

namespace CodeAlta.Tests;

/// <summary>
/// `alta fleet policy` (<c>doc/fleet/security-and-privacy.md</c>, policies and the kill switch): an agent reads the rules and the
/// stop of the kill switch, and is told to start nothing where it reaches; nothing signs.
/// </summary>
public sealed partial class AltaLiveToolTests
{
    [TestMethod]
    public async Task FleetPolicy_IsReadOnly_AndAStopTellsTheAgentToStartNothingThere()
    {
        var policy = new FakeFleetPolicy();
        var dispatcher = CreateDispatcher(new AltaServiceCollection().Add<IAltaFleet>(new FakeFleet()).Add<IAltaFleetPolicy>(policy));

        var running = await dispatcher.InvokeAsync(["fleet", "policy", "show"], caller: FleetAgent);
        Assert.AreEqual(AltaExitCodes.Success, running.ExitCode, running.Transcript);
        var read = Records(running, "alta.fleet.policy").Single();
        Assert.AreEqual(3, read.GetProperty("number").GetInt64());
        Assert.AreEqual("Only these kinds of providers: anthropic", read.GetProperty("rules")[0].GetString());
        Assert.IsFalse(read.TryGetProperty("stop", out var none) && none.ValueKind != System.Text.Json.JsonValueKind.Null, "no stop while the work goes on");

        policy.Stopped = true;
        var stopped = Records(await dispatcher.InvokeAsync(["fleet", "policy", "show"], caller: FleetAgent), "alta.fleet.policy").Single();
        Assert.AreEqual(("machines", "Runaway job", true), (stopped.GetProperty("stop").GetProperty("scope").GetString(), stopped.GetProperty("stop").GetProperty("reason").GetString(),
            stopped.GetProperty("stopsThisComputer").GetBoolean()));
        StringAssert.Contains(stopped.GetProperty("note").GetString(), "start nothing");

        foreach (var verb in new[] { "sign", "stop", "lift" })
            Assert.AreNotEqual(AltaExitCodes.Success, (await dispatcher.InvokeAsync(["fleet", "policy", verb], caller: FleetAgent)).ExitCode, verb);
        Assert.IsTrue(AltaSandboxGate.Allows("alta fleet policy show"));
    }

    private sealed class FakeFleetPolicy : IAltaFleetPolicy
    {
        public bool Stopped { get; set; }

        public Task<AltaFleetPolicy> ReadPolicyAsync(string? server, CancellationToken cancellationToken)
            => Task.FromResult(new AltaFleetPolicy("ok", null)
            {
                Server = "https://fleet.test", Number = 3, Rules = ["Only these kinds of providers: anthropic"],
                Stop = Stopped ? new AltaFleetPolicyStop("machines", ["Build box"], "Runaway job") : null, StopsThisComputer = Stopped,
            });
    }
}
