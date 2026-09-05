namespace CodeAlta.Tui.App;

internal sealed record DeleteSessionResult(
    IReadOnlyList<string> DeletedSessionIds,
    bool DeletedFromSessionStore);
