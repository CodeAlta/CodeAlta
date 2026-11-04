using CodeAlta.Automations;
using CodeAlta.Catalog;
using CodeAlta.Catalog.SessionMoves;
using CodeAlta.Fleet.Server;

namespace CodeAlta.Desktop.Fleet;

/// <summary>
/// The automations of this computer as a transfer moves them (<c>doc/fleet/access-control.md</c>, ownership over time): what
/// one does, read to seal it for the person it is given to; one taken, added to the automations of this computer, which gives
/// its machines versions of its own; one given away, taken off this computer, which takes it off its machines.
/// </summary>
internal interface IDesktopAutomationHandover
{
    /// <summary>Reads what an automation of the user does, to hand it over; null when it is not one of the user's automations here.</summary>
    /// <param name="automationId">The automation.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>What it does, or null.</returns>
    Task<FleetAutomationContent?> ReadAsync(string automationId, CancellationToken cancellationToken);

    /// <summary>Adds an automation handed over to the user's automations, on this computer.</summary>
    /// <param name="content">What it does.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Why it could not be added, or null once it is.</returns>
    Task<string?> TakeAsync(FleetAutomationContent content, CancellationToken cancellationToken);

    /// <summary>Takes an automation given to another owner off this computer, when it is one of the user's automations here.</summary>
    /// <param name="automationId">The automation.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Whether it was here and is gone.</returns>
    Task<bool> GiveUpAsync(string automationId, CancellationToken cancellationToken);
}

/// <summary>The hand-overs of the automations of the user's configuration.</summary>
internal sealed class DesktopAutomationHandover : IDesktopAutomationHandover
{
    private readonly AutomationService _automations;
    private readonly Func<CancellationToken, Task<IReadOnlyList<ProjectDescriptor>>> _projects;
    private readonly Func<string, CancellationToken, Task<string?>> _repositoryOf;

    /// <summary>Creates the hand-overs of a host.</summary>
    /// <param name="automations">The automations of this computer.</param>
    /// <param name="projects">The projects of CodeAlta.</param>
    /// <param name="repositoryOf">The repository key of a project folder; null for the one Git gives.</param>
    internal DesktopAutomationHandover(AutomationService automations, Func<CancellationToken, Task<IReadOnlyList<ProjectDescriptor>>> projects,
        Func<string, CancellationToken, Task<string?>>? repositoryOf = null)
    {
        ArgumentNullException.ThrowIfNull(automations);
        ArgumentNullException.ThrowIfNull(projects);
        _automations = automations;
        _projects = projects;
        _repositoryOf = repositoryOf ?? RepositoryKey.ReadAsync;
    }

    /// <inheritdoc />
    public async Task<FleetAutomationContent?> ReadAsync(string automationId, CancellationToken cancellationToken)
    {
        if (Own(automationId) is not { } entry || entry.Problem is not null) return null;
        var repository = entry.ProjectPath is { } folder ? await _repositoryOf(folder, cancellationToken).ConfigureAwait(false) : null;
        if (entry.ProjectPath is not null && repository is null) return null;
        // Where it runs and the folder of its project are this computer's: the new owner places it on machines of their own.
        return new FleetAutomationContent(AutomationConfig.Write(string.Empty, entry.Definition with { RunsOn = null, Project = null }), repository);
    }

    /// <inheritdoc />
    public async Task<string?> TakeAsync(FleetAutomationContent content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        var (definitions, faults) = AutomationConfig.Read(content.Definition, new AutomationSource("handover", null, null));
        if (definitions.Count != 1 || faults.Count > 0) return "What was handed over is not an automation this version reads.";
        var definition = definitions[0];
        if (_automations.Snapshot.Find(definition.Id) is not null) return "An automation with the same id is here already.";
        if (content.Repository is { } repository)
        {
            string? folder = null;
            foreach (var project in await _projects(cancellationToken).ConfigureAwait(false))
            {
                if (project.Archived || string.IsNullOrEmpty(project.ProjectPath)) continue;
                if (await _repositoryOf(project.ProjectPath, cancellationToken).ConfigureAwait(false) == repository)
                {
                    folder = project.ProjectPath;
                    break;
                }
            }

            if (folder is null) return $"Add the project of {repository} to CodeAlta first: the automation runs in it.";
            definition = definition with { Project = folder };
        }

        return await _automations.SaveAsync(definition with { RunsOn = null }, null, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> GiveUpAsync(string automationId, CancellationToken cancellationToken)
    {
        if (Own(automationId) is null) return false;
        return await _automations.DeleteAsync(automationId, cancellationToken).ConfigureAwait(false) is null;
    }

    // An automation of the user's configuration: a project's travels with its repository, and one assigned here is another computer's.
    private AutomationEntry? Own(string automationId)
        => _automations.Snapshot.Find(automationId) is { Source: { IsGlobal: true, IsAssigned: false } } entry ? entry : null;
}
