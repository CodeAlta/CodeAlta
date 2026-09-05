namespace CodeAlta.Tui.Models;

internal readonly record struct SessionVisualState(
    bool IsRunning,
    bool HasPromptDraft,
    bool HasActiveReminder);
