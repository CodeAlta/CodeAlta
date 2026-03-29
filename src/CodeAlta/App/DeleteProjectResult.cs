namespace CodeAlta.App;

internal sealed record DeleteProjectResult(
    string ProjectId,
    IReadOnlyList<string> DeletedThreadIds);
