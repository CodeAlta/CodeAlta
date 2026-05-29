using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugins;

/// <summary>
/// Describes a runtime-owned contribution registration.
/// </summary>
public sealed record PluginContributionRegistration
{
    /// <summary>Gets the contribution handle.</summary>
    public required PluginContributionHandle Handle { get; init; }

    /// <summary>Gets the contribution instance.</summary>
    public required object Contribution { get; init; }

    /// <summary>Gets the source plugin scope.</summary>
    public required PluginScope Scope { get; init; }

    /// <summary>Gets the project id for project-scoped contributions, when known.</summary>
    public string? ScopeProjectId { get; init; }

    /// <summary>Gets the project path for project-scoped contributions, when known.</summary>
    public string? ScopeProjectPath { get; init; }

    /// <summary>Gets the load unit kind that supplied the contribution.</summary>
    public PluginLoadUnitKind LoadUnitKind { get; init; } = PluginLoadUnitKind.Source;
}

/// <summary>
/// Describes a contribution summary for diagnostics and management UI display.
/// </summary>
public sealed record PluginContributionSummary
{
    /// <summary>Gets the contribution handle.</summary>
    public required PluginContributionHandle Handle { get; init; }

    /// <summary>Gets the plugin load unit kind.</summary>
    public required PluginLoadUnitKind LoadUnitKind { get; init; }

    /// <summary>Gets the plugin scope.</summary>
    public required PluginScope Scope { get; init; }

    /// <summary>Gets the project id for project-scoped contributions, when known.</summary>
    public string? ScopeProjectId { get; init; }

    /// <summary>Gets the project path for project-scoped contributions, when known.</summary>
    public string? ScopeProjectPath { get; init; }

    /// <summary>Gets the contribution implementation type name.</summary>
    public required string ContributionTypeName { get; init; }
}

/// <summary>
/// Stores plugin-owned contributions and removes them by plugin owner during deactivation.
/// </summary>
public sealed class PluginContributionRegistry
{
    private readonly object _gate = new();
    private readonly List<PluginContributionRegistration> _registrations = [];
    private readonly List<PluginRuntimeDiagnostic> _diagnostics = [];

    /// <summary>
    /// Registers contribution instances for a plugin.
    /// </summary>
    /// <param name="descriptor">The plugin descriptor.</param>
    /// <param name="scope">The plugin scope.</param>
    /// <param name="scopeProjectId">The scoped project id, when known.</param>
    /// <param name="scopeProjectPath">The scoped project path, when known.</param>
    /// <param name="point">The contribution point.</param>
    /// <param name="contributions">The contribution instances.</param>
    /// <param name="activationGeneration">The activation generation.</param>
    /// <returns>The created contribution registrations.</returns>
    public IReadOnlyList<PluginContributionRegistration> Register(
        PluginDescriptor descriptor,
        PluginScope scope,
        string? scopeProjectId,
        string? scopeProjectPath,
        PluginPoint point,
        IEnumerable<object> contributions,
        int activationGeneration)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(contributions);
        var loadUnitKind = GetLoadUnitKind(descriptor);
        var created = contributions
            .Select((contribution, ordinal) => new PluginContributionRegistration
            {
                Handle = PluginContributionHandle.Create(
                    descriptor.RuntimeKey,
                    descriptor.TypeName,
                    point,
                    GetNaturalName(contribution),
                    ordinal,
                    activationGeneration),
                Contribution = contribution,
                Scope = scope,
                ScopeProjectId = scopeProjectId,
                ScopeProjectPath = scopeProjectPath,
                LoadUnitKind = loadUnitKind,
            })
            .ToArray();

        lock (_gate)
        {
            AddConflictDiagnostics(descriptor, created);
            _registrations.AddRange(created);
            SortRegistrations(_registrations);
        }

        return created;
    }

    /// <summary>
    /// Gets a snapshot of registered contributions.
    /// </summary>
    /// <returns>Contribution registrations in deterministic runtime order.</returns>
    public IReadOnlyList<PluginContributionRegistration> GetSnapshot()
    {
        lock (_gate)
        {
            return _registrations.ToArray();
        }
    }

    /// <summary>
    /// Gets a contribution summary snapshot for diagnostics and management UI display.
    /// </summary>
    /// <returns>Contribution summaries in deterministic runtime order.</returns>
    public IReadOnlyList<PluginContributionSummary> GetContributionSummaries()
    {
        lock (_gate)
        {
            return _registrations.Select(static registration => new PluginContributionSummary
            {
                Handle = registration.Handle,
                LoadUnitKind = registration.LoadUnitKind,
                Scope = registration.Scope,
                ScopeProjectId = registration.ScopeProjectId,
                ScopeProjectPath = registration.ScopeProjectPath,
                ContributionTypeName = registration.Contribution.GetType().FullName ?? registration.Contribution.GetType().Name,
            }).ToArray();
        }
    }

    /// <summary>
    /// Gets contribution conflict/override diagnostics recorded during registration.
    /// </summary>
    /// <returns>Diagnostic snapshot.</returns>
    public IReadOnlyList<PluginRuntimeDiagnostic> GetDiagnostics()
    {
        lock (_gate)
        {
            return _diagnostics.ToArray();
        }
    }

    /// <summary>
    /// Removes all contributions owned by a plugin runtime key.
    /// </summary>
    /// <param name="pluginRuntimeKey">The plugin runtime key.</param>
    /// <returns>The removed contributions.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="pluginRuntimeKey"/> is empty.</exception>
    public IReadOnlyList<PluginContributionRegistration> RemoveByPlugin(string pluginRuntimeKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginRuntimeKey);
        lock (_gate)
        {
            var removed = _registrations
                .Where(registration => string.Equals(registration.Handle.PluginRuntimeKey, pluginRuntimeKey, StringComparison.Ordinal))
                .ToArray();
            _registrations.RemoveAll(registration => string.Equals(registration.Handle.PluginRuntimeKey, pluginRuntimeKey, StringComparison.Ordinal));
            return removed;
        }
    }

    /// <summary>
    /// Determines whether a registration applies to a project.
    /// </summary>
    /// <param name="registration">The registration.</param>
    /// <param name="projectId">The project id, when known.</param>
    /// <param name="projectPath">The project path, when known.</param>
    /// <returns><see langword="true"/> when the registration applies to the supplied project.</returns>
    public static bool AppliesToProject(PluginContributionRegistration registration, string? projectId, string? projectPath)
    {
        ArgumentNullException.ThrowIfNull(registration);
        if (registration.Scope == PluginScope.Global)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(registration.ScopeProjectId) &&
            !string.IsNullOrWhiteSpace(projectId) &&
            string.Equals(registration.ScopeProjectId, projectId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(registration.ScopeProjectPath) &&
            !string.IsNullOrWhiteSpace(projectPath))
        {
            return string.Equals(
                Path.GetFullPath(registration.ScopeProjectPath),
                Path.GetFullPath(projectPath),
                StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static void SortRegistrations(List<PluginContributionRegistration> registrations)
    {
        registrations.Sort(static (left, right) =>
        {
            var kind = GetLoadUnitKindOrder(left.LoadUnitKind).CompareTo(GetLoadUnitKindOrder(right.LoadUnitKind));
            if (kind != 0)
            {
                return kind;
            }

            var scope = GetScopeOrder(left.Scope).CompareTo(GetScopeOrder(right.Scope));
            if (scope != 0)
            {
                return scope;
            }

            var point = left.Handle.Point.CompareTo(right.Handle.Point);
            if (point != 0)
            {
                return point;
            }

            var order = GetContributionOrder(left.Contribution).CompareTo(GetContributionOrder(right.Contribution));
            if (order != 0)
            {
                return order;
            }

            var plugin = string.Compare(left.Handle.PluginRuntimeKey, right.Handle.PluginRuntimeKey, StringComparison.Ordinal);
            if (plugin != 0)
            {
                return plugin;
            }

            return left.Handle.Ordinal.CompareTo(right.Handle.Ordinal);
        });
    }

    private void AddConflictDiagnostics(PluginDescriptor descriptor, IReadOnlyList<PluginContributionRegistration> created)
    {
        foreach (var registration in created)
        {
            foreach (var key in GetConflictKeys(registration).DistinctBy(static key => key.Value, StringComparer.OrdinalIgnoreCase))
            {
                var conflict = _registrations.FirstOrDefault(existing =>
                    existing.Handle.Point == registration.Handle.Point &&
                    GetConflictKeys(existing).Any(existingKey => string.Equals(existingKey.Value, key.Value, StringComparison.OrdinalIgnoreCase)));
                if (conflict is null)
                {
                    continue;
                }

                _diagnostics.Add(new PluginRuntimeDiagnostic
                {
                    Severity = PluginDiagnosticSeverity.Warning,
                    Source = PluginRuntimeDiagnosticSource.Contribution,
                    Message = $"Contribution '{key.DisplayName}' for point '{registration.Handle.Point}' from plugin '{registration.Handle.PluginRuntimeKey}' shadows contribution from plugin '{conflict.Handle.PluginRuntimeKey}'.",
                    RuntimeKey = descriptor.RuntimeKey,
                    Metadata = new Dictionary<string, string>
                    {
                        ["Point"] = registration.Handle.Point.ToString(),
                        ["NaturalName"] = registration.Handle.NaturalName ?? string.Empty,
                        ["ConflictKind"] = key.Kind,
                        ["ConflictKey"] = key.Value,
                        ["ShadowedPluginRuntimeKey"] = conflict.Handle.PluginRuntimeKey,
                        ["ShadowingPluginRuntimeKey"] = registration.Handle.PluginRuntimeKey,
                    },
                });
            }
        }
    }

    private static IEnumerable<ContributionConflictKey> GetConflictKeys(PluginContributionRegistration registration)
    {
        switch (registration.Contribution)
        {
            case PluginCommandContribution command:
                yield return new ContributionConflictKey("command", $"command:{command.Name}", command.Name);
                if (command.KeyBinding?.DisplayText is { Length: > 0 } displayText)
                {
                    yield return new ContributionConflictKey("keybinding", $"keybinding:{displayText}", displayText);
                }

                if (command.KeyBinding?.Gesture is { } gesture)
                {
                    yield return new ContributionConflictKey("keybinding", $"keybinding-gesture:{gesture}", gesture.ToString() ?? string.Empty);
                }

                if (command.KeyBinding?.Sequence is { } sequence)
                {
                    yield return new ContributionConflictKey("keybinding", $"keybinding-sequence:{sequence}", sequence.ToString() ?? string.Empty);
                }

                yield break;
            case PluginAgentToolContribution tool:
                yield return new ContributionConflictKey("tool", $"tool:{tool.Definition.Spec.Name}", tool.Definition.Spec.Name);
                yield break;
            case PluginAltaCommandContribution alta:
                yield return new ContributionConflictKey("alta-command", $"alta:{alta.Path}", alta.Path);
                yield break;
            case PluginSystemPromptContribution prompt when !string.IsNullOrWhiteSpace(prompt.Title):
                yield return new ContributionConflictKey("prompt-part", $"prompt:{prompt.Channel}:{prompt.Title}", prompt.Title!);
                yield break;
            case PluginPromptEditorContribution promptEditor:
                yield return new ContributionConflictKey("prompt-editor", $"prompt-editor:{promptEditor.Name}", promptEditor.Name);
                yield break;
            case PluginResourceContribution resource:
                yield return new ContributionConflictKey("resource", $"resource:{resource.Kind}:{resource.Path}", $"{resource.Kind}:{resource.Path}");
                yield break;
            case PluginRendererContribution renderer:
                yield return new ContributionConflictKey("ui-renderer", $"ui:{renderer.Region}:renderer:{renderer.Target}", $"{renderer.Region}:{renderer.Target}");
                yield break;
            case PluginUiContribution ui:
                yield return new ContributionConflictKey("ui-region", $"ui:{ui.Region}:{ui.Name ?? ui.GetType().Name}", $"{ui.Region}:{ui.Name ?? ui.GetType().Name}");
                yield break;
        }

        if (registration.Handle.NaturalName is not null)
        {
            yield return new ContributionConflictKey("natural-name", $"{registration.Handle.Point}:{registration.Handle.NaturalName}", registration.Handle.NaturalName);
        }
    }

    private static PluginLoadUnitKind GetLoadUnitKind(PluginDescriptor descriptor)
    {
        if (descriptor.Metadata.TryGetValue("PluginKind", out var value) &&
            Enum.TryParse<PluginLoadUnitKind>(value, ignoreCase: true, out var kind))
        {
            return kind;
        }

        return PluginLoadUnitKind.Source;
    }

    private static int GetLoadUnitKindOrder(PluginLoadUnitKind kind)
        => kind == PluginLoadUnitKind.BuiltIn ? 0 : 1;

    private static int GetScopeOrder(PluginScope scope)
        => scope == PluginScope.Global ? 1 : 2;

    private static int GetContributionOrder(object contribution)
        => contribution switch
        {
            PluginStartupContribution startup => startup.Order,
            PluginCommandContribution command => command.Order,
            PluginSystemPromptContribution prompt => prompt.Order,
            PluginPromptProcessorContribution processor => processor.Order,
            PluginPromptEditorContribution promptEditor => promptEditor.Order,
            PluginUiContribution ui => ui.Order,
            PluginResourceContribution resource => resource.Precedence,
            PluginAltaCommandContribution alta => alta.Order,
            PluginCompactionContribution compaction => compaction.Order,
            _ => 0,
        };

    private static string? GetNaturalName(object contribution)
        => contribution switch
        {
            PluginStartupContribution startup => startup.Name,
            PluginCommandContribution command => command.Name,
            PluginAgentToolContribution tool => tool.Definition.Spec.Name,
            PluginAltaCommandContribution alta => alta.Path,
            PluginSystemPromptContribution prompt => prompt.Title,
            PluginPromptProcessorContribution _ => null,
            PluginPromptEditorContribution promptEditor => promptEditor.Name,
            PluginUiContribution ui => ui.Name,
            PluginResourceContribution resource => resource.Path,
            PluginCompactionContribution _ => null,
            _ => null,
        };

    private sealed record ContributionConflictKey(string Kind, string Value, string DisplayName);
}
