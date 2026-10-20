using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime.Tools;

namespace CodeAlta.Tests;

/// <summary>What the host recognizes of a commit in a command line, and the note it adds to a prompt.</summary>
[TestClass]
public sealed class AgentCommandGuardTests
{
    [TestMethod]
    public void IsCommit_SeesGitCommitInEveryCommandOfALine()
    {
        foreach (var command in new[]
        {
            "git commit -m \"Fix it\"", "git commit", "  git   commit -am x", "git -C ../other commit -m x", "git -c user.name=x commit",
            "git --no-pager commit --amend", "/usr/bin/git commit", "C:\\Program Files\\Git\\bin\\git.exe commit", "dotnet test && git commit -m done",
            "git add -A; git commit -m x", "npm test || git commit -m x", "cd src\ngit commit -m x", "(git commit -m x)", "sudo git commit -m x",
            "GIT_AUTHOR_NAME=x git commit -m y",
        })
        {
            Assert.IsTrue(AgentGitCommands.IsCommit(command), command);
        }

        foreach (var command in new[] { null, "", "git status", "git commit-tree abc", "git log --grep commit", "git show HEAD:commit", "gitcommit", "git -C commit status" })
        {
            Assert.IsFalse(AgentGitCommands.IsCommit(command), command ?? "null");
        }

        // A guardrail errs on the side of asking for a review: the words "git commit" anywhere in a command count.
        Assert.IsTrue(AgentGitCommands.IsCommit("echo git commit"));
    }

    [TestMethod]
    public void AHostNote_IsKeptInTheInput_AsAKindOfItsOwn()
    {
        var input = new AgentInput([new AgentInputItem.Text("Go on."), new AgentInputItem.HostContext("review", "Review before commit: on.")]);
        var json = JsonSerializer.Serialize(input, AgentJsonSerializerContext.Default.AgentInput);
        StringAssert.Contains(json, "\"$type\":\"hostContext\"");
        var read = JsonSerializer.Deserialize(json, AgentJsonSerializerContext.Default.AgentInput)!;
        Assert.AreEqual(new AgentInputItem.HostContext("review", "Review before commit: on."), read.Items[1]);
    }
}
