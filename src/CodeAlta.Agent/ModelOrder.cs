namespace CodeAlta.Agent;

/// <summary>The order the models of a provider are listed in.</summary>
public enum ModelOrder
{
    /// <summary>The order of the provider (configuration value <c>provider</c>).</summary>
    Provider,

    /// <summary>By name, then by id (configuration value <c>name</c>).</summary>
    Name,

    /// <summary>
    /// By name, the highest version first among the names that differ only by their numbers (configuration value
    /// <c>name_version</c>, see <see cref="ModelNameComparer"/>).
    /// </summary>
    NameVersion,
}

/// <summary>Reads, writes and applies a <see cref="ModelOrder"/>.</summary>
public static class ModelOrdering
{
    /// <summary>Gets the configuration values of the orders, in the order of <see cref="ModelOrder"/>.</summary>
    public static IReadOnlyList<string> Values { get; } = ["provider", "name", "name_version"];

    /// <summary>Reads a configuration value: <c>provider</c>, <c>name</c> or <c>name_version</c>, in any case.</summary>
    /// <param name="value">The value.</param>
    /// <param name="order">The order it names.</param>
    /// <returns>False when the value names no order.</returns>
    public static bool TryParse(string? value, out ModelOrder order)
    {
        var trimmed = value?.Trim();
        for (var i = 0; i < Values.Count; i++)
        {
            if (string.Equals(Values[i], trimmed, StringComparison.OrdinalIgnoreCase))
            {
                order = (ModelOrder)i;
                return true;
            }
        }

        order = ModelOrder.Provider;
        return false;
    }

    /// <summary>Gets the configuration value of an order.</summary>
    /// <param name="order">The order.</param>
    /// <returns>Its value, such as <c>name_version</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="order"/> is not an order.</exception>
    public static string Format(ModelOrder order)
        => order is >= ModelOrder.Provider and <= ModelOrder.NameVersion ? Values[(int)order] : throw new ArgumentOutOfRangeException(nameof(order), order, "Not a model order.");

    /// <summary>
    /// The order a provider's settings give: <c>models_order</c> when it names one, else the older <c>sort_models</c>
    /// (<c>true</c> is <see cref="ModelOrder.Name"/>, <c>false</c> <see cref="ModelOrder.Provider"/>), else the default.
    /// </summary>
    /// <param name="modelsOrder">The value of <c>models_order</c>, or null.</param>
    /// <param name="sortModels">The value of <c>sort_models</c>, or null.</param>
    /// <param name="fallback">The order of the provider's type.</param>
    public static ModelOrder Resolve(string? modelsOrder, bool? sortModels, ModelOrder fallback)
        => TryParse(modelsOrder, out var order) ? order
            : sortModels is { } sort ? sort ? ModelOrder.Name : ModelOrder.Provider
            : fallback;

    /// <summary>Puts models in an order.</summary>
    /// <param name="models">The models, in the order of the provider; not changed.</param>
    /// <param name="order">The order.</param>
    /// <returns><paramref name="models"/> itself for <see cref="ModelOrder.Provider"/>, else a new list.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="models"/> is null.</exception>
    public static IReadOnlyList<AgentModelInfo> Apply(IReadOnlyList<AgentModelInfo> models, ModelOrder order)
    {
        ArgumentNullException.ThrowIfNull(models);
        return order switch
        {
            ModelOrder.Name => [.. models
                .OrderBy(static model => model.DisplayName ?? model.Id, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static model => model.Id, StringComparer.OrdinalIgnoreCase)],
            ModelOrder.NameVersion => ModelNameComparer.Sort(models),
            _ => models,
        };
    }
}
