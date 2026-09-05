using CodeAlta.Catalog;
using CodeAlta.Tui.Models;

namespace CodeAlta.Tui.App;

internal sealed record ShellSelectionSnapshot(
    ShellSelection Selection,
    ProjectDescriptor? SelectedProject,
    SessionViewDescriptor? SelectedSession,
    PromptSessionBinding? PromptSession);

internal interface IShellSelectionPort
{
    ShellSelectionSnapshot GetSnapshot();

    Task SelectAsync(ShellSelection selection, CancellationToken cancellationToken = default);

    bool IsSelectedSession(string sessionId);

    ProjectDescriptor? GetSelectedProject();

    SessionViewDescriptor? GetSelectedSession();

    PromptSessionBinding? GetSelectedPromptSession();
}

internal sealed class DelegatingShellSelectionPort : IShellSelectionPort
{
    private readonly Func<ShellSelection> _getSelection;
    private readonly Func<ProjectDescriptor?> _getSelectedProject;
    private readonly Func<SessionViewDescriptor?> _getSelectedSession;
    private readonly Func<PromptSessionBinding?> _getSelectedPromptSession;
    private readonly Func<ShellSelection, CancellationToken, Task> _selectAsync;
    private readonly Func<string, bool> _isSelectedSession;

    public DelegatingShellSelectionPort(
        Func<ShellSelection> getSelection,
        Func<ProjectDescriptor?> getSelectedProject,
        Func<SessionViewDescriptor?> getSelectedSession,
        Func<PromptSessionBinding?> getSelectedPromptSession,
        Func<ShellSelection, CancellationToken, Task> selectAsync,
        Func<string, bool> isSelectedSession)
    {
        ArgumentNullException.ThrowIfNull(getSelection);
        ArgumentNullException.ThrowIfNull(getSelectedProject);
        ArgumentNullException.ThrowIfNull(getSelectedSession);
        ArgumentNullException.ThrowIfNull(getSelectedPromptSession);
        ArgumentNullException.ThrowIfNull(selectAsync);
        ArgumentNullException.ThrowIfNull(isSelectedSession);

        _getSelection = getSelection;
        _getSelectedProject = getSelectedProject;
        _getSelectedSession = getSelectedSession;
        _getSelectedPromptSession = getSelectedPromptSession;
        _selectAsync = selectAsync;
        _isSelectedSession = isSelectedSession;
    }

    public ShellSelectionSnapshot GetSnapshot()
        => new(_getSelection(), _getSelectedProject(), _getSelectedSession(), _getSelectedPromptSession());

    public Task SelectAsync(ShellSelection selection, CancellationToken cancellationToken = default)
        => _selectAsync(selection, cancellationToken);

    public bool IsSelectedSession(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return _isSelectedSession(sessionId);
    }

    public ProjectDescriptor? GetSelectedProject()
        => _getSelectedProject();

    public SessionViewDescriptor? GetSelectedSession()
        => _getSelectedSession();

    public PromptSessionBinding? GetSelectedPromptSession()
        => _getSelectedPromptSession();
}
