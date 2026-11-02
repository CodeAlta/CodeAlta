using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.LiveTool;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Tests;

public sealed partial class AltaLiveToolTests
{
    [TestMethod]
    public async Task SessionSetWorktree_SendsASessionToAnotherCheckoutOfItsRepository_AndBack()
    {
        using var root = TempDirectory.Create();
        // A project in the main checkout of a repository and a worktree of it elsewhere, as git lays them out.
        var projectPath = Directory.CreateDirectory(Path.Combine(root.Path, "project")).FullName;
        var linked = Directory.CreateDirectory(Path.Combine(projectPath, ".git", "worktrees", "feature")).FullName;
        File.WriteAllText(Path.Combine(linked, "commondir"), "../..");
        var tree = Directory.CreateDirectory(Path.Combine(root.Path, "feature")).FullName;
        File.WriteAllText(Path.Combine(tree, ".git"), $"gitdir: {linked}");
        var clone = Directory.CreateDirectory(Path.Combine(root.Path, "clone")).FullName;
        Directory.CreateDirectory(Path.Combine(clone, ".git"));

        var options = new CatalogOptions { GlobalRoot = Path.Combine(root.Path, "global") };
        Directory.CreateDirectory(options.GlobalRoot);
        var projectCatalog = new ProjectCatalog(options);
        var project = await projectCatalog.UpsertFromPathAsync(projectPath).ConfigureAwait(false);
        var providerId = new ModelProviderId("worktree-switch");
        var runtime = CreateRuntime(options, new StatefulProviderRuntime(providerId));
        await using var _ = runtime.ConfigureAwait(false);
        var owner = new OwnedSessionCommandService(runtime, projectCatalog, options, 8, () => true);
        await using var __ = owner.ConfigureAwait(false);
        var sessionId = (await owner.CreateDraftSessionAsync(project, new ModelProviderDescriptor(providerId, "Switch"), "Plan the parser").ConfigureAwait(false)).SessionId;
        var dispatcher = CreateDispatcher(new AltaServiceCollection()
            .Add(options)
            .Add(projectCatalog)
            .Add(new SessionViewCatalog(options))
            .Add(runtime)
            .Add(owner));

        foreach (var (args, code) in new (string[], string)[]
                 {
                     (["session", "set_worktree", sessionId], "usage.missingTarget"),
                     (["session", "set_worktree", sessionId, "--path", tree, "--project-folder"], "usage.missingTarget"),
                     (["session", "set_worktree", sessionId, "--path", tree, "--base", "main"], "usage.invalidBase"),
                     (["session", "set_worktree", sessionId, "--path", "relative"], "usage.invalidPath"),
                 })
        {
            var refused = await dispatcher.InvokeAsync(args, caller: AltaCallerIdentity.Cli).ConfigureAwait(false);
            Assert.AreEqual(AltaExitCodes.Usage, refused.ExitCode, string.Join(' ', args));
            StringAssert.Contains(refused.Stdout + refused.Stderr, code);
        }

        var other = await dispatcher.InvokeAsync(["session", "set_worktree", sessionId, "--path", clone], caller: AltaCallerIdentity.Cli).ConfigureAwait(false);
        Assert.AreEqual(AltaExitCodes.Unsupported, other.ExitCode);
        StringAssert.Contains(other.Stdout + other.Stderr, "worktree.otherRepository");
        StringAssert.Contains(other.Stdout + other.Stderr, "git worktree add");

        static JsonElement Set(string output) => ReadJsonLines(output).Single(static line => line.GetProperty("type").GetString() == "alta.session.worktree_set");
        static string? Worktree(JsonElement record)
            => record.TryGetProperty("worktreeDirectory", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        var moved = await dispatcher.InvokeAsync(["session", "set_worktree", sessionId, "--path", tree], caller: AltaCallerIdentity.Cli).ConfigureAwait(false);
        Assert.AreEqual(AltaExitCodes.Success, moved.ExitCode, moved.Stderr);
        Assert.AreEqual("ok", Set(moved.Stdout).GetProperty("status").GetString());
        Assert.AreEqual(tree, Worktree(Set(moved.Stdout)));
        Assert.AreEqual(projectPath, Set(moved.Stdout).GetProperty("workingDirectory").GetString());
        Assert.AreEqual(tree, await runtime.GetSessionWorktreeAsync(sessionId).ConfigureAwait(false));
        var info = await dispatcher.InvokeAsync(["session", "info", sessionId], caller: AltaCallerIdentity.Cli).ConfigureAwait(false);
        Assert.IsTrue(ReadJsonLines(info.Stdout).Any(line => line.TryGetProperty("worktreeDirectory", out var folder) && folder.GetString() == tree));

        var back = await dispatcher.InvokeAsync(["session", "set_worktree", sessionId, "--project-folder"], caller: AltaCallerIdentity.Cli).ConfigureAwait(false);
        Assert.AreEqual(AltaExitCodes.Success, back.ExitCode, back.Stderr);
        Assert.AreEqual("ok", Set(back.Stdout).GetProperty("status").GetString());
        Assert.IsNull(Worktree(Set(back.Stdout)));
        Assert.IsNull(await runtime.GetSessionWorktreeAsync(sessionId).ConfigureAwait(false));

        // A sandboxed session writes in the folders of its project: it is not sent to a checkout outside them.
        Assert.IsTrue(AltaSandboxGate.Allows("alta session set_worktree"));
        var sandboxed = CreateDispatcher(new AltaServiceCollection()
            .Add(options)
            .Add(projectCatalog)
            .Add(new SessionViewCatalog(options))
            .Add(runtime)
            .Add(owner)
            .Add(new AltaSandboxedSessions((_, _) => Task.FromResult<AltaSessionSandbox?>(new AltaSessionSandbox("workspace", [])))));
        var caller = new AltaCallerIdentity { Kind = "agent", SourceSessionId = sessionId, SourceProjectId = project.Id };
        var denied = await sandboxed.InvokeAsync(["session", "set_worktree", "--path", tree], caller: caller).ConfigureAwait(false);
        Assert.AreEqual(AltaExitCodes.PolicyDenied, denied.ExitCode, denied.Stderr);
        StringAssert.Contains(denied.Stdout + denied.Stderr, "sandbox.folderDenied");
        Assert.IsNull(await runtime.GetSessionWorktreeAsync(sessionId).ConfigureAwait(false));
    }
}
