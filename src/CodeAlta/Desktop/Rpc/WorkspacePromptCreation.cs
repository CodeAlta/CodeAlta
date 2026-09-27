namespace CodeAlta.Desktop.Rpc;

internal sealed partial class WorkspaceService
{
    private Task<PromptCreateResponse>? _promptCreateWork;

    // Hold existing catalog-writer admission through bounded ownership validation and publication.
    // In particular, this prevents this host archiving/deleting the original scope mid-create.
    internal Task<PromptCreateResponse> RunPromptCreationAsync(Func<Task<PromptCreateResponse>> create, Func<string, PromptCreateResponse> reply)
    {
        lock (_importGate)
        {
            if (CatalogAdmissionClosed) return Task.FromResult(reply("closed"));
            if (CatalogAdmissionBusy || _renameWork is not null || _deleteWork is not null) return Task.FromResult(reply("busy"));
            _promptCreateWork = Task.Run(create, CancellationToken.None);
            return _promptCreateWork;
        }
    }
}
