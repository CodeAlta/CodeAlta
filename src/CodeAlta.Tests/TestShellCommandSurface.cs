using CodeAlta.App;
using CodeAlta.Frontend.Commands;
using CodeAlta.Models;
using XenoAtom.Terminal.UI;

namespace CodeAlta.Tests;

internal static class TestShellCommandSurface
{
    public static ShellCommandSurfaceCoordinator Create()
    {
        var registry = new ShellCommandRegistry([]);
        var status = new DelegatingShellStatusService(static (_, _, _) => { });
        var presenter = new Presenter();
        var context = new ShellCommandContext
        {
            PromptInput = new DelegatingShellPromptInputService(static () => null, static () => true),
            PromptDispatch = new DelegatingShellPromptDispatchService(static (_, _, _) => Task.CompletedTask),
            Sessions = new DelegatingShellSessionCommandService(static () => null, static _ => null!),
            Dialogs = new DelegatingShellDialogCommandService(
                static () => null,
                static () => null,
                static () => [],
                static (_, _) => Task.CompletedTask,
                static () => Task.CompletedTask,
                static _ => Task.CompletedTask,
                static () => Task.CompletedTask,
                static () => { },
                static () => { },
                static () => { },
                static () => Task.CompletedTask,
                static () => Task.CompletedTask,
                static () => Task.CompletedTask,
                static () => { },
                static () => { },
                static () => { },
                static () => { },
                static () => { },
                static () => { }),
            Navigation = new DelegatingShellNavigationCommandService(
                static () => { },
                static () => { },
                static () => { },
                static () => { },
                static () => Task.CompletedTask,
                static () => Task.CompletedTask,
                static () => Task.CompletedTask,
                static () => Task.CompletedTask,
                static () => Task.CompletedTask,
                static () => Task.CompletedTask),
            Tabs = new DelegatingShellTabCommandService(static () => Task.CompletedTask),
            Status = status,
            Availability = new DelegatingShellCommandAvailabilityService(
                static () => true,
                static () => true,
                static () => false,
                static () => false,
                static () => false,
                static () => false,
                static () => false,
                static () => false,
                static () => false),
            Presenter = presenter,
            Plugins = new PluginCommands(),
            SessionActions = new DelegatingShellSessionActionService(static () => Task.CompletedTask, static () => Task.CompletedTask, static () => Task.CompletedTask),
            Diagnostics = new DelegatingShellDiagnosticsCommandService(static () => { }),
            GetCommands = () => registry.Commands,
            IsCommandBarMultiLine = static () => false,
        };

        return new ShellCommandSurfaceCoordinator(context, registry, new UiCommandRunner(status.SetStatus), presenter);
    }

    private sealed class Presenter : IShellCommandPresenter
    {
        public Task ShowHelpDialogAsync(IReadOnlyList<ShellCommand> commands, string? filterText = null) => Task.CompletedTask;

        public void ShowCommandPalette()
        {
        }

        public void ShowOpenFolderDialog(string? initialPath = null)
        {
        }
    }

    private sealed class PluginCommands : IPluginCommandService
    {
        public IReadOnlyList<global::CodeAlta.Plugins.Abstractions.PluginCommandContribution> GetCommandContributions() => [];

        public Task<global::CodeAlta.Plugins.Abstractions.PluginCommandResult> ExecuteCommandAsync(
            global::CodeAlta.Plugins.Abstractions.PluginCommandContribution contribution,
            CancellationToken cancellationToken = default)
            => Task.FromResult(global::CodeAlta.Plugins.Abstractions.PluginCommandResult.NotHandled);
    }
}
