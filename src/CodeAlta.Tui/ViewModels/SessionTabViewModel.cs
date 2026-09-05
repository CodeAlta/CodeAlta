using CodeAlta.Tui.Models;

namespace CodeAlta.Tui.ViewModels;

internal sealed class SessionTabViewModel
{
    public string SessionId { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string? StatusMessage { get; set; }

    public StatusTone StatusTone { get; set; } = StatusTone.Ready;

    public bool StatusBusy { get; set; }

    public bool HasCustomStatus { get; set; }
}
