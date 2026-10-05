using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugins.Tests;

/// <summary>Plugins that name the applications they support, and the contributions a host reads for its window.</summary>
[TestClass]
public sealed class PluginFrontendTests
{
    [TestMethod]
    public void Supports_ComparesThePluginWithTheApplicationOfTheHost()
    {
        Assert.IsTrue(PluginFrontends.All.Supports(PluginFrontends.Desktop));
        Assert.IsTrue(PluginFrontends.All.Supports(PluginFrontends.Terminal));
        Assert.IsTrue(PluginFrontends.Desktop.Supports(PluginFrontends.Desktop));
        Assert.IsFalse(PluginFrontends.Desktop.Supports(PluginFrontends.Terminal));
        Assert.IsFalse(PluginFrontends.Terminal.Supports(PluginFrontends.Desktop));
        Assert.IsFalse(PluginFrontends.None.Supports(PluginFrontends.Desktop));
        // A host that names no application (a headless tool) starts every plugin.
        Assert.IsTrue(PluginFrontends.Terminal.Supports(PluginFrontends.None));
        Assert.AreEqual("desktop application", PluginFrontends.Desktop.ToDisplayName());
        Assert.AreEqual("terminal application", PluginFrontends.Terminal.ToDisplayName());
    }

    [TestMethod]
    public void Descriptor_CarriesTheApplicationsOfTheAttribute()
    {
        Assert.AreEqual(PluginFrontends.Terminal, PluginDescriptorFactory.FromType(typeof(TerminalOnlyPlugin)).Frontends);
        Assert.AreEqual(PluginFrontends.All, PluginDescriptorFactory.FromType(typeof(BothPlugin)).Frontends);
        Assert.AreEqual(PluginFrontends.Desktop, new BuiltInPluginDefinition
        {
            Id = "desk", DisplayName = "Desk", Factory = static () => new BothPlugin(), Frontends = PluginFrontends.Desktop,
        }.CreateDescriptor().Frontends);
    }

    [TestMethod]
    public async Task Start_LeavesOutThePluginsThatDoNotSupportTheApplication()
    {
        using var temp = new TestTempDirectory();
        BuiltInPluginDefinition[] builtIns =
        [
            new() { Id = "both", DisplayName = "Both", PluginType = typeof(BothPlugin), Factory = static () => new BothPlugin() },
            new() { Id = "terminal-only", DisplayName = "Terminal Only", PluginType = typeof(TerminalOnlyPlugin), Factory = static () => new TerminalOnlyPlugin(), Frontends = PluginFrontends.Terminal },
        ];

        await using var desktop = new PluginRuntimeManager();
        var started = await desktop.StartAsync(new PluginRuntimeManagerOptions { GlobalRoot = temp.Path, BuiltIns = builtIns, Frontend = PluginFrontends.Desktop });

        var active = started.ActivePlugins.Single();
        Assert.AreEqual("builtin:both", active.Descriptor.RuntimeKey);
        Assert.AreEqual(PluginFrontends.Desktop, active.RuntimeContext.Host.Frontend);
        Assert.IsTrue(active.RuntimeContext.Host.HasInteractiveUi);
        var skipped = started.Diagnostics.Single(static diagnostic => diagnostic.Metadata.ContainsKey(PluginRuntimeManager.UnsupportedFrontendMetadataKey));
        Assert.AreEqual(PluginDiagnosticSeverity.Info, skipped.Severity);
        Assert.AreEqual("builtin:terminal-only", skipped.RuntimeKey);
        Assert.AreEqual("Plugin 'Terminal Only' was not started: it does not support the desktop application.", skipped.Message);
        Assert.IsFalse(desktop.Registry.GetSnapshot().Any(static registration => registration.Handle.PluginRuntimeKey == "builtin:terminal-only"));

        await using var terminal = new PluginRuntimeManager();
        var both = await terminal.StartAsync(new PluginRuntimeManagerOptions { GlobalRoot = temp.Path, BuiltIns = builtIns, Frontend = PluginFrontends.Terminal });
        Assert.AreEqual(2, both.ActivePlugins.Count);

        await using var headless = new PluginRuntimeManager();
        var all = await headless.StartAsync(new PluginRuntimeManagerOptions { GlobalRoot = temp.Path, BuiltIns = builtIns, IsHeadless = true });
        Assert.AreEqual(2, all.ActivePlugins.Count);
        Assert.IsFalse(all.ActivePlugins[0].RuntimeContext.Host.HasInteractiveUi);
    }

    [TestMethod]
    public async Task PromptPickers_AreRegisteredOncePerCharacter_AndSearched()
    {
        using var temp = new TestTempDirectory();
        await using var runtime = new PluginRuntimeManager();
        await runtime.StartAsync(new PluginRuntimeManagerOptions
        {
            GlobalRoot = temp.Path, Frontend = PluginFrontends.Desktop,
            BuiltIns =
            [
                new() { Id = "both", DisplayName = "Both", PluginType = typeof(BothPlugin), Factory = static () => new BothPlugin() },
                new() { Id = "other", DisplayName = "Other", PluginType = typeof(OtherPickerPlugin), Factory = static () => new OtherPickerPlugin() },
            ],
        });
        var options = new PluginAdapterOperationOptions { ProjectId = "project", SessionId = "session", HasInteractiveUi = true };
        var pickers = runtime.Adapter.GetContributions<PluginPromptPickerContribution>(PluginPoint.PromptPicker, options)
            .Select(static registration => (PluginPromptPickerContribution)registration.Contribution).ToArray();

        CollectionAssert.AreEqual(new[] { "first", "second" }, pickers.Select(static picker => picker.Name).ToArray());
        Assert.IsTrue(runtime.Registry.GetDiagnostics().Any(static diagnostic => diagnostic.Metadata.TryGetValue("ConflictKey", out var key) && key == "prompt-picker:!"),
            "two plugins with a picker on one character are reported");

        var (items, diagnostics) = await runtime.Adapter.SearchPromptPickerAsync(runtime.ActivePlugins, pickers[0], "ab", options);
        Assert.AreEqual(0, diagnostics.Count);
        Assert.AreEqual(new PluginPromptPickerItem { Label = "ab:project:session", InsertText = "ab " }, items.Single());

        var (none, failure) = await runtime.Adapter.SearchPromptPickerAsync(runtime.ActivePlugins, pickers[0], "throw", options);
        Assert.AreEqual(0, none.Count);
        Assert.AreEqual(PluginDiagnosticSeverity.Error, failure.Single().Severity);

        var unknown = PluginUi.PromptPicker("other", '%', "Other", static (_, _) => ValueTask.FromResult<IReadOnlyList<PluginPromptPickerItem>>([]));
        var (unregistered, quiet) = await runtime.Adapter.SearchPromptPickerAsync(runtime.ActivePlugins, unknown, "x", options);
        Assert.AreEqual(0, unregistered.Count);
        Assert.AreEqual(0, quiet.Count);
    }

    [TestMethod]
    public async Task StatusAndContentEntries_NameTheContributionTheyComeFrom()
    {
        using var temp = new TestTempDirectory();
        await using var runtime = new PluginRuntimeManager();
        await runtime.StartAsync(new PluginRuntimeManagerOptions
        {
            GlobalRoot = temp.Path, Frontend = PluginFrontends.Desktop,
            BuiltIns = [new() { Id = "both", DisplayName = "Both", PluginType = typeof(BothPlugin), Factory = static () => new BothPlugin() }],
        });
        var options = new PluginAdapterOperationOptions { HasInteractiveUi = true };

        var status = runtime.Adapter.GetStatusEntries(runtime.ActivePlugins, PluginUiRegion.SessionStatus, options).Single();
        Assert.AreEqual("builtin:both", status.Registration.Handle.PluginRuntimeKey);
        Assert.AreEqual(("Both", "ready", "open"), (status.Item.Label, status.Item.Text, status.Item.Command));
        Assert.AreEqual(status.Item, runtime.Adapter.GetStatusItems(runtime.ActivePlugins, PluginUiRegion.SessionStatus, options).Single());

        var content = runtime.Adapter.CreateContentEntries(runtime.ActivePlugins, PluginUiRegion.SessionFooter, options).Single();
        Assert.AreEqual("builtin:both", content.Registration.Handle.PluginRuntimeKey);
        Assert.AreEqual(("<b>footer</b>", "footer"), (content.Content.Html, content.Content.Text));
        Assert.AreEqual(0, runtime.Adapter.CreateContentEntries(runtime.ActivePlugins, PluginUiRegion.CommandBar, options).Count, "a contribution without content is left out");
        Assert.AreEqual(0, runtime.Adapter.CreateContentEntries(runtime.ActivePlugins, PluginUiRegion.SessionFooter, new PluginAdapterOperationOptions { IsHeadless = true }).Count);
    }

    [TestMethod]
    public void InstalledPackageVersions_AreReadFromTheAssembliesOfTheApplication()
    {
        var versions = PluginRuntimeManager.ResolveInstalledPackageVersions(AppContext.BaseDirectory);

        // The product version of an assembly, without its source revision.
        var terminal = versions.Single(static version => version.Include == "XenoAtom.Terminal.UI");
        Assert.IsTrue(char.IsAsciiDigit(terminal.Version[0]), terminal.Version);
        Assert.IsFalse(terminal.Version.Contains('+'));
        Assert.IsTrue(versions.All(static version => File.Exists(Path.Combine(AppContext.BaseDirectory, version.Include + ".dll"))));
        using var empty = new TestTempDirectory();
        Assert.AreEqual(0, PluginRuntimeManager.ResolveInstalledPackageVersions(empty.Path).Count);
    }

    [TestMethod]
    public void FirstBuildError_IsTheFileTheCodeAndTheMessage()
    {
        const string Output = """
            Restore complete (0.4s)
              plugin failed with 1 error(s) (1.2s)
                C:\Users\me\.alta\plugins\notes\plugin.cs(6,27): error CS0103: The name 'NotDefined' does not exist in the current context [C:\Users\me\.alta\plugins\notes\plugin.csproj]
                /home/me/plugin.cs(9,1): error CS1002: ; expected
            """;

        Assert.AreEqual("plugin.cs(6,27): error CS0103: The name 'NotDefined' does not exist in the current context", PluginBuildService.FindFirstBuildError(Output));
        Assert.AreEqual("MSBUILD : error MSB1009: Project file does not exist.", PluginBuildService.FindFirstBuildError("MSBUILD : error MSB1009: Project file does not exist."));
        Assert.IsNull(PluginBuildService.FindFirstBuildError("Build succeeded.\n    0 Error(s)"));
    }

    [Plugin("terminal-only", Frontends = PluginFrontends.Terminal)]
    public sealed class TerminalOnlyPlugin : PluginBase
    {
        public override IEnumerable<PluginCommandContribution> GetCommands()
        {
            yield return Command.Shell("terminal-only", "Runs in the terminal application.", static (_, _) => ValueTask.FromResult(PluginCommandResult.Handled));
        }
    }

    public sealed class BothPlugin : PluginBase
    {
        public override IEnumerable<PluginUiContribution> GetUiContributions()
        {
            yield return new PluginStatusContribution
            {
                Region = PluginUiRegion.SessionStatus, Name = "ready",
                GetStatus = static _ => new PluginStatusItem { Label = "Both", Text = "ready", Command = "open" },
            };
            yield return PluginUi.Content(PluginUiRegion.SessionFooter, static _ => PluginRenderResult.FromHtml("<b>footer</b>", "footer"), "footer");
            yield return PluginUi.Content(PluginUiRegion.CommandBar, static _ => null, "nothing");
        }

        public override IEnumerable<PluginPromptPickerContribution> GetPromptPickers()
        {
            yield return PluginUi.PromptPicker("first", '!', "First", static (context, _) => context.Query == "throw"
                ? throw new InvalidOperationException("search failed")
                : ValueTask.FromResult<IReadOnlyList<PluginPromptPickerItem>>(
                    [new PluginPromptPickerItem { Label = $"{context.Query}:{context.ProjectId}:{context.SessionId}", InsertText = context.Query + " " }]));
        }
    }

    public sealed class OtherPickerPlugin : PluginBase
    {
        public override IEnumerable<PluginPromptPickerContribution> GetPromptPickers()
        {
            yield return PluginUi.PromptPicker("second", '!', "Second", static (_, _) => ValueTask.FromResult<IReadOnlyList<PluginPromptPickerItem>>([]));
        }
    }
}
