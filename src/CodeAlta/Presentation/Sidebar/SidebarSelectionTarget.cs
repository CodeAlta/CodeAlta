namespace CodeAlta.Presentation.Sidebar
{
    internal enum SidebarSelectionKind
    {
        GlobalScope,
        ProjectScope,
        Thread,
    }

    internal readonly record struct SidebarSelectionTarget(
        SidebarSelectionKind Kind,
        string? ProjectId,
        string? ThreadId)
    {
        public static SidebarSelectionTarget Global()
            => new(SidebarSelectionKind.GlobalScope, null, null);

        public static SidebarSelectionTarget Project(string projectId)
            => new(SidebarSelectionKind.ProjectScope, projectId, null);

        public static SidebarSelectionTarget Thread(string threadId)
            => new(SidebarSelectionKind.Thread, null, threadId);
    }
}