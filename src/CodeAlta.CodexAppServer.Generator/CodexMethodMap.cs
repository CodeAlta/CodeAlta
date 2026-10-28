using System.Text.Json;

namespace CodeAlta.CodexAppServer.Generator;

/// <summary>A method of the protocol and its types.</summary>
/// <param name="Method">The name of the method, such as <c>thread/start</c>.</param>
/// <param name="ParamsType">The type of its parameters, or <see langword="null"/> for a method without parameters.</param>
/// <param name="ParamsOptional">Whether the parameters may be <see langword="null"/>.</param>
/// <param name="ResultType">The type of its response, or <see langword="null"/> for a notification.</param>
internal sealed record CodexMethod(string Method, string? ParamsType, bool ParamsOptional, string? ResultType);

/// <summary>
/// The methods CodeAlta uses (<c>codex-app-server-methods.json</c>), with the types of their parameters as the schema
/// lists them (<c>ClientRequest.json</c>, <c>ServerNotification.json</c>, <c>ServerRequest.json</c>).
/// </summary>
internal sealed class CodexMethodMap
{
    public required IReadOnlyList<CodexMethod> Requests { get; init; }

    public required IReadOnlyList<CodexMethod> Notifications { get; init; }

    public required IReadOnlyList<CodexMethod> ServerRequests { get; init; }

    /// <summary>Gets every type the methods name: the roots of the generated types.</summary>
    public IReadOnlyCollection<string> RootTypes
        => [.. Requests.Concat(Notifications).Concat(ServerRequests)
            .SelectMany(static method => new[] { method.ParamsType, method.ResultType })
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)];

    /// <summary>Loads the map and resolves its parameter types in the schema.</summary>
    /// <param name="mapPath">The path of <c>codex-app-server-methods.json</c>.</param>
    /// <param name="schemaDirectory">The folder of the schema.</param>
    /// <exception cref="InvalidOperationException">A method is not in the schema.</exception>
    public static CodexMethodMap Load(string mapPath, string schemaDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mapPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaDirectory);
        using var map = JsonDocument.Parse(File.ReadAllText(mapPath));
        var clientRequests = ReadParams(Path.Combine(schemaDirectory, "ClientRequest.json"));
        var notifications = ReadParams(Path.Combine(schemaDirectory, "ServerNotification.json"));
        var serverRequests = ReadParams(Path.Combine(schemaDirectory, "ServerRequest.json"));

        var requests = new List<CodexMethod>();
        foreach (var entry in map.RootElement.GetProperty("requests").EnumerateObject())
        {
            requests.Add(Resolve(entry.Name, entry.Value.GetString(), clientRequests, "ClientRequest.json"));
        }

        var notified = new List<CodexMethod>();
        foreach (var entry in map.RootElement.GetProperty("notifications").EnumerateArray())
        {
            notified.Add(Resolve(entry.GetString()!, null, notifications, "ServerNotification.json"));
        }

        var asked = new List<CodexMethod>();
        foreach (var entry in map.RootElement.GetProperty("serverRequests").EnumerateObject())
        {
            asked.Add(Resolve(entry.Name, entry.Value.GetString(), serverRequests, "ServerRequest.json"));
        }

        return new CodexMethodMap { Requests = requests, Notifications = notified, ServerRequests = asked };
    }

    private static CodexMethod Resolve(string method, string? resultType, IReadOnlyDictionary<string, (string? Type, bool Optional)> known, string listName)
    {
        if (!known.TryGetValue(method, out var parameters))
        {
            throw new InvalidOperationException($"'{method}' is not a method of {listName}: the protocol renamed or removed it.");
        }

        return new CodexMethod(method, parameters.Type, parameters.Optional, resultType);
    }

    // The method of each variant of a list of messages, and the type of its parameters.
    private static Dictionary<string, (string? Type, bool Optional)> ReadParams(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var result = new Dictionary<string, (string?, bool)>(StringComparer.Ordinal);
        foreach (var variant in document.RootElement.GetProperty("oneOf").EnumerateArray())
        {
            var properties = variant.GetProperty("properties");
            var method = properties.GetProperty("method").GetProperty("enum")[0].GetString()!;
            if (!properties.TryGetProperty("params", out var parameters))
            {
                result[method] = (null, false);
                continue;
            }

            if (Reference(parameters) is { } direct)
            {
                result[method] = (direct, false);
                continue;
            }

            string? type = null;
            var optional = false;
            if (parameters.TryGetProperty("anyOf", out var anyOf))
            {
                foreach (var option in anyOf.EnumerateArray())
                {
                    if (Reference(option) is { } referenced)
                    {
                        type = referenced;
                    }
                    else if (option.TryGetProperty("type", out var kind) && kind.GetString() == "null")
                    {
                        optional = true;
                    }
                }
            }

            result[method] = (type, optional);
        }

        return result;
    }

    private static string? Reference(JsonElement schema)
        => schema.TryGetProperty("$ref", out var reference) ? reference.GetString()?.Split('/')[^1] : null;
}
