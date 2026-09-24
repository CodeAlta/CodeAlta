using CodeAlta.Plugin.Mcp;

namespace CodeAlta.Tests;

[TestClass]
public sealed class McpInventoryPolicyTests
{
    [TestMethod]
    public void ExistingPluginPolicyReadRemainsUnboundedWhileInventoryReadIsBounded()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-mcp-policy-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, "config.toml");
            File.WriteAllText(path, "[plugins.mcp]\nenabled = false\n#" + new string(' ', 1024 * 1024));
            var loader = new McpPolicyLoader();
            Assert.IsFalse(loader.Load(path, null).Enabled);
            Assert.ThrowsExactly<InvalidDataException>(() => loader.LoadBoundedForInventory(path, null));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
