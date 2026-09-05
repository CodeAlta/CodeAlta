namespace CodeAlta.Tui.App.Context;

internal enum WorkspaceRefreshReason
{
    SelectedSessionUsageInvalidated,
    HeaderAndSessionWorkspace,
}

internal readonly record struct WorkspaceRefreshRequest(WorkspaceRefreshReason Reason);

internal sealed class WorkspaceRefreshContext
{
    private readonly Action<WorkspaceRefreshRequest> _publishRefreshRequest;

    public WorkspaceRefreshContext(Action<WorkspaceRefreshRequest> publishRefreshRequest)
    {
        ArgumentNullException.ThrowIfNull(publishRefreshRequest);

        _publishRefreshRequest = publishRefreshRequest;
    }

    public void ApplySessionUsageProjection()
        => Publish(WorkspaceRefreshReason.SelectedSessionUsageInvalidated);

    public void ApplyHeaderProjection()
        => Publish(WorkspaceRefreshReason.HeaderAndSessionWorkspace);

    private void Publish(WorkspaceRefreshReason reason)
        => _publishRefreshRequest(new WorkspaceRefreshRequest(reason));
}
