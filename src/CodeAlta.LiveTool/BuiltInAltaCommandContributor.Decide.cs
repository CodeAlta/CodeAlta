using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeAlta.Catalog;
using XenoAtom.CommandLine;

namespace CodeAlta.LiveTool;

// The `alta decide` command: an agent consults a decision model (the Jev model of TypeSafe AI, a provider of type
// `typesafe`) with typed questions about a state, and gets typed answers with probabilities. The decision model
// never runs a session: it is asked for a judgment, the agent acts on it. A host without the service has no such group.
internal sealed partial class BuiltInAltaCommandContributor
{
    // It changes nothing of the user's: it sends the state to the service of the provider and reads its answer.
    private static readonly AltaCommandPolicy[] DecidePolicies =
    [
        Read("decide", requiresRuntime: false, supportsCatalogOnlyContext: true),
    ];

    private static Command CreateDecideCommand(AltaCommandContext context)
    {
        var words = new List<string>();
        string? provider = null, model = null;
        var useStdin = false;
        var command = Leaf("decide", "Consult the decision model (TypeSafe Jev) on a judgment: yes or no, one of options, or a level of a scale, with probabilities.");
        command.Add("<action>*", "Left out to decide; `providers` lists the decision providers; `models` lists the models of one and tests its key.", value =>
        {
            if (value is not null) words.Add(value);
        });
        command.Add("stdin", EscapeHelpText("Read the request from stdin: `{\"state\": ..., \"questions\": {...}}`. Required to decide."), value => useStdin = value is not null);
        command.Add("provider=", "The decision provider; the first enabled one when left out.", value => provider = value);
        command.Add("model=", "The model, such as `jev-latest` or `jev-preview`; the model of the provider when left out.", value => model = value);
        command.Add(async (_, _) => words switch
        {
            [] => await HandleDecideAsync(context, useStdin, provider, model).ConfigureAwait(false),
            ["providers"] => await HandleDecideProvidersAsync(context).ConfigureAwait(false),
            ["models"] => await HandleDecideModelsAsync(context, provider).ConfigureAwait(false),
            _ => UsageError(context, "usage.invalidAction", $"Unknown action '{string.Join(' ', words)}': decide with `--stdin`, or use `providers` or `models`.", "alta decide"),
        });
        AddHelpText(
            command,
            EscapeHelpText("The request on stdin: `state` is what is judged (a text, an object or an array: the diff, the error, the options and their facts); `questions` are the judgments, by the id their answer comes back under."),
            EscapeHelpText("Question types: `noul` (is a statement true? `criteria` optional: {\"true\": ..., \"false\": ...}), `choice` (`criteria`: an object of 1 to 255 options, each with its description or null), `score` (`criteria`: an array of 2 to 10 levels, the lowest first). Each has `instructions`: what to judge."),
            "The answer: a noul gives the probability of yes, 0 to 1 (0.5 is unsure; its certainty is |2p-1|); a choice gives the option, the probabilities of all options and a confidence; a score gives the expected level (it can be fractional), its probabilities and a confidence. `summary` says each in one line.",
            "Ask narrow questions, the kind an expert answers at a glance; split a judgment of several factors into one question each, and combine the answers yourself. Several questions in one request cost little more: they are answered in parallel, each on its own.",
            "Act on a high confidence; at a medium one, check further or ask the user; at a low one, do not act on it. The model judges only the state you give: it does not see the project, run tools or explain its answer.",
            "The state is sent to TypeSafe AI, which charges its input tokens: do not send secrets. The decision providers are set in Settings > Providers (type `typesafe`); they never run a session.",
            EscapeHelpText("Example: `alta decide --stdin` with {\"state\": \"<the failing test output>\", \"questions\": {\"flaky\": {\"type\": \"noul\", \"instructions\": \"The failure comes from timing, not from the change.\"}, \"area\": {\"type\": \"choice\", \"instructions\": \"Where the defect is\", \"criteria\": {\"parser\": \"Parsing of the input\", \"io\": \"File access\", \"ui\": \"Rendering\"}}}}."));
        return command;
    }

    private static async Task<int> HandleDecideAsync(AltaCommandContext context, bool useStdin, string? providerKey, string? model)
    {
        const string CommandPath = "alta decide";
        if (!TryGetDecisionService(context, out var decisions)) return AltaExitCodes.ServiceUnavailable;
        if (!useStdin) return UsageError(context, "usage.missingStdin", "A decision reads its request from stdin: `alta decide --stdin`.", CommandPath);
        var stdin = await context.Stdin.ReadToEndAsync(context.CancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(stdin)) return UsageError(context, "usage.missingPayload", "The request on stdin is empty: give `{\"state\": ..., \"questions\": {...}}`.", CommandPath);
        JsonObject request;
        try
        {
            if (JsonNode.Parse(stdin) is not JsonObject parsed) return UsageError(context, "usage.invalidPayload", "The request is a JSON object with `state` and `questions`.", CommandPath);
            request = parsed;
        }
        catch (JsonException exception)
        {
            return UsageError(context, "usage.invalidJson", $"The request is not valid JSON: {exception.Message}", CommandPath);
        }

        if (request.Select(static entry => entry.Key).FirstOrDefault(static name => name is not ("state" or "questions" or "model")) is { } unknown)
            return UsageError(context, "usage.invalidPayload", $"The request has an unknown field `{unknown}`: it has `state`, `questions` and, optionally, `model`.", CommandPath);
        var requestedModel = NormalizeOptionalText(model) ?? (request["model"] is JsonValue value && value.GetValueKind() == JsonValueKind.String ? NormalizeOptionalText(value.GetValue<string>()) : null);
        if (request["questions"] is not JsonObject questions || request["state"] is not { } state)
            return UsageError(context, "usage.invalidPayload", "The request needs `state` (what is judged) and `questions` (an object of questions by id).", CommandPath);

        AltaDecisionResult result;
        try
        {
            var key = await DecisionProviderInSpaceAsync(context, decisions, providerKey).ConfigureAwait(false);
            result = await decisions.DecideAsync(key, requestedModel, state, questions, context.CancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException exception)
        {
            return UsageError(context, "usage.invalidQuestion", exception.Message, CommandPath);
        }
        catch (AltaDecisionException exception)
        {
            return DecisionError(context, exception);
        }

        var evaluation = result.Evaluation;
        WriteCanvasRecord(context, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = "alta.decide.result",
            ["version"] = 1,
            ["correlationId"] = context.CorrelationId,
            ["providerKey"] = result.Provider.Key,
            ["model"] = evaluation.Model,
            ["summary"] = Summarize(evaluation.Answers),
            ["answers"] = evaluation.Answers,
            ["usage"] = evaluation.InputTokens is null && evaluation.OutputTokens is null ? null
                : new Dictionary<string, object?>(StringComparer.Ordinal) { ["inputTokens"] = evaluation.InputTokens, ["outputTokens"] = evaluation.OutputTokens },
        });
        return AltaExitCodes.Success;
    }

    private static async Task<int> HandleDecideProvidersAsync(AltaCommandContext context)
    {
        if (!TryGetDecisionService(context, out var decisions)) return AltaExitCodes.ServiceUnavailable;
        IReadOnlyList<AltaDecisionProvider> providers;
        try
        {
            // A session sees the decision providers of its space, as it sees its model providers.
            var filter = await CallerSpaceFilterAsync(context).ConfigureAwait(false);
            providers = [.. decisions.ListProviders().Where(provider => filter?.Allows(SpaceResourceKind.Provider, provider.Key) != false)];
        }
        catch (InvalidOperationException exception)
        {
            AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, "config.invalid", AltaExitCodes.Failure, exception.Message);
            return AltaExitCodes.Failure;
        }

        foreach (var provider in providers)
        {
            WriteCanvasRecord(context, new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["type"] = "alta.decide.provider",
                ["version"] = 1,
                ["correlationId"] = context.CorrelationId,
                ["providerKey"] = provider.Key,
                ["displayName"] = provider.DisplayName,
                ["model"] = provider.Model,
                ["hasCredential"] = provider.HasCredential,
            });
        }

        if (providers.Count == 0)
        {
            AltaJsonlWriter.WriteWarning(context.Stderr, context.CorrelationId, "decide.noProvider",
                "No decision provider is configured: the user adds the TypeSafe Jev provider in Settings > Providers.");
        }

        return AltaExitCodes.Success;
    }

    private static async Task<int> HandleDecideModelsAsync(AltaCommandContext context, string? providerKey)
    {
        if (!TryGetDecisionService(context, out var decisions)) return AltaExitCodes.ServiceUnavailable;
        try
        {
            var key = await DecisionProviderInSpaceAsync(context, decisions, providerKey).ConfigureAwait(false);
            var (provider, models) = await decisions.ListModelsAsync(key, context.CancellationToken).ConfigureAwait(false);
            foreach (var model in models)
            {
                WriteCanvasRecord(context, new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["type"] = "alta.decide.model",
                    ["version"] = 1,
                    ["correlationId"] = context.CorrelationId,
                    ["providerKey"] = provider.Key,
                    ["name"] = model.Name,
                    ["description"] = model.Description,
                    ["releaseDate"] = model.ReleaseDate,
                    ["default"] = string.Equals(model.Name, provider.Model, StringComparison.OrdinalIgnoreCase) ? true : null,
                });
            }

            return AltaExitCodes.Success;
        }
        catch (AltaDecisionException exception)
        {
            return DecisionError(context, exception);
        }
    }

    // One line per answer, for the agent that reads it: the answer first, then how sure it is.
    internal static Dictionary<string, string> Summarize(JsonObject answers)
    {
        var summary = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (id, node) in answers)
        {
            if (node is not JsonObject answer) continue;
            var line = Text(answer["type"]) switch
            {
                "noul" when Number(answer["noul"]) is { } p => $"{(p >= 0.5 ? "yes" : "no")} (p(yes)={Format(p)}, certainty {Format(Math.Abs(2 * p - 1))})",
                "choice" when Text(answer["choice"]) is { } choice => $"{choice} (confidence {Format(Number(answer["confidence"]))})",
                "score" when Number(answer["score"]) is { } score => $"{Format(score)}{Level(answer, score)} (confidence {Format(Number(answer["confidence"]))})",
                _ => null,
            };
            if (line is not null) summary[id] = line;
        }

        return summary;

        static string Level(JsonObject answer, double score)
            => answer["legend"] is JsonObject legend && Text(legend[Math.Round(score, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture)]) is { } level
                ? $" ≈ \"{level}\""
                : string.Empty;

        static string Format(double? value) => value is { } number ? number.ToString("0.##", CultureInfo.InvariantCulture) : "?";

        static string? Text(JsonNode? node) => node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

        static double? Number(JsonNode? node) => node is JsonValue value && value.GetValueKind() == JsonValueKind.Number && value.TryGetValue<double>(out var number) ? number : null;
    }

    // The provider a session may consult: the one it names when its space has it, else the first of its space. Outside a
    // session every provider is there; without any, the service says that none is configured.
    private static async Task<string?> DecisionProviderInSpaceAsync(AltaCommandContext context, IAltaDecisionService decisions, string? providerKey)
    {
        if (await CallerSpaceFilterAsync(context).ConfigureAwait(false) is not { } filter) return providerKey;
        var key = NormalizeOptionalText(providerKey);
        if (key is not null)
        {
            return filter.Allows(SpaceResourceKind.Provider, key) ? key
                : throw new AltaDecisionException("decide.notInSpace", ResourceNotInSpaceException.MessageOf(SpaceResourceKind.Provider, key, filter.SpaceId));
        }

        var providers = decisions.ListProviders();
        if (providers.Count == 0) return null;
        return providers.FirstOrDefault(provider => filter.Allows(SpaceResourceKind.Provider, provider.Key))?.Key
            ?? throw new AltaDecisionException("decide.notInSpace",
                $"No decision provider is in the space '{filter.SpaceId}' of this session: the user adds one to it in Settings > Providers.");
    }

    private static int DecisionError(AltaCommandContext context, AltaDecisionException exception)
    {
        var exitCode = exception.Code switch
        {
            "decide.notInSpace" => AltaExitCodes.Unsupported,
            "decide.noProvider" or "decide.noCredential" or "decide.busy" => AltaExitCodes.ServiceUnavailable,
            "decide.providerNotFound" => AltaExitCodes.NotFound,
            "decide.invalidRequest" => AltaExitCodes.Usage,
            "decide.unauthorized" => AltaExitCodes.PolicyDenied,
            _ => AltaExitCodes.Failure,
        };
        AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, exception.Code, exitCode, exception.Message);
        return exitCode;
    }

    private static bool TryGetDecisionService(AltaCommandContext context, out IAltaDecisionService decisions)
    {
        decisions = context.Services.Get<IAltaDecisionService>()!;
        if (decisions is not null) return true;
        AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, "service.unavailable", AltaExitCodes.ServiceUnavailable, "Decisions are not available in this application.");
        return false;
    }
}
