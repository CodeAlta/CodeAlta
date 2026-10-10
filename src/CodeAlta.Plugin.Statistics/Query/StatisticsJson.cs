using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using CodeAlta.Plugin.Statistics.History;

namespace CodeAlta.Plugin.Statistics.Query;

/// <summary>
/// The JSON of the results of the statistics: camelCase names, enums as camelCase text, null properties left out. A canvas and
/// <c>alta statistics</c> read the same shapes; the names are stable.
/// </summary>
public static class StatisticsJson
{
    /// <summary>Writes a result as JSON.</summary>
    /// <typeparam name="T">The type of the result: one of the records of <c>CodeAlta.Plugin.Statistics.Query</c>, or a <see cref="StatisticsStatus"/>.</typeparam>
    /// <param name="value">The result.</param>
    /// <returns>The JSON text on one line.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
    /// <exception cref="NotSupportedException"><typeparamref name="T"/> is not a type of the statistics.</exception>
    public static string Serialize<T>(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return JsonSerializer.Serialize(value, (JsonTypeInfo<T>)StatisticsResultJsonContext.Default.GetTypeInfo(typeof(T))!);
    }

    /// <summary>Reads a request from JSON, as a page sends it: camelCase names, enums as text in any case.</summary>
    /// <param name="json">The JSON: <c>{"period":"30d","frequency":"week","comparison":"previousPeriod","filter":{"project":"CodeAlta"},"limit":10}</c>.</param>
    /// <returns>The request; an empty one for <c>null</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="json"/> is not a request.</exception>
    public static StatisticsRequest ParseRequest(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            var request = JsonSerializer.Deserialize(json, StatisticsResultJsonContext.Default.StatisticsRequest) ?? new StatisticsRequest();
            // The deserializer does not run the initializers of the properties a request leaves out.
            return request with { Period = request.Period ?? new StatisticsRequest().Period, Filter = request.Filter ?? new StatisticsFilter() };
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("The request is not valid: " + exception.Message, nameof(json), exception);
        }
    }

    /// <summary>Gets the type information the serializer uses, for a host that writes the results itself.</summary>
    internal static JsonSerializerContext Context => StatisticsResultJsonContext.Default;
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true,
    WriteIndented = false)]
[JsonSerializable(typeof(SeriesResult))]
[JsonSerializable(typeof(SummaryResult))]
[JsonSerializable(typeof(TopResult))]
[JsonSerializable(typeof(DistributionResult))]
[JsonSerializable(typeof(CalendarResult))]
[JsonSerializable(typeof(WeekHourResult))]
[JsonSerializable(typeof(SessionsResult))]
[JsonSerializable(typeof(ToolsResult))]
[JsonSerializable(typeof(ModelsResult))]
[JsonSerializable(typeof(ProjectsResult))]
[JsonSerializable(typeof(RecordsResult))]
[JsonSerializable(typeof(HealthResult))]
[JsonSerializable(typeof(SessionDetailResult))]
[JsonSerializable(typeof(StatisticsStatus))]
[JsonSerializable(typeof(DetailsResult))]
[JsonSerializable(typeof(RunsResult))]
[JsonSerializable(typeof(StatisticsCoverage))]
[JsonSerializable(typeof(StatisticsRequest))]
internal sealed partial class StatisticsResultJsonContext : JsonSerializerContext;
