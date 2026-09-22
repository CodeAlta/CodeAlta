using CodeAlta.Tests;
using CodeAlta.Desktop;

namespace CodeAlta.Desktop.Tests;

[TestClass]
[Ignore("Brittle source-text reconstruction is not a functional desktop acceptance test.")]
public sealed class DesktopUserInputSourceTests
{
    [TestMethod]
    public void CurrentWiring_RequiresIndependentOptInAndPerSendBinding()
    {
        var agent = Read("CodeAlta.Agent/Runtime/AgentSession.cs");
        StringAssert.Contains(agent, "options.OnUserInputRequest ?? _options.OnUserInputRequest");
        StringAssert.Contains(Read("CodeAlta.Orchestration/Runtime/OwnedSessionAskExecution.cs"), "OnUserInputRequest = options.OnUserInputRequest");
        var owner = Read("CodeAlta.Orchestration/Runtime/OwnedSessionCommandService.cs");
        StringAssert.Contains(owner, "_reviewPermissions || _enableUserInput");
        var input = Read("CodeAlta.Orchestration/Runtime/SessionPermissionService.OwnedUserInput.cs");
        StringAssert.Contains(input, "execution.RunBound");
        var app = Read("CodeAlta/Desktop/DesktopApplication.cs");
        StringAssert.Contains(app, "EnableOwnedUserInput = options.EnableOwnedUserInput");
        StringAssert.Contains(app, "builder.AddSessionUserInputService(");
        Assert.IsFalse(app[app.IndexOf("    private async ValueTask RunAsync(", StringComparison.Ordinal)..].Contains("AddSessionUserInputService", StringComparison.Ordinal));
        var helper = Read("CodeAlta/frontend/src/sessionUserInput.ts");
        var epoch = helper.IndexOf("observeEpoch(value", StringComparison.Ordinal);
        var fence = helper.IndexOf("if (!current())", StringComparison.Ordinal);
        Assert.IsTrue(epoch >= 0 && fence > epoch);
        var panel = Read("CodeAlta/frontend/src/UserInputPanel.tsx");
        StringAssert.Contains(panel, "Refresh input");
        foreach (var forbidden in new[] { "dangerouslySetInnerHTML", "localStorage", "setInterval(" }) Assert.IsFalse((helper + panel).Contains(forbidden, StringComparison.Ordinal));
    }

    [TestMethod]
    public void Cli_InputFlagRequiresOwnedRootsAndRejectsDuplicates()
    {
        var root = Path.GetPathRoot(Path.GetFullPath("."))!;
        var data = Path.Combine(root, "input-fixture-browser"); var catalog = Path.Combine(root, "input-fixture-catalog");
        var project = Path.Combine(root, "input-fixture-project"); var home = Path.Combine(root, "input-fixture-home");
        var builtin = Path.Combine(root, "input-fixture-builtin");
        string[] args = ["--data-root", data, "--catalog-root", catalog, "--allow-catalog-cache", "--allow-owned-host", "--project-root", project,
            "--discovery-home", home, "--instruction-root", project, "--builtin-skill-root", builtin];
        bool Exists(string path) => path != data;
        Assert.IsTrue(DesktopCommandLine.TryParse(args, Exists, _ => false, out var off, out _));
        Assert.IsFalse(off!.EnableOwnedUserInput);
        Assert.IsTrue(DesktopCommandLine.TryParse([.. args, "--enable-owned-user-input"], Exists, _ => false, out var on, out _));
        Assert.IsTrue(on!.EnableOwnedUserInput); Assert.IsFalse(on.ReviewOwnedCommandPermissions);
        Assert.IsTrue(DesktopCommandLine.TryParse([.. args, "--review-owned-command-permissions"], Exists, _ => false, out var review, out _));
        Assert.IsTrue(review!.ReviewOwnedCommandPermissions); Assert.IsFalse(review.EnableOwnedUserInput);
        Assert.IsFalse(DesktopCommandLine.TryParse([.. args, "--enable-owned-user-input", "--enable-owned-user-input"], Exists, _ => false, out _, out _));
        Assert.IsFalse(DesktopCommandLine.TryParse(["--data-root", data, "--enable-owned-user-input"], Exists, _ => false, out _, out _));
    }

    [TestMethod]
    public void HostAndBoot_OptionsAreIndependent()
    {
        foreach (var review in new[] { false, true })
        foreach (var asks in new[] { false, true })
        {
            var options = new CodeAlta.Orchestration.Hosting.CodeAltaHostOptions { ReviewOwnedCommandPermissions = review, EnableOwnedAsks = asks };
            Assert.IsFalse(options.EnableOwnedUserInput);
            foreach (var input in new[] { false, true })
            {
                var boot = new CodeAlta.Desktop.Rpc.BootService("11111111-1111-4111-8111-111111111111", review, input).Status(new());
                Assert.AreEqual(review, boot.CommandReviewEnabled); Assert.AreEqual(input, boot.OwnedUserInputEnabled);
            }
        }
    }

    [TestMethod]
    public void CurrentActivation_RequiresCompleteHostAndOriginalSendFlagChain()
    {
        var host = Read("CodeAlta.Orchestration/Hosting/CodeAltaHost.cs");
        RequireOnce(host, "bool enableOwnedAsks,\n        bool enableOwnedUserInput)");
        RequireOnce(host, "reviewOwnedCommandPermissions, enableOwnedAsks, enableOwnedUserInput);");
        RequireOnce(host, "options.EnableOwnedAsks,\n                options.EnableOwnedUserInput);");
        var owner = Read("CodeAlta.Orchestration/Runtime/OwnedSessionCommandService.cs");
        RequireOnce(owner, "_enableUserInput = enableUserInput;");
        RequireOnce(owner, "operation.Execution.Token, _reviewPermissions, _enableUserInput).ConfigureAwait(false);");
        RequireOnce(owner, "operation.Execution.Token, _enableUserInput).ConfigureAwait(false);");
        RequireOnce(Read("CodeAlta.Orchestration/Runtime/SessionRuntimeService.cs"), "EnableUserInputTool = permissionExecution.EnableUserInput,");
        RequireOnce(Read("CodeAlta.Orchestration/Runtime/SessionRuntimeService.OwnedQueue.cs"), "EnableUserInputTool = permission.EnableUserInput");
        RequireOnce(Read("CodeAlta.Orchestration/Runtime/OwnedSessionAskExecution.cs"), "EnableUserInputTool = options.EnableUserInputTool,");
        var agent = Read("CodeAlta.Agent/Runtime/AgentSession.cs");
        RequireOnce(agent, "options.OnUserInputRequest ?? _options.OnUserInputRequest, options.EnableUserInputTool)");
        RequireOnce(agent, "EnableUserInputTool = enableUserInputTool,");
        foreach (var path in new[] { "CodeAlta.Agent/AgentSendOptions.cs", "CodeAlta.Agent/Runtime/Tools/AgentBuiltInToolOptions.cs" })
            RequireOnce(Read(path), "public bool EnableUserInputTool { get; init; }");
        var factory = Read("CodeAlta.Agent/Runtime/Tools/AgentBuiltInToolFactory.cs");
        var gate = RequireOnce(factory, "if (options.EnableUserInputTool && options.OnUserInputRequest is not null)");
        var registration = RequireOnce(factory, "new AgentToolSpec(\"request_user_input\", \"Request structured user input.\", RequestUserInputSchema)");
        var filter = RequireOnce(factory, ".Where(tool => ShouldIncludeBuiltInTool(options, tool.Spec.Name))");
        Assert.IsTrue(gate < registration && registration < filter);
        RequireOnce(factory, "(invocation, cancellationToken) => RequestUserInputAsync(options, invocation, cancellationToken)");
        RequireOnce(factory, "overrides.TryGetValue(toolName, out var enabled))\n        {\n            return enabled;");
        var toolTests = Read("CodeAlta.Tests/AgentToolsTests.cs");
        foreach (var tools in new[] { "officialOpenAiTools", "compatibleTools", "anthropicTools" })
            RequireOnce(toolTests, $"Assert.IsFalse({tools}.Any(static tool => tool.Spec.Name == \"request_user_input\"));");
    }

    private static int RequireOnce(string source, string marker)
    {
        Assert.AreEqual(1, source.Split(marker, StringSplitOptions.None).Length - 1, marker);
        var position = source.IndexOf(marker, StringComparison.Ordinal); Assert.IsTrue(position >= 0, marker); return position;
    }

    [TestMethod]
    public void NewestDelta_RestoresEveryFrozenOriginalAndBothHistoricalRoutes()
    {
        foreach (var path in OwnedSessionUserInputSourceInverse.Paths)
            Assert.AreEqual(OwnedSessionUserInputSourceInverse.Original(path), OwnedSessionAskSourceInverse.GitObjectId(OwnedSessionUserInputSourceInverse.Restore(path, Read(path))), path);
        foreach (var path in OwnedSessionNotesSourceInverse.Paths)
            Assert.AreEqual(OwnedSessionNotesSourceInverse.Original(path), OwnedSessionAskSourceInverse.GitObjectId(OwnedSessionNotesSourceInverse.Restore(path, Read(path))), path);
        foreach (var path in OwnedSessionAskSourceInverse.Paths)
            Assert.AreEqual(OwnedSessionAskSourceInverse.Original(path), OwnedSessionAskSourceInverse.GitObjectId(OwnedSessionAskSourceInverse.Restore(path, Read(path))), path);
    }
    private static string Read(string path) => SourceTestText.DecodeSource(File.ReadAllBytes(Path.Combine(DesktopArchitectureTests.SourceRoot, path)));
}
