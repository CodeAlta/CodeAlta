namespace CodeAlta.Agent;

/// <summary>
/// The environment variables a session gives every process it starts (its shell commands, its provider's CLI, its
/// background jobs), over the environment of the application: a variable named here replaces the inherited one,
/// the others are inherited as they are. It is fixed when the session attaches, so that a running session keeps it.
/// </summary>
/// <remarks>An instance is immutable; two environments with the same variables are equal.</remarks>
public sealed class AgentProcessEnvironment : IEquatable<AgentProcessEnvironment>
{
    private readonly KeyValuePair<string, string>[] _variables;

    /// <summary>Initializes an environment.</summary>
    /// <param name="variables">The variables; a name given twice keeps its last value.</param>
    /// <exception cref="ArgumentNullException"><paramref name="variables"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A name is empty or holds <c>=</c> or a control character, or a value holds a NUL character.</exception>
    public AgentProcessEnvironment(IEnumerable<KeyValuePair<string, string>> variables)
    {
        ArgumentNullException.ThrowIfNull(variables);
        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in variables)
        {
            if (string.IsNullOrEmpty(name) || name.Contains('=', StringComparison.Ordinal) || name.Any(char.IsControl))
                throw new ArgumentException($"'{name}' is not the name of an environment variable.", nameof(variables));
            ArgumentNullException.ThrowIfNull(value, nameof(variables));
            if (value.Contains('\0', StringComparison.Ordinal)) throw new ArgumentException($"The value of {name} holds a NUL character.", nameof(variables));
            map[name] = value;
        }

        _variables = [.. map];
    }

    /// <summary>Gets the environment that sets nothing: the processes inherit the environment of the application.</summary>
    public static AgentProcessEnvironment Empty { get; } = new([]);

    /// <summary>Gets the variables, by their names in ordinal order.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Variables => _variables;

    /// <summary>Gets whether no variable is set.</summary>
    public bool IsEmpty => _variables.Length == 0;

    /// <summary>Gets the value of a variable.</summary>
    /// <param name="name">The name of the variable.</param>
    /// <returns>Its value, or <see langword="null"/> when this environment does not set it.</returns>
    public string? Get(string name)
    {
        foreach (var (key, value) in _variables)
        {
            if (string.Equals(key, name, StringComparison.Ordinal)) return value;
        }

        return null;
    }

    /// <summary>Sets the variables in the environment of a process about to start.</summary>
    /// <param name="environment">The environment of the process, such as <see cref="System.Diagnostics.ProcessStartInfo.Environment"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="environment"/> is <see langword="null"/>.</exception>
    public void ApplyTo(IDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        foreach (var (name, value) in _variables) environment[name] = value;
    }

    /// <inheritdoc />
    public bool Equals(AgentProcessEnvironment? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        if (_variables.Length != other._variables.Length) return false;
        for (var index = 0; index < _variables.Length; index++)
        {
            if (!string.Equals(_variables[index].Key, other._variables[index].Key, StringComparison.Ordinal)
                || !string.Equals(_variables[index].Value, other._variables[index].Value, StringComparison.Ordinal)) return false;
        }

        return true;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as AgentProcessEnvironment);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var (name, value) in _variables)
        {
            hash.Add(name, StringComparer.Ordinal);
            hash.Add(value, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    /// <summary>Lists the names of the variables, never their values, which may be private.</summary>
    /// <returns>The names, separated by commas.</returns>
    public override string ToString() => string.Join(", ", _variables.Select(static variable => variable.Key));
}
