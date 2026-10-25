using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Orchestration.Tests;

/// <summary>The programs a session starts know that they run under CodeAlta, in which session and with which model.</summary>
[TestClass]
public sealed class SessionProcessEnvironmentTests
{
    [TestMethod]
    public void WithMarkers_KeepsTheIdentityAndNamesTheSessionAndItsModel()
    {
        var identity = new AgentProcessEnvironment([new("GIT_AUTHOR_NAME", "Ada")]);

        var environment = SessionProcessEnvironment.WithMarkers(identity, "session-1", "openai", "gpt-6", AgentReasoningEffort.XHigh);

        Assert.AreEqual("Ada", environment.Get("GIT_AUTHOR_NAME"));
        Assert.AreEqual("session-1", environment.Get(SessionProcessEnvironment.SessionIdVariable));
        Assert.AreEqual("openai:gpt-6@xhigh", environment.Get(SessionProcessEnvironment.ModelVariable));
        Assert.AreEqual("codealta", environment.Get(SessionProcessEnvironment.AgentVariable));
        Assert.AreEqual("openai", SessionProcessEnvironment.FormatModel("openai", null, null));
    }

    [TestMethod]
    public async Task ShellCommandOfASession_SeesTheVariablesOfTheSession()
    {
        await using var root = new HeadlessTestRoot();
        var provider = new ScriptedTurnProvider();
        var host = await root.CreateHostAsync(provider, readOnlyGlobalState: true);
        var command = OperatingSystem.IsWindows()
            ? "Write-Output \"[$env:CODEALTA_SESSION_ID|$env:CODEALTA_MODEL|$env:AI_AGENT]\""
            : "echo \"[$CODEALTA_SESSION_ID|$CODEALTA_MODEL|$AI_AGENT]\"";
        provider.Turns.Enqueue(ScriptedTurnProvider.CallTool("shell_command", JsonSerializer.Serialize(new { command })));
        provider.Turns.Enqueue(ScriptedTurnProvider.Reply("Seen."));
        var options = HeadlessTestRoot.Options(provider, root.ProjectRoot, reasoningEffort: AgentReasoningEffort.High);

        var session = await host.RuntimeService.CreateProjectSessionAsync(host.CurrentProject, options, "Markers");
        await host.RuntimeService.SendAsync(session, options, new AgentSendOptions { Input = AgentInput.Text("print them") });

        var requests = provider.Requests.ToArray();
        Assert.AreEqual(2, requests.Length);
        Assert.AreEqual(session.SessionId, requests[0].Environment?.Get(SessionProcessEnvironment.SessionIdVariable));
        var output = string.Concat(requests[1].Conversation.SelectMany(static message => message.Parts).OfType<AgentMessagePart.ToolResult>()
            .SelectMany(static result => result.Result.Items).OfType<AgentToolResultItem.Text>().Select(static item => item.Value));
        StringAssert.Contains(output, $"[{session.SessionId}|{provider.ProviderId.Value}:{provider.Model}@high|codealta]");
        var environment = await host.RuntimeService.GetSessionProcessEnvironmentAsync(session.SessionId);
        Assert.AreEqual(session.SessionId, environment.Get(SessionProcessEnvironment.SessionIdVariable), "A job of the session starts with the same variables.");
    }
}
