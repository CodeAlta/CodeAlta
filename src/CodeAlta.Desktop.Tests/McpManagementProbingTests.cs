using CodeAlta.Plugin.Mcp;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class McpManagementProbingTests
{
    [TestMethod]
    public async Task RefreshWithoutProbing_ReportsNoWritabilityAndStaysOffAcrossMutations()
    {
        var root = Path.Combine(Path.GetTempPath(), "CodeAlta-mcp-probing-" + Guid.NewGuid().ToString("N"));
        try
        {
            var home = Directory.CreateDirectory(Path.Combine(root, "home", ".alta")).Parent!.FullName;
            var project = Directory.CreateDirectory(Path.Combine(root, "project", ".alta")).Parent!.FullName;
            var management = new McpManagementService();
            var probing = new McpManagementRequest { ProjectDirectory = project, UserHomeDirectory = home };
            Assert.IsTrue(management.RefreshSnapshot(probing).Sources.All(source => source.IsWritable), "The default still probes.");

            var quiet = probing with { ProbeWritability = false };
            Assert.IsTrue(management.RefreshSnapshot(quiet).Sources.All(source => !source.IsWritable));
            await management.AddOrUpdateServerAsync(new McpManagementServerEdit { Key = "docs", Transport = McpManagementTransport.Stdio, Command = "npx" },
                McpManagementScope.Global, request: quiet);
            Assert.IsTrue(management.CachedSnapshot!.Sources.All(source => !source.IsWritable), "The refresh after a save keeps the request's choice.");
            await management.SetServerEnabledAsync("docs", false, McpManagementScope.Global, quiet);
            Assert.IsTrue(management.CachedSnapshot!.Sources.All(source => !source.IsWritable));
            await management.RemoveServerAsync("docs", McpManagementScope.Global, quiet);
            Assert.IsTrue(management.CachedSnapshot!.Sources.All(source => !source.IsWritable));
            Assert.AreEqual(project, management.CachedSnapshot.ProjectDirectory);
            Assert.HasCount(2, management.CachedSnapshot.Sources);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
