using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Orchestration.Tests;

/// <summary>The sessions of a host without a user: full trust, the headless paragraph of their prompt, no review before commit.</summary>
[TestClass]
public sealed class HeadlessTrustTests
{
    [TestMethod]
    public async Task HeadlessInstructions_EndEverySystemPromptWithTheSameParagraph_AndNoSessionReviewsBeforeCommit()
    {
        await using var root = new HeadlessTestRoot();
        File.WriteAllText(Path.Combine(root.GlobalRoot, "config.toml"), "[review]\nbefore_commit = true\n");
        var provider = new ScriptedTurnProvider();
        var host = await root.CreateHostAsync(provider, readOnlyGlobalState: true, headlessInstructions: true);
        var options = HeadlessTestRoot.Options(provider, root.ProjectRoot);

        var first = await host.RuntimeService.CreateProjectSessionAsync(host.CurrentProject, options, "First");
        var second = await host.RuntimeService.CreateProjectSessionAsync(host.CurrentProject, options, "Second");
        await host.RuntimeService.SetReviewBeforeCommitAsync(second, true);
        await host.RuntimeService.SendAsync(first, options, new AgentSendOptions { Input = AgentInput.Text("one") });
        await host.RuntimeService.SendAsync(second, options, new AgentSendOptions { Input = AgentInput.Text("two") });

        var requests = provider.Requests.ToArray();
        Assert.AreEqual(2, requests.Length);
        foreach (var request in requests)
        {
            StringAssert.EndsWith(request.SystemMessage, SessionRuntimeService.HeadlessGuidance);
            // Nobody reads the summaries of its reasoning.
            Assert.IsTrue(request.OmitReasoningSummaries);
            // No note tells the agent to wait for a review that nobody makes.
            Assert.IsFalse(request.Conversation.SelectMany(static message => message.Parts).OfType<AgentMessagePart.Text>()
                .Any(static part => part.Value.Contains("Review before commit: on", StringComparison.Ordinal)));
        }

        Assert.IsFalse(host.RuntimeService.GetEffectiveReviewBeforeCommit(first.SessionId), "The setting asks for reviews; a headless run makes none.");
        Assert.IsFalse(host.RuntimeService.GetEffectiveReviewBeforeCommit(second.SessionId), "Nor does a session that chose it.");
        Assert.IsFalse((await host.Commands.GetReviewBeforeCommitAsync(second.SessionId)).Effective);
    }

    [TestMethod]
    public async Task UsualHost_KeepsItsPrompt_AndTheReviewBeforeCommitOfTheSetting()
    {
        await using var root = new HeadlessTestRoot();
        File.WriteAllText(Path.Combine(root.GlobalRoot, "config.toml"), "[review]\nbefore_commit = true\n");
        var provider = new ScriptedTurnProvider();
        var host = await root.CreateHostAsync(provider, readOnlyGlobalState: true);
        var options = HeadlessTestRoot.Options(provider, root.ProjectRoot);

        var session = await host.RuntimeService.CreateProjectSessionAsync(host.CurrentProject, options, "Usual");
        await host.RuntimeService.SendAsync(session, options, new AgentSendOptions { Input = AgentInput.Text("one") });

        Assert.IsTrue(provider.Requests.TryPeek(out var request));
        Assert.IsFalse(request.SystemMessage!.Contains(SessionRuntimeService.HeadlessGuidance, StringComparison.Ordinal));
        Assert.IsFalse(request.OmitReasoningSummaries, "CodeAlta Desktop shows the summaries of the reasoning.");
        Assert.IsTrue(host.RuntimeService.GetEffectiveReviewBeforeCommit(session.SessionId));
        Assert.IsTrue(request.Conversation.SelectMany(static message => message.Parts).OfType<AgentMessagePart.Text>()
            .Any(static part => part.Value.Contains("Review before commit: on", StringComparison.Ordinal)), "The agent is told of the review it waits for.");
    }

    [TestMethod]
    public async Task FullTrust_GrantsEveryRequest_WhateverTheModeOfTheSession_TheLoadOfASkillIncluded()
    {
        await using var root = new HeadlessTestRoot();
        var provider = new ScriptedTurnProvider();
        var host = await root.CreateHostAsync(provider, readOnlyGlobalState: true, fullTrust: true);
        var options = HeadlessTestRoot.Options(provider, root.ProjectRoot);
        var session = await host.Commands.CreateDraftSessionAsync(host.CurrentProject, provider.Descriptor, "Trusted", null, null, null,
            permissionMode: SessionPermissionModes.Ask);

        Assert.AreEqual(SessionPermissionPolicy.Approve, host.RuntimeService.GetPermissionPolicy(session.SessionId), "A mode that asks asks nobody.");
        var command = new AgentCommandPermissionRequest(provider.ProviderId, session.SessionId, DateTimeOffset.UtcNow, null, "i-1", null,
            "rm -rf build", root.ProjectRoot, null, null, null, null, null);
        Assert.AreEqual(AgentPermissionDecisionKind.AllowOnce, (await host.RuntimeService.Permissions.OwnedDefaultPermissionHandler(command, CancellationToken.None)).Kind);
        var skill = new AgentSkillPermissionRequest(provider.ProviderId, session.SessionId, DateTimeOffset.UtcNow, null, "i-2", "server", "skill",
            "skill://server/skill/SKILL.md", 1, 10, Dynamic: false);
        Assert.AreEqual(AgentPermissionDecisionKind.AllowOnce, (await host.RuntimeService.Permissions.RequestForSessionAsync(session.SessionId, skill, CancellationToken.None)).Kind,
            "The load of a skill, which asks the user in every mode, is granted too.");
        Assert.AreEqual(AgentPermissionDecisionKind.Deny, (await host.RuntimeService.Permissions.OwnedDefaultPermissionHandler(command with { OutsideSandbox = true }, CancellationToken.None)).Kind,
            "But not a command out of a sandbox: the user chose the sandbox, and nobody is there to let the command out.");
    }
}
