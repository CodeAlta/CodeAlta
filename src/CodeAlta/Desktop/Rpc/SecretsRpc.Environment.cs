using System.Text.Json;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Secrets;
using CodeAlta.Credentials;
using CodeAlta.Plugin.Mcp;
using NeoAstra.Rpc;
using Tomlyn;
using Tomlyn.Model;

namespace CodeAlta.Desktop.Rpc;

internal sealed partial class SecretsService
{
    internal SpaceCatalog? Spaces { get; init; }
    internal CodeAltaConfigStore? Config { get; init; }

    /// <summary>The home the MCP servers are read from, for what names a secret; null reads none.</summary>
    internal McpHome? McpHome { get; init; }

    /// <summary>Lists the project environment and CLI profiles as written, with local availability, without resolving values.</summary>
    [NeoRpcMethod("listEnvironment")]
    public async Task<ProjectEnvironmentList> ListEnvironmentAsync(SecretsRequest request, CancellationToken cancellationToken)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused) return new(refused, [], []);
        if (Config is not { } config || Spaces is null) return new("unavailable", [], []);
        try
        {
            var (projects, spaces) = await ReadProjectsAndSpacesAsync(cancellationToken).ConfigureAwait(false);
            var secrets = _store!.ListSecrets().ToDictionary(static secret => secret.Name, StringComparer.OrdinalIgnoreCase);
            var variables = _store.ListVariables().Select(static variable => variable.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var copies = _store.ListFleetCopies().Where(static copy => !copy.HostOnly).ToLookup(static copy => (copy.Kind, copy.Name.ToUpperInvariant()));
            var list = new List<ProjectEnvironmentView>(projects.Count);
            foreach (var project in projects)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var scope = SecretScope.ForProject(null, project, null);
                var spaceId = SpaceCatalog.ResolveSessionSpace(null, project, spaces);
                var settings = config.LoadProjectEnvironment(project.ProjectPath);
                var local = config.LoadProjectEnvironment(project.ProjectPath, includeInherited: false)
                    .Select(static variable => variable.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var profiles = config.LoadProjectCliProfiles(project.ProjectPath);
                var issues = new Dictionary<SecretReference, EnvironmentReferenceIssue?>();
                EnvironmentReferenceIssue[] IssuesOf(IEnumerable<SecretReference> references)
                {
                    var result = new List<EnvironmentReferenceIssue>();
                    foreach (var reference in references)
                    {
                        if (!issues.TryGetValue(reference, out var issue))
                        {
                            bool HasCopy(string kind) => copies[(kind, reference.Name.ToUpperInvariant())].Any(copy => copy.ScopeKind switch
                            {
                                FleetSecretScopes.Person => true,
                                FleetSecretScopes.Project => string.Equals(copy.ScopeId, project.Id, StringComparison.OrdinalIgnoreCase),
                                FleetSecretScopes.Space => string.Equals(copy.ScopeId, spaceId, StringComparison.OrdinalIgnoreCase),
                                _ => false, // Admitted environments belong to specific runs, not to the project.
                            });
                            issue = reference.Kind switch
                            {
                                SecretReferenceKind.Environment => Environment.GetEnvironmentVariable(reference.Name) is null ? NotSet(reference) : null,
                                SecretReferenceKind.Variable => variables.Contains(reference.Name) || HasCopy(FleetSecretValue.VariableKind) ? null : NotSet(reference),
                                _ => secrets.TryGetValue(reference.Name, out var secret)
                                    ? SecretIssue(secret, scope)
                                    : HasCopy(FleetSecretValue.SecretKind) ? null : NotSet(reference),
                            };
                            issues.Add(reference, issue);
                        }
                        if (issue is not null && !result.Contains(issue)) result.Add(issue);
                    }
                    return [.. result];
                }

                var entries = settings.Select(pair => new ProjectEnvironmentVariableView(pair.Key, pair.Value, local.Contains(pair.Key),
                    IssuesOf(SecretReferences.Find(pair.Value)))).ToArray();
                _ = IssuesOf(profiles.SelectMany(static profile => References(profile.Environment)));
                list.Add(new ProjectEnvironmentView(project.Id, project.DisplayName, null, Path.Combine(project.ProjectPath, ".alta", "config.toml"), entries,
                    ProfileViews(profiles),
                    [.. issues.Keys.Where(static reference => reference.Kind == SecretReferenceKind.Secret).Select(static reference => reference.Name)
                        .Distinct(StringComparer.OrdinalIgnoreCase)],
                    [.. issues.Values.OfType<EnvironmentReferenceIssue>().Distinct()]));
            }

            return new("ok", [.. list], [.. spaces.Where(static space => space.Settings.Identity?.Cli is { Count: > 0 })
                .Select(static space => new SpaceEnvironmentView(space.Id, space.Name, ProfileViews(space.Settings.Identity!.Cli!)))]);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new("read_failed", [], []);
        }
    }

    /// <summary>Sets a variable in a project's <c>[env]</c>, writing only that project's configuration.</summary>
    [NeoRpcMethod("setProjectEnvironment")]
    public Task<SecretsActionResult> SetProjectEnvironmentAsync(ProjectEnvironmentWriteRequest request, CancellationToken cancellationToken)
        => EditProjectEnvironmentAsync(request?.ExpectedEpoch, request?.ProjectId, request?.Name, request?.Value, delete: false, cancellationToken);

    /// <summary>Deletes the local entry for a project variable; it does not change any inherited setting.</summary>
    [NeoRpcMethod("deleteProjectEnvironment")]
    public Task<SecretsActionResult> DeleteProjectEnvironmentAsync(ProjectEnvironmentDeleteRequest request, CancellationToken cancellationToken)
        => EditProjectEnvironmentAsync(request?.ExpectedEpoch, request?.ProjectId, request?.Name, null, delete: true, cancellationToken);

    /// <summary>Sets which projects may use a secret of this computer; an empty list allows every project.</summary>
    [NeoRpcMethod("setSecretProjects")]
    public async Task<SecretsActionResult> SetSecretProjectsAsync(SecretProjectsRequest request, CancellationToken cancellationToken)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused) return new(refused, null);
        if (Spaces is not { } spaces) return new("unavailable", null);
        if (!LocalSecretStore.IsName(request!.Name) || request.ProjectIds is null)
            return new("invalid", "Choose a secret and its allowed projects.");
        try
        {
            if (_store!.ListSecrets().FirstOrDefault(secret => string.Equals(secret.Name, request.Name, StringComparison.OrdinalIgnoreCase)) is not { } secret)
                return new("not_found", null);
            var projects = (await spaces.Projects.LoadReadableAsync(cancellationToken).ConfigureAwait(false)).Projects
                .ToLookup(static project => project.Id, StringComparer.OrdinalIgnoreCase);
            var allowed = new List<SecretProject>();
            foreach (var id in request.ProjectIds.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (id is not null && projects[id].FirstOrDefault() is { } project)
                    allowed.Add(new(project.Id, project.Slug));
                else if (secret.Projects.FirstOrDefault(project => string.Equals(project.Id, id, StringComparison.OrdinalIgnoreCase)) is { } previous)
                    allowed.Add(previous); // Keep an old restriction without trusting a request to add an unknown project.
                else
                    return new("invalid", "One of the projects is no longer in the catalog.");
            }

            if (_store.Bindings.SetProjects(request.Name, allowed)) Credentials?.ForgetConfirmations(request.Name);
            return new("ok", null);
        }
        catch (ArgumentException exception) { return new("invalid", exception.Message.Split(" (Parameter", 2)[0]); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new("write_failed", exception.Message);
        }
    }

    private async Task<SecretsActionResult> EditProjectEnvironmentAsync(string? epoch, string? projectId, string? name, string? value, bool delete,
        CancellationToken cancellationToken)
    {
        if (Refuse(epoch) is { } refused) return new(refused, null);
        if (Spaces is not { } spaces || Config is not { } config) return new("unavailable", null);
        if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(name) || !delete && value is null)
            return new("invalid", "Choose a project and give the variable a name and a value.");
        try
        {
            var projects = (await spaces.Projects.LoadReadableAsync(cancellationToken).ConfigureAwait(false)).Projects;
            var project = projects.FirstOrDefault(candidate => string.Equals(projectId, candidate.Id, StringComparison.OrdinalIgnoreCase));
            if (project is null || !Directory.Exists(project.ProjectPath)) return new("not_found", "The project has no folder on this computer.");
            var changed = config.SaveProjectEnvironmentVariable(project.ProjectPath, name, value);
            return delete && !changed ? new("not_found", "The project does not set that variable in its own configuration.") : new("ok", null);
        }
        catch (ArgumentException exception) { return new("invalid", exception.Message.Split(" (Parameter", 2)[0]); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            return new("write_failed", exception.Message);
        }
    }

    private async Task<SecretsList> WithProjectUsageAsync(SecretsList list, CancellationToken cancellationToken)
    {
        if (Spaces is null || Config is not { } config) return list;
        try
        {
            var (projects, spaces) = await ReadProjectsAndSpacesAsync(cancellationToken).ConfigureAwait(false);
            var picker = projects.Select(static project => new SecretProjectView(project.Id, project.DisplayName, null)).ToArray();
            if (list.Secrets.Length == 0) return list with { Projects = picker };
            var usage = new Dictionary<string, HashSet<SecretUsageView>>(StringComparer.OrdinalIgnoreCase);
            foreach (var secret in list.Secrets) usage[secret.Name] = [];
            void Add(IEnumerable<SecretReference> references, SecretUsageView owner)
            {
                foreach (var reference in references)
                    if (reference.Kind == SecretReferenceKind.Secret && usage.TryGetValue(reference.Name, out var used)) used.Add(owner);
            }

            // Read the table as a model so plugin-defined providers' settings are included too, without probing providers.
            try
            {
                if (TomlSerializer.Deserialize<TomlTable>(config.LoadGlobalConfigContent()) is { } document
                    && document.TryGetValue("providers", out var value) && value is TomlTable providers)
                {
                    foreach (var (key, section) in providers)
                    {
                        var owner = new SecretUsageView("provider", key);
                        Add(TextValues(section).SelectMany(SecretReferences.Find), owner);
                        if (section is TomlTable provider && provider.TryGetValue("api_key_secret", out var name)
                            && name is string secret && usage.TryGetValue(secret, out var used)) used.Add(owner);
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or TomlException)
            {
                // An unreadable user file does not hide uses in the catalog.
            }

            foreach (var space in spaces)
                foreach (var profile in space.Settings.Identity?.Cli ?? [])
                    Add(References(profile.Environment), new("space", space.Name));
            var mcp = McpHome is { } home ? new McpManagementService() : null;
            // The servers of the global files once, then those of each project's own files, named with their project.
            void AddMcpServers(string? projectRoot, string? projectName)
            {
                if (mcp is null) return;
                IReadOnlyList<McpManagementServerSnapshot> servers;
                try
                {
                    servers = mcp.RefreshSnapshot(new McpManagementRequest { ProjectDirectory = projectRoot, Home = McpHome!.Value, ProbeWritability = false }).Servers;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
                {
                    return;
                }

                foreach (var server in servers)
                {
                    if (projectName is not null && server.SourceScope != McpManagementScope.Project) continue;
                    var texts = (server.EditableEnv ?? server.Env).Values.Concat((server.EditableHeaders ?? server.Headers).Values)
                        .Concat(server.EditableArgs ?? server.Args).Append(server.EditableUrl ?? server.Url ?? string.Empty);
                    Add(texts.SelectMany(SecretReferences.Find), new("mcp", projectName is null ? server.DisplayName : $"{server.DisplayName} ({projectName})"));
                }
            }

            AddMcpServers(null, null);
            foreach (var project in projects)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var owner = new SecretUsageView("project", project.DisplayName);
                Add(References(config.LoadProjectEnvironment(project.ProjectPath)), owner);
                foreach (var profile in config.LoadProjectCliProfiles(project.ProjectPath))
                    Add(References(profile.Environment), owner);
                AddMcpServers(project.ProjectPath, project.DisplayName);
            }

            return list with
            {
                Projects = picker,
                Secrets = [.. list.Secrets.Select(secret => secret with { UsedBy = [.. usage[secret.Name]] })],
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return list;
        }
    }

    private async Task<(IReadOnlyList<ProjectDescriptor> Projects, IReadOnlyList<SpaceDescriptor> Spaces)> ReadProjectsAndSpacesAsync(CancellationToken cancellationToken)
        => ((await Spaces!.Projects.LoadReadableAsync(cancellationToken).ConfigureAwait(false)).Projects,
            await Spaces.LoadAsync(cancellationToken).ConfigureAwait(false));

    private EnvironmentReferenceIssue? SecretIssue(SecretInfo secret, SecretScope scope)
    {
        if (SecretProject.Refusal(secret.Name, secret.Projects, scope) is { } refusal)
            return new("secret", secret.Name, "restricted", refusal);
        if (Credentials is not { } broker)
            return secret.Provider is null ? null : new("secret", secret.Name, "unavailable", null);
        var availability = broker.GetAvailability(secret.Name);
        return availability?.State switch
        {
            null or SecretAvailabilityState.Ready or SecretAvailabilityState.WaitsForYou => null,
            SecretAvailabilityState.Locked => new("secret", secret.Name, "locked", availability.Message),
            _ => new("secret", secret.Name, "unavailable", availability.Message),
        };
    }

    private static EnvironmentReferenceIssue NotSet(SecretReference reference)
        => new(SecretReferences.PrefixOf(reference.Kind), reference.Name, "not_set", SecretReferences.MissingMessage(reference));

    private static EnvironmentProfileView[] ProfileViews(IReadOnlyList<SpaceCliProfile> profiles)
        => [.. profiles.Select(static profile => new EnvironmentProfileView(profile.Name, [.. profile.Environment.Select(static variable => variable.Key)]))];

    private static IEnumerable<SecretReference> References(IReadOnlyList<KeyValuePair<string, string>> environment)
        => environment.SelectMany(static pair => SecretReferences.Find(pair.Value));

    private static IEnumerable<string> TextValues(object? value) => value switch
    {
        string text => [text],
        TomlTable table => table.Values.SelectMany(TextValues),
        TomlArray array => array.SelectMany(TextValues),
        TomlTableArray tables => tables.SelectMany(TextValues),
        _ => [],
    };
}

/// <summary>A catalog project in the project restriction editor.</summary>
/// <param name="Id">The stable project id.</param>
/// <param name="Name">Its display name.</param>
/// <param name="Icon">Its icon, or null for the usual project icon.</param>
internal sealed record SecretProjectView(string Id, string Name, string? Icon);

/// <summary>Where the configuration names a secret.</summary>
/// <param name="Kind">Provider, space or project.</param>
/// <param name="Name">Its name.</param>
internal sealed record SecretUsageView(string Kind, string Name);

/// <summary>Changes the list of allowed projects.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="Name">The secret.</param>
/// <param name="ProjectIds">Catalog identifiers; empty for all projects.</param>
internal sealed record SecretProjectsRequest(string ExpectedEpoch, string Name, string[] ProjectIds);

/// <summary>The environment settings and CLI profiles of the catalog.</summary>
/// <param name="Status">Whether the environment settings were read.</param>
/// <param name="Projects">Projects that can be configured, including projects with no settings yet.</param>
/// <param name="Spaces">Spaces that give their sessions CLI profiles.</param>
internal sealed record ProjectEnvironmentList(string Status, ProjectEnvironmentView[] Projects, SpaceEnvironmentView[] Spaces);

/// <summary>A project's configured environment without resolved values.</summary>
/// <param name="Id">The project id.</param>
/// <param name="Name">Its display name.</param>
/// <param name="Icon">Its icon, or null for the usual project icon.</param>
/// <param name="Path">The path to its local configuration file.</param>
/// <param name="Variables">The effective <c>[env]</c> variables, with the written expressions.</param>
/// <param name="Profiles">Its effective CLI profiles, by name and variable names.</param>
/// <param name="Secrets">The names of secrets referenced in its environment or profiles.</param>
/// <param name="Issues">Unavailable references, without resolving values or asking for confirmation.</param>
internal sealed record ProjectEnvironmentView(string Id, string Name, string? Icon, string Path, ProjectEnvironmentVariableView[] Variables,
    EnvironmentProfileView[] Profiles, string[] Secrets, EnvironmentReferenceIssue[] Issues);

/// <summary>A configured entry of a project's <c>[env]</c> table.</summary>
/// <param name="Name">The name of the variable.</param>
/// <param name="Value">The expression as written, never what it resolves to.</param>
/// <param name="Local">Whether the variable is set in this project's own file.</param>
/// <param name="Issues">The references not available on this computer for the project.</param>
internal sealed record ProjectEnvironmentVariableView(string Name, string Value, bool Local, EnvironmentReferenceIssue[] Issues);

/// <summary>A reference that is not available for a project's environment.</summary>
/// <param name="Kind">The prefix of the reference: <c>secret</c>, <c>var</c> or <c>env</c>.</param>
/// <param name="Name">The referenced name.</param>
/// <param name="State"><c>not_set</c>, <c>locked</c>, <c>restricted</c> or <c>unavailable</c>.</param>
/// <param name="Message">What is wrong when details are known.</param>
internal sealed record EnvironmentReferenceIssue(string Kind, string Name, string State, string? Message);

/// <summary>A CLI profile and the environment variable names it sets.</summary>
/// <param name="Name">The tool's name.</param>
/// <param name="Variables">The names, never their values.</param>
internal sealed record EnvironmentProfileView(string Name, string[] Variables);

/// <summary>The CLI profiles of a space.</summary>
/// <param name="Id">The identifier of the space.</param>
/// <param name="Name">Its display name.</param>
/// <param name="Profiles">The CLI profiles of the space's Identity.</param>
internal sealed record SpaceEnvironmentView(string Id, string Name, EnvironmentProfileView[] Profiles);

/// <summary>Sets a variable in the project's own <c>[env]</c>.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="ProjectId">The project.</param>
/// <param name="Name">The name of the variable.</param>
/// <param name="Value">The expression to write.</param>
internal sealed record ProjectEnvironmentWriteRequest(string ExpectedEpoch, string ProjectId, string Name, string Value);

/// <summary>Deletes a variable from the project's own <c>[env]</c>.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="ProjectId">The project.</param>
/// <param name="Name">The name of the variable.</param>
internal sealed record ProjectEnvironmentDeleteRequest(string ExpectedEpoch, string ProjectId, string Name);
