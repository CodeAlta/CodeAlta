using CodeAlta.Tui.ViewModels;

namespace CodeAlta.Tui.App;

internal sealed class ProjectSessionsDialogState
{
    private readonly List<ProjectSessionsDialogRowViewModel> _rows;

    public ProjectSessionsDialogState(IEnumerable<ProjectSessionsDialogRowViewModel> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        _rows = rows.ToList();
    }

    public IReadOnlyList<ProjectSessionsDialogRowViewModel> Rows => _rows;

    public int SelectedCount => _rows.Count(static row => row.IsSelected);

    public IReadOnlyList<string> GetSelectedSessionIds()
        => _rows
            .Where(static row => row.IsSelected)
            .Select(static row => row.SessionId)
            .ToArray();

    public void SelectAll()
    {
        foreach (var row in _rows)
        {
            row.IsSelected = true;
        }
    }

    public void SelectNone()
    {
        foreach (var row in _rows)
        {
            row.IsSelected = false;
        }
    }

    public void InvertSelection()
    {
        foreach (var row in _rows)
        {
            row.IsSelected = !row.IsSelected;
        }
    }

    public int RemoveSessions(IReadOnlyCollection<string> sessionIds)
    {
        ArgumentNullException.ThrowIfNull(sessionIds);
        return _rows.RemoveAll(row => sessionIds.Contains(row.SessionId, StringComparer.OrdinalIgnoreCase));
    }
}
