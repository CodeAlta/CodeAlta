namespace CodeAlta.Catalog.Speech;

/// <summary>The choices for speech to prompt, from the global <c>[speech]</c> table.</summary>
/// <param name="Enabled">Whether the microphone of the prompt is offered.</param>
/// <param name="Model">The id of a preset model, or the address of a Hugging Face repository.</param>
public sealed record SpeechSettings(bool Enabled, string Model)
{
    /// <summary>The id of the model used when none is chosen.</summary>
    public const string DefaultModel = "parakeet-tdt-0.6b-v3";

    /// <summary>The address every custom model starts with.</summary>
    public const string RepositoryPrefix = "https://huggingface.co/";

    private const int MaximumModelLength = 512;

    /// <summary>Gets the choices when configuration is absent: on, with the default model.</summary>
    public static SpeechSettings Default { get; } = new(true, DefaultModel);

    /// <summary>Reads the global speech table.</summary>
    /// <exception cref="InvalidDataException">The model is neither a model id nor a Hugging Face address.</exception>
    public static SpeechSettings Read(CodeAltaSpeechSettingsDocument? document)
    {
        var model = document?.Model ?? DefaultModel;
        if (GetModelError(model) is { } error) throw new InvalidDataException(error);
        return new(document?.Enabled ?? true, model);
    }

    /// <summary>Gets whether a model is the address of a repository and not the id of a preset.</summary>
    public static bool IsRepository(string model)
        => model is not null && model.StartsWith(RepositoryPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Gets a configuration error, or null when the model is an id or a Hugging Face address.</summary>
    public static string? GetModelError(string? model)
    {
        if (string.IsNullOrEmpty(model) || model.Length > MaximumModelLength) return "The speech model must be a model id or a Hugging Face repository address.";
        if (IsRepository(model))
        {
            return Uri.TryCreate(model, UriKind.Absolute, out var address) && address.Scheme == Uri.UriSchemeHttps
                && string.Equals(address.Host, "huggingface.co", StringComparison.OrdinalIgnoreCase)
                && address.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Length >= 2
                && address.Query.Length == 0 && address.Fragment.Length == 0 && address.UserInfo.Length == 0
                    ? null
                    : "The speech model address must be https://huggingface.co/<owner>/<repository>.";
        }

        foreach (var character in model)
        {
            if (character is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '.'))
                return "The speech model must be a model id or a Hugging Face repository address.";
        }

        return model[0] is '-' or '.' ? "The speech model must be a model id or a Hugging Face repository address." : null;
    }
}
