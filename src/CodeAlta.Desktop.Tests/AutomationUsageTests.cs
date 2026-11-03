using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Automations;

namespace CodeAlta.Desktop.Tests;

/// <summary>What the session of a run used, summed from its usage records for the run record and its reports.</summary>
[TestClass]
public sealed class AutomationUsageTests
{
    [TestMethod]
    public void TheTokensAndTheCostOfTheRequestsAddUp_InOneUnit()
    {
        var usage = AutomationUsage.Sum([
            Update(new AgentOperationUsageSnapshot(InputTokens: 100, OutputTokens: 20, Cost: 0.1)),
            Update(new AgentOperationUsageSnapshot(InputTokens: 50, OutputTokens: 5, Cost: 0.2, CostUnit: "USD")),
            Update(new AgentOperationUsageSnapshot(OutputTokens: 1)),
        ]);
        Assert.AreEqual(new AutomationUsage(150, 26, 0.3, "usd"), usage, "an unnamed unit is US dollars, the same as USD");

        var credits = AutomationUsage.Sum([Update(new AgentOperationUsageSnapshot(InputTokens: 10, Cost: 2, CostUnit: "AI credits"))]);
        Assert.AreEqual(new AutomationUsage(10, null, 2, "AI credits"), credits);
    }

    [TestMethod]
    public void CostsInTwoUnitsAreNotAdded_NorIsAnythingThatDoesNotHold()
    {
        Assert.AreEqual(new AutomationUsage(30, null, null, null), AutomationUsage.Sum([
            Update(new AgentOperationUsageSnapshot(InputTokens: 10, Cost: 0.1)),
            Update(new AgentOperationUsageSnapshot(InputTokens: 20, Cost: 1, CostUnit: "AI credits")),
        ]));
        Assert.IsNull(AutomationUsage.Sum([Update(new AgentOperationUsageSnapshot(InputTokens: -1))]));
        Assert.IsNull(AutomationUsage.Sum([Update(new AgentOperationUsageSnapshot(Cost: double.NaN))]));
        Assert.IsNull(AutomationUsage.Sum([]), "a session that reported nothing has no usage");
    }

    private static AgentSessionUsageUpdate Update(AgentOperationUsageSnapshot operation) => new("run", operation);
}
