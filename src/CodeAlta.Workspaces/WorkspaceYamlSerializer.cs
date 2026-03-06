using SharpYaml;

namespace CodeAlta.Workspaces;

/// <summary>
/// Serializes and deserializes workspace YAML descriptors.
/// </summary>
public sealed class WorkspaceYamlSerializer
{
    /// <summary>
    /// Initializes a new instance of the <see cref="WorkspaceYamlSerializer"/> class.
    /// </summary>
    public WorkspaceYamlSerializer()
    {
    }

    /// <summary>
    /// Deserializes a workspace descriptor from YAML.
    /// </summary>
    /// <param name="yaml">The YAML text.</param>
    /// <returns>The workspace descriptor.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="yaml"/> is <see langword="null"/>.</exception>
    public WorkspaceDescriptor DeserializeWorkspace(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        var descriptor = YamlSerializer.Deserialize<WorkspaceDescriptor>(yaml) ?? new WorkspaceDescriptor();

        descriptor.Projects ??= [];
        descriptor.Tags ??= [];
        return descriptor;
    }

    /// <summary>
    /// Deserializes a project descriptor from YAML.
    /// </summary>
    /// <param name="yaml">The YAML text.</param>
    /// <returns>The project descriptor.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="yaml"/> is <see langword="null"/>.</exception>
    public ProjectDescriptor DeserializeProject(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        return YamlSerializer.Deserialize<ProjectDescriptor>(yaml) ?? new ProjectDescriptor();
    }

    /// <summary>
    /// Deserializes a machine profile from YAML.
    /// </summary>
    /// <param name="yaml">The YAML text.</param>
    /// <returns>The machine profile.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="yaml"/> is <see langword="null"/>.</exception>
    public MachineProfile DeserializeMachineProfile(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);

        var profile = YamlSerializer.Deserialize<MachineProfile>(yaml) ?? new MachineProfile();
        profile.WorkspaceCheckoutRoots ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        profile.ProjectOverrides ??= new Dictionary<string, MachineProjectOverride>(StringComparer.OrdinalIgnoreCase);
        return profile;
    }

    /// <summary>
    /// Serializes a workspace descriptor to YAML.
    /// </summary>
    /// <param name="descriptor">The workspace descriptor.</param>
    /// <returns>Serialized YAML text.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="descriptor"/> is <see langword="null"/>.</exception>
    public string SerializeWorkspace(WorkspaceDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return YamlSerializer.Serialize(descriptor);
    }
}
