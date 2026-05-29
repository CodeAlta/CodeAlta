using CodeAlta.Agent;
using CodeAlta.App;
using CodeAlta.Models;
using CodeAlta.Plugins.Abstractions;
using XenoAtom.Terminal.UI;

namespace CodeAlta.Frontend.Commands;

internal static class PluginShellCommandAdapter
{
    public static IEnumerable<ShellCommand> CreateCommands(IPluginCommandService plugins)
    {
        ArgumentNullException.ThrowIfNull(plugins);
        foreach (var contribution in plugins.GetCommandContributions())
        {
            yield return CreateCommand(contribution);
        }
    }

    private static ShellCommand CreateCommand(PluginCommandContribution contribution)
    {
        ArgumentNullException.ThrowIfNull(contribution);
        return new ShellCommand
        {
            Id = $"Plugin.{contribution.Name}",
            Label = string.IsNullOrWhiteSpace(contribution.Label) ? contribution.Name : contribution.Label,
            Description = string.IsNullOrWhiteSpace(contribution.Description) ? contribution.Name : contribution.Description,
            HelpCategory = ShellCommandHelpCategory.General,
            Placement = ResolvePlacement(contribution.Placement),
            Name = contribution.Name,
            SearchText = contribution.SearchText,
            Gesture = contribution.KeyBinding?.Gesture,
            Sequence = contribution.KeyBinding?.Sequence,
            ShowInCommandBar = contribution.ShowInCommandBar,
            ShowInCommandPalette = contribution.ShowInCommandPalette,
            ShowInHelp = contribution.ShowInHelp,
            CanExecute = (context, target) => CanExecutePluginCommand(contribution.Availability, context, target),
            ExecuteAsync = async (context, target, cancellationToken) => await ExecutePluginCommandAsync(contribution, context, target, cancellationToken),
        };
    }

    private static ShellCommandPlacement ResolvePlacement(PluginCommandPlacement placement)
    {
        var resolved = ShellCommandPlacement.None;
        if ((placement & PluginCommandPlacement.ShellRoot) != 0)
        {
            resolved |= ShellCommandPlacement.ShellRoot;
        }

        if ((placement & PluginCommandPlacement.PromptEditor) != 0)
        {
            resolved |= ShellCommandPlacement.PromptEditor;
        }

        if ((placement & PluginCommandPlacement.WorkspaceRoot) != 0)
        {
            resolved |= ShellCommandPlacement.WorkspaceRoot;
        }

        return resolved;
    }

    private static bool CanExecutePluginCommand(PluginCommandAvailability availability, ShellCommandContext context, Visual target)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(target);
        if (availability.RequiresInteractiveUi && target.App is null)
        {
            return false;
        }

        var selectedSession = context.Sessions.GetSelectedSession();
        if (availability.RequiresProject && selectedSession?.ProjectRef is null)
        {
            return false;
        }

        if (availability.RequiresSession && selectedSession is null)
        {
            return false;
        }

        if (availability.RequiresIdleSession && (selectedSession is null || context.Sessions.EnsureSessionTab(selectedSession).StatusBusy))
        {
            return false;
        }

        if (availability.RequiresBusySession && (selectedSession is null || !context.Sessions.EnsureSessionTab(selectedSession).StatusBusy))
        {
            return false;
        }

        if ((availability.RequiresCodeAltaManagedProvider || availability.ProviderFamilies.Count > 0) && selectedSession is null)
        {
            return false;
        }

        if (availability.RequiresCodeAltaManagedProvider && !IsCodeAltaManagedProvider(selectedSession!.ProviderId))
        {
            return false;
        }

        if (availability.ProviderFamilies.Count > 0 && !availability.ProviderFamilies.Contains(selectedSession!.ProviderId, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static async ValueTask ExecutePluginCommandAsync(PluginCommandContribution contribution, ShellCommandContext context, Visual target, CancellationToken cancellationToken)
    {
        var result = await context.Plugins.ExecuteCommandAsync(contribution, cancellationToken);
        if (result.Disposition == PluginCommandDisposition.NotHandled)
        {
            context.Status.SetStatus($"Plugin command '{contribution.Name}' was not handled.", tone: StatusTone.Warning);
            return;
        }

        if (!string.IsNullOrWhiteSpace(result.UserMessage))
        {
            context.Status.SetStatus(result.UserMessage);
        }

        if (!string.IsNullOrWhiteSpace(result.PromptText))
        {
            await context.PromptDispatch.SendPromptAsync(result.PromptText, steer: false, cancellationToken);
        }
    }

    private static bool IsCodeAltaManagedProvider(string providerId)
        => !string.Equals(providerId, ModelProviderIds.Codex.Value, StringComparison.OrdinalIgnoreCase) &&
           !string.Equals(providerId, ModelProviderIds.Copilot.Value, StringComparison.OrdinalIgnoreCase);
}
