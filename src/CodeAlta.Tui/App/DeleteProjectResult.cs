namespace CodeAlta.Tui.App;

internal sealed record DeleteProjectResult(
    string ProjectId,
    IReadOnlyList<string> DeletedSessionIds);
