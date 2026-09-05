using CodeAlta.Catalog;
using CodeAlta.Tui.App;
using CodeAlta.Tui.Models;
using CodeAlta.Tui.App.State;
using CodeAlta.Tui.Threading;
using CodeAlta.Tui.Views;
using SharpYaml.Model;

namespace CodeAlta.Tests;

// Audited: literal YAML and task-owned temp files only. No application/host startup or discovery.
[TestClass]
public sealed class UiStatePersistenceTests
{
    private const string Fixture = """
        open_session_ids: [session-1]
        selected_session_id: session-1
        project_preferences:
          project-1:
            model_id: old-model
            future_preference: {nested: [one, {two: true}]}
        navigator:
          theme_scheme_name: old-theme
          future_navigator: {density: [1, 2]}
        selection:
          surface: Session
          session_id: session-1
          future_selection: {cursor: [3, 4]}
        session_preferences:
          session-1: {model_id: transient-model}
        session_states:
          session-1: {message_count: 7}
        frontend_layouts:
          tui: {split: 30, future: {values: [true, null, '001']}}
          desktop: {window: {width: 1200}, unavailable: {plugin: absent, data: [a, {b: c}]}}
          future-head: [{opaque: [1, 2]}]
        logical_tabs:
          - {id: session-1, kind: session, reference: session-1}
          - {id: missing-panel, kind: plugin, contribution: absent.panel, data: {nested: [a, {b: c}]}}
        future_root: {nested: [one, {two: true}]}
        future_tagged: !future {data: [!opaque '001', 9007199254740993]}
        """;

    [TestMethod]
    public void Serializer_TuiDesktopTui_RetainsNestedUnknownStateAndOmitsLegacyState()
    {
        var serializer = new SessionViewYamlSerializer();
        var tui = serializer.DeserializeViewState(Fixture);
        Assert.AreEqual(7, tui.SessionStates["session-1"].MessageCount);
        tui.Navigator = SessionViewStateCoordinator.CloneNavigatorSettings(tui.Navigator);
        tui.Navigator.ThemeSchemeName = "tui-theme";
        tui.Selection = SessionViewSelectionState.GlobalDraft();
        tui.SelectedSessionId = null;
        tui.ProjectPreferences["project-1"].ModelId = "new-model";
        var first = serializer.SerializeViewState(tui);
        AssertRetained(first);
        Assert.IsFalse(first.Contains("transient-model", StringComparison.Ordinal));
        Assert.IsFalse(first.Contains("session_states:", StringComparison.Ordinal));
        Assert.IsFalse(first.Contains("session_preferences:", StringComparison.Ordinal));

        var desktop = serializer.DeserializeViewState(first);
        desktop.Navigator.LanguageName = "fr";
        ((YamlMapping)((YamlMapping)desktop.FrontendLayouts["desktop"]!)["window"]!)["width"] = new YamlValue(1400);
        ((YamlMapping)desktop.FrontendLayouts["tui"]!)["split"] = new YamlValue(40);
        var second = serializer.SerializeViewState(desktop);
        var finalTui = serializer.DeserializeViewState(second);
        finalTui.Navigator = SessionViewStateCoordinator.CloneNavigatorSettings(finalTui.Navigator);
        var final = serializer.SerializeViewState(finalTui);
        AssertRetained(final);
        Assert.AreEqual("new-model", finalTui.ProjectPreferences["project-1"].ModelId);
        Assert.AreEqual("tui-theme", finalTui.Navigator.ThemeSchemeName);
        Assert.AreEqual("fr", finalTui.Navigator.LanguageName);
        Assert.AreEqual(SessionViewSelectionSurface.Draft, finalTui.Selection.Surface);
        Assert.IsNull(finalTui.Selection.SessionId);
        Assert.AreEqual("1400", ((YamlValue)((YamlMapping)((YamlMapping)finalTui.FrontendLayouts["desktop"]!)["window"]!)["width"]!).Value);
        Assert.AreEqual("40", ((YamlValue)((YamlMapping)finalTui.FrontendLayouts["tui"]!)["split"]!).Value);
        Assert.AreEqual(2, finalTui.LogicalTabs.Count);
        Assert.AreEqual("absent.panel", ((YamlValue)((YamlMapping)finalTui.LogicalTabs[1]!)["contribution"]!).Value);
        var values = (YamlSequence)((YamlMapping)((YamlMapping)finalTui.FrontendLayouts["tui"]!)["future"]!)["values"]!;
        Assert.AreEqual("001", ((YamlValue)values[2]!).Value);
        Assert.AreEqual("true", ((YamlValue)values[0]!).Value);
        Assert.AreEqual("null", ((YamlValue)values[1]!).Value);
    }

    [TestMethod]
    public void Serializer_LegacySelectionStillMigrates()
    {
        var state = new SessionViewYamlSerializer().DeserializeViewState("open_session_ids: [legacy]\nselected_session_id: legacy\n");
        Assert.AreEqual("legacy", state.Selection.SessionId);
        state.Validate();
    }

    [TestMethod]
    [DataRow("")]
    [DataRow(" \n")]
    [DataRow("# legacy empty state\n")]
    [DataRow("---\n...\n")]
    public void Serializer_EmptyDocumentsRemainEmptyState(string yaml)
    {
        var state = new SessionViewYamlSerializer().DeserializeViewState(yaml);
        state.Validate();
        Assert.AreEqual(0, state.OpenSessionIds.Count);
    }

    [TestMethod]
    public void Serializer_KnownClearsAndPreferenceRemovalWinOverRetainedValues()
    {
        var serializer = new SessionViewYamlSerializer();
        var state = serializer.DeserializeViewState(Fixture);
        state.Navigator.ThemeSchemeName = null;
        state.ProjectPreferences["project-1"].ModelId = null;
        var cleared = serializer.DeserializeViewState(serializer.SerializeViewState(state));
        Assert.IsNull(cleared.Navigator.ThemeSchemeName);
        Assert.IsNull(cleared.ProjectPreferences["project-1"].ModelId);
        StringAssert.Contains(serializer.SerializeViewState(cleared), "future_preference:");
        state.ProjectPreferences.Remove("project-1");
        var removed = serializer.SerializeViewState(state);
        Assert.IsFalse(removed.Contains("future_preference:", StringComparison.Ordinal));
        Assert.AreEqual(0, serializer.DeserializeViewState(removed).ProjectPreferences.Count);
    }

    [TestMethod]
    public async Task Catalog_StaleSameMtimeSaveDoesNotOverwriteExternalEdit()
    {
        using var root = new OwnedRoot();
        var catalog = new SessionViewCatalog(root.Options);
        await File.WriteAllTextAsync(root.Options.UiStatePath, Fixture);
        var state = await catalog.LoadViewStateAsync();
        var timestamp = File.GetLastWriteTimeUtc(root.Options.UiStatePath);
        const string external = "navigator: {theme_scheme_name: external}\n";
        await File.WriteAllTextAsync(root.Options.UiStatePath, external);
        File.SetLastWriteTimeUtc(root.Options.UiStatePath, timestamp);
        state.Navigator.ThemeSchemeName = "pending";
        var baseline = state.Revision;
        var result = await catalog.SaveViewStateAsync(state);
        Assert.IsTrue(result.IsConflict);
        Assert.IsNull(result.AcknowledgedRevision);
        Assert.AreEqual(baseline, state.Revision);
        Assert.AreEqual(external, await File.ReadAllTextAsync(root.Options.UiStatePath));
    }

    [TestMethod]
    public async Task Catalog_MissingBaselineDoesNotOverwriteExternalCreation()
    {
        using var root = new OwnedRoot();
        var catalog = new SessionViewCatalog(root.Options);
        var state = await catalog.LoadViewStateAsync();
        await File.WriteAllTextAsync(root.Options.UiStatePath, string.Empty);
        var result = await catalog.SaveViewStateAsync(state);
        Assert.IsTrue(result.IsConflict);
        Assert.AreEqual(string.Empty, await File.ReadAllTextAsync(root.Options.UiStatePath));
    }

    [TestMethod]
    public async Task Catalog_DeletionConflictsWithLoadedEmptyFile()
    {
        using var root = new OwnedRoot();
        var catalog = new SessionViewCatalog(root.Options);
        await File.WriteAllTextAsync(root.Options.UiStatePath, string.Empty);
        var state = await catalog.LoadViewStateAsync();
        Assert.IsTrue(state.Revision.Exists);
        File.Delete(root.Options.UiStatePath);
        var result = await catalog.SaveViewStateAsync(state);
        Assert.IsTrue(result.IsConflict);
        Assert.IsFalse(result.CurrentRevision.Exists);
        Assert.IsFalse(File.Exists(root.Options.UiStatePath));
    }

    [TestMethod]
    public async Task Catalog_CancellationAndReadOnlyFailureRetainOriginalBytesAndBaseline()
    {
        using var root = new OwnedRoot();
        var catalog = new SessionViewCatalog(root.Options);
        await File.WriteAllTextAsync(root.Options.UiStatePath, Fixture, new System.Text.UTF8Encoding(true));
        var bytes = await File.ReadAllBytesAsync(root.Options.UiStatePath);
        var state = await catalog.LoadViewStateAsync();
        var baseline = state.Revision;
        state.Navigator.ThemeSchemeName = "pending";
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => catalog.SaveViewStateAsync(state, cancelled.Token));
        File.SetAttributes(root.Options.UiStatePath, FileAttributes.ReadOnly);
        try
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => catalog.SaveViewStateAsync(state));
        }
        finally
        {
            File.SetAttributes(root.Options.UiStatePath, FileAttributes.Normal);
        }

        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(root.Options.UiStatePath));
        Assert.AreEqual(baseline, state.Revision);
        Assert.AreEqual(1, Directory.GetFiles(root.Path).Length);
    }

    [TestMethod]
    [DataRow("navigator: [broken")]
    [DataRow("frontend_layouts: []")]
    [DataRow("logical_tabs: {}")]
    [DataRow("unknown: {duplicate: 1, duplicate: 2}")]
    [DataRow("---\nnavigator: {}\n---\nfuture: value")]
    [DataRow("unknown: &anchor {value: 1}\ncopy: *anchor")]
    [DataRow("unknown: {<<: {value: 1}}")]
    [DataRow("[not, a, mapping]")]
    public async Task Catalog_MalformedOrUnsupportedDataIsNotReplaced(string yaml)
    {
        using var root = new OwnedRoot();
        var catalog = new SessionViewCatalog(root.Options);
        await File.WriteAllTextAsync(root.Options.UiStatePath, yaml);
        var bytes = await File.ReadAllBytesAsync(root.Options.UiStatePath);
        var loadError = await Assert.ThrowsAsync<Exception>(() => catalog.LoadViewStateAsync());
        Assert.IsTrue(loadError is InvalidDataException or SharpYaml.YamlException);
        var result = await catalog.SaveViewStateAsync(new SessionViewViewState());
        Assert.IsTrue(result.IsConflict);
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(root.Options.UiStatePath));
        var revision = await catalog.TextFiles.GetRevisionAsync(root.Options.UiStatePath);
        var saveError = await Assert.ThrowsAsync<Exception>(() => catalog.SaveViewStateAsync(new SessionViewStateSaveRequest(yaml, revision)));
        Assert.IsTrue(saveError is InvalidDataException or SharpYaml.YamlException);
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(root.Options.UiStatePath));
    }

    [TestMethod]
    public async Task Tui_ProjectionsAndRepeatedSavesRetainExtensionData()
    {
        using var root = new OwnedRoot();
        var catalog = new SessionViewCatalog(root.Options);
        await File.WriteAllTextAsync(root.Options.UiStatePath, Fixture);
        var coordinator = new SessionViewStateCoordinator(catalog);
        var selection = new ShellSelectionCoordinator();
        selection.ApplyInitialSelection(await coordinator.LoadViewStateAsync(CancellationToken.None), [], []);
        selection.Selection = ShellSelection.GlobalDraft();
        var settings = coordinator.GetNavigatorSettingsSnapshot(selection.ViewState);
        settings.ThemeSchemeName = "first";
        await coordinator.SaveNavigatorSettingsAsync(selection.ViewState, settings);
        var firstAck = coordinator.AcknowledgedRevision;
        Assert.IsNotNull(firstAck);
        settings.ThemeSchemeName = "second";
        await coordinator.SaveNavigatorSettingsAsync(selection.ViewState, settings);
        Assert.AreNotEqual(firstAck, coordinator.AcknowledgedRevision);
        Assert.IsNull(coordinator.PendingYaml);
        var reloaded = await catalog.LoadViewStateAsync();
        Assert.AreEqual("second", reloaded.Navigator.ThemeSchemeName);
        AssertRetained(await File.ReadAllTextAsync(root.Options.UiStatePath));
    }

    [TestMethod]
    public async Task Tui_ConflictsAndFailuresDoNotAcknowledgeOrAdoptExternalRevision()
    {
        using var root = new OwnedRoot();
        var catalog = new SessionViewCatalog(root.Options);
        var coordinator = new SessionViewStateCoordinator(catalog);
        var state = await coordinator.LoadViewStateAsync(CancellationToken.None);
        Assert.IsTrue((await coordinator.PersistViewStateAsync(state)).IsAcknowledged);
        var acknowledged = coordinator.AcknowledgedRevision;
        var original = await File.ReadAllBytesAsync(root.Options.UiStatePath);
        state.Navigator.ThemeSchemeName = "pending";
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var cancelledResult = await coordinator.PersistViewStateAsync(state, cancelled.Token);
        Assert.IsInstanceOfType<OperationCanceledException>(cancelledResult.Error);
        Assert.IsFalse(cancelledResult.IsAcknowledged);
        Assert.AreEqual(acknowledged, coordinator.AcknowledgedRevision);
        StringAssert.Contains(coordinator.PendingYaml!, "pending");
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(root.Options.UiStatePath));
        File.SetAttributes(root.Options.UiStatePath, FileAttributes.ReadOnly);
        try
        {
            var failure = await coordinator.PersistViewStateAsync(state);
            Assert.IsInstanceOfType<UnauthorizedAccessException>(failure.Error);
            Assert.IsFalse(failure.IsAcknowledged);
            Assert.AreEqual(acknowledged, coordinator.AcknowledgedRevision);
            CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(root.Options.UiStatePath));
        }
        finally
        {
            File.SetAttributes(root.Options.UiStatePath, FileAttributes.Normal);
        }

        const string external = "navigator: {theme_scheme_name: external}\n";
        await File.WriteAllTextAsync(root.Options.UiStatePath, external);
        var conflict = await coordinator.PersistViewStateAsync(state);
        Assert.IsTrue(conflict.Save!.IsConflict);
        Assert.IsFalse(conflict.IsAcknowledged);
        Assert.AreEqual(acknowledged, coordinator.AcknowledgedRevision);
        state.Navigator.ThemeSchemeName = "still-pending";
        var repeated = await coordinator.PersistViewStateAsync(state);
        Assert.IsTrue(repeated.Save!.IsConflict);
        Assert.AreEqual(acknowledged, coordinator.AcknowledgedRevision);
        StringAssert.Contains(coordinator.PendingYaml!, "still-pending");
        Assert.AreEqual(external, await File.ReadAllTextAsync(root.Options.UiStatePath));
    }

    [TestMethod]
    public async Task Tui_OrderedSavesFreezeMutableCollectionsBeforeReturning()
    {
        using var root = new OwnedRoot();
        var catalog = new SessionViewCatalog(root.Options);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var coordinator = new SessionViewStateCoordinator(catalog, async (request, cancellationToken) =>
        {
            if (++calls == 1)
            {
                entered.SetResult();
                await release.Task.WaitAsync(cancellationToken);
            }

            return await catalog.SaveViewStateAsync(request, cancellationToken);
        });
        var state = await coordinator.LoadViewStateAsync(CancellationToken.None);
        state.OpenSessionIds.Add("first");
        var first = coordinator.PersistViewStateAsync(state);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        state.OpenSessionIds.Add("second");
        var second = coordinator.PersistViewStateAsync(state);
        // Neither queued request may enumerate this now-mutated collection asynchronously.
        state.OpenSessionIds.Clear();
        state.OpenSessionIds.Add("not-submitted");
        Assert.AreEqual(1, calls);
        release.SetResult();
        Assert.IsTrue((await first).IsAcknowledged);
        Assert.IsTrue((await second).IsAcknowledged);
        CollectionAssert.AreEqual(new[] { "first", "second" }, (await catalog.LoadViewStateAsync()).OpenSessionIds);
    }

    [TestMethod]
    public async Task Tui_ShellCatalogRecoveryAndNavigatorProjectionRetainUnknownDescriptors()
    {
        using var root = new OwnedRoot();
        var catalog = new SessionViewCatalog(root.Options);
        await File.WriteAllTextAsync(root.Options.UiStatePath, Fixture);
        var dispatcher = new InlineDispatcher();
        // Audited helper: inert delegates for prompts/providers/tabs/history; no controls or host startup.
        var shell = TestSessionStateServices.CreateCoordinator(new ProjectCatalog(root.Options), catalog,
            dispatcher, new ShellStateStore(dispatcher));
        shell.ApplyNavigatorSettingsSnapshot(await shell.LoadNavigatorSettingsAsync(CancellationToken.None));
        await shell.LoadCatalogStateAsync(CancellationToken.None);
        shell.ApplyRecoveredCatalogState([], []);
        var settings = shell.GetNavigatorSettingsSnapshot();
        settings.ThemeSchemeName = "shell-theme";
        Assert.IsTrue((await shell.SaveNavigatorSettingsAsync(settings)).IsAcknowledged);
        Assert.IsFalse(shell.HasPendingViewStateChanges);
        Assert.IsTrue(shell.ViewStatePersistenceResult!.IsAcknowledged);
        Assert.IsTrue((await shell.PersistViewStateAsync()).IsAcknowledged);
        var persisted = await catalog.LoadViewStateAsync();
        Assert.AreEqual(0, persisted.OpenSessionIds.Count); // Existing missing-session pruning remains.
        Assert.AreEqual(2, persisted.LogicalTabs.Count); // Opaque descriptors are NOT availability-pruned.
        Assert.AreEqual("shell-theme", persisted.Navigator.ThemeSchemeName);
        AssertRetained(await File.ReadAllTextAsync(root.Options.UiStatePath));
    }

    [TestMethod]
    public async Task Catalog_SharedEditorCodecConflictsAgainstUiStateAcknowledgment()
    {
        using var root = new OwnedRoot();
        var catalog = new SessionViewCatalog(root.Options);
        var state = await catalog.LoadViewStateAsync();
        var saved = await catalog.SaveViewStateAsync(state);
        var editor = await catalog.TextFiles.LoadAsync(root.Options.UiStatePath);
        var update = catalog.CreateViewStateSaveRequest(state, saved.AcknowledgedRevision!);
        Assert.IsFalse((await catalog.SaveViewStateAsync(update with { Yaml = update.Yaml + "\nfuture: value\n" })).IsConflict);
        var editorSave = await catalog.TextFiles.SaveAsync(new TextFileSaveRequest(root.Options.UiStatePath, "{}", editor.Encoding, editor.HasByteOrderMark, editor.Revision));
        Assert.IsTrue(editorSave.IsConflict);
    }

    [TestMethod]
    public async Task Catalog_DirectRequestOmitsTransientFieldsWithoutTouchingJournals()
    {
        using var root = new OwnedRoot();
        var catalog = new SessionViewCatalog(root.Options);
        Directory.CreateDirectory(root.Options.SessionsRoot);
        var journal = System.IO.Path.Combine(root.Options.SessionsRoot, "owned-sentinel.jsonl");
        const string journalText = "{\"unchanged\":true}\n";
        await File.WriteAllTextAsync(journal, journalText);
        var saved = await catalog.SaveViewStateAsync(new SessionViewStateSaveRequest(Fixture, TextFileRevision.Missing));
        Assert.IsFalse(saved.IsConflict);
        var yaml = await File.ReadAllTextAsync(root.Options.UiStatePath);
        AssertRetained(yaml);
        Assert.IsFalse(yaml.Contains("session_states:", StringComparison.Ordinal));
        Assert.IsFalse(yaml.Contains("session_preferences:", StringComparison.Ordinal));
        Assert.AreEqual(journalText, await File.ReadAllTextAsync(journal));
    }

    [TestMethod]
    public async Task Catalog_TuiDesktopTuiHandoffRetainsDesktopEditsAndUnavailableTabs()
    {
        using var root = new OwnedRoot();
        var catalog = new SessionViewCatalog(root.Options);
        await File.WriteAllTextAsync(root.Options.UiStatePath, Fixture);
        var tui = new SessionViewStateCoordinator(catalog);
        var tuiState = await tui.LoadViewStateAsync(CancellationToken.None);
        Assert.IsTrue((await tui.PersistViewStateAsync(tuiState)).IsAcknowledged);

        // Simulated head handoff, not simultaneous live frontends or rendered desktop qualification.
        var desktop = await catalog.LoadViewStateAsync();
        ((YamlMapping)((YamlMapping)desktop.FrontendLayouts["desktop"]!)["window"]!)["width"] = new YamlValue(1500);
        desktop.LogicalTabs.Add(new YamlMapping
        {
            ["id"] = new YamlValue("editor-1"),
            ["kind"] = new YamlValue("editor"),
            ["reference"] = new YamlValue("docs.md"),
        });
        desktop.ProjectPreferences["project-1"].ModelId = "desktop-model";
        Assert.IsFalse((await catalog.SaveViewStateAsync(desktop)).IsConflict);

        var returningTui = new SessionViewStateCoordinator(catalog);
        var returnedState = await returningTui.LoadViewStateAsync(CancellationToken.None);
        var settings = returningTui.GetNavigatorSettingsSnapshot(returnedState);
        settings.ThemeSchemeName = "returning-tui";
        Assert.IsTrue((await returningTui.SaveNavigatorSettingsAsync(returnedState, settings)).IsAcknowledged);
        var final = await catalog.LoadViewStateAsync();
        Assert.AreEqual("desktop-model", final.ProjectPreferences["project-1"].ModelId);
        Assert.AreEqual("returning-tui", final.Navigator.ThemeSchemeName);
        Assert.AreEqual(3, final.LogicalTabs.Count);
        Assert.AreEqual("1500", ((YamlValue)((YamlMapping)((YamlMapping)final.FrontendLayouts["desktop"]!)["window"]!)["width"]!).Value);
        Assert.AreEqual(tuiState.LogicalTabs[1]!.ToString(), final.LogicalTabs[1]!.ToString());
        Assert.AreEqual(desktop.LogicalTabs[2]!.ToString(), final.LogicalTabs[2]!.ToString());
        Assert.AreEqual(tuiState.FrontendLayouts["future-head"]!.ToString(), final.FrontendLayouts["future-head"]!.ToString());
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task Tui_SettingsAdapter_ReportsRepeatedFailuresAndAcknowledgedRetry(bool conflict)
    {
        using var root = new OwnedRoot();
        var catalog = new SessionViewCatalog(root.Options);
        await File.WriteAllTextAsync(root.Options.UiStatePath, Fixture);
        var original = await File.ReadAllBytesAsync(root.Options.UiStatePath);
        var dispatcher = new InlineDispatcher();
        var shell = TestSessionStateServices.CreateCoordinator(new ProjectCatalog(root.Options), catalog,
            dispatcher, new ShellStateStore(dispatcher));
        await shell.LoadCatalogStateAsync(CancellationToken.None);
        var refreshes = 0;
        var statuses = new List<StatusTone>();
        INavigatorSettingsDialogService adapter = new NavigatorSettingsCoordinator(shell, () => null, () => null,
            () => refreshes++, (_, _, tone) => statuses.Add(tone));
        var settings = shell.GetNavigatorSettingsSnapshot();
        settings.ThemeSchemeName = "pending-adapter-theme";
        // UiTheme's acknowledged path only assigns this existing in-memory translation identity.
        // No dialog, native controls, application startup or culture/profile discovery is invoked.
        settings.LanguageName = SR.Language;
        var external = System.Text.Encoding.UTF8.GetBytes("navigator: {theme_scheme_name: external}\n");
        if (conflict)
        {
            await File.WriteAllBytesAsync(root.Options.UiStatePath, external);
        }
        else
        {
            File.SetAttributes(root.Options.UiStatePath, FileAttributes.ReadOnly);
        }

        try
        {
            await adapter.SaveNavigatorSettingsAsync(settings);
            await adapter.SaveNavigatorSettingsAsync(settings);
            Assert.AreEqual(0, refreshes);
            Assert.AreEqual(2, statuses.Count);
            Assert.IsTrue(statuses.All(tone => tone is StatusTone.Warning or StatusTone.Error));
            Assert.IsTrue(shell.HasPendingViewStateChanges);
            Assert.IsFalse(shell.ViewStatePersistenceResult!.IsAcknowledged);
            CollectionAssert.AreEqual(conflict ? external : original, await File.ReadAllBytesAsync(root.Options.UiStatePath));
        }
        finally
        {
            File.SetAttributes(root.Options.UiStatePath, FileAttributes.Normal);
        }

        if (conflict)
        {
            // Deliberately resolve the external fixture by restoring the original bytes, not by
            // adopting its revision in the coordinator. Ordinary retry must still use the old ACK.
            await File.WriteAllBytesAsync(root.Options.UiStatePath, original);
        }

        await adapter.SaveNavigatorSettingsAsync(settings);
        Assert.AreEqual(1, refreshes);
        Assert.AreEqual(StatusTone.Info, statuses[^1]);
        Assert.IsFalse(shell.HasPendingViewStateChanges);
        Assert.IsTrue(shell.ViewStatePersistenceResult!.IsAcknowledged);
        Assert.AreEqual("pending-adapter-theme", (await catalog.LoadViewStateAsync()).Navigator.ThemeSchemeName);
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task Tui_OrdinaryAndSidebarAdapters_ReportFailureAndRetry(bool sidebar, bool conflict)
    {
        using var root = new OwnedRoot();
        var catalog = new SessionViewCatalog(root.Options);
        await File.WriteAllTextAsync(root.Options.UiStatePath, Fixture);
        var original = await File.ReadAllBytesAsync(root.Options.UiStatePath);
        var statuses = new List<StatusTone>();
        var feedback = new ViewStatePersistenceFeedback((_, spinner, tone) =>
        {
            Assert.IsFalse(spinner);
            statuses.Add(tone);
        });
        var dispatcher = new InlineDispatcher();
        var shell = TestSessionStateServices.CreateCoordinator(new ProjectCatalog(root.Options), catalog,
            dispatcher, new ShellStateStore(dispatcher), persistenceFeedback: feedback);
        await shell.LoadCatalogStateAsync(CancellationToken.None);
        shell.ViewState.Navigator.ThemeSchemeName = "pending-feedback-theme";
        var refreshes = 0;
        async Task SaveAsync()
        {
            if (sidebar)
            {
                await SidebarServicesFactory.ToggleSortModeAsync(shell, () => refreshes++, feedback);
            }
            else
            {
                await shell.PersistViewStateAsync();
            }
        }

        var external = System.Text.Encoding.UTF8.GetBytes("navigator: {theme_scheme_name: external}\n");
        if (conflict)
        {
            await File.WriteAllBytesAsync(root.Options.UiStatePath, external);
        }
        else
        {
            File.SetAttributes(root.Options.UiStatePath, FileAttributes.ReadOnly);
        }

        try
        {
            await SaveAsync();
            await SaveAsync();
            Assert.AreEqual(0, refreshes);
            Assert.AreEqual(2, statuses.Count);
            Assert.IsTrue(statuses.All(tone => tone == (conflict ? StatusTone.Warning : StatusTone.Error)));
            Assert.IsTrue(shell.HasPendingViewStateChanges);
            CollectionAssert.AreEqual(conflict ? external : original, await File.ReadAllBytesAsync(root.Options.UiStatePath));
        }
        finally
        {
            File.SetAttributes(root.Options.UiStatePath, FileAttributes.Normal);
        }

        if (conflict)
        {
            await File.WriteAllBytesAsync(root.Options.UiStatePath, original);
        }

        await SaveAsync();
        Assert.AreEqual(sidebar ? 1 : 0, refreshes);
        Assert.AreEqual(StatusTone.Info, statuses[^1]);
        Assert.IsFalse(shell.HasPendingViewStateChanges);
        Assert.IsTrue(shell.ViewStatePersistenceResult!.IsAcknowledged);
        Assert.AreEqual("pending-feedback-theme", (await catalog.LoadViewStateAsync()).Navigator.ThemeSchemeName);
    }

    [TestMethod]
    public async Task Tui_ShutdownSaveReturnsFailureWithoutInvokingUiFeedback()
    {
        using var root = new OwnedRoot();
        var catalog = new SessionViewCatalog(root.Options);
        await File.WriteAllTextAsync(root.Options.UiStatePath, Fixture);
        var dispatcher = new InlineDispatcher();
        var shell = TestSessionStateServices.CreateCoordinator(new ProjectCatalog(root.Options), catalog,
            dispatcher, new ShellStateStore(dispatcher), persistenceFeedback:
            new ViewStatePersistenceFeedback((_, _, _) => Assert.Fail("Shutdown must not call disposed UI feedback.")));
        await shell.LoadCatalogStateAsync(CancellationToken.None);
        File.SetAttributes(root.Options.UiStatePath, FileAttributes.ReadOnly);
        try
        {
            var result = await shell.PersistViewStateAsync(reportStatus: false);
            Assert.IsFalse(result.IsAcknowledged);
            Assert.IsInstanceOfType<UnauthorizedAccessException>(result.Error);
            Assert.IsTrue(shell.HasPendingViewStateChanges);
        }
        finally
        {
            File.SetAttributes(root.Options.UiStatePath, FileAttributes.Normal);
        }
    }

    private sealed class OwnedRoot : IDisposable
    {
        public OwnedRoot()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CodeAlta-UiState-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            Options = new CatalogOptions { GlobalRoot = Path };
        }

        public string Path { get; }
        public CatalogOptions Options { get; }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private sealed class InlineDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;
        public void Post(Action action) => action();
        public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
        public Task<T> InvokeAsync<T>(Func<T> action) => Task.FromResult(action());
    }

    private static void AssertRetained(string yaml)
    {
        foreach (var key in new[] { "future_root:", "future_navigator:", "future_selection:", "future_preference:", "future-head:", "absent.panel", "frontend_layouts:", "logical_tabs:" })
        {
            StringAssert.Contains(yaml, key);
        }

        using var beforeReader = new StringReader(Fixture);
        using var afterReader = new StringReader(yaml);
        var before = (YamlMapping)YamlStream.Load(beforeReader)[0].Contents!;
        var after = (YamlMapping)YamlStream.Load(afterReader)[0].Contents!;
        foreach (var key in new[] { "future_root", "future_tagged", "logical_tabs" })
        {
            Assert.AreEqual(before[key]!.ToString(), after[key]!.ToString());
        }

        foreach (var (section, key) in new[] { ("navigator", "future_navigator"), ("selection", "future_selection"), ("frontend_layouts", "future-head") })
        {
            Assert.AreEqual(((YamlMapping)before[section]!)[key]!.ToString(), ((YamlMapping)after[section]!)[key]!.ToString());
        }

        Assert.AreEqual(((YamlMapping)((YamlMapping)before["project_preferences"]!)["project-1"]!)["future_preference"]!.ToString(),
            ((YamlMapping)((YamlMapping)after["project_preferences"]!)["project-1"]!)["future_preference"]!.ToString());
    }
}
