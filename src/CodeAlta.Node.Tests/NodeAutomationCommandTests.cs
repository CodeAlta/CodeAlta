using System.Text.Json;
using CodeAlta.Automations;

namespace CodeAlta.Node.Tests;

/// <summary>
/// <c>altanode automations</c>: the owner of a node reads what other devices gave it and their runs, and shows or sets its
/// policy, on the files of the profile, without editing them by hand.
/// </summary>
[TestClass]
public sealed class NodeAutomationCommandTests
{
    private const string Id = "0199f4c2-6d1e-7c3a-b5f0-2f9c8e4a1f31";

    [TestMethod]
    public async Task Policy_IsShown_TurnedOffAndOn_AndLimitedToAccountCredentials()
    {
        using var root = new NodeTestRoot();
        var fleetRoot = FleetRoot(root);

        var (code, output, _) = await RunAsync(root, "automations", "policy");
        Assert.AreEqual(NodeExitCodes.Success, code);
        StringAssert.Contains(output, "This machine runs the automations given to it.");
        StringAssert.Contains(output, "any the machine has");

        Assert.AreEqual(NodeExitCodes.Success, (await RunAsync(root, "automations", "policy", "off")).Code);
        Assert.IsFalse(NodeAutomationPolicy.Load(fleetRoot).Enabled, "the node reads the file before each run");
        (code, output, _) = await RunAsync(root, "automations", "policy", "credentials", "account");
        Assert.AreEqual(NodeExitCodes.Success, code);
        StringAssert.Contains(output, "they are off");
        StringAssert.Contains(output, "accounts only");
        Assert.AreEqual((false, NodeAutomationPolicy.AccountCredentials), (NodeAutomationPolicy.Load(fleetRoot).Enabled, NodeAutomationPolicy.Load(fleetRoot).Credentials));
        Assert.IsTrue((await RunAsync(root, "automations", "policy", "on")).Output.Contains("runs the automations given to it", StringComparison.Ordinal));

        var (refused, _, error) = await RunAsync(root, "automations", "policy", "credentials", "personal");
        Assert.AreEqual(NodeExitCodes.InvalidUsage, refused);
        StringAssert.Contains(error, "on, off, or credentials any|account");
        Assert.AreEqual(NodeExitCodes.InvalidUsage, (await RunAsync(root, "automations", "forget")).Code);
    }

    [TestMethod]
    public async Task ListAndRuns_ShowWhatDevicesGave_AndTheirRuns_AsTheNodeWroteThem()
    {
        using var root = new NodeTestRoot();
        var fleetRoot = FleetRoot(root);
        StringAssert.Contains((await RunAsync(root, "automations")).Output, "No device gave this machine an automation.");

        Directory.CreateDirectory(fleetRoot);
        File.WriteAllText(Path.Combine(fleetRoot, NodeAutomations.AssignedFileName), $$"""
            [automations.{{Id}}]
            name = "Nightly report"
            prompt = "Report."
            triggers = [{ type = "daily", at = ["23:00"] }]
            """);
        var assignments = new Dictionary<string, NodeAutomationAssignment>
        {
            [Id] = new("laptop-device", "laptop", "fingerprint", DateTimeOffset.UtcNow)
            {
                Number = 1793715785519, Via = "link-1",
                Signed = new CodeAlta.Fleet.Protocol.FleetSignedAutomationVersion("{}", "sig"),
            },
            ["0199f4c2-6d1e-7c3a-b5f0-2f9c8e4a1f32"] = new("laptop-device", "laptop", string.Empty, DateTimeOffset.UtcNow) { Number = 5, Removed = true },
        };
        File.WriteAllText(Path.Combine(fleetRoot, NodeAutomations.AssignmentsFileName),
            JsonSerializer.Serialize(assignments, NodeAutomationJsonContext.Default.DictionaryStringNodeAutomationAssignment));

        var (code, output, _) = await RunAsync(root, "automations", "list");
        Assert.AreEqual(NodeExitCodes.Success, code);
        var line = output.Split('\n').Single(text => text.StartsWith(Id, StringComparison.Ordinal));
        StringAssert.Contains(line, "Nightly report");
        StringAssert.Contains(line, "laptop");
        StringAssert.Contains(line, "server");
        StringAssert.Contains(line, "1793715785519");
        StringAssert.Contains(line, "assigned");
        StringAssert.Contains(output, "taken away");

        // A run in progress is listed as the node wrote it, never taken for interrupted by the reading.
        var state = Path.Combine(root.Profile().StateRoot, "automations.json");
        var store = new AutomationStateStore(state, pausedByDefault: false);
        store.Add(new AutomationRun("run-1", Id, DateTimeOffset.UtcNow.AddMinutes(-5), "daily")
            { Name = "Nightly report", Status = AutomationRun.Completed, EndedAt = DateTimeOffset.UtcNow.AddMinutes(-4), SessionId = "s-1", InputTokens = 120, OutputTokens = 30 });
        store.Add(new AutomationRun("run-2", Id, DateTimeOffset.UtcNow, "manual") { Name = "Nightly report", SessionId = "s-2" });
        store.Add(new AutomationRun("run-own", "of-the-profile", DateTimeOffset.UtcNow, "manual") { Name = "Mine" });
        (code, output, _) = await RunAsync(root, "automations", "runs");
        Assert.AreEqual(NodeExitCodes.Success, code);
        StringAssert.Contains(output.Split('\n').Single(text => text.Contains("s-2", StringComparison.Ordinal)), AutomationRun.Running);
        StringAssert.Contains(output.Split('\n').Single(text => text.Contains("s-1", StringComparison.Ordinal)), "120/30");
        Assert.IsFalse(output.Contains("Mine", StringComparison.Ordinal), "the automations of the profile are not what devices gave");
        StringAssert.Contains((await RunAsync(root, "automations", "runs", "another")).Output, "has not run on this machine");
    }

    private static string FleetRoot(NodeTestRoot root)
    {
        var profile = root.Profile();
        return new CodeAlta.Catalog.CatalogOptions { GlobalRoot = profile.GlobalRoot, StateRoot = profile.StateRoot }.FleetRoot;
    }

    private static async Task<(int Code, string Output, string Error)> RunAsync(NodeTestRoot root, params string[] words)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = await new NodeApplication(output, error) { InitializeLogging = false, ServiceEnvironment = static () => null }
            .RunAsync([.. words, "--data-root", root.DataRoot]);
        return (code, output.ToString(), error.ToString());
    }
}
