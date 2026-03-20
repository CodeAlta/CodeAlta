using CodeAlta.Models;

namespace CodeAlta.ViewModels;

internal sealed class ThreadTabViewModel
{
    public string ThreadId { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string? StatusMessage { get; set; }

    public StatusTone StatusTone { get; set; } = StatusTone.Ready;

    public bool StatusBusy { get; set; }

    public bool HasCustomStatus { get; set; }
}