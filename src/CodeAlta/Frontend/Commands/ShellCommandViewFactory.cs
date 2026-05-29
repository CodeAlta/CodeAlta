using CodeAlta.App;
using CodeAlta.Models;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Commands;

namespace CodeAlta.Frontend.Commands;

internal static class ShellCommandViewFactory
{
    public static Command Create(ShellCommand command, ShellCommandContext context, UiCommandRunner runner)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(runner);

        return new Command
        {
            Id = command.Id,
            LabelMarkup = command.Label,
            Name = command.Name,
            SearchText = BuildSearchText(command),
            DescriptionMarkup = $"[dim]{XenoAtom.Ansi.AnsiMarkup.Escape(command.Description)}[/]",
            Gesture = command.Gesture,
            Sequence = command.Sequence,
            Importance = command.Importance,
            Presentation = ResolvePresentation(command),
            ConsumesGestureWhenUnavailable = command.ConsumesGestureWhenUnavailable,
            RouteGesture = command.RouteGesture,
            CanExecute = target => command.CanExecuteFor(context, target),
            IsVisible = target => command.IsVisibleFor(context, target),
            Execute = target => runner.Run(command, context, target),
        };
    }

    private static string BuildSearchText(ShellCommand command)
        => string.Join(' ', new[] { command.Label, command.Name, command.SearchText }.Where(static text => !string.IsNullOrWhiteSpace(text))!);

    private static CommandPresentation ResolvePresentation(ShellCommand command)
    {
        var presentation = CommandPresentation.None;
        if (command.ShowInCommandBar)
        {
            presentation |= CommandPresentation.CommandBar;
        }

        if (command.ShowInCommandPalette)
        {
            presentation |= CommandPresentation.CommandPalette;
        }

        return presentation;
    }
}

internal sealed class UiCommandRunner
{
    private readonly Action<string, bool, StatusTone> _setStatus;

    public UiCommandRunner(Action<string, bool, StatusTone> setStatus)
    {
        ArgumentNullException.ThrowIfNull(setStatus);
        _setStatus = setStatus;
    }

    public void Run(ShellCommand command, ShellCommandContext context, Visual target)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(target);
        _ = UiTaskDiagnostics.ObserveAsync(
            async () => await command.ExecuteAsync(context, target, CancellationToken.None),
            $"run command {command.Id}",
            _setStatus);
    }
}
