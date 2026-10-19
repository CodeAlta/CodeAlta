#pragma warning disable OPENAI001
#pragma warning disable SCME0001

using System.Buffers.Binary;
using System.ClientModel.Primitives;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeAlta.Agent.OpenAI;
using OpenAI.Responses;

namespace CodeAlta.Tests;

/// <summary>
/// The decision whether a Codex request continues the previous response of its session, made with the fingerprints of its
/// JSON, is the one the comparison of the JSON text made: that comparison is kept here as the reference.
/// </summary>
[TestClass]
public sealed class OpenAIResponsesContinuationFingerprintTests
{
    // Text the JSON encoder escapes, text out of ASCII and a separator that JavaScript does not take in a string.
    private const string AwkwardText = "Hello <world> & 'friends' + é 漢字 😀 \u2028 \"quoted\" \\ end";

    [TestMethod]
    public void RequestThatExtendsThePreviousOne_Continues()
        => AssertSameDecision(
            expected: true,
            previous: () => Request(Prefix()),
            replay: Replay,
            next: () => Request([.. Prefix(), .. Replay(), User("Next question"), .. ToolExchange("call-2", "image-2")]));

    [TestMethod]
    public void RequestWithNothingNew_ContinuesWithAnEmptyInput()
        => AssertSameDecision(
            expected: true,
            previous: () => Request(Prefix()),
            replay: Replay,
            next: () => Request([.. Prefix(), .. Replay()]));

    [TestMethod]
    public void PreviousResponseIdAndClientMetadata_AreNotCompared()
        => AssertSameDecision(
            expected: true,
            previous: () => Request(Prefix()),
            replay: Replay,
            next: () => Request(
                [.. Prefix(), .. Replay(), User("Next question")],
                configure: static options =>
                {
                    options.PreviousResponseId = "response-0";
                    options.Patch.Set("$.client_metadata.turn_id"u8, BinaryData.FromString("\"turn-2\""));
                }));

    [TestMethod]
    public void ChangedItemOfThePreviousInput_DoesNotContinue()
        => AssertSameDecision(
            expected: false,
            previous: () => Request(Prefix()),
            replay: Replay,
            next: () => Request([User(AwkwardText + "!"), .. Prefix().Skip(1), .. Replay(), User("Next question")]));

    [TestMethod]
    public void ChangedImageOfAToolResultOfThePreviousInput_DoesNotContinue()
        => AssertSameDecision(
            expected: false,
            previous: () => Request(Prefix()),
            replay: Replay,
            next: () => Request([.. Prefix().Take(2), .. ToolExchange("call-1", "other-image"), .. Replay(), User("Next question")]));

    [TestMethod]
    public void ChangedReplayOfThePreviousOutput_DoesNotContinue()
        => AssertSameDecision(
            expected: false,
            previous: () => Request(Prefix()),
            replay: Replay,
            next: () => Request([.. Prefix(), Assistant("Another answer."), User("Next question")]));

    [TestMethod]
    public void FewerItemsThanThePreviousRequestAndItsOutput_DoNotContinue()
        => AssertSameDecision(
            expected: false,
            previous: () => Request(Prefix()),
            replay: Replay,
            next: () => Request(Prefix()));

    [TestMethod]
    public void ChangedInstructions_DoNotContinue()
        => AssertSameDecision(
            expected: false,
            previous: () => Request(Prefix()),
            replay: Replay,
            next: () => Request([.. Prefix(), .. Replay(), User("Next")], configure: static options => options.Instructions += " Be brief."));

    [TestMethod]
    public void ChangedTool_DoesNotContinue()
        => AssertSameDecision(
            expected: false,
            previous: () => Request(Prefix()),
            replay: Replay,
            next: () => Request([.. Prefix(), .. Replay(), User("Next")], toolDescription: "Reads a file of the project."));

    [TestMethod]
    public void ChangedReasoningEffort_DoesNotContinue()
        => AssertSameDecision(
            expected: false,
            previous: () => Request(Prefix()),
            replay: Replay,
            next: () => Request(
                [.. Prefix(), .. Replay(), User("Next")],
                configure: static options => options.ReasoningOptions!.ReasoningEffortLevel = ResponseReasoningEffortLevel.High));

    [TestMethod]
    public void ChangedPatchedField_DoesNotContinue()
        => AssertSameDecision(
            expected: false,
            previous: () => Request(Prefix()),
            replay: Replay,
            next: () => Request(
                [.. Prefix(), .. Replay(), User("Next")],
                configure: static options => options.Patch.Set("$.prompt_cache_key"u8, "another-session")));

    [TestMethod]
    public void Fingerprints_AreThoseOfTheJsonTextComparedBefore()
    {
        var items = Prefix().Concat(Replay()).Concat(ToolExchange("call-3", "image-3")).ToArray();
        var sameItems = Prefix().Concat(Replay()).Concat(ToolExchange("call-3", "image-3")).ToArray();
        for (var index = 0; index < items.Length; index++)
        {
            // Equal items of separate instances have one fingerprint: the SHA-256 of the text compared before.
            Assert.AreEqual(OpenAIResponsesRequestFingerprint.OfItem(items[index]), OpenAIResponsesRequestFingerprint.OfItem(sameItems[index]));
            Assert.AreEqual(
                BinaryPrimitives.ReadUInt128LittleEndian(SHA256.HashData(Encoding.UTF8.GetBytes(ReferenceItemJson(items[index])))),
                OpenAIResponsesRequestFingerprint.OfItem(sameItems[index]));
        }

        Assert.AreEqual(
            OpenAIResponsesRequestFingerprint.OfRequestWithoutInput(Request(Prefix())),
            OpenAIResponsesRequestFingerprint.OfRequestWithoutInput(Request([User("Anything else")], configure: static options => options.PreviousResponseId = "x")));
        Assert.AreNotEqual(
            OpenAIResponsesRequestFingerprint.OfItem(User("a")),
            OpenAIResponsesRequestFingerprint.OfItem(User("b")));
    }

    private static void AssertSameDecision(
        bool expected,
        Func<CreateResponseOptions> previous,
        Func<IReadOnlyList<ResponseItem>> replay,
        Func<CreateResponseOptions> next)
    {
        // Each side reads requests of its own, as the executor does with each request it builds.
        var reference = new ReferenceContinuation(
            ReferenceRequestWithoutInputJson(previous()),
            previous().InputItems.Select(ReferenceItemJson).ToArray(),
            replay().Select(ReferenceItemJson).ToArray());
        var referenceDecision = reference.Continues(next(), out var baselineCount);
        Assert.AreEqual(expected, referenceDecision, "The case is not the one it claims to be.");

        var continuation = OpenAIResponsesTurnExecutor.OpenAIResponsesLiveContinuation.Create(previous(), replay(), "response-1");
        var nextRequest = next();
        var decision = OpenAIResponsesTurnExecutor.TryCreateContinuationOptions(nextRequest, continuation, out var continuationOptions);

        Assert.AreEqual(referenceDecision, decision);
        if (decision)
        {
            Assert.AreEqual("response-1", continuationOptions.PreviousResponseId);
            CollectionAssert.AreEqual(nextRequest.InputItems.Skip(baselineCount).ToArray(), continuationOptions.InputItems.ToArray());
        }
    }

    private static CreateResponseOptions Request(
        IEnumerable<ResponseItem> input,
        Action<CreateResponseOptions>? configure = null,
        string toolDescription = "Reads a file.")
    {
        // A request as the executor builds one for the ChatGPT subscription, with the fields it patches in.
        var options = new CreateResponseOptions
        {
            Model = "gpt-5.3-codex",
            Instructions = "You are a coding agent. " + AwkwardText,
            ParallelToolCallsEnabled = true,
            StoredOutputEnabled = false,
            StreamingEnabled = true,
            ReasoningOptions = new ResponseReasoningOptions
            {
                ReasoningEffortLevel = ResponseReasoningEffortLevel.Medium,
                ReasoningSummaryVerbosity = ResponseReasoningSummaryVerbosity.Auto,
            },
            TextOptions = new ResponseTextOptions(),
            ToolChoice = ResponseToolChoice.CreateAutoChoice(),
        };
        options.IncludedProperties.Add(IncludedResponseProperty.ReasoningEncryptedContent);
        options.Tools.Add(ResponseTool.CreateFunctionTool(
            "read_file",
            BinaryData.FromString("""{"type":"object","properties":{"path":{"type":"string","description":"The <path> & more"}},"required":["path"],"additionalProperties":false}"""),
            strictModeEnabled: true,
            functionDescription: toolDescription));
        var webSearch = ResponseTool.CreateWebSearchTool();
        webSearch.Patch.Set("$.external_web_access"u8, false);
        options.Tools.Add(webSearch);
        options.TextOptions.Patch.Set("$.verbosity"u8, "medium");
        options.Patch.Set("$.prompt_cache_key"u8, "session-1");
        options.Patch.Set("$.stream_options.reasoning_summary_delivery"u8, "sequential_cutoff");
        options.Patch.Set("$.client_metadata.session_id"u8, BinaryData.FromString("\"session-1\""));
        options.Patch.Set("$.client_metadata.turn_id"u8, BinaryData.FromString("\"turn-1\""));
        foreach (var item in input)
        {
            options.InputItems.Add(item);
        }

        configure?.Invoke(options);
        return options;
    }

    private static IReadOnlyList<ResponseItem> Prefix()
        => [User(AwkwardText), Reasoning("Thinking about <it> 😀", "encrypted-1"), .. ToolExchange("call-1", "image-1")];

    private static IReadOnlyList<ResponseItem> Replay()
        => [Reasoning("Concluding", "encrypted-2"), Assistant("Here is the answer: " + AwkwardText)];

    private static ResponseItem User(string text) => ResponseItem.CreateUserMessageItem(text);

    private static ResponseItem Assistant(string text)
        => ResponseItem.CreateAssistantMessageItem([ResponseContentPart.CreateOutputTextPart(text, [])]);

    private static ResponseItem Reasoning(string summary, string encryptedContent)
    {
        var item = new ReasoningResponseItem([ReasoningSummaryPart.CreateTextPart(summary)]);
        item.EncryptedContent = encryptedContent;
        return item;
    }

    private static IEnumerable<ResponseItem> ToolExchange(string callId, string imageData)
    {
        yield return ResponseItem.CreateFunctionCallItem(callId, "read_file", BinaryData.FromString("""{"path":"src/a <b>.cs"}"""));
        var output = ResponseItem.CreateFunctionCallOutputItem(callId, "The file holds an image.");
        // A tool result with an image is sent as a list of parts, as the executor patches it in.
        output.Patch.Set("$.output"u8, BinaryData.FromString(
            $$"""[{"type":"input_text","text":"The file holds an image."},{"type":"input_image","image_url":"data:image/png;base64,{{Convert.ToBase64String(Encoding.UTF8.GetBytes(imageData))}}"}]"""));
        yield return output;
    }

    // The comparison of before, on the JSON text of the request and of its items.
    private sealed record ReferenceContinuation(
        string RequestWithoutInputJson,
        IReadOnlyList<string> InputItemsJson,
        IReadOnlyList<string> OutputItemsJson)
    {
        public bool Continues(CreateResponseOptions fullOptions, out int baselineCount)
        {
            baselineCount = InputItemsJson.Count + OutputItemsJson.Count;
            if (!string.Equals(ReferenceRequestWithoutInputJson(fullOptions), RequestWithoutInputJson, StringComparison.Ordinal))
            {
                return false;
            }

            var currentInputItemsJson = fullOptions.InputItems.Select(ReferenceItemJson).ToArray();
            if (currentInputItemsJson.Length < baselineCount)
            {
                return false;
            }

            for (var index = 0; index < InputItemsJson.Count; index++)
            {
                if (!string.Equals(currentInputItemsJson[index], InputItemsJson[index], StringComparison.Ordinal))
                {
                    return false;
                }
            }

            for (var index = 0; index < OutputItemsJson.Count; index++)
            {
                if (!string.Equals(currentInputItemsJson[InputItemsJson.Count + index], OutputItemsJson[index], StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }
    }

    private static string ReferenceRequestWithoutInputJson(CreateResponseOptions options)
    {
        using var document = JsonDocument.Parse(SerializeModel(options));
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!property.NameEquals("input"u8) &&
                    !property.NameEquals("previous_response_id"u8) &&
                    !property.NameEquals("client_metadata"u8))
                {
                    property.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
        }

        using var normalized = JsonDocument.Parse(stream.ToArray());
        return normalized.RootElement.GetRawText();
    }

    private static string ReferenceItemJson(ResponseItem item)
    {
        using var document = JsonDocument.Parse(SerializeModel(item));
        return document.RootElement.GetRawText();
    }

    private static string SerializeModel<T>(T model)
        where T : notnull
        => model is IPersistableModel<T> persistable
            ? persistable.Write(new ModelReaderWriterOptions("J")).ToString()
            : model.ToString() ?? string.Empty;
}
