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

    /// <summary>Gets whether an automation with this id is one of the automations of this computer.</summary>
    /// <param name="automationId">The automation.</param>
    /// <returns>Whether it is here.</returns>
    bool Holds(string automationId);

    /// <summary>
    /// Adds an automation of a team or of the organization, as its machine holds it, to the user's automations on this computer
    /// so that the user changes it in their editor: it runs on that machine, published to that scope, and the next change is a
    /// version signed here.
    /// </summary>
    /// <param name="content">What it does.</param>
    /// <param name="machineId">The machine it runs on.</param>
    /// <param name="scope">The scope it is published to.</param>
    /// <param name="linkId">The server it was given through.</param>
    /// <param name="number">The number of the version the server holds.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Why it could not be added, or null once it is.</returns>
    Task<string?> EditAsync(FleetAutomationContent content, string machineId, string scope, string linkId, long number, CancellationToken cancellationToken);

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
        return new FleetAutomationContent(AutomationConfig.Write(string.Empty, entry.Definition with { RunsOn = null, RunsOnTags = null, PublishTo = null, Project = null }), repository);
    }

    /// <summary>Gets what records the automations given through a server, which an automation taken to be changed here is recorded in; null for none.</summary>
    internal DesktopAutomationPlacement? Placement { get; set; }

    /// <inheritdoc />
    public async Task<string?> TakeAsync(FleetAutomationContent content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        var (definition, problem) = await ReadHereAsync(content, cancellationToken).ConfigureAwait(false);
        if (definition is null) return problem;
        return await _automations.SaveAsync(definition with { RunsOn = null, RunsOnTags = null, PublishTo = null }, null, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public bool Holds(string automationId) => _automations.Snapshot.Find(automationId) is not null;

    /// <inheritdoc />
    public async Task<string?> EditAsync(FleetAutomationContent content, string machineId, string scope, string linkId, long number, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrEmpty(machineId);
        ArgumentException.ThrowIfNullOrEmpty(scope);
        ArgumentException.ThrowIfNullOrEmpty(linkId);
        var (read, problem) = await ReadHereAsync(content, cancellationToken).ConfigureAwait(false);
        if (read is null) return problem;
        var definition = read with { RunsOn = machineId, RunsOnTags = null, PublishTo = scope };
        Task<string?> Save() => _automations.SaveAsync(definition, null, cancellationToken);
        // Recorded as given at the version its machine holds: opening it gives the machine nothing; saving a change gives it the next version.
        return Placement is { } placement
            ? await placement.AdoptAsync(definition, content.Repository, linkId, number, Save, cancellationToken).ConfigureAwait(false)
            : await Save().ConfigureAwait(false);
    }

    // What an automation of another computer is here: read, and in the project of its repository; null and why not.
    private async Task<(AutomationDefinition? Definition, string? Problem)> ReadHereAsync(FleetAutomationContent content, CancellationToken cancellationToken)
    {
        var (definitions, faults) = AutomationConfig.Read(content.Definition, new AutomationSource("handover", null, null));
        if (definitions.Count != 1 || faults.Count > 0) return (null, "What was handed over is not an automation this version reads.");
        var definition = definitions[0];
        if (_automations.Snapshot.Find(definition.Id) is not null) return (null, "An automation with the same id is here already.");
        if (content.Repository is not { } repository) return (definition, null);
        foreach (var project in await _projects(cancellationToken).ConfigureAwait(false))
        {
            if (project.Archived || string.IsNullOrEmpty(project.ProjectPath)) continue;
            if (await _repositoryOf(project.ProjectPath, cancellationToken).ConfigureAwait(false) == repository) return (definition with { Project = project.ProjectPath }, null);
        }

        return (null, $"Add the project of {repository} to CodeAlta first: the automation runs in it.");
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
