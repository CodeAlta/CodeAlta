using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugins.Tests;

// The hooks of tool calls, as the adapter composes them: each plugin in turn, with its own limit.
[TestClass]
public sealed class PluginToolHookTests
{
    private static readonly AgentToolInvocation ShellCall = new(new ModelProviderId("local"), "session", "call-1", "shell_command",
        JsonSerializer.SerializeToElement(new { command = "Get-ChildItem" }));

    [TestMethod]
    public async Task ToolCallHooks_RunInOrder_EachSeesTheArgumentsTheOneBeforeLeft_AndTheFirstBlockWins()
    {
        var (registry, plugins) = await ActivateAsync(typeof(FirstHookPlugin), typeof(SecondHookPlugin), typeof(ThirdHookPlugin));
        var adapter = new PluginContributionAdapterService(registry);
        var seen = new List<string>();
        Hooks<FirstHookPlugin>(plugins).OnCall = context =>
        {
            seen.Add("first:" + context.ShellCommand!.Command);
            return PluginToolCallResult.ReplaceArguments(JsonSerializer.SerializeToElement(new { command = "Get-ChildItem -Force" }));
        };
        Hooks<SecondHookPlugin>(plugins).OnCall = context =>
        {
            seen.Add("second:" + context.Arguments.GetProperty("command").GetString() + "|" + context.ShellCommand!.Command);
            return PluginToolCallResult.Block("Missing closing quote at 1:12.");
        };
        Hooks<ThirdHookPlugin>(plugins).OnCall = context =>
        {
            seen.Add("third");
            return null;
        };

        var result = await adapter.RunToolCallHooksAsync(plugins, Template(plugins[0], ShellCall, shell: "Get-ChildItem"));

        CollectionAssert.AreEqual(new[] { "first:Get-ChildItem", "second:Get-ChildItem -Force|Get-ChildItem -Force" }, seen, "the third plugin is not asked once a call is blocked");
        Assert.AreEqual(PluginToolCallDisposition.Block, result.Result!.Disposition);
        Assert.AreEqual("Missing closing quote at 1:12.", result.Result.BlockReason);
        Assert.AreEqual("Blocked by Second hook: Missing closing quote at 1:12.", result.BlockMessage);
        Assert.AreEqual(0, result.Diagnostics.Count);
        await DeactivateAsync(plugins);
    }

    [TestMethod]
    public async Task ToolCallHooks_ThatReplaceArguments_GiveTheLastArguments_AndAPluginWithoutHooksIsNotAsked()
    {
        var (registry, plugins) = await ActivateAsync(typeof(FirstHookPlugin), typeof(SecondHookPlugin), typeof(NoHookPlugin));
        var adapter = new PluginContributionAdapterService(registry);
        Hooks<FirstHookPlugin>(plugins).OnCall = _ => PluginToolCallResult.ReplaceArguments(JsonSerializer.SerializeToElement(new { command = "a" }));
        Hooks<SecondHookPlugin>(plugins).OnCall = context => PluginToolCallResult.ReplaceArguments(
            JsonSerializer.SerializeToElement(new { command = context.Arguments.GetProperty("command").GetString() + "b" }));

        var result = await adapter.RunToolCallHooksAsync(plugins, Template(plugins[0], ShellCall));

        Assert.AreEqual(PluginToolCallDisposition.ReplaceArguments, result.Result!.Disposition);
        Assert.AreEqual("ab", result.Result.ReplacementArguments!.Value.GetProperty("command").GetString());
        Assert.IsTrue(PluginContributionAdapterService.HasToolHooks(plugins));
        Assert.IsFalse(PluginContributionAdapterService.HasToolHooks([plugins[2]]), "a plugin that overrides no tool hook is not called for each call");
        await DeactivateAsync(plugins);
    }

    [TestMethod]
    public async Task AToolCallHookThatDoesNotAnswer_IsLeftBehind_AndTheCallGoesOn_UnlessItsPluginFailsClosed()
    {
        var (registry, plugins) = await ActivateAsync(typeof(SlowHookPlugin), typeof(SecondHookPlugin));
        var adapter = new PluginContributionAdapterService(registry);
        var never = new TaskCompletionSource<PluginToolCallResult?>();
        Hooks<SlowHookPlugin>(plugins).OnCallAsync = (_, _) => new ValueTask<PluginToolCallResult?>(never.Task);
        var secondAsked = 0;
        Hooks<SecondHookPlugin>(plugins).OnCall = _ =>
        {
            secondAsked++;
            return null;
        };

        var open = await adapter.RunToolCallHooksAsync(plugins, Template(plugins[0], ShellCall));

        Assert.IsNull(open.Result, "a hook that does not answer in time does not decide");
        Assert.AreEqual(1, secondAsked, "the next plugin is still asked");
        StringAssert.Contains(open.Diagnostics.Single().Message, "did not answer in time");
        await DeactivateAsync(plugins);

        (registry, plugins) = await ActivateAsync(typeof(SlowFailClosedHookPlugin));
        adapter = new PluginContributionAdapterService(registry);
        Hooks<SlowFailClosedHookPlugin>(plugins).OnCallAsync = (_, _) => new ValueTask<PluginToolCallResult?>(never.Task);

        var closed = await adapter.RunToolCallHooksAsync(plugins, Template(plugins[0], ShellCall));

        Assert.AreEqual(PluginToolCallDisposition.Block, closed.Result!.Disposition);
        StringAssert.StartsWith(closed.BlockMessage, "Blocked by Slow fail-closed hook: its check did not answer within 0.1 seconds");
        await DeactivateAsync(plugins);
    }

    [TestMethod]
    public async Task AToolCallHookThatThrows_IsADiagnostic_AndBlocksOnlyForAPluginThatFailsClosed()
    {
        var (registry, plugins) = await ActivateAsync(typeof(FirstHookPlugin), typeof(FailClosedHookPlugin));
        var adapter = new PluginContributionAdapterService(registry);
        Hooks<FirstHookPlugin>(plugins).OnCall = _ => throw new InvalidOperationException("parser crashed");
        Hooks<FailClosedHookPlugin>(plugins).OnCall = _ => null;

        var open = await adapter.RunToolCallHooksAsync(plugins, Template(plugins[0], ShellCall));

        Assert.IsNull(open.Result);
        Assert.AreEqual("Tool-call hook failed.", open.Diagnostics.Single().Message);

        Hooks<FailClosedHookPlugin>(plugins).OnCall = _ => throw new InvalidOperationException("pwsh is missing");
        var closed = await adapter.RunToolCallHooksAsync(plugins, Template(plugins[0], ShellCall));

        Assert.AreEqual("Blocked by Fail-closed hook: its check failed (pwsh is missing)", closed.BlockMessage);
        Assert.AreEqual(2, closed.Diagnostics.Count);
        await DeactivateAsync(plugins);
    }

    [TestMethod]
    public async Task AHookOfAToolTheAgentRunsItself_CanBlock_ButItsArgumentChangeIsIgnored()
    {
        var (registry, plugins) = await ActivateAsync(typeof(FirstHookPlugin));
        var adapter = new PluginContributionAdapterService(registry);
        PluginToolCallContext? seen = null;
        Hooks<FirstHookPlugin>(plugins).OnCall = context =>
        {
            seen = context;
            return PluginToolCallResult.ReplaceArguments(JsonSerializer.SerializeToElement(new { command = "other" }));
        };
        var template = Template(plugins[0], ShellCall with { ToolName = "Bash" }, shell: "Get-ChildItem");
        var agentTemplate = new PluginToolCallContext
        {
            Plugin = template.Plugin,
            Services = template.Services,
            Invocation = template.Invocation,
            ToolKind = PluginToolKind.Agent,
            ShellCommand = template.ShellCommand,
            Capabilities = PluginToolHookCapabilities.Block,
        };

        var result = await adapter.RunToolCallHooksAsync(plugins, agentTemplate);

        Assert.IsNull(result.Result);
        Assert.AreEqual(PluginDiagnosticSeverity.Warning, result.Diagnostics.Single().Severity);
        Assert.IsTrue(seen!.CanBlock);
        Assert.IsFalse(seen.CanModifyArguments);
        Assert.IsFalse(seen.CanChangeResult);
        Assert.AreEqual(PluginToolKind.Agent, seen.ToolKind);
        Assert.AreEqual("Bash", seen.ToolName);
        await DeactivateAsync(plugins);
    }

    [TestMethod]
    public async Task ToolResultHooks_RunInOrder_OnTheResultTheOneBeforeLeft_AndDetailsAreKeptByPlugin()
    {
        var (registry, plugins) = await ActivateAsync(typeof(FirstHookPlugin), typeof(SecondHookPlugin), typeof(ThirdHookPlugin), typeof(SlowHookPlugin));
        var adapter = new PluginContributionAdapterService(registry);
        Hooks<FirstHookPlugin>(plugins).OnResult = _ => PluginToolResult.Replace(new AgentToolResult(false, [new AgentToolResultItem.Text("exit 1")], "exit 1"));
        Hooks<SecondHookPlugin>(plugins).OnResult = context => PluginToolResult.Append($"Hint for {context.ToolName}: {((AgentToolResultItem.Text)context.Result.Items[0]).Value} means a parse error.");
        Hooks<ThirdHookPlugin>(plugins).OnResult = _ => PluginToolResult.AttachDetails(JsonSerializer.SerializeToElement(new { diagnostics = 1 }));
        var never = new TaskCompletionSource<PluginToolResult?>();
        Hooks<SlowHookPlugin>(plugins).OnResultAsync = (_, _) => new ValueTask<PluginToolResult?>(never.Task);

        var result = await adapter.RunToolResultHooksAsync(plugins, new PluginToolResultContext
        {
            Plugin = plugins[0].Descriptor,
            Services = plugins[0].RuntimeContext.Services,
            Invocation = ShellCall,
            Result = new AgentToolResult(true, [new AgentToolResultItem.Text("ok")]),
        });

        Assert.IsTrue(result.Changed);
        Assert.IsFalse(result.Result.Success);
        CollectionAssert.AreEqual(new[] { "exit 1", "Hint for shell_command: exit 1 means a parse error." },
            result.Result.Items.OfType<AgentToolResultItem.Text>().Select(static item => item.Value).ToArray());
        Assert.AreEqual(1, result.Result.Details!.Value.GetProperty(PluginIdOf(plugins[2])).GetProperty("diagnostics").GetInt32());
        StringAssert.Contains(result.Diagnostics.Single().Message, "did not answer in time");

        // The former form gives the whole result as a replacement.
        var (replacement, _) = await adapter.OnToolResultAsync(plugins, new PluginToolResultContext
        {
            Plugin = plugins[0].Descriptor,
            Services = plugins[0].RuntimeContext.Services,
            Invocation = ShellCall,
            Result = new AgentToolResult(true, [new AgentToolResultItem.Text("ok")]),
        });
        Assert.AreEqual(PluginToolResultDisposition.Replace, replacement!.Disposition);
        Assert.AreEqual(2, replacement.ReplacementResult!.Items.Count);
        await DeactivateAsync(plugins);
    }

    [TestMethod]
    public void HookSettings_AreReadFromThePlugin_AndKeptWithinTheirBounds()
    {
        Assert.AreEqual(PluginBase.DefaultHookTimeout, TimeSpan.FromSeconds(10));
        Assert.IsFalse(new NoHookPlugin().ToolHooksFailClosed);
        Assert.IsTrue(new FailClosedHookPlugin().ToolHooksFailClosed);
        Assert.AreEqual(TimeSpan.FromMilliseconds(1), new SlowHookPlugin().HookTimeout);
    }

    private static T Hooks<T>(IReadOnlyList<ActivePluginInstance> plugins) where T : HookPlugin
        => plugins.Select(static plugin => plugin.Instance).OfType<T>().Single();

    private static string PluginIdOf(ActivePluginInstance active)
        => PluginContributionRegistry.PluginIdOf(active.Descriptor.RuntimeKey, active.SourcePackage?.PackageId);

    private static PluginToolCallContext Template(ActivePluginInstance seed, AgentToolInvocation invocation, string? shell = null)
        => new()
        {
            Plugin = seed.Descriptor,
            Services = seed.RuntimeContext.Services,
            Invocation = invocation,
            ShellCommand = shell is null ? null : new PluginShellCommand(shell, Environment.CurrentDirectory),
        };

    private static async Task<(PluginContributionRegistry Registry, IReadOnlyList<ActivePluginInstance> Plugins)> ActivateAsync(params Type[] types)
    {
        var registry = new PluginContributionRegistry();
        var activator = new PluginRuntimeActivator(registry);
        var plugins = new List<ActivePluginInstance>();
        foreach (var type in types)
        {
            var result = await activator.ActivateAsync(new DiscoveredPluginType { Type = type, Descriptor = PluginDescriptorFactory.FromType(type) }, null, null,
                new PluginActivationOptions
                {
                    HostInfo = new PluginHostInfo
                    {
                        ApplicationName = "CodeAlta.Tests",
                        Version = "1.0.0",
                        HostApiVersion = "1.0.0",
                        UserDataDirectory = Path.GetTempPath(),
                        IsHeadless = true,
                    },
                });
            Assert.IsTrue(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
            plugins.Add(result.ActivePlugin!);
        }

        return (registry, plugins);
    }

    private static async Task DeactivateAsync(IReadOnlyList<ActivePluginInstance> plugins)
    {
        foreach (var plugin in plugins) await plugin.DeactivateAsync(TimeSpan.FromSeconds(5));
    }

    public abstract class HookPlugin : PluginBase
    {
        public Func<PluginToolCallContext, PluginToolCallResult?>? OnCall { get; set; }

        public Func<PluginToolCallContext, CancellationToken, ValueTask<PluginToolCallResult?>>? OnCallAsync { get; set; }

        public Func<PluginToolResultContext, PluginToolResult?>? OnResult { get; set; }

        public Func<PluginToolResultContext, CancellationToken, ValueTask<PluginToolResult?>>? OnResultAsync { get; set; }

        public override ValueTask<PluginToolCallResult?> OnToolCallAsync(PluginToolCallContext context, CancellationToken cancellationToken = default)
            => OnCallAsync is { } asynchronous ? asynchronous(context, cancellationToken) : ValueTask.FromResult(OnCall?.Invoke(context));

        public override ValueTask<PluginToolResult?> OnToolResultAsync(PluginToolResultContext context, CancellationToken cancellationToken = default)
            => OnResultAsync is { } asynchronous ? asynchronous(context, cancellationToken) : ValueTask.FromResult(OnResult?.Invoke(context));
    }

    [Plugin("first-hook", DisplayName = "First hook")]
    public sealed class FirstHookPlugin : HookPlugin;

    [Plugin("second-hook", DisplayName = "Second hook")]
    public sealed class SecondHookPlugin : HookPlugin;

    [Plugin("third-hook", DisplayName = "Third hook")]
    public sealed class ThirdHookPlugin : HookPlugin;

    [Plugin("slow-hook", DisplayName = "Slow hook")]
    public sealed class SlowHookPlugin : HookPlugin
    {
        // Below the minimum: the host waits 100 milliseconds.
        public override TimeSpan HookTimeout => TimeSpan.FromMilliseconds(1);
    }

    [Plugin("slow-fail-closed-hook", DisplayName = "Slow fail-closed hook")]
    public sealed class SlowFailClosedHookPlugin : HookPlugin
    {
        public override TimeSpan HookTimeout => TimeSpan.FromMilliseconds(100);

        public override bool ToolHooksFailClosed => true;
    }

    [Plugin("fail-closed-hook", DisplayName = "Fail-closed hook")]
    public sealed class FailClosedHookPlugin : HookPlugin
    {
        public override bool ToolHooksFailClosed => true;
    }

    [Plugin("no-hook", DisplayName = "No hook")]
    public sealed class NoHookPlugin : PluginBase;
}
