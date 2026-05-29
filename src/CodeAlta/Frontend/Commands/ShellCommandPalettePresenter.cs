using CodeAlta.Views;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Styling;

namespace CodeAlta.Frontend.Commands;

internal sealed class ShellCommandPalettePresenter : IShellCommandPresenter
{
    internal static CommandPaletteStyle CommandPalettePopupStyle { get; } = CommandPaletteStyle.Default with
    {
        PopupWidthPercent = 50,
        MaxWidth = int.MaxValue,
        PopupHorizontalAlignment = Align.Center,
        PopupVerticalAlignment = Align.End,
        PopupOffsetY = -2,
    };

    internal static CommandPaletteStyle DialogCommandPalettePopupStyle { get; } = CommandPaletteStyle.Default with
    {
        PopupWidthPercent = 50,
        MaxWidth = int.MaxValue,
        PopupHorizontalAlignment = Align.Center,
        PopupVerticalAlignment = Align.Center,
    };

    private readonly IShellDialogCommandService _dialogCommandService;
    private CommandPaletteStyle _activeCommandPaletteStyle = CommandPalettePopupStyle;
    private CommandPalette? _commandPalette;
    private ShellHelpDialog? _helpDialog;

    public ShellCommandPalettePresenter(IShellDialogCommandService dialogCommandService)
    {
        ArgumentNullException.ThrowIfNull(dialogCommandService);
        _dialogCommandService = dialogCommandService;
    }

    public Task ShowHelpDialogAsync(IReadOnlyList<ShellCommand> commands, string? filterText = null)
    {
        ArgumentNullException.ThrowIfNull(commands);
        _helpDialog ??= new ShellHelpDialog(_dialogCommandService.GetDialogBounds, _dialogCommandService.GetDialogFocusTarget);
        return _helpDialog.ShowAsync(commands, filterText);
    }

    public void ShowCommandPalette()
    {
        var app = _dialogCommandService.GetDialogFocusTarget()?.App ?? _commandPalette?.App;
        _activeCommandPaletteStyle = ResolveCommandPalettePopupStyle(app?.FocusedElement);
        (_commandPalette ??= CreateCommandPalette(() => _activeCommandPaletteStyle)).Show();
    }

    public void ShowOpenFolderDialog(string? initialPath = null)
        => new DirectoryPathDialog(
            "Open Project",
            "Type a project name from the sidebar or a rooted folder path.",
            "Open",
            new DirectoryPathDialogService(
                _dialogCommandService.GetDialogBounds,
                _dialogCommandService.GetDialogFocusTarget,
                _dialogCommandService.OpenFolderAsync,
                _dialogCommandService.GetDialogFocusTarget,
                _dialogCommandService.GetProjects),
            initialPath,
            placeholder: "CodeAlta or C:\\code\\SomeFolder")
            .Show();

    internal static CommandPaletteStyle ResolveCommandPalettePopupStyle(Visual? focusElement)
    {
        return IsInsideDialog(focusElement)
            ? DialogCommandPalettePopupStyle
            : CommandPalettePopupStyle;
    }

    private static bool IsInsideDialog(Visual? visual)
    {
        for (var current = visual; current is not null; current = current.Parent)
        {
            if (current is Dialog)
            {
                return true;
            }
        }

        return false;
    }

    private static CommandPalette CreateCommandPalette(Func<CommandPaletteStyle> getStyle)
        => new CommandPalette().Style(() => getStyle());
}
