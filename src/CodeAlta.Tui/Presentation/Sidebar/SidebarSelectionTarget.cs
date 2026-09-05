namespace CodeAlta.Tui.Presentation.Sidebar
{
    internal enum SidebarSelectionKind
    {
        GlobalScope,
        ProjectScope,
        Session,
    }

    internal readonly record struct SidebarSelectionTarget(
        SidebarSelectionKind Kind,
        string? ProjectId,
        string? SessionId)
    {
        public static SidebarSelectionTarget Global()
            => new(SidebarSelectionKind.GlobalScope, null, null);

        public static SidebarSelectionTarget Project(string projectId)
            => new(SidebarSelectionKind.ProjectScope, projectId, null);

        public static SidebarSelectionTarget Session(string sessionId)
            => new(SidebarSelectionKind.Session, null, sessionId);
    }
}
