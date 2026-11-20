namespace CodeAlta.Catalog;

public sealed partial class CodeAltaConfigStore
{
    /// <summary>Reads the valid values as written in a project's <c>[env]</c>, with those of its monorepo underneath.</summary>
    /// <param name="projectRoot">The folder of the project; null for no project.</param>
    /// <returns>The names and expressions, not resolved; empty when none are set or the file cannot be read.</returns>
    public IReadOnlyList<KeyValuePair<string, string>> LoadProjectEnvironment(string? projectRoot)
        => LoadProjectEnvironment(projectRoot, includeInherited: true);

    /// <summary>Reads the valid values as written in a project's <c>[env]</c>.</summary>
    /// <param name="projectRoot">The folder of the project; null for no project.</param>
    /// <param name="includeInherited">Whether to include variables inherited from the project's monorepo.</param>
    /// <returns>The names and expressions, not resolved; empty when none are set or the file cannot be read.</returns>
    public IReadOnlyList<KeyValuePair<string, string>> LoadProjectEnvironment(string? projectRoot, bool includeInherited)
    {
        if (string.IsNullOrWhiteSpace(projectRoot)) return [];
        try
        {
            var document = includeInherited ? ReadEffectiveProjectDocument(projectRoot) : ReadSharedProjectDocument(projectRoot);
            if (document.Env is not { Count: > 0 } table) return [];
            var variables = new List<KeyValuePair<string, string>>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, raw) in table)
            {
                if (raw is string value && SpaceCliProfile.VariableProblem(name, value) is null && seen.Add(name))
                    variables.Add(new(name, value));
            }
            return variables;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return [];
        }
    }

    /// <summary>Sets or removes a variable in a project's own <c>[env]</c> without rewriting other settings or comments.</summary>
    /// <param name="projectRoot">The folder of the project.</param>
    /// <param name="name">The name of the variable.</param>
    /// <param name="value">The value as written, or null to remove the local entry.</param>
    /// <returns>True when the file changed.</returns>
    /// <exception cref="ArgumentException">The folder, name or value is invalid.</exception>
    /// <exception cref="InvalidOperationException">The store is read-only.</exception>
    /// <exception cref="InvalidDataException">The file is not valid TOML or writes <c>[env]</c> in a form not edited here.</exception>
    /// <exception cref="IOException">The file cannot be read or written.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the file is denied.</exception>
    public bool SaveProjectEnvironmentVariable(string projectRoot, string name, string? value)
    {
        var path = GetProjectConfigPath(projectRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        // Validate deletion the same way as a write; an environment reference is valid for a secret-looking name.
        if (SpaceCliProfile.VariableProblem(name, value ?? "${env:NAME}") is { } problem)
            throw new ArgumentException(problem);
        ThrowIfReadOnly();
        try
        {
            return EditConfigFile(path, content =>
            {
                var table = ParseLoadedContent(path, content).Env;
                var key = table?.Keys.FirstOrDefault(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase));
                var edited = ConfigTableSyntax.SetText(content, ["env"], key ?? name, value);
                if (value is null && key is not null && edited == content)
                    throw new InvalidDataException("[env] is written in a form CodeAlta does not edit: change it by hand, or make it a table.");
                return edited;
            });
        }
        finally
        {
            Written();
        }
    }
}
