using CodeAlta.App;
using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;
using XenoAtom.Terminal.UI.Controls;

namespace CodeAlta.Tests;

[TestClass]
public sealed class PluginFrontendContributionFixtureTests
{
    [TestMethod]
    public async Task PluginHostBridge_ExposesCommandStatusAndVisualContributions()
    {
        var globalRoot = Path.Combine(Path.GetTempPath(), "codealta-plugin-frontend-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(globalRoot);
        try
        {
            await using var runtime = new PluginRuntimeManager();
            var start = await runtime.StartAsync(
                new PluginRuntimeManagerOptions
                {
                    GlobalRoot = globalRoot,
                    BuiltIns =
                    [
                        new BuiltInPluginDefinition
                        {
                            Id = "frontend-fixture",
                            DisplayName = "Frontend Fixture",
                            Factory = CreatePlugin,
                        },
                    ],
                });
            Assert.AreEqual(0, start.Diagnostics.Count, string.Join(Environment.NewLine, start.Diagnostics.Select(static diagnostic => diagnostic.Message)));
            var bridge = new PluginFrontendBridge(runtime, static () => null);

            var command = bridge.GetCommandContributions().Single();
            var commandResult = await bridge.ExecuteCommandAsync(command);
            var status = bridge.GetStatusItems(PluginUiRegion.SessionStatus).Single();
            var footerStatus = bridge.GetStatusItems(PluginUiRegion.SessionFooter).Single();
            var visuals = bridge.CreateVisuals(PluginUiRegion.CommandBar);

            Assert.AreEqual("fixture", command.Name);
            Assert.AreEqual(PluginCommandDisposition.Handled, commandResult.Disposition);
            Assert.AreEqual("frontend", commandResult.UserMessage);
            Assert.AreEqual("Fixture", status.Label);
            Assert.AreEqual("ready", status.Text);
            Assert.AreEqual(PluginStatusTone.Success, status.Tone);
            Assert.AreEqual("Footer", footerStatus.Label);
            Assert.AreEqual("visible", footerStatus.Text);
            Assert.AreEqual(1, visuals.Count);
            Assert.IsInstanceOfType<Markup>(visuals[0]);
        }
        finally
        {
            if (Directory.Exists(globalRoot))
            {
                Directory.Delete(globalRoot, recursive: true);
            }
        }
    }

    private static PluginBase CreatePlugin() => new FrontendFixturePlugin();

    public sealed class FrontendFixturePlugin : PluginBase
    {
        public override IEnumerable<PluginCommandContribution> GetCommands()
        {
            yield return new PluginCommandContribution
            {
                Name = "fixture",
                Label = "Fixture",
                Handler = static (_, _) => ValueTask.FromResult(PluginCommandResult.Message("frontend")),
            };
        }

        public override IEnumerable<PluginUiContribution> GetUiContributions()
        {
            yield return new PluginStatusContribution
            {
                Region = PluginUiRegion.SessionStatus,
                GetStatus = static _ => new PluginStatusItem
                {
                    Label = "Fixture",
                    Text = "ready",
                    Tone = PluginStatusTone.Success,
                },
            };
            yield return new PluginStatusContribution
            {
                Region = PluginUiRegion.SessionFooter,
                GetStatus = static _ => new PluginStatusItem
                {
                    Label = "Footer",
                    Text = "visible",
                },
            };
            yield return new PluginVisualContribution
            {
                Region = PluginUiRegion.CommandBar,
                CreateVisual = static _ => new Markup("[dim]fixture[/]"),
            };
        }
    }
}
