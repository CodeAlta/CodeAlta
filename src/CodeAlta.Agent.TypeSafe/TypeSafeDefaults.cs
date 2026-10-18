namespace CodeAlta.Agent.TypeSafe;

/// <summary>
/// The endpoint and the model of the TypeSafe AI System One API when a provider names neither.
/// </summary>
public static class TypeSafeDefaults
{
    /// <summary>The address of the API; the paths below are added to it.</summary>
    public const string BaseUrl = "https://api.typesafe.ai";

    /// <summary>The model a provider asks for when it names none: the alias of the latest stable Jev.</summary>
    public const string Model = "jev-latest";

    internal const string SystemOnePath = "/v1/systemone";

    internal const string ModelsPath = "/v1/models";
}
