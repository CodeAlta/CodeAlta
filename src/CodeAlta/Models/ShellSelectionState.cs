using CodeAlta.Catalog;

namespace CodeAlta.Models;

internal sealed class ShellSelectionState
{
    public WorkThreadViewState ViewState { get; set; } = new();

    public bool DraftTabOpen { get; set; }

    public bool GlobalScopeSelected { get; set; } = true;

    public string? SelectedProjectId { get; set; }

    public string? SelectedThreadId { get; set; }

    public string? PendingStartupThreadRestoreId { get; set; }
}