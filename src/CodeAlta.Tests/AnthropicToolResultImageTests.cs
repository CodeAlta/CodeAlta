using System.Net;
using System.Text;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Anthropic;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Tests;

// The Claude API takes an image inside the tool_result that returns it; another endpoint of the protocol gets it in a
// user message after the tool results, as the other transports do.
[TestClass]
public sealed class AnthropicToolResultImageTests
{
    [TestMethod]
    public async Task ClaudeApi_KeepsTheImageOfAToolInsideItsToolResult()
    {
        using var body = await SendAsync(new Uri("https://api.anthropic.com"), profile: null).ConfigureAwait(false);

        var messages = body.RootElement.GetProperty("messages").EnumerateArray().ToArray();
        Assert.AreEqual(3, messages.Length, "No user message is added for the image.");
        var toolResult = messages[2].GetProperty("content").EnumerateArray().Single(static block => block.GetProperty("type").GetString() == "tool_result");
        var content = toolResult.GetProperty("content").EnumerateArray().ToArray();
        Assert.AreEqual("Viewed image shot.png.", content[0].GetProperty("text").GetString());
        var image = content.Single(static block => block.GetProperty("type").GetString() == "image");
        Assert.AreEqual("image/png", image.GetProperty("source").GetProperty("media_type").GetString());
        Assert.AreEqual(Convert.ToBase64String(AgentToolImagesTests.CreateImage(4, 4, SkiaSharp.SKEncodedImageFormat.Png)), image.GetProperty("source").GetProperty("data").GetString());
    }

    [TestMethod]
    public async Task AnotherEndpoint_OrAProfileThatSaysSo_GetsTheImageInAUserMessage()
    {
        using (var gateway = await SendAsync(new Uri("https://gateway.example.test/anthropic"), profile: null).ConfigureAwait(false))
        {
            AssertImageInUserMessage(gateway);
        }

        using (var refused = await SendAsync(new Uri("https://api.anthropic.com"), new AgentProviderProfile { SupportsToolResultImages = false }).ConfigureAwait(false))
        {
            AssertImageInUserMessage(refused);
        }

        static void AssertImageInUserMessage(JsonDocument body)
        {
            var messages = body.RootElement.GetProperty("messages").EnumerateArray().ToArray();
            var toolResult = messages[2].GetProperty("content").EnumerateArray().Single(static block => block.GetProperty("type").GetString() == "tool_result");
            Assert.IsFalse(toolResult.GetRawText().Contains("\"image\"", StringComparison.Ordinal));
            Assert.IsTrue(messages.Skip(2).SelectMany(static message => message.GetProperty("content").EnumerateArray())
                .Any(static block => block.GetProperty("type").GetString() == "image"), "The image follows the tool results.");
        }
    }

    private static async Task<JsonDocument> SendAsync(Uri baseUri, AgentProviderProfile? profile)
    {
        string? body = null;
        var stream = AnthropicStopReasonTests.Stream(
            AnthropicStopReasonTests.TextBlock(0, "Seen."),
            """{"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":1}}""");
        var executor = AnthropicModelProviderRuntime.CreateTurnExecutor(new AnthropicProviderOptions
        {
            ProviderKey = "anthropic",
            ApiKey = "test-key",
            BaseUri = baseUri,
            HttpClient = new HttpClient(new StubHandler(request =>
            {
                body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(stream, Encoding.UTF8, "text/event-stream") };
            })),
        });
        var image = Convert.ToBase64String(AgentToolImagesTests.CreateImage(4, 4, SkiaSharp.SKEncodedImageFormat.Png));
        var request = AnthropicStopReasonTests.CreateRequest() with
        {
            Provider = AnthropicStopReasonTests.CreateRequest().Provider with { BaseUri = baseUri, Profile = profile },
            Conversation =
            [
                new AgentConversationMessage(AgentConversationRole.User, [new AgentMessagePart.Text("Look at the screenshot.")]),
                new AgentConversationMessage(AgentConversationRole.Assistant,
                    [new AgentMessagePart.ToolCall("call-view", "view_image", JsonDocument.Parse("""{"path":"shot.png"}""").RootElement.Clone())]),
                new AgentConversationMessage(AgentConversationRole.Tool,
                [
                    new AgentMessagePart.ToolResult("call-view", new AgentToolResult(true,
                    [
                        new AgentToolResultItem.Text("Viewed image shot.png."),
                        new AgentToolResultItem.Image(image, "image/png", "shot.png"),
                    ])),
                ]),
            ],
        };

        _ = await executor.ExecuteTurnAsync(request, static (_, _) => ValueTask.CompletedTask).ConfigureAwait(false);

        return JsonDocument.Parse(body!);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(handler(request));
    }
}
