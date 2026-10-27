using System.Text.Json;

namespace CodeAlta.Agent.ModelCatalog;

/// <summary>
/// The public price of a model per million tokens, in US dollars: the one models.dev gives its provider, else its vendor
/// (<see cref="ModelsDevCatalogService.Describe"/>), as <see cref="AgentModelInfo.Capabilities"/> carries it.
/// </summary>
/// <param name="Input">The price of a million input tokens neither read from nor written to the prompt cache.</param>
/// <param name="Output">The price of a million output tokens, reasoning included.</param>
/// <param name="CacheRead">The price of a million input tokens read from the prompt cache; null when the catalog gives none.</param>
/// <param name="CacheWrite">The price of a million input tokens written to the prompt cache; null when the catalog gives none.</param>
/// <remarks>
/// It is the base price: models.dev gives some models a higher one for requests above a size of context (GPT-6 Astra
/// above 272k tokens), which needs the input of each request to apply.
/// </remarks>
public sealed record AgentModelPrice(decimal Input, decimal Output, decimal? CacheRead, decimal? CacheWrite)
{
    /// <summary>Reads the price a model carries.</summary>
    /// <param name="model">The model, described by models.dev.</param>
    /// <returns>The price, or <see langword="null"/> when the model has no input and output price.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="model"/> is <see langword="null"/>.</exception>
    public static AgentModelPrice? Of(AgentModelInfo model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var capabilities = model.Capabilities;
        return Read(capabilities, AgentModelMetadataEnricher.InputCostKey) is { } input && Read(capabilities, AgentModelMetadataEnricher.OutputCostKey) is { } output
            ? new AgentModelPrice(input, output, Read(capabilities, AgentModelMetadataEnricher.CacheReadCostKey), Read(capabilities, AgentModelMetadataEnricher.CacheWriteCostKey))
            : null;
    }

    /// <summary>
    /// Estimates what tokens cost at this price. Input read from or written to the prompt cache is priced as input when
    /// the catalog gives no price for it.
    /// </summary>
    /// <param name="inputTokens">The input tokens, those read from or written to the prompt cache included.</param>
    /// <param name="cacheReadTokens">The part of the input read from the prompt cache.</param>
    /// <param name="cacheWriteTokens">The part of the input written to the prompt cache.</param>
    /// <param name="outputTokens">The output tokens, reasoning included.</param>
    /// <returns>The cost in US dollars.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A count is negative.</exception>
    public decimal Estimate(long inputTokens, long cacheReadTokens, long cacheWriteTokens, long outputTokens)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(inputTokens);
        ArgumentOutOfRangeException.ThrowIfNegative(cacheReadTokens);
        ArgumentOutOfRangeException.ThrowIfNegative(cacheWriteTokens);
        ArgumentOutOfRangeException.ThrowIfNegative(outputTokens);
        var uncached = Math.Max(0, inputTokens - cacheReadTokens - cacheWriteTokens);
        return (uncached * Input + cacheReadTokens * (CacheRead ?? Input) + cacheWriteTokens * (CacheWrite ?? Input) + outputTokens * Output) / 1_000_000m;
    }

    private static decimal? Read(IReadOnlyDictionary<string, object?>? capabilities, string key)
    {
        if (capabilities is null || !capabilities.TryGetValue(key, out var raw)) return null;
        decimal? value = raw switch
        {
            decimal number => number,
            double number when double.IsFinite(number) => (decimal)number,
            long number => number,
            int number => number,
            JsonElement { ValueKind: JsonValueKind.Number } element when element.TryGetDecimal(out var number) => number,
            _ => null,
        };
        return value is >= 0 ? value : null;
    }
}
