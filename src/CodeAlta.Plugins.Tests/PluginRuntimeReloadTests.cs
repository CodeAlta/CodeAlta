using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugins.Tests;

[TestClass]
public sealed class PluginRuntimeReloadTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [TestCategory("RequiresDotNet10FileBuild")]
    public async Task StopOrReload_RetainsThePreviousActivationWhileALandingCallbackStillRuns(bool reload)
    {
        using var temp = new TestTempDirectory();
        WritePlugin(temp, "notes", Source("notes", "first"));
        await using var runtime = await StartAsync(temp);
        var package = Single(runtime, "notes");
        SkipWithoutFileBuilds(package);
        Assert.AreEqual(PluginPackageState.Running, package.State, Describe(package));
        var previous = runtime.ActivePlugins.Single();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callback = new TaskCompletionSource<PluginLandingCard?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = previous.LifetimeToken.Register(() => cancelled.TrySetResult());
        runtime.Registry.Register(previous.Descriptor, PluginScope.Global, null, null, PluginPoint.LandingCard,
        [
            new PluginLandingCardContribution
            {
                Id = "held", Title = "Held",
                GetCard = (_, _) => { entered.TrySetResult(); return new ValueTask<PluginLandingCard?>(callback.Task); },
            },
        ], 0);
        Task<IReadOnlyList<PluginLandingCardEntry>>? reading = null;
        Task<PluginPackageChangeResult>? change = null;
        Task? disposal = null;
        try
        {
            reading = runtime.Adapter.GetLandingCardEntriesAsync(runtime.ActivePlugins, null, null, TimeSpan.FromMilliseconds(50)).AsTask();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsTrue((await reading.WaitAsync(TimeSpan.FromSeconds(5))).Single().Failed);

            change = (reload ? runtime.ReloadPackageAsync(package.Package) : runtime.StopPackageAsync(package.Package)).AsTask();
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(30));
            // This is the same original deactivation that stop/reload started, not a second disposal.
            var diagnostics = await previous.DeactivateAsync(TimeSpan.FromMilliseconds(50));
            StringAssert.Contains(diagnostics.Single().Message, "remain retained");
            Assert.IsNotNull(previous.Instance, "stop/reload must not release an instance whose callback outlived the reader");
            Assert.AreEqual(PluginRuntimeState.Deactivating, previous.State);
            Assert.IsFalse(callback.Task.IsCompleted);

            callback.TrySetResult(PluginLandingCard.Of("<p>late</p>"));
            var result = await change.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.AreEqual(reload ? PluginPackageChange.Reloaded : PluginPackageChange.Stopped, result.Change, Describe(result.Status));
            disposal = previous.DisposeAsync().AsTask();
            await disposal.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsNull(previous.Instance);
        }
        finally
        {
            callback.TrySetResult(null);
            // Release the original even after an assertion failed, and independently observe all started work before fixture cleanup.
            var originals = new List<Task> { callback.Task };
            if (reading is not null) originals.Add(reading);
            if (change is not null) originals.Add(change);
            if (disposal is not null) originals.Add(disposal);
            await Task.WhenAll(originals.Select(task => task.WaitAsync(TimeSpan.FromSeconds(30))));
        }
    }

    [TestMethod]
    [TestCategory("RequiresDotNet10FileBuild")]
    public async Task APackage_IsBuiltAgainAndReplaced_WhileItsHostRuns()
    {
        using var temp = new TestTempDirectory();
        var file = WritePlugin(temp, "notes", Source("notes", "first"));
        await using var runtime = await StartAsync(temp);
        var package = Single(runtime, "notes");
        SkipWithoutFileBuilds(package);
        Assert.AreEqual(PluginPackageState.Running, package.State, Describe(package));
        Assert.IsFalse(package.SourceChanged);
        CollectionAssert.AreEqual(new[] { "first" }, Commands(runtime));
        var changes = new List<string>();
        runtime.Changed += (_, change) => changes.AddRange(change.PackageIds);

        // Twice: the second build writes the assembly the first reload loaded.
        foreach (var name in new[] { "second", "third" })
        {
            File.WriteAllText(file, Source("notes", name));
            Assert.IsTrue(Single(runtime, "notes").SourceChanged);

            var result = await runtime.ReloadPackageAsync(package.Package);

            Assert.AreEqual(PluginPackageChange.Reloaded, result.Change, Describe(result.Status));
            Assert.AreEqual(PluginPackageState.Running, result.Status.State);
            Assert.IsFalse(result.Status.SourceChanged);
            CollectionAssert.AreEqual(new[] { name }, Commands(runtime));
            Assert.AreEqual(1, runtime.ActivePlugins.Count);
        }

        CollectionAssert.AreEqual(new[] { "notes", "notes" }, changes);
        Assert.IsFalse(runtime.Diagnostics.Any(static diagnostic => diagnostic.Severity >= PluginDiagnosticSeverity.Warning), string.Join("\n", runtime.Diagnostics.Select(static diagnostic => diagnostic.Message)));
    }

    [TestMethod]
    [TestCategory("RequiresDotNet10FileBuild")]
    public async Task ABuildThatFails_LeavesThePluginThatRuns_AndSaysWhere()
    {
        using var temp = new TestTempDirectory();
        var file = WritePlugin(temp, "notes", Source("notes", "first"));
        await using var runtime = await StartAsync(temp);
        var package = Single(runtime, "notes");
        SkipWithoutFileBuilds(package);

        File.WriteAllText(file, Source("notes", "second").Replace("PluginCommandResult.Handled", "missing", StringComparison.Ordinal));
        var failed = await runtime.ReloadPackageAsync(package.Package);

        Assert.AreEqual(PluginPackageChange.BuildFailed, failed.Change);
        Assert.AreEqual(PluginPackageState.Running, failed.Status.State);
        Assert.IsTrue(failed.Status.SourceChanged);
        CollectionAssert.AreEqual(new[] { "first" }, Commands(runtime));
        var error = failed.Status.Build!.Diagnostics.Single(static diagnostic => diagnostic.Severity == PluginDiagnosticSeverity.Error);
        Assert.AreEqual("CS0103", error.Code);
        Assert.AreEqual("plugin.cs", error.File);
        Assert.IsTrue(error.LineNumber > 0 && error.ColumnNumber > 0);
        StringAssert.Contains(error.Message, "missing");
        Assert.IsTrue(failed.Status.Diagnostics.Any(static diagnostic => diagnostic.Severity == PluginDiagnosticSeverity.Error && diagnostic.Source == PluginRuntimeDiagnosticSource.Build));

        // The build alone says the same, and loads nothing.
        var built = await runtime.BuildPackageAsync(package.Package);
        Assert.IsFalse(built.Build!.Succeeded);
        CollectionAssert.AreEqual(new[] { "first" }, Commands(runtime));

        File.WriteAllText(file, Source("notes", "second"));
        var fixedResult = await runtime.ReloadPackageAsync(package.Package);

        Assert.AreEqual(PluginPackageChange.Reloaded, fixedResult.Change, Describe(fixedResult.Status));
        CollectionAssert.AreEqual(new[] { "second" }, Commands(runtime));
        Assert.IsFalse(fixedResult.Status.Diagnostics.Any(static diagnostic => diagnostic.Severity >= PluginDiagnosticSeverity.Error));
    }

    [TestMethod]
    [TestCategory("RequiresDotNet10FileBuild")]
    public async Task APackageThatDoesNotBuildAtTheStart_IsStartedOnceItIsFixed()
    {
        using var temp = new TestTempDirectory();
        var file = WritePlugin(temp, "notes", "this is not C#");
        await using var runtime = await StartAsync(temp);
        var package = Single(runtime, "notes");
        SkipWithoutFileBuilds(package);
        Assert.AreEqual(PluginPackageState.Failed, package.State);
        Assert.IsTrue(package.Build!.Diagnostics.Any(static diagnostic => diagnostic.Severity == PluginDiagnosticSeverity.Error && diagnostic.File == "plugin.cs"), Describe(package));
        Assert.AreEqual(0, runtime.ActivePlugins.Count);

        File.WriteAllText(file, Source("notes", "first"));
        var result = await runtime.ReloadPackageAsync(package.Package);
        SkipWithoutFileBuilds(result.Status);

        Assert.AreEqual(PluginPackageChange.Started, result.Change, Describe(result.Status));
        Assert.AreEqual(PluginPackageState.Running, result.Status.State);
        Assert.AreEqual("plugin:notes", result.Status.Plugins.Single().RuntimeKey);
        CollectionAssert.AreEqual(new[] { "first" }, Commands(runtime));
    }

    [TestMethod]
    [TestCategory("RequiresDotNet10FileBuild")]
    public async Task Refresh_StartsWhatIsNew_ReloadsWhatChanged_AndStopsWhatIsGone()
    {
        using var temp = new TestTempDirectory();
        var kept = WritePlugin(temp, "kept", Source("kept", "kept-one"));
        WritePlugin(temp, "gone", Source("gone", "gone-one"));
        WritePlugin(temp, "same", Source("same", "same-one"));
        await using var runtime = await StartAsync(temp);
        SkipWithoutFileBuilds(Single(runtime, "kept"));
        CollectionAssert.AreEquivalent(new[] { "kept-one", "gone-one", "same-one" }, Commands(runtime));

        File.WriteAllText(kept, Source("kept", "kept-two"));
        Directory.Delete(Path.Combine(temp.Path, "home", "plugins", "gone"), recursive: true);
        var created = SourcePluginScaffold.Create(runtime.Roots[0], "fresh");
        Assert.IsNull(created.Error, created.Message);

        var results = await runtime.RefreshPackagesAsync();

        string Change(string id) => results.Single(result => result.Status.Package.PackageId == id).Change.ToString();
        Assert.AreEqual("Reloaded", Change("kept"));
        Assert.AreEqual("Stopped", Change("gone"));
        Assert.AreEqual("Started", Change("fresh"));
        Assert.AreEqual("Unchanged", Change("same"));
        CollectionAssert.AreEquivalent(new[] { "kept-two", "fresh", "same-one" }, Commands(runtime));

        // Nothing changed since: nothing is built or replaced.
        var again = await runtime.RefreshPackagesAsync();
        Assert.IsTrue(again.All(static result => result.Change == PluginPackageChange.Unchanged), string.Join(", ", again.Select(static result => result.Change)));
    }

    [TestMethod]
    [TestCategory("RequiresDotNet10FileBuild")]
    public async Task APackage_IsStoppedWhenItIsTurnedOff_AndStartedWhenItIsTurnedOn()
    {
        using var temp = new TestTempDirectory();
        WritePlugin(temp, "notes", Source("notes", "first"));
        await using var runtime = await StartAsync(temp);
        var package = Single(runtime, "notes");
        SkipWithoutFileBuilds(package);
        var config = Path.Combine(temp.Path, "home", "config.toml");

        File.WriteAllText(config, "[plugins.notes]\nenabled = false\n");
        var stopped = await runtime.ReloadPackageAsync(package.Package);

        Assert.AreEqual(PluginPackageChange.Stopped, stopped.Change);
        Assert.AreEqual(PluginPackageState.Disabled, stopped.Status.State);
        Assert.AreEqual(0, Commands(runtime).Length);
        Assert.AreEqual(PluginPackageChange.Disabled, (await runtime.ReloadPackageAsync(package.Package)).Change);

        File.WriteAllText(config, "[plugins.notes]\nenabled = true\n");
        var started = (await runtime.RefreshPackagesAsync()).Single();

        Assert.AreEqual(PluginPackageChange.Started, started.Change, Describe(started.Status));
        CollectionAssert.AreEqual(new[] { "first" }, Commands(runtime));
    }

    [TestMethod]
    [TestCategory("RequiresDotNet10FileBuild")]
    public async Task TwoPackagesWithTheSameKey_OnlyTheFirstIsStarted()
    {
        using var temp = new TestTempDirectory();
        WritePlugin(temp, "first", Source("shared", "first"));
        await using var runtime = await StartAsync(temp);
        SkipWithoutFileBuilds(Single(runtime, "first"));
        WritePlugin(temp, "second", Source("shared", "second"));

        var result = await runtime.ReloadPackageAsync(Single(runtime, "second").Package);

        Assert.AreEqual(PluginPackageChange.StartFailed, result.Change);
        Assert.AreEqual(PluginPackageState.Failed, result.Status.State);
        StringAssert.Contains(result.Status.Diagnostics.Single(static diagnostic => diagnostic.Severity == PluginDiagnosticSeverity.Error).Message, "is the key of the plugin package 'first'");
        CollectionAssert.AreEqual(new[] { "first" }, Commands(runtime));
        Assert.AreEqual(PluginPackageState.Running, Single(runtime, "first").State);
    }

    [TestMethod]
    [TestCategory("RequiresDotNet10FileBuild")]
    public async Task APackageWithoutAPluginClass_SaysSo()
    {
        using var temp = new TestTempDirectory();
        WritePlugin(temp, "empty", "namespace Nothing;\n\ninternal static class Helper;\n");
        await using var runtime = await StartAsync(temp);
        var package = Single(runtime, "empty");
        SkipWithoutFileBuilds(package);

        Assert.AreEqual(PluginPackageState.Failed, package.State);
        StringAssert.Contains(package.Diagnostics.Single(static diagnostic => diagnostic.Severity == PluginDiagnosticSeverity.Error).Message, "declares no plugin");
    }

    [TestMethod]
    public async Task AChange_IsRefusedBeforeTheStartAndAfterTheClose()
    {
        using var temp = new TestTempDirectory();
        var file = WritePlugin(temp, "notes", Source("notes", "first"));
        var package = new SourcePluginDiscoveryService().Discover(new PluginRoot { RootPath = Path.GetDirectoryName(Path.GetDirectoryName(file))!, Scope = PluginScope.Global }).Single();
        var runtime = new PluginRuntimeManager();
        Assert.AreEqual(0, runtime.GetPackages().Count);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await runtime.ReloadPackageAsync(package));

        // Plugins are turned off for this run: nothing is built, and the package says it is off.
        await runtime.StartAsync(new PluginRuntimeManagerOptions { GlobalRoot = Path.Combine(temp.Path, "home"), IsHeadless = true, SafeMode = true });
        Assert.AreEqual(PluginPackageState.Disabled, runtime.GetPackages().Single().State);
        Assert.AreEqual(PluginPackageChange.Disabled, (await runtime.ReloadPackageAsync(package)).Change);

        await runtime.DeactivateAllAsync();
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await runtime.ReloadPackageAsync(package));
        await runtime.DisposeAsync();
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(async () => await runtime.RefreshPackagesAsync());
    }

    [TestMethod]
    public void BuildOutput_GivesTheErrorsAndTheWarnings()
    {
        var directory = OperatingSystem.IsWindows() ? @"C:\home\plugins\notes" : "/home/plugins/notes";
        var file = Path.Combine(directory, "plugin.cs");
        var output = string.Join('\n',
            "  Determining projects to restore...",
            $"{file}(11,42): error CS0103: The name 'missing' does not exist in the current context [{Path.Combine(directory, "plugin.cs.csproj")}]",
            $"{file}(8,13): warning CS0168: The variable 'unused' is declared but never used",
            $"{file}(8,13): warning CS0168: The variable 'unused' is declared but never used",
            "CSC : error CS2001: Source file 'other.cs' could not be found.",
            $"{Path.Combine(directory, "plugin.cs.csproj")} : error NU1101: Unable to find package Nothing. No packages exist with this id in source(s): nuget.org",
            "  plugin.cs -> somewhere",
            "Time Elapsed 00:00:01.20: error in the text of a line that is not one");

        var diagnostics = PluginBuildService.ParseDiagnostics(output, directory);

        Assert.AreEqual(4, diagnostics.Count);
        Assert.AreEqual((PluginDiagnosticSeverity.Error, "CS0103", "plugin.cs", 11, 42), (diagnostics[0].Severity, diagnostics[0].Code, diagnostics[0].File, diagnostics[0].LineNumber, diagnostics[0].ColumnNumber));
        Assert.AreEqual("The name 'missing' does not exist in the current context", diagnostics[0].Message);
        Assert.AreEqual((PluginDiagnosticSeverity.Warning, "CS0168", 8), (diagnostics[1].Severity, diagnostics[1].Code, diagnostics[1].LineNumber));
        Assert.AreEqual(("CS2001", (string?)null, 0), (diagnostics[2].Code, diagnostics[2].File, diagnostics[2].LineNumber));
        Assert.AreEqual(("NU1101", "plugin.cs.csproj"), (diagnostics[3].Code, diagnostics[3].File));
    }

    [TestMethod]
    public void ANewPackage_HasAPluginNamedAfterItsId()
    {
        using var temp = new TestTempDirectory();
        var root = new PluginRoot { RootPath = Path.Combine(temp.Path, "plugins"), Scope = PluginScope.Global };

        var created = SourcePluginScaffold.Create(root, "my-notes", description: "Keeps \"short\" notes.\n");

        Assert.IsNull(created.Error, created.Message);
        Assert.AreEqual("my-notes", created.Package!.PackageId);
        var source = File.ReadAllText(created.Package.EntryFilePath);
        StringAssert.Contains(source, """[Plugin("my-notes", DisplayName = "My notes", Description = "Keeps \"short\" notes.")]""");
        StringAssert.Contains(source, "public sealed class MyNotesPlugin : PluginBase");
        StringAssert.Contains(source, """Command.Shell("my-notes",""");
        Assert.AreEqual("# My notes\n\nKeeps \"short\" notes.\n", File.ReadAllText(Path.Combine(created.Package.PackageDirectory, "README.md")));

        Assert.AreEqual("exists", SourcePluginScaffold.Create(root, "my-notes").Error);
        Assert.AreEqual("invalid_id", SourcePluginScaffold.Create(root, "../outside").Error);
        Assert.AreEqual("invalid_id", SourcePluginScaffold.Create(root, " ").Error);
        Assert.AreEqual("invalid_id", SourcePluginScaffold.Create(root, new string('a', SourcePluginScaffold.MaximumIdLength + 1)).Error);
        Assert.AreEqual("PluginPlugin", SourcePluginScaffold.ClassName("plugin"));
        Assert.AreEqual("My2faPlugin", SourcePluginScaffold.ClassName("2fa"));
        Assert.AreEqual("NotesPlugin", SourcePluginScaffold.ClassName("notes.plugin"));
    }

    [TestMethod]
    [TestCategory("RequiresDotNet10FileBuild")]
    public async Task ANewPackage_BuildsAndStarts()
    {
        using var temp = new TestTempDirectory();
        Directory.CreateDirectory(Path.Combine(temp.Path, "home"));
        await using var runtime = await StartAsync(temp);
        var created = SourcePluginScaffold.Create(runtime.Roots[0], "fresh.one", "Fresh", "Says hello.");
        Assert.IsNull(created.Error, created.Message);

        var result = await runtime.ReloadPackageAsync(created.Package!);
        SkipWithoutFileBuilds(result.Status);

        Assert.AreEqual(PluginPackageChange.Started, result.Change, Describe(result.Status));
        Assert.AreEqual("Fresh", result.Status.Plugins.Single().DisplayName);
        CollectionAssert.AreEqual(new[] { "fresh.one" }, Commands(runtime));
    }

    [TestMethod]
    [TestCategory("RequiresDotNet10FileBuild")]
    public async Task APackageThatIsAboutToBeRemoved_IsStopped_WhateverTheConfigurationSays()
    {
        using var temp = new TestTempDirectory();
        WritePlugin(temp, "notes", Source("notes", "first"));
        await using var runtime = await StartAsync(temp);
        var package = Single(runtime, "notes");
        SkipWithoutFileBuilds(package);
        Assert.AreEqual(PluginPackageState.Running, package.State, Describe(package));
        var changes = new List<string>();
        runtime.Changed += (_, change) => changes.AddRange(change.PackageIds);

        var stopped = await runtime.StopPackageAsync(package.Package);

        Assert.AreEqual((PluginPackageChange.Stopped, PluginPackageState.Stopped), (stopped.Change, stopped.Status.State));
        Assert.AreEqual(0, runtime.ActivePlugins.Count);
        Assert.AreEqual(0, Commands(runtime).Length, "What the plugin contributed goes with it.");
        CollectionAssert.AreEqual(new[] { "notes" }, changes);
        // The package is still on disk and turned on: it is listed as not started, and starts again when asked.
        Assert.AreEqual(PluginPackageState.Stopped, Single(runtime, "notes").State);
        Assert.IsNull(Single(runtime, "notes").Build, "What was built of it is forgotten.");

        // Nothing of it runs: stopping it again changes nothing, and tells nobody.
        Assert.AreEqual(PluginPackageChange.Unchanged, (await runtime.StopPackageAsync(package.Package)).Change);
        CollectionAssert.AreEqual(new[] { "notes" }, changes);
        Assert.AreEqual(PluginPackageChange.Started, (await runtime.ReloadPackageAsync(package.Package)).Change);
        CollectionAssert.AreEqual(new[] { "first" }, Commands(runtime));
    }

    private static async Task<PluginRuntimeManager> StartAsync(TestTempDirectory temp)
    {
        var runtime = new PluginRuntimeManager();
        await runtime.StartAsync(new PluginRuntimeManagerOptions { GlobalRoot = Path.Combine(temp.Path, "home"), IsHeadless = true });
        return runtime;
    }

    private static string WritePlugin(TestTempDirectory temp, string id, string source)
    {
        var directory = Path.Combine(temp.Path, "home", "plugins", id);
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "plugin.cs");
        File.WriteAllText(file, source);
        return file;
    }

    private static string Source(string key, string command)
        => $$"""
using CodeAlta.Plugins.Abstractions;

[Plugin("{{key}}")]
public sealed class SamplePlugin : PluginBase
{
    public override IEnumerable<PluginCommandContribution> GetCommands()
    {
        yield return Command.Shell("{{command}}", "A command.", static (_, _) => ValueTask.FromResult(PluginCommandResult.Handled));
    }
}
""";

    private static PluginPackageStatus Single(PluginRuntimeManager runtime, string id)
        => runtime.GetPackages().Single(package => package.Package.PackageId == id);

    private static string[] Commands(PluginRuntimeManager runtime)
        => [.. runtime.Registry.GetSnapshot().Select(static registration => registration.Contribution).OfType<PluginCommandContribution>().Select(static command => command.Name)];

    private static string Describe(PluginPackageStatus status)
        => string.Join(Environment.NewLine, status.Diagnostics.Select(static diagnostic => $"{diagnostic.Severity}/{diagnostic.Source}: {diagnostic.Message}")
            .Concat(status.Build is null ? [] : [status.Build.StandardOutput, status.Build.StandardError]));

    private static void SkipWithoutFileBuilds(PluginPackageStatus status)
    {
        if (status.Build is { Succeeded: false } build && (build.StandardOutput + build.StandardError).Contains("The project file could not be loaded", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Inconclusive("The installed .NET SDK did not accept `dotnet build plugin.cs` file-based builds in this environment.");
        }
    }
}
