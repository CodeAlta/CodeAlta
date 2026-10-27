using CodeAlta.Agent;
using CodeAlta.LiveTool;

namespace CodeAlta.Tests;

/// <summary>
/// The <c>alta</c> commands that start a session, for a session without sub-sessions: hidden from its help and from the
/// description of its tool, and refused when it calls them anyway.
/// </summary>
[TestClass]
public sealed class AltaSubSessionsTests
{
    private static readonly AltaCallerIdentity WithoutSubSessions = new() { Kind = "agent", SourceSessionId = "session-leaf", SubSessions = false };

    [TestMethod]
    public void Gates_TheCommandsThatStartASession()
    {
        foreach (var path in new[] { "alta session create", "alta session fork", "alta script run", "alta automation run", "alta automation create" })
            Assert.IsTrue(AltaSubSessionsGate.Gates(path), path);
        foreach (var path in new[] { "alta session send", "alta session list", "alta script list", "alta automation list", "alta", "alta session" })
            Assert.IsFalse(AltaSubSessionsGate.Gates(path), path);
    }

    [TestMethod]
    public async Task TheHelp_OfASessionWithoutSubSessions_ShowsNoCommandThatStartsOne()
    {
        var dispatcher = new AltaCommandDispatcher(new AltaCommandRegistry(), new AltaServiceCollection());

        var full = await dispatcher.InvokeAsync(["--help"], caller: AltaCallerIdentity.Cli);
        StringAssert.Contains(full.Stdout, "alta session create --project <project> --reasoning low");
        StringAssert.Contains(full.Stdout, "--no-sub-sessions");
        var fullSession = await dispatcher.InvokeAsync(["session", "--help"], caller: AltaCallerIdentity.Cli);
        StringAssert.Contains(fullSession.Stdout, "\n  create ");
        StringAssert.Contains(fullSession.Stdout, "\n  fork ");
        var create = await dispatcher.InvokeAsync(["session", "create", "--help"], caller: AltaCallerIdentity.Cli);
        StringAssert.Contains(create.Stdout, "--no-sub-sessions");

        var root = await dispatcher.InvokeAsync(["--help"], caller: WithoutSubSessions);
        Assert.AreEqual(AltaExitCodes.Success, root.ExitCode);
        Assert.DoesNotContain("session create", root.Stdout);
        Assert.DoesNotContain("session fork", root.Stdout);
        Assert.DoesNotContain("--no-sub-sessions", root.Stdout);
        StringAssert.Contains(root.Stdout, "this session starts no session of its own");
        var session = await dispatcher.InvokeAsync(["session", "--help"], caller: WithoutSubSessions);
        Assert.DoesNotContain("\n  create ", session.Stdout);
        Assert.DoesNotContain("\n  fork ", session.Stdout);
        Assert.DoesNotContain("session fork", session.Stdout);
        Assert.DoesNotContain("create child sessions", session.Stdout);
        StringAssert.Contains(session.Stdout, "\n  send ");
    }

    [TestMethod]
    public async Task ACommandThatStartsASession_IsRefused_ToASessionWithoutSubSessions()
    {
        var dispatcher = new AltaCommandDispatcher(new AltaCommandRegistry(), new AltaServiceCollection());
        foreach (var args in new[]
                 {
                     new[] { "session", "create", "--project", "demo" },
                     ["session", "fork", "session-other"],
                 })
        {
            var result = await dispatcher.InvokeAsync(args, caller: WithoutSubSessions);

            Assert.AreEqual(AltaExitCodes.PolicyDenied, result.ExitCode, string.Join(' ', args) + ": " + result.Stdout + result.Stderr);
            StringAssert.Contains(result.Stdout + result.Stderr, "subSessions.refused");
            StringAssert.Contains(result.Stdout + result.Stderr, "without sub-sessions");
        }
    }

    [TestMethod]
    public void TheAltaTool_OfASessionWithoutSubSessions_SaysNothingOfChildSessions()
    {
        var dispatcher = new AltaCommandDispatcher(new AltaCommandRegistry(), new AltaServiceCollection());

        var full = AltaSessionToolFactory.Create(dispatcher, new AltaSessionToolOptions());
        var leaf = AltaSessionToolFactory.Create(dispatcher, new AltaSessionToolOptions { SessionOptions = new AgentSessionOptions { SubSessions = false } });

        StringAssert.Contains(full.Spec.Description, "create project/global child sessions");
        Assert.DoesNotContain("child sessions", leaf.Spec.Description);
        StringAssert.Contains(leaf.Spec.Description, "send, queue, steer");
    }
}
