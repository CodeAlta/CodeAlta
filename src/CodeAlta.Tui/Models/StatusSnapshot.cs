namespace CodeAlta.Tui.Models;

internal readonly record struct StatusSnapshot(
    string Message,
    bool Busy,
    StatusTone Tone,
    string? IconMarkup = null);
