using XenoAtom.Ansi;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Commands;
using XenoAtom.Terminal.UI.Input;

namespace CodeAlta.Frontend.Commands;

internal sealed class ShellCommand
{
    public required string Id { get; init; }

    public required string Label { get; init; }

    public required string Description { get; init; }

    public ShellCommandHelpCategory HelpCategory { get; init; } = ShellCommandHelpCategory.General;

    public ShellCommandPlacement Placement { get; init; } = ShellCommandPlacement.None;

    public string? Name { get; init; }

    public string? SearchText { get; init; }

    public KeyGesture? Gesture { get; init; }

    public KeySequence? Sequence { get; init; }

    public bool ShowInCommandBar { get; init; } = true;

    public bool ShowInCommandPalette { get; init; } = true;

    public bool ShowInHelp { get; init; } = true;

    public CommandImportance Importance { get; init; } = CommandImportance.Secondary;

    public bool ConsumesGestureWhenUnavailable { get; init; } = true;

    public bool RouteGesture { get; init; } = true;

    public IReadOnlyList<string> AdditionalHelpBindings { get; init; } = [];

    public Func<ShellCommandContext, Visual, bool>? CanExecute { get; init; }

    public Func<ShellCommandContext, Visual, bool>? IsVisible { get; init; }

    public Func<ShellCommandContext, Visual, string>? GetLabelMarkup { get; init; }

    public required Func<ShellCommandContext, Visual, CancellationToken, ValueTask> ExecuteAsync { get; init; }

    public bool CanExecuteFor(ShellCommandContext context, Visual target)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(target);
        return CanExecute?.Invoke(context, target) ?? true;
    }

    public bool IsVisibleFor(ShellCommandContext context, Visual target)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(target);
        return IsVisible?.Invoke(context, target) ?? true;
    }

    public string GetLabelMarkupFor(ShellCommandContext context, Visual target)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(target);
        return GetLabelMarkup?.Invoke(context, target) ?? AnsiMarkup.Escape(Label);
    }
}

internal enum ShellCommandHelpCategory
{
    General,
    Prompt,
    Session,
    Navigation,
    Inspection,
}

[Flags]
internal enum ShellCommandPlacement
{
    None = 0,
    ShellRoot = 1,
    PromptEditor = 2,
    WorkspaceRoot = 4,
}

internal enum SessionMessageScrollTarget
{
    Previous,
    Next,
    First,
    Last,
}
