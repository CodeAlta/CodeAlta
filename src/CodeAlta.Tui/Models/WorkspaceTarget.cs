namespace CodeAlta.Tui.Models;

internal abstract record WorkspaceTarget
{
    public sealed record Draft(string? ProjectId, bool IsGlobal) : WorkspaceTarget;

    public sealed record Session(string SessionId, string? ProjectId) : WorkspaceTarget;
}
