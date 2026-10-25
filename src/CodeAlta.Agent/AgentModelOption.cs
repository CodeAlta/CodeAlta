namespace CodeAlta.Agent;

/// <summary>
/// A choice of a model other than its reasoning effort, such as the size of its context or a fast mode, as its provider
/// offers it. A provider remembers the value of each option for each of its models, and sends it with every turn of
/// the model; a model without options offers none (<see cref="AgentModelInfo.Options"/> is null or empty).
/// </summary>
/// <param name="Id">The identifier the provider knows the option by, such as <c>context</c> or <c>fast</c>.</param>
/// <param name="Name">The name the window shows.</param>
/// <param name="Values">The values the option takes, in the order the provider gives them.</param>
/// <param name="Value">The value the option has now, one of <paramref name="Values"/>.</param>
public sealed record AgentModelOption(string Id, string Name, IReadOnlyList<AgentModelOptionValue> Values, string Value)
{
    /// <summary>Gets a description of the option, or <see langword="null"/>.</summary>
    public string? Description { get; init; }

    /// <summary>Gets whether the option is turned on or off: its values are <c>false</c> and <c>true</c>.</summary>
    public bool IsToggle => Values.Count == 2 &&
        Values.Any(static value => string.Equals(value.Value, "true", StringComparison.OrdinalIgnoreCase)) &&
        Values.Any(static value => string.Equals(value.Value, "false", StringComparison.OrdinalIgnoreCase));
}

/// <summary>A value of a <see cref="AgentModelOption"/>.</summary>
/// <param name="Value">The value the provider takes.</param>
/// <param name="Name">The name the window shows, or <see langword="null"/> for the value itself.</param>
public sealed record AgentModelOptionValue(string Value, string? Name = null);

/// <summary>
/// A model catalog whose models have options (<see cref="AgentModelInfo.Options"/>) that a user can set: implemented by
/// the runtime of a provider that sends them with the turns of a model.
/// </summary>
public interface IModelProviderModelOptions
{
    /// <summary>
    /// Sets an option of a model: the value the provider sends with the turns of the model from now on, in every
    /// session of the provider.
    /// </summary>
    /// <param name="modelId">The model.</param>
    /// <param name="optionId">An option of the model, one of its <see cref="AgentModelInfo.Options"/>.</param>
    /// <param name="value">One of the values of the option.</param>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns>The model with its options as they are now, or <see langword="null"/> when the provider does not list the model.</returns>
    /// <exception cref="ArgumentException">The option or the value is not one the model offers.</exception>
    Task<AgentModelInfo?> SetModelOptionAsync(string modelId, string optionId, string value, CancellationToken cancellationToken = default);
}
