using System.Globalization;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Plugin.Mcp;
using CodeAlta.Plugins.Abstractions;
using XenoAtom.CommandLine;

namespace CodeAlta.Tests;

public sealed partial class McpRuntimeServiceTests
{
    [TestMethod]
    public async Task DirectTool_ReturnsTheImagesOfItsResultWithItsText()
    {
        using var project = TempDirectory.Create();
        WriteTinyServerConfig(project.Path, "tiny", logPath: null, extraEnv: new Dictionary<string, string> { ["MCP_TEST_RICH_TOOLS"] = "1" });
        await using var plugin = new McpPlugin(createPresentation: null, _home.Path);
        var stderr = new StringWriter(CultureInfo.InvariantCulture);
        var app = new CommandApp("alta", "test")
        {
            plugin.GetAltaCommands().Single().CreateCommandNode(CreateAltaContext(new StringWriter(CultureInfo.InvariantCulture), stderr, project.Path, "one")),
        };
        Assert.AreEqual(0, await app.RunAsync(["mcp", "activate", "tiny"], new CommandRunConfig { Out = TextWriter.Null, Error = stderr }), stderr.ToString());
        var before = await plugin.OnBeforeAgentRunAsync(new PluginBeforeAgentRunContext
        {
            Plugin = CreatePluginDescriptor(), Services = NoopPluginServices.Create(), ProjectPath = project.Path, SessionId = "one",
        });
        var rich = before!.AdditionalTools.Single(tool => tool.Spec.Name == "mcp__tiny__rich");
        using var arguments = JsonDocument.Parse("""{"arguments_json":"{}"}""");

        var result = await rich.Handler(new AgentToolInvocation(new ModelProviderId("test"), "one", "call", rich.Spec.Name, arguments.RootElement.Clone()), CancellationToken.None);

        Assert.IsTrue(result.Success, string.Join(" | ", result.Items.OfType<AgentToolResultItem.Text>().Select(static text => text.Value)));
        Assert.AreEqual(2, result.Items.Count);
        Assert.AreEqual("alpha", Assert.IsInstanceOfType<AgentToolResultItem.Text>(result.Items[0]).Value);
        // The image block of the result reaches the model: a session then checks it and saves it.
        var image = Assert.IsInstanceOfType<AgentToolResultItem.Image>(result.Items[1]);
        Assert.AreEqual("image/png", image.MediaType);
        Assert.AreEqual("tiny-rich", image.DisplayName);
        StringAssert.StartsWith(image.Base64Data, "iVBORw0KGgo");
    }
}
