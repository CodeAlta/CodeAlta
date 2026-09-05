namespace CodeAlta.Tui.Models;

internal sealed record ShellSelection(ShellSurface Surface, WorkspaceTarget Target)
{
    public static ShellSelection GlobalDraft(string? projectId = null)
        => new(ShellSurface.DraftWorkspace, new WorkspaceTarget.Draft(projectId, IsGlobal: true));

    public static ShellSelection ProjectDraft(string projectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        return new ShellSelection(ShellSurface.DraftWorkspace, new WorkspaceTarget.Draft(projectId, IsGlobal: false));
    }

    public static ShellSelection Session(string sessionId, string? projectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return new ShellSelection(ShellSurface.SessionWorkspace, new WorkspaceTarget.Session(sessionId, projectId));
    }

    public bool DraftTabOpen => Surface == ShellSurface.DraftWorkspace;

    public bool GlobalScopeSelected => Target is WorkspaceTarget.Draft { IsGlobal: true };

    public string? SelectedProjectId => Target switch
    {
        WorkspaceTarget.Draft draft => draft.ProjectId,
        WorkspaceTarget.Session session => session.ProjectId,
        _ => null,
    };

    public string? SelectedSessionId => Target is WorkspaceTarget.Session session ? session.SessionId : null;
}
