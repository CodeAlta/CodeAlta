using System.Diagnostics;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog.Reviews;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Orchestration.Tests;

/// <summary>
/// The review before commit as a session runs: the agent is told with a note of its prompt when the choice changes,
/// never through its instructions, and a commit is refused until a review approves the work tree.
/// </summary>
[TestClass]
public sealed class ReviewBeforeCommitRuntimeTests
{
    [TestMethod]
    public async Task TheChoice_ReachesTheAgentAsANoteOfItsPrompt_AndTheInstructionsStayTheSame()
    {
        using var temp = TempDirectory.Create();
        var provider = new RecordingProvider();
        await using var host = await CreateHostAsync(temp, provider);
        var sessionId = (await host.Commands.CreateDraftSessionAsync(host.CurrentProject, provider.Descriptor, null)).SessionId;

        // Off, as the setting says: nothing to tell.
        await SendAsync(host, sessionId, "Fix the bug.");
        Assert.IsFalse(provider.LastUserText.Contains("codealta-context", StringComparison.Ordinal));

        // Turned on for this session, during its life: told once, with the next prompt.
        var on = await host.Commands.SetReviewBeforeCommitAsync(sessionId, true);
        Assert.AreEqual(("ok", true, true, false), (on.Status, on.Own, on.Effective, on.Setting));
        await SendAsync(host, sessionId, "Go on.");
        StringAssert.Contains(provider.LastUserText, "<codealta-context>Review before commit: on.");
        StringAssert.Contains(provider.LastUserText, "Go on.");
        await SendAsync(host, sessionId, "And again.");
        Assert.IsFalse(provider.LastUserText.Contains("codealta-context", StringComparison.Ordinal), "Told once.");

        // Back to the setting, which is off: told that it is off.
        await host.Commands.SetReviewBeforeCommitAsync(sessionId, null);
        await SendAsync(host, sessionId, "Last one.");
        StringAssert.Contains(provider.LastUserText, "Review before commit: off.");

        // The instructions of every turn are the same text: the cache of the provider is kept.
        Assert.HasCount(4, provider.Instructions);
        Assert.AreEqual(1, provider.Instructions.Distinct().Count());
        Assert.AreEqual(1, provider.SystemMessages.Distinct().Count());
        var read = await host.Commands.GetReviewBeforeCommitAsync(sessionId);
        Assert.AreEqual(("ok", null, false), (read.Status, read.Own, read.Effective));
    }

    [TestMethod]
    public async Task ACommit_IsRefusedUntilAReviewApprovesTheWorkTree()
    {
        using var temp = TempDirectory.Create();
        if (!Git(temp.ProjectRoot, "init", "-q", "-b", "main"))
        {
            Assert.Inconclusive("git is not installed.");
        }

        foreach (var (name, value) in new[] { ("user.name", "Test"), ("user.email", "test@example.invalid"), ("commit.gpgsign", "false") }) Git(temp.ProjectRoot, "config", name, value);
        File.WriteAllText(Path.Combine(temp.ProjectRoot, "a.txt"), "one\n");
        Git(temp.ProjectRoot, "add", "-A");
        Git(temp.ProjectRoot, "commit", "-q", "-m", "one");

        var provider = new RecordingProvider();
        await using var host = await CreateHostAsync(temp, provider);
        var reviews = new ReviewService(Path.Combine(temp.Root, "state"));
        host.RuntimeService.Reviews = reviews;
        var sessionId = (await host.Commands.CreateDraftSessionAsync(host.CurrentProject, provider.Descriptor, null)).SessionId;
        await SendAsync(host, sessionId, "Work.");
        var guard = provider.CommandGuard!;

        // Off: nothing is refused.
        Assert.IsNull(await guard("git commit -m x", temp.ProjectRoot, default));

        await host.Commands.SetReviewBeforeCommitAsync(sessionId, true);
        File.WriteAllText(Path.Combine(temp.ProjectRoot, "a.txt"), "one\ntwo\n");
        foreach (var command in new[] { "git commit -m x", "git -C . commit -am x", "npm test && git commit -m done", "/usr/bin/git --no-pager commit" })
            StringAssert.Contains(await guard(command, temp.ProjectRoot, default), "alta review request changes", command);
        Assert.IsNull(await guard("git status", temp.ProjectRoot, default));
        Assert.IsNull(await guard("git commit-tree abc", temp.ProjectRoot, default));

        // The user approves the work tree as it is: it may be committed, in several commits.
        var scope = new ReviewScope(host.CurrentProject, null);
        reviews.CommitRound(scope, await reviews.PrepareRoundAsync(scope, new(ReviewTargets.Changes, null, ReviewVerdicts.Approved, null, sessionId), default));
        Assert.IsNull(await guard("git commit -am approved", temp.ProjectRoot, default));

        // An edit after the approval needs a new review.
        File.WriteAllText(Path.Combine(temp.ProjectRoot, "a.txt"), "one\ntwo\nthree\n");
        Assert.IsNotNull(await guard("git commit -am more", temp.ProjectRoot, default));
    }

    private static bool Git(string folder, params string[] arguments)
    {
        try
        {
            var start = new ProcessStartInfo("git") { WorkingDirectory = folder, RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
            start.Environment["GIT_CONFIG_GLOBAL"] = Path.Combine(folder, "..", "no-global-config");
            using var process = Process.Start(start)!;
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static async Task SendAsync(CodeAltaHost host, string sessionId, string prompt)
    {
        var receipt = host.Commands.AdmitSend(new(Guid.NewGuid().ToString("N"), sessionId, prompt)).Receipt;
        Assert.IsNotNull(receipt);
        var result = await receipt.Completion.WaitAsync(TestDeadlines.Hang);
        Assert.AreEqual(OwnedSessionCommandOutcome.Completed, result.Outcome, result.Code);
    }

    private static Task<CodeAltaHost> CreateHostAsync(TempDirectory temp, RecordingProvider provider)
        => CodeAltaHost.CreateAsync(
            new CodeAltaHostOptions
            {
                GlobalRoot = temp.GlobalRoot,
                CurrentProjectPath = temp.ProjectRoot,
                IsHeadless = true,
                AutoApproveOwnedPermissions = true,
                OwnedCommandReceiptCapacity = 32,
                ConfigureModelProviders = registry => registry.RegisterOrReplace(provider.Descriptor, () => provider),
            },
            CancellationToken.None);

    private sealed class RecordingProvider : IAgentModelProviderRuntime, IModelProviderTurnExecutor
    {
        public List<string?> Instructions { get; } = [];

        public List<string?> SystemMessages { get; } = [];

        public string LastUserText { get; private set; } = string.Empty;

        public CodeAlta.Agent.Runtime.Tools.AgentCommandGuard? CommandGuard { get; private set; }

        public ModelProviderDescriptor Descriptor { get; } = new(new ModelProviderId("fake-review"), "Fake Review") { DefaultModelId = "fake-model" };

        public ModelProviderRuntimeDescriptor RuntimeDescriptor { get; } = new()
        {
            ProtocolFamily = "test", ProviderKey = "fake-review", DisplayName = "Fake Review", TransportKind = AgentTransportKind.OpenAIResponses,
        };

        public IModelProviderModelCatalog? ModelCatalog => null;

        public AgentRuntimeProviderRegistration CreateProviderRegistration() => new() { Provider = RuntimeDescriptor, TurnExecutor = this };

        public IModelProviderTurnExecutor CreateTurnExecutor() => this;

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<ModelProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new ModelProviderProbeResult
            {
                ProviderId = Descriptor.ProviderId,
                Availability = ModelProviderAvailability.Ready,
                Models = [new AgentModelInfo("fake-model", DisplayName: "Fake Model")],
                SelectedModelId = "fake-model",
            });

        public Task<AgentTurnResponse> ExecuteTurnAsync(AgentTurnRequest request, Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate, CancellationToken cancellationToken = default)
        {
            lock (Instructions)
            {
                Instructions.Add(request.DeveloperInstructions);
                SystemMessages.Add(request.SystemMessage);
                CommandGuard = request.CommandGuard;
                var user = request.Conversation.Last(static message => message.Role == AgentConversationRole.User);
                LastUserText = string.Join("\n", user.Parts.OfType<AgentMessagePart.Text>().Select(static part => part.Value));
            }

            return Task.FromResult(new AgentTurnResponse
            {
                AssistantMessage = new AgentConversationMessage(AgentConversationRole.Assistant, [new AgentMessagePart.Text("Done.")]),
            });
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string root)
        {
            Root = root;
            GlobalRoot = Directory.CreateDirectory(Path.Combine(root, "global")).FullName;
            ProjectRoot = Directory.CreateDirectory(Path.Combine(root, "project")).FullName;
        }

        public string Root { get; }

        public string GlobalRoot { get; }

        public string ProjectRoot { get; }

        public static TempDirectory Create()
        {
            var root = Path.Combine(Path.GetTempPath(), $"CodeAlta.Review.{Guid.NewGuid():N}");
            // The real path: git answers with it, and a temporary folder may be reached through a link.
            if (Directory.ResolveLinkTarget(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), returnFinalTarget: true) is { } target)
            {
                root = Path.Combine(target.FullName, Path.GetFileName(root));
            }

            return new(root);
        }

        public void Dispose()
        {
            try
            {
                // Git writes its objects read-only, which Windows refuses to delete as they are.
                foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(Root, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A folder still held by a process that is ending is left to the cleanup of the temporary folder.
            }
        }
    }
}
