using CodeAlta.LiveTool;

namespace CodeAlta.Tests;

[TestClass]
public sealed class AltaSandboxGateBrowserTests
{
    [TestMethod]
    public void ASandboxedSession_TurnsTheBrowserToolsOn_WhichKeepToItsSandbox()
    {
        foreach (var leaf in new[] { "list", "activate", "status", "close", "deactivate" }) Assert.IsTrue(AltaSandboxGate.Allows($"alta browser {leaf}"), leaf);
        Assert.IsFalse(AltaSandboxGate.Allows("alta browser"), "a path allows the commands under it, not its parent");
    }
}
