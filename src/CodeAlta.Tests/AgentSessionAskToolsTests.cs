using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Agent.Runtime.Tools;

namespace CodeAlta.Tests;

/// <summary>Pure checks of the actual per-send registration path; no session or provider acquisition.</summary>
[TestClass]
public sealed class AgentSessionAskToolsTests
{
    [TestMethod]
    public void EmptyAddition_PreservesOriginalListAndAliases()
    {
        AgentToolDefinition[] original = [Tool("_same_"), Tool("same")];
        Assert.AreSame(original, AgentSession.AppendSendTools(original, null));
        Assert.AreSame(original, AgentSession.AppendSendTools(original, []));
        CollectionAssert.AreEqual(new[] { "same", "same_2" }, AgentToolBridge.CreateDefinitionMap(original).Keys.ToArray());
    }

    [TestMethod]
    public void Addition_RejectsNormalizedTruncatedAndGeneratedSuffixAliases()
    {
        AgentToolDefinition[] original = [Tool("_same_"), Tool("same"), Tool(new string('x', 65))];
        foreach (var name in new[] { "same", "__same__", "same_2", new string('x', 64) })
            Assert.Throws<ArgumentException>(() => AgentSession.AppendSendTools(original, [Tool(name)]));
        Assert.Throws<ArgumentException>(() => AgentSession.AppendSendTools([], [Tool("_same_"), Tool("same")]));
    }

    [TestMethod]
    public void SequentialAdditions_DoNotChangeSessionOrRetargetHandlers()
    {
        AgentToolDefinition[] original = [Tool("existing")];
        var first = Tool("alta");
        var second = Tool("alta");
        var a = AgentSession.AppendSendTools(original, [first]);
        var b = AgentSession.AppendSendTools(original, [second]);
        Assert.AreSame(first, a[1]);
        Assert.AreSame(second, b[1]);
        Assert.AreEqual(1, original.Length);
        Assert.AreSame(original[0], a[0]);
    }

    private static AgentToolDefinition Tool(string name)
    {
        using var document = JsonDocument.Parse("{\"type\":\"object\"}");
        return new(new(name, "inert", document.RootElement.Clone()), static (_, _) =>
            Task.FromResult(new AgentToolResult(true, [])));
    }
}
