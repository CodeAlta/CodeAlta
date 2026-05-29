using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Commands;

namespace CodeAlta.Frontend.Commands;

internal sealed class ShellCommandSurfaceCoordinator
{
    private readonly ShellCommandContext _context;
    private readonly ShellCommandRegistry _registry;
    private readonly UiCommandRunner _runner;
    private readonly IShellCommandPresenter _presenter;

    public ShellCommandSurfaceCoordinator(
        ShellCommandContext context,
        ShellCommandRegistry registry,
        UiCommandRunner runner,
        IShellCommandPresenter presenter)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
    }

    public IReadOnlyList<ShellCommand> Commands => _registry.Commands;

    public IEnumerable<ShellCommand> CommandsFor(ShellCommandPlacement placement)
        => _registry.CommandsFor(placement);

    public Command CreateViewCommand(ShellCommand command)
        => ShellCommandViewFactory.Create(command, _context, _runner);

    public Task HandleAcceptedPromptAsync(string? rawInput, CancellationToken cancellationToken = default)
        => _context.PromptDispatch.SendPromptAsync(rawInput, steer: false, cancellationToken);

    public Task SubmitCurrentPromptAsync(bool steer, CancellationToken cancellationToken = default)
        => _context.PromptDispatch.SendPromptAsync(_context.PromptInput.GetPromptText(), steer, cancellationToken);

    public Task AbortSelectedSessionAsync(CancellationToken cancellationToken = default)
        => _context.SessionActions.AbortSelectedSessionAsync();

    public Task CompactSelectedSessionAsync(CancellationToken cancellationToken = default)
        => _context.SessionActions.CompactSelectedSessionAsync();

    public Task CloseCurrentTabAsync(CancellationToken cancellationToken = default)
        => _context.Tabs.CloseCurrentTabAsync();

    public Task ShowHelpAsync(string? filterText = null, CancellationToken cancellationToken = default)
        => _presenter.ShowHelpDialogAsync(_registry.Commands, filterText);

    public void ShowCommandPalette()
        => _presenter.ShowCommandPalette();

    public Task ShowCommandPaletteAsync()
    {
        _presenter.ShowCommandPalette();
        return Task.CompletedTask;
    }

    public bool TryCreateCommand(string id, out Command command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (!_registry.TryGetById(id, out var shellCommand))
        {
            command = null!;
            return false;
        }

        command = CreateViewCommand(shellCommand);
        return true;
    }
}
