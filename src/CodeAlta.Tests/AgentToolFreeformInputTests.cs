using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Tests;

[TestClass]
public sealed class AgentToolFreeformInputTests
{
    [TestMethod]
    public void Constructor_NeedsAnArgumentName_AndADefinitionForAGrammar()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new AgentToolFreeformInput(" "));
        Assert.ThrowsExactly<ArgumentException>(() => new AgentToolFreeformInput("code", AgentToolFreeformSyntax.Lark));
        Assert.ThrowsExactly<ArgumentException>(() => new AgentToolFreeformInput("code", AgentToolFreeformSyntax.Regex, ""));
        Assert.IsNull(new AgentToolFreeformInput("code", AgentToolFreeformSyntax.Text, "ignored").Definition);
        Assert.AreEqual("start: /.+/", new AgentToolFreeformInput("code", AgentToolFreeformSyntax.Lark, "start: /.+/").Definition);
    }

    [TestMethod]
    public void CreateArguments_AndTryGetText_KeepTheTextAsItIs()
    {
        const string text = "line \"one\"\n\tline two\\";
        var input = new AgentToolFreeformInput("code");

        var arguments = input.CreateArguments(text);

        Assert.AreEqual(JsonValueKind.Object, arguments.ValueKind);
        Assert.AreEqual(text, arguments.GetProperty("code").GetString());
        Assert.IsTrue(input.TryGetText(arguments, out var read));
        Assert.AreEqual(text, read);
        Assert.IsFalse(input.TryGetText(JsonDocument.Parse("""{"other":"x"}""").RootElement, out _));
        Assert.IsFalse(input.TryGetText(JsonDocument.Parse("""{"code":1}""").RootElement, out _));
    }

    [TestMethod]
    public void ToolCall_KeepsItsRawTextKind_InTheJournal_AndOmitsItForAJsonCall()
    {
        AgentConversationMessage message = new(
            AgentConversationRole.Assistant,
            [
                new AgentMessagePart.ToolCall("call-1", "exec", AgentToolFreeformInput.CreateArguments("code", "return 1;")) { Freeform = true },
                new AgentMessagePart.ToolCall("call-2", "read_file", JsonDocument.Parse("""{"path":"a"}""").RootElement.Clone()),
            ]);

        var json = JsonSerializer.Serialize(message, AgentJsonSerializerContext.Default.AgentConversationMessage);
        var read = JsonSerializer.Deserialize(json, AgentJsonSerializerContext.Default.AgentConversationMessage)!;

        using var document = JsonDocument.Parse(json);
        Assert.IsTrue(document.RootElement.GetProperty("parts")[0].GetProperty("freeform").GetBoolean());
        Assert.IsFalse(document.RootElement.GetProperty("parts")[1].TryGetProperty("freeform", out _));
        var calls = read.Parts.Cast<AgentMessagePart.ToolCall>().ToArray();
        Assert.AreEqual(true, calls[0].Freeform);
        Assert.AreEqual("return 1;", calls[0].Arguments.GetProperty("code").GetString());
        Assert.IsNull(calls[1].Freeform);
    }
}
