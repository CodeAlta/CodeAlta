using CodeAlta.Plugins.Abstractions;
using CodeAlta.Plugins.Tui;
using XenoAtom.Terminal.UI.Controls;

[Plugin("ui-all-regions", DisplayName = "UI All Regions", Description = "Contributes sample UI hooks for every PluginUiRegion.")]
public sealed class UiAllRegionsPlugin : PluginBase
{
    public override IEnumerable<PluginCommandContribution> GetCommands()
    {
        yield return Command.Prompt("ui-sample", "Show that the all-regions UI sample is active.", static async (context, cancellationToken) =>
        {
            if (context.Ui.HasInteractiveUi)
            {
                await context.Ui.NotifyAsync("UI all-regions sample command executed.", cancellationToken).ConfigureAwait(false);
            }

            return PluginCommandResult.Message("UI all-regions sample is active; check the command bar/session footer for visible markers.");
        });
    }

    public override IEnumerable<PluginUiContribution> GetUiContributions()
    {
        foreach (var region in Enum.GetValues<PluginUiRegion>())
        {
            yield return PluginTui.Visual(region, static context => new Markup($"[dim]plugin-ui:{context.Region}[/]"),
                static context => new PluginRenderResult { Text = $"plugin-ui:{context.Region}" }, $"visual-{region}");
        }

        yield return new PluginStatusContribution
        {
            Region = PluginUiRegion.SessionStatus,
            Name = "session-status",
            GetStatus = static _ => new PluginStatusItem
            {
                Label = "UI sample status",
                Text = "session status",
                IconMarkup = "[green]●[/]",
                Tone = PluginStatusTone.Success,
            },
        };

        yield return new PluginStatusContribution
        {
            Region = PluginUiRegion.SessionFooter,
            Name = "session-footer-status",
            GetStatus = static _ => new PluginStatusItem
            {
                Label = "UI sample footer",
                Text = "session footer",
                IconMarkup = "[blue]●[/]",
                Tone = PluginStatusTone.Info,
            },
        };

    }
}
