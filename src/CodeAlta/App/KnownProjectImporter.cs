using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Runtime;
using XenoAtom.Logging;

namespace CodeAlta.App;

internal sealed class KnownProjectImporter : IKnownProjectImporter
{
    private static readonly Logger Logger = LogManager.GetLogger("CodeAlta.App");
    private readonly AgentHub _agentHub;
    private readonly ProjectCatalog _projectCatalog;

    public KnownProjectImporter(AgentHub agentHub, ProjectCatalog projectCatalog)
    {
        ArgumentNullException.ThrowIfNull(agentHub);
        ArgumentNullException.ThrowIfNull(projectCatalog);

        _agentHub = agentHub;
        _projectCatalog = projectCatalog;
    }

    public async Task ImportAsync(CancellationToken cancellationToken)
    {
        var workingDirectories = new List<string?>();
        foreach (var backendId in new[] { AgentBackendIds.Codex, AgentBackendIds.Copilot })
        {
            try
            {
                var sessions = await _agentHub.ListSessionsAsync(backendId, cancellationToken: cancellationToken).ConfigureAwait(false);
                workingDirectories.AddRange(sessions.Select(static session => session.Context?.Cwd ?? session.WorkspacePath));
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"Failed to import project history from backend '{backendId.Value}'.");
            }
        }

        await _projectCatalog.ImportWorkingDirectoriesAsync(workingDirectories, cancellationToken).ConfigureAwait(false);
    }
}