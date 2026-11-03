using CodeAlta.Catalog;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Hosting;

namespace CodeAlta.Node;

/// <summary>The options of <c>altanode projects</c>.</summary>
internal sealed class NodeProjectOptions
{
    /// <summary>Gets the profile options.</summary>
    public NodeProfileOptions Profile { get; } = new();

    /// <summary>Gets or sets the space a project joins too, by id or name; null for the default space only.</summary>
    public string? Space { get; set; }

    /// <summary>Gets the words: the subcommand and its argument.</summary>
    public List<string> Words { get; } = [];
}

/// <summary>
/// The projects of the node of a profile, managed by its owner: <c>altanode projects list</c>, <c>add</c>, <c>clone</c> and
/// <c>remove</c>. While a node that listens runs on the profile, they go through its local control (the node writes its
/// catalog, and refuses to remove a project that has sessions); otherwise <c>altanode</c> does the same on the catalog files
/// of the profile (<see cref="FleetProjectEditor"/> over <see cref="ProjectCatalog"/>, which every CodeAlta of the profile shares).
/// </summary>
/// <param name="output">Standard output.</param>
/// <param name="error">Standard error.</param>
/// <param name="cloneRoot">Where a profile's node clones repositories when no node runs (the clone root of its options).</param>
internal sealed class NodeProjectCommands(TextWriter output, TextWriter error, Func<CodeAltaInstanceProfile, string?> cloneRoot)
{
    /// <summary>Gets or sets what stops a call to the node, for tests; none by default.</summary>
    public CancellationToken Stop { get; init; }

    public async ValueTask<int> RunAsync(NodeProjectOptions options)
    {
        if (options.Profile.Resolve(out var invalid) is not { } profile) return await UsageAsync(invalid!).ConfigureAwait(false);
        var (command, arguments) = options.Words is [var first, .. var rest] ? (first, rest) : ("list", []);
        switch (command)
        {
            case "list" when arguments.Count == 0 && options.Space is null:
                return await ListAsync(NodeLocalControl.Read(profile)).ConfigureAwait(false);
            case "add" when arguments is [var path]:
                return await ChangeAsync(NodeLocalControl.Read(profile),
                    (client, token) => client.AddProjectAsync(new FleetProjectAddParams(Path.GetFullPath(path), options.Space), token),
                    (editor, token) => editor.AddAsync(Path.GetFullPath(path), options.Space, token)).ConfigureAwait(false);
            case "clone" when arguments is [var url]:
                return await ChangeAsync(NodeLocalControl.Read(profile),
                    (client, token) => client.CloneProjectAsync(new FleetProjectCloneParams(url, options.Space), token),
                    (editor, token) => editor.CloneAsync(url, options.Space, token)).ConfigureAwait(false);
            case "remove" when arguments is [var project] && options.Space is null:
                return await RemoveAsync(NodeLocalControl.Read(profile), project).ConfigureAwait(false);
            case "list" or "add" or "clone" or "remove":
                return await UsageAsync(command switch
                {
                    "list" => "projects list takes no argument.",
                    "add" => "projects add takes the folder of the project: altanode projects add <path> [--space <space>].",
                    "clone" => "projects clone takes the URL of the repository: altanode projects clone <url> [--space <space>].",
                    _ => "projects remove takes the project: its id, its slug or its name (altanode projects list lists them).",
                }).ConfigureAwait(false);
            default:
                return await UsageAsync($"'{command}' is not a command of projects: list, add, clone or remove.").ConfigureAwait(false);
        }
    }

    private async Task<int> ListAsync(NodeLocalControl control)
    {
        var list = control.NodeListens
            ? await CallAsync(control, static (client, token) => client.ListProjectsAsync(token)).ConfigureAwait(false)
            : await Editor(control).ListAsync(Stop).ConfigureAwait(false);
        if (list is null) return NodeExitCodes.Failure;
        if (list.Projects.Count == 0) await output.WriteLineAsync("No project is in the catalog. altanode projects add <path> or clone <url> adds one.").ConfigureAwait(false);
        else
        {
            var nameWidth = Math.Min(32, Math.Max(4, list.Projects.Max(static project => project.Name.Length)));
            var idWidth = list.Projects.Max(static project => project.Id.Length);
            await output.WriteLineAsync($"{"NAME".PadRight(nameWidth)}  {"ID".PadRight(idWidth)}  {"SPACES",-16}  PATH").ConfigureAwait(false);
            foreach (var project in list.Projects)
            {
                var spaces = project.Spaces.Count == 0 ? "-" : string.Join(",", project.Spaces);
                await output.WriteLineAsync($"{Fit(project.Name, nameWidth)}  {project.Id.PadRight(idWidth)}  {spaces,-16}  {project.Path}{(project.Archived ? "  (archived)" : "")}")
                    .ConfigureAwait(false);
            }
        }

        foreach (var diagnostic in list.Diagnostics) await output.WriteLineAsync("warning: " + diagnostic.Message).ConfigureAwait(false);
        if (!control.NodeListens) await output.WriteLineAsync($"(read from {control.Catalog.ProjectsRoot}: no node that listens runs on this profile)").ConfigureAwait(false);
        return NodeExitCodes.Success;
    }

    private async Task<int> ChangeAsync(NodeLocalControl control,
        Func<Fleet.Client.FleetNodeClient, CancellationToken, Task<FleetProjectChange>> remote,
        Func<FleetProjectEditor, CancellationToken, Task<FleetProjectChange>> local)
    {
        var change = control.NodeListens
            ? await CallAsync(control, remote).ConfigureAwait(false)
            : await LocalAsync(() => local(Editor(control), Stop)).ConfigureAwait(false);
        if (change is null) return NodeExitCodes.Failure;
        if (change.Note is { } note) await output.WriteLineAsync(note).ConfigureAwait(false);
        var spaces = change.Project.Spaces.Count == 0 ? "" : $", in {string.Join(", ", change.Project.Spaces)}";
        await output.WriteLineAsync(change.Changed
            ? $"Added {change.Project.Name} ({change.Project.Id}): {change.Project.Path}{spaces}."
            : $"{change.Project.Name} ({change.Project.Id}) is in the catalog already: {change.Project.Path}{spaces}.").ConfigureAwait(false);
        return NodeExitCodes.Success;
    }

    private async Task<int> RemoveAsync(NodeLocalControl control, string project)
    {
        if (!control.NodeListens && control.Status.IsHeld)
        {
            // The instance that holds the profile runs its sessions: it is the one that knows whether the project has some.
            await error.WriteLineAsync($"altanode projects: {control.Status.Describe()} Remove the project there, or while a node runs on the profile.")
                .ConfigureAwait(false);
            return NodeExitCodes.Failure;
        }

        var change = control.NodeListens
            ? await CallAsync(control, (client, token) => client.RemoveProjectAsync(project, token)).ConfigureAwait(false)
            : await LocalAsync(() => Editor(control).RemoveAsync(project, null, Stop)).ConfigureAwait(false);
        if (change is null) return NodeExitCodes.Failure;
        await output.WriteLineAsync($"Removed {change.Project.Name} ({change.Project.Id}) from the catalog. {change.Note}".TrimEnd()).ConfigureAwait(false);
        if (!control.NodeListens)
            await output.WriteLineAsync("No node runs on this profile: its sessions were not looked at. Those it had are listed without a project until the folder is added again.")
                .ConfigureAwait(false);
        return NodeExitCodes.Success;
    }

    private FleetProjectEditor Editor(NodeLocalControl control) => new(new SpaceCatalog(new ProjectCatalog(control.Catalog)), cloneRoot(control.Profile));

    private async Task<T?> CallAsync<T>(NodeLocalControl control, Func<Fleet.Client.FleetNodeClient, CancellationToken, Task<T>> call) where T : class
    {
        var (client, identity) = await control.ConnectAsync(Stop).ConfigureAwait(false);
        using (identity)
        await using (client.ConfigureAwait(false))
        {
            return await LocalAsync(() => call(client, Stop)).ConfigureAwait(false);
        }
    }

    // A refusal (an unknown folder, space or project, a failed clone) is told as it is, with a failure.
    private async Task<T?> LocalAsync<T>(Func<Task<T>> run) where T : class
    {
        try
        {
            return await run().ConfigureAwait(false);
        }
        catch (FleetException exception) when (exception.Error.Code is not (FleetErrorCodes.ConnectionClosed or FleetErrorCodes.TransportError or FleetErrorCodes.InternalError))
        {
            await error.WriteLineAsync("altanode projects: " + exception.Error.Message).ConfigureAwait(false);
            return null;
        }
    }

    private static string Fit(string text, int width) => text.Length <= width ? text.PadRight(width) : text[..(width - 1)] + "…";

    private async ValueTask<int> UsageAsync(string message)
    {
        await error.WriteLineAsync("altanode: " + message).ConfigureAwait(false);
        return NodeExitCodes.InvalidUsage;
    }
}
