using System.Text.Json.Nodes;
using CodeAlta.Agent.TypeSafe;

namespace CodeAlta.LiveTool;

/// <summary>
/// Consults the decision providers of the configuration (<c>type = "typesafe"</c>, the Jev model of TypeSafe AI)
/// for the <c>alta decide</c> commands: typed questions about a state, answered with probabilities. A decision
/// provider never runs a session.
/// </summary>
public interface IAltaDecisionService
{
    /// <summary>Lists the enabled decision providers, in the order of the configuration.</summary>
    /// <returns>The providers; empty when none is configured and enabled.</returns>
    /// <exception cref="InvalidOperationException">The configuration cannot be read.</exception>
    IReadOnlyList<AltaDecisionProvider> ListProviders();

    /// <summary>Asks typed questions about a state.</summary>
    /// <param name="providerKey">The provider to ask; <see langword="null"/> for the first enabled one.</param>
    /// <param name="model">The model to ask; <see langword="null"/> for the model of the provider.</param>
    /// <param name="state">What the questions are about.</param>
    /// <param name="questions">The questions by the id their answers come back under.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The provider that answered and its answers.</returns>
    /// <exception cref="AltaDecisionException">No provider can answer, or the provider refused or failed.</exception>
    /// <exception cref="ArgumentException">The state or the questions break a rule of the API.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    Task<AltaDecisionResult> DecideAsync(string? providerKey, string? model, JsonNode state, JsonObject questions, CancellationToken cancellationToken);

    /// <summary>Lists the models a provider's account can ask for; the request is not charged and tests the key.</summary>
    /// <param name="providerKey">The provider; <see langword="null"/> for the first enabled one.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The provider asked and the model names of its account.</returns>
    /// <exception cref="AltaDecisionException">No provider can answer, or the provider refused or failed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    Task<(AltaDecisionProvider Provider, IReadOnlyList<AltaDecisionModel> Models)> ListModelsAsync(string? providerKey, CancellationToken cancellationToken);
}

/// <summary>One decision provider of the configuration, without its secret.</summary>
/// <param name="Key">The provider key.</param>
/// <param name="DisplayName">Its name, as the configuration gives it or derived from the key.</param>
/// <param name="Model">The model it asks when a request names none.</param>
/// <param name="HasCredential">Whether an API key is set, directly or in the environment variable it names.</param>
public sealed record AltaDecisionProvider(string Key, string DisplayName, string Model, bool HasCredential);

/// <summary>One model name the account of a decision provider can ask for.</summary>
/// <param name="Name">The name to give as the model, such as <c>jev-latest</c>.</param>
/// <param name="Description">What the provider says of it.</param>
/// <param name="ReleaseDate">Its release date, as the provider wrote it.</param>
public sealed record AltaDecisionModel(string Name, string? Description, string? ReleaseDate);

/// <summary>The answers of one decision.</summary>
/// <param name="Provider">The provider that answered.</param>
/// <param name="Evaluation">The typed answers, the versioned model and the tokens.</param>
public sealed record AltaDecisionResult(AltaDecisionProvider Provider, TypeSafeEvaluation Evaluation);

/// <summary>A decision that could not be made, with the <c>alta.error</c> code that says why.</summary>
public sealed class AltaDecisionException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="code">The error code: <c>decide.noProvider</c>, <c>decide.providerNotFound</c>, <c>decide.noCredential</c>,
    /// <c>decide.unauthorized</c>, <c>decide.invalidRequest</c>, <c>decide.busy</c> or <c>decide.failed</c>.</param>
    /// <param name="message">What failed and what to do.</param>
    /// <param name="innerException">The cause, if any.</param>
    public AltaDecisionException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Code = code;
    }

    /// <summary>The error code of the <c>alta.error</c> record.</summary>
    public string Code { get; }
}
