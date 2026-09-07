using System.Runtime.CompilerServices;

namespace CodeAlta.Tests;

[TestClass]
public sealed class FileEditorWorkspaceCleanupSourceTests
{
    [TestMethod]
    public void WorkspaceCleanup_SourceWiring_UsesMandatoryCore()
    {
        var workspace = ReadSource("CodeAlta.Tui/Views/FileEditorWorkspaceCoordinator.cs");
        var app = ReadSource("CodeAlta.Tui/App/CodeAltaApp.cs");
        var shell = ReadSource("CodeAlta.Tui/App/ShellFrontendHost.cs");
        RequireOnce(workspace, "await DisposeWorkspaceAsync(");
        var adapter = Scope(workspace, "    public async ValueTask DisposeAsync()", "    public Task ShowOpenFilePickerAsync()");
        Assert.AreEqual("""
                public async ValueTask DisposeAsync()
                {
                    await DisposeWorkspaceAsync(
                        _filePickerController.DisposeAsync,
                        () => _fileTabsById.Values.ToArray());
                }
            """ + "\n\n", adapter);
        RequireOrdered(adapter,
            "await DisposeWorkspaceAsync(",
            "_filePickerController.DisposeAsync,",
            "() => _fileTabsById.Values.ToArray());");
        Reject(adapter, "async ()", "ConfigureAwait", "try", "catch", "throw", " = null", "Clear(",
            "Distinct(", "OrderBy(", "Task.Run(", "Task.WhenAll(", "Task.WhenAny(", "WaitAsync(");

        RequireOrdered(workspace,
            "    public async ValueTask DisposeAsync()",
            "    private List<string> GetOpenEditorTabIds()",
            "    internal static Task DisposeWorkspaceAsync(");
        var helperAndDocumentation = Scope(workspace,
            "    private List<string> GetOpenEditorTabIds()",
            "    internal static Task DisposeWorkspaceAsync(");
        const string finalHelper = """
                private List<string> GetOpenEditorTabIds()
                    => _shellTabs.GetTabs()
                        .Where(static tab => tab.Kind == ShellTabKind.Editor)
                        .Select(static tab => tab.TabId.Value)
                        .ToList();
            """;
        Assert.IsTrue(helperAndDocumentation.StartsWith(finalHelper + "\n\n", StringComparison.Ordinal));
        var documentation = helperAndDocumentation[finalHelper.Length..];
        foreach (var marker in new[]
        {
            "    /// <summary>", "    /// <remarks>",
            "    /// <param name=\"disposePicker\">", "    /// <param name=\"snapshotTabs\">",
            "    /// <exception cref=\"ArgumentNullException\">", "    /// <exception cref=\"InvalidOperationException\">",
            "    /// <exception cref=\"Exception\">", "    /// <exception cref=\"OperationCanceledException\">",
            "    /// <exception cref=\"AggregateException\">",
        }) RequireOnce(documentation, marker);
        // Require contract concepts, not an invented exact XML body. The future author must retain
        // these explicit qualifications rather than present this as complete editor shutdown.
        foreach (var token in new[]
        {
            "mandatory", "synchronous", "null snapshot", "null entry", "EDI", "OCE", "direct", "aggregate", "inline", "plain await",
            "picker", "terminal", "once", "late snapshot", "entry", "reference", "fallback", "cache",
            "retry", "admission", "concurrent", "noncompletion", "search", "tab", "save", "load",
            "queued", "durable", "native",
        }) Assert.IsTrue(documentation.Contains(token, StringComparison.Ordinal), $"Missing XML qualification: {token}");
        Assert.IsTrue(documentation.EndsWith("\n", StringComparison.Ordinal));
        foreach (var line in documentation[2..^1].Split('\n'))
            Assert.IsTrue(line.StartsWith("    ///", StringComparison.Ordinal),
                "Only appended XML may separate the complete original final helper and the core.");
        // This literal is forbidden throughout Views production, even in its comments/XML.
        Reject(workspace, "ConfigureAwait(false)", "#line");

        RequireOnce(app, """
                async ValueTask IShellFrontendHostLifecycle.DisposeFrontendAsync()
                {
                    await ShellFrontendHost.DisposeFrontendResourcesAsync(
                        _projectionCoordinator.Dispose, _reminderUiCoordinator.Dispose,
                        () => _sessionStateCoordinator.PersistViewStateAsync(reportStatus: false),
                        _fileEditorWorkspaceCoordinator.DisposeAsync,
                        _runtimeEventPump.DisposeAsync,
                        _shellController.DisposeAsync,
                        _promptDraftUiCoordinator.DisposeAsync);
                }
            """);
        RequireOnce(app, "    public async ValueTask DisposeAsync()\n        => await _frontendHost.DisposeAsync();");
        RequireOnce(app, "    IAsyncDisposable? IShellFrontendHostLifecycle.OwnedServices => _ownedServices;");
        // Count the unchanged CRLF checkout representation, not a new filesystem metadata probe.
        Assert.IsTrue(System.Text.Encoding.UTF8.GetByteCount(app.Replace("\n", "\r\n", StringComparison.Ordinal)) < 47_064);
        var frontend = Scope(shell, "    internal static Task DisposeFrontendResourcesAsync(", "\n    }\n");
        RequireOrdered(frontend,
            "Action disposeProjection,", "Action disposeReminderUi,", "Func<Task> persistViewState,",
            "Func<ValueTask> disposeFileEditors,", "Func<ValueTask> disposeRuntimeEventPump,",
            "Func<ValueTask> disposeShellController,", "Func<ValueTask> disposePromptDrafts)",
            "ArgumentNullException.ThrowIfNull(disposeProjection);", "ArgumentNullException.ThrowIfNull(disposeReminderUi);",
            "ArgumentNullException.ThrowIfNull(persistViewState);", "ArgumentNullException.ThrowIfNull(disposeFileEditors);",
            "ArgumentNullException.ThrowIfNull(disposeRuntimeEventPump);", "ArgumentNullException.ThrowIfNull(disposeShellController);",
            "ArgumentNullException.ThrowIfNull(disposePromptDrafts);", "return CoreAsync();",
            "disposeProjection();", "disposeReminderUi();", "await persistViewState();", "await disposeFileEditors();",
            "await disposeRuntimeEventPump();", "await disposeShellController();", "await disposePromptDrafts();",
            "ExceptionDispatchInfo.Throw(failures[0]);", "throw new AggregateException(failures);");
        Reject(frontend, "ConfigureAwait", "catch (OperationCanceledException)", "Flatten(", "Task.Run(");
        RequireOnce(frontend, """
                        try
                        {
                            await disposeFileEditors();
                        }
                        catch (Exception ex)
                        {
                            (failures ??= []).Add(ex);
                        }
            """);
        RequireOnce(shell, """
                private async ValueTask DisposeFrontendAndOwnedServicesAsync()
                {
                    Exception? frontendFailure = null;
                    try
                    {
                        await _lifecycle.DisposeFrontendAsync();
                    }
                    catch (Exception ex)
                    {
                        frontendFailure = ex;
                    }

                    // A failed draft acknowledgement must not abandon runtime/provider/plugin ownership.
                    try
                    {
                        if (_lifecycle.OwnedServices is { } ownedServices)
                        {
                            await ownedServices.DisposeAsync();
                        }
                    }
                    catch (Exception ex) when (frontendFailure is not null)
                    {
                        throw new AggregateException(frontendFailure, ex);
                    }

                    if (frontendFailure is not null)
                    {
                        ExceptionDispatchInfo.Throw(frontendFailure);
                    }
                }
            """);
        RequireOnce(shell, """
                public async ValueTask DisposeAsync()
                    => await DisposeRemindersThenFrontendAsync(
                        () => _reminders?.DisposeAsync() ?? ValueTask.CompletedTask,
                        DisposeFrontendAndOwnedServicesAsync);
            """);
    }

    [TestMethod]
    public void WorkspaceCleanup_SourceCore_ContainsFailuresInOriginalOrder()
    {
        var workspace = ReadSource("CodeAlta.Tui/Views/FileEditorWorkspaceCoordinator.cs");
        RequireOnce(workspace, "    internal static Task DisposeWorkspaceAsync(");
        var core = Scope(workspace, "    internal static Task DisposeWorkspaceAsync(", "\n    }\n");
        Assert.AreEqual("""
                internal static Task DisposeWorkspaceAsync(
                    Func<ValueTask> disposePicker,
                    Func<IAsyncDisposable[]> snapshotTabs)
                {
                    ArgumentNullException.ThrowIfNull(disposePicker);
                    ArgumentNullException.ThrowIfNull(snapshotTabs);
                    return CoreAsync();

                    async Task CoreAsync()
                    {
                        List<Exception>? failures = null;
                        try
                        {
                            await disposePicker();
                        }
                        catch (Exception ex)
                        {
                            (failures ??= []).Add(ex);
                        }

                        IAsyncDisposable[]? tabs = null;
                        try
                        {
                            tabs = snapshotTabs() ?? throw new InvalidOperationException("The tab snapshot must not be null.");
                        }
                        catch (Exception ex)
                        {
                            (failures ??= []).Add(ex);
                        }

                        if (tabs is not null)
                        {
                            foreach (var tab in tabs)
                            {
                                try
                                {
                                    if (tab is null)
                                    {
                                        throw new InvalidOperationException("The tab snapshot contains a null entry.");
                                    }

                                    await tab.DisposeAsync();
                                }
                                catch (Exception ex)
                                {
                                    (failures ??= []).Add(ex);
                                }
                            }
                        }

                        if (failures is { Count: 1 })
                        {
                            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failures[0]);
                        }

                        if (failures is { Count: > 1 })
                        {
                            throw new AggregateException(failures);
                        }
                    }
                }
            """, core + "\n    }");
        RequireOrdered(core,
            "ArgumentNullException.ThrowIfNull(disposePicker);", "ArgumentNullException.ThrowIfNull(snapshotTabs);",
            "return CoreAsync();", "async Task CoreAsync()", "await disposePicker();",
            "IAsyncDisposable[]? tabs = null;", "tabs = snapshotTabs() ?? throw new InvalidOperationException(\"The tab snapshot must not be null.\");",
            "if (tabs is not null)", "foreach (var tab in tabs)", "if (tab is null)",
            "throw new InvalidOperationException(\"The tab snapshot contains a null entry.\");", "await tab.DisposeAsync();",
            "System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failures[0]);", "throw new AggregateException(failures);");
        Reject(core,
            "ConfigureAwait", "catch (OperationCanceledException)", " when (", "IsCanceled", "IsFaulted", "IsCompleted",
            "CancellationToken", ".Flatten(", ".Distinct(", ".OrderBy(", ".GetBaseException(", "ReferenceEquals(",
            "Task.Run(", "Task.Factory", "Task.WhenAny(", "Task.WhenAll(", "Task.Delay(", "ContinueWith(",
            ".WaitAsync(", "GetAwaiter().GetResult()", ".Result", ".Wait(", "Lazy<", "Interlocked.", "lock (",
            "_fileTabsById", "_fileTabsByPath", "_filePickerController", "CodeAltaApp", "CodeAltaTaskMonitor");
        Assert.IsTrue(workspace.EndsWith(core + "\n    }\n}\n", StringComparison.Ordinal),
            "Only the appended core and existing class close may follow its XML documentation.");
    }

    [TestMethod]
    public void WorkspaceCleanup_Source_PreservesCompleteOriginalOutsideDisposal()
    {
        var workspace = ReadSource("CodeAlta.Tui/Views/FileEditorWorkspaceCoordinator.cs");
        const string original = """
            using CodeAlta.Tui.App;
            using CodeAlta.Catalog;
            using CodeAlta.Tui.Models;
            using CodeAlta.Tui.Presentation.Editing;
            using CodeAlta.Tui.Presentation.Prompting;
            using XenoAtom.Terminal.UI;
            using XenoAtom.Terminal.UI.Controls;

            namespace CodeAlta.Tui.Views;

            internal sealed class FileEditorWorkspaceCoordinator : IAsyncDisposable
            {
                private readonly TextFileCodec _textFiles;
                private readonly Func<SessionWorkspaceView?> _getWorkspaceView;
                private readonly Func<Visual?> _getSessionFocusTarget;
                private readonly Action<Action> _dispatchToUiDeferred;
                private readonly Action _syncSessionTabControl;
                private readonly Action<string, bool, StatusTone> _setStatus;
                private readonly IShellTabService _shellTabs;
                private readonly Func<Func<Visual>, ComputedVisual> _createComputedVisual;
                private readonly ProjectFileOpenDialogController _filePickerController;
                private readonly Dictionary<string, FileEditorTab> _fileTabsById = new(StringComparer.OrdinalIgnoreCase);
                private readonly Dictionary<string, FileEditorTab> _fileTabsByPath = new(StringComparer.OrdinalIgnoreCase);

                public FileEditorWorkspaceCoordinator(
                    TextFileCodec textFiles,
                    IProjectFileSearchService projectFileSearchService,
                    IShellTabService shellTabs,
                    Func<string?> resolveProjectRoot,
                    Func<Visual?> getSessionFocusTarget,
                    Func<SessionWorkspaceView?> getWorkspaceView,
                    Func<Func<Visual>, ComputedVisual> createComputedVisual,
                    Action<Action> dispatchToUiDeferred,
                    Action syncSessionTabControl,
                    Action<string, bool, StatusTone> setStatus)
                {
                    ArgumentNullException.ThrowIfNull(textFiles);
                    ArgumentNullException.ThrowIfNull(projectFileSearchService);
                    ArgumentNullException.ThrowIfNull(shellTabs);
                    ArgumentNullException.ThrowIfNull(resolveProjectRoot);
                    ArgumentNullException.ThrowIfNull(getSessionFocusTarget);
                    ArgumentNullException.ThrowIfNull(getWorkspaceView);
                    ArgumentNullException.ThrowIfNull(createComputedVisual);
                    ArgumentNullException.ThrowIfNull(dispatchToUiDeferred);
                    ArgumentNullException.ThrowIfNull(syncSessionTabControl);
                    ArgumentNullException.ThrowIfNull(setStatus);

                    _textFiles = textFiles;
                    _shellTabs = shellTabs;
                    _getWorkspaceView = getWorkspaceView;
                    _getSessionFocusTarget = getSessionFocusTarget;
                    _createComputedVisual = createComputedVisual;
                    _dispatchToUiDeferred = dispatchToUiDeferred;
                    _syncSessionTabControl = syncSessionTabControl;
                    _setStatus = setStatus;
                    _filePickerController = new ProjectFileOpenDialogController(
                        projectFileSearchService,
                        ProjectFileAppearanceRegistry.Default,
                        resolveProjectRoot,
                        GetActiveWorkspaceFocusTarget,
                        OpenFileTab,
                        setStatus);
                }

                public IReadOnlyList<string> OpenTabIds => GetOpenEditorTabIds();

                public string? SelectedTabId
                    => _shellTabs.GetTabs()
                        .FirstOrDefault(static shellTab => shellTab is { IsSelected: true, Kind: ShellTabKind.Editor })
                        ?.TabId.Value;

                public async ValueTask DisposeAsync()
                {
                    await _filePickerController.DisposeAsync();
                    foreach (var fileTab in _fileTabsById.Values.ToArray())
                    {
                        await fileTab.DisposeAsync();
                    }
                }

                public Task ShowOpenFilePickerAsync()
                    => _filePickerController.ShowAsync();

                public Task OpenFilePathAsync(string fullPath, CancellationToken cancellationToken = default)
                    => OpenDocumentAsync(new TextFileDocument(fullPath), cancellationToken);

                public Task OpenDocumentAsync(TextFileDocument document, CancellationToken cancellationToken = default)
                {
                    ArgumentNullException.ThrowIfNull(document);

                    var resolvedPath = document.FullPath;
                    if (!File.Exists(resolvedPath))
                    {
                        _setStatus(SR.T("Cannot open missing file '{0}'.", resolvedPath), false, StatusTone.Warning);
                        return Task.CompletedTask;
                    }

                    var projectRoot = Path.GetDirectoryName(resolvedPath) ?? resolvedPath;
                    var basename = Path.GetFileName(resolvedPath);
                    var relativePath = basename;
                    var extension = Path.GetExtension(resolvedPath);
                    DateTimeOffset? lastWriteTimeUtc = null;
                    try
                    {
                        lastWriteTimeUtc = new DateTimeOffset(File.GetLastWriteTimeUtc(resolvedPath), TimeSpan.Zero);
                    }
                    catch (IOException)
                    {
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }

                    var item = new ProjectFileSearchItem
                    {
                        Kind = ProjectFileSearchItemKind.File,
                        ProjectRoot = projectRoot,
                        RelativePath = relativePath,
                        FullPath = resolvedPath,
                        Basename = basename,
                        ParentPath = projectRoot,
                        Extension = extension,
                        LastWriteTimeUtc = lastWriteTimeUtc,
                        SearchFields = new ProjectFileSearchFields(
                            basename.ToLowerInvariant(),
                            relativePath.ToLowerInvariant(),
                            [relativePath.ToLowerInvariant()],
                            extension.ToLowerInvariant()),
                    };

                    return OpenFileTabAsync(item, ProjectFileAppearanceRegistry.Default.GetAppearance(item), cancellationToken, document);
                }

                public FileEditorTab? GetSelectedFileTab()
                    => SelectedTabId is { Length: > 0 } selectedTabId && _fileTabsById.TryGetValue(selectedTabId, out var fileTab)
                        ? fileTab
                        : null;

                public FileEditorTab? GetFileTab(string tabId)
                    => _fileTabsById.GetValueOrDefault(tabId);

                public void SelectFileTab(string tabId)
                {
                    if (!_fileTabsById.ContainsKey(tabId))
                    {
                        return;
                    }

                    _shellTabs.SelectTabAsync(new ShellTabId(tabId)).GetAwaiter().GetResult();
                    _syncSessionTabControl();
                    if (_fileTabsById.TryGetValue(tabId, out var fileTab))
                    {
                        _dispatchToUiDeferred(fileTab.Focus);
                    }
                }

                public async Task CloseFileTabAsync(string tabId)
                {
                    if (!_fileTabsById.TryGetValue(tabId, out var fileTab))
                    {
                        return;
                    }

                    var openTabIds = GetOpenEditorTabIds();
                    var removedIndex = openTabIds.FindIndex(candidate => string.Equals(candidate, tabId, StringComparison.OrdinalIgnoreCase));
                    var wasSelected = string.Equals(SelectedTabId, tabId, StringComparison.OrdinalIgnoreCase);
                    await fileTab.RequestCloseAsync(
                        async () =>
                        {
                            await fileTab.DisposeAsync();
                            _fileTabsById.Remove(tabId);
                            _fileTabsByPath.Remove(fileTab.FullPath);
                            await _shellTabs.CloseTabAsync(new ShellTabId(tabId), ShellTabCloseReason.FileEditorClosed);
                            _getWorkspaceView()?.RemoveTabPage(tabId);
                            if (wasSelected)
                            {
                                SelectRemainingFileTabOrSessionSurface(removedIndex);
                            }

                            _syncSessionTabControl();
                            _setStatus(SR.T("Closed '{0}'.", fileTab.Item.Basename), false, StatusTone.Info);
                        });
                }

                public void ActivateSessionSurface()
                {
                }

                public Visual? GetActiveWorkspaceFocusTarget()
                    => GetSelectedFileTab()?.Editor as Visual ?? _getSessionFocusTarget();

                private void OpenFileTab(ProjectFileSearchItem item, ProjectFileAppearance appearance)
                    => _ = OpenFileTabAsync(item, appearance);

                private async Task OpenFileTabAsync(
                    ProjectFileSearchItem item,
                    ProjectFileAppearance appearance,
                    CancellationToken cancellationToken = default,
                    TextFileDocument? document = null)
                {
                    ArgumentNullException.ThrowIfNull(item);
                    ArgumentNullException.ThrowIfNull(appearance);

                    if (_fileTabsByPath.TryGetValue(item.FullPath, out var existingTab))
                    {
                        if (document is { IsReadOnly: true } && !existingTab.TryProtect(document))
                        {
                            _setStatus(SR.T("Cannot open read-only while a save is in progress. Retry after the save finishes."), false, StatusTone.Warning);
                            return;
                        }
                        SelectFileTab(existingTab.TabId);
                        existingTab.Focus();
                        if (existingTab.IsReadOnly)
                        {
                            _setStatus(SR.T("Read-only skill document. Unsaved text is retained for copying; saving is disabled."), false, StatusTone.Info);
                        }
                        return;
                    }

                    try
                    {
                        var fileTab = await FileEditorTab.CreateAsync(item, appearance, _textFiles, (message, showSpinner, tone) => _setStatus(message, showSpinner, tone), cancellationToken, document);
                        // Another UI open may have completed while the file was loading. Reuse through the
                        // same tightening rule rather than replacing a protected tab with this older request.
                        if (_fileTabsByPath.ContainsKey(item.FullPath))
                        {
                            await fileTab.DisposeAsync();
                            await OpenFileTabAsync(item, appearance, cancellationToken, document);
                            return;
                        }
                        _fileTabsById[fileTab.TabId] = fileTab;
                        _fileTabsByPath[fileTab.FullPath] = fileTab;
                        _shellTabs.OpenOrGetTab(new ShellTabDescriptor
                        {
                            TabId = new ShellTabId(fileTab.TabId),
                            Kind = ShellTabKind.Editor,
                            Association = new ShellTabAssociation.Editor(ProjectId.NewVersion7(), fileTab.FullPath),
                            Header = fileTab.CreateTabHeader(_createComputedVisual),
                            Content = fileTab.Root,
                            ViewModel = fileTab,
                        });
                        SelectFileTab(fileTab.TabId);
                        _dispatchToUiDeferred(fileTab.Focus);
                        _setStatus(fileTab.IsReadOnly ? SR.T("Read-only skill document. Unsaved text is retained for copying; saving is disabled.") : SR.T("Opened '{0}' for editing.", item.Basename), false, StatusTone.Ready);
                    }
                    catch (Exception ex)
                    {
                        _setStatus(SR.T("Failed to open '{0}': {1}", item.RelativePath, ex.Message), false, StatusTone.Error);
                    }
                }

                private void SelectRemainingFileTabOrSessionSurface(int removedIndex)
                {
                    var openTabIds = GetOpenEditorTabIds();
                    if (openTabIds.Count == 0)
                    {
                        ActivateSessionSurface();
                        return;
                    }

                    var nextIndex = removedIndex <= 0
                        ? 0
                        : Math.Min(removedIndex - 1, openTabIds.Count - 1);
                    SelectFileTab(openTabIds[nextIndex]);
                }

                private List<string> GetOpenEditorTabIds()
                    => _shellTabs.GetTabs()
                        .Where(static tab => tab.Kind == ShellTabKind.Editor)
                        .Select(static tab => tab.TabId.Value)
                        .ToList();
            }
            """ + "\n";
        const string disposalStart = "    public async ValueTask DisposeAsync()";
        const string afterDisposal = "    public Task ShowOpenFilePickerAsync()";
        RequireOnce(workspace, disposalStart);
        RequireOnce(workspace, afterDisposal);
        var actualDisposal = Scope(workspace, disposalStart, afterDisposal);
        var originalDisposal = Scope(original, disposalStart, afterDisposal);
        var reconstructed = workspace.Replace(actualDisposal, originalDisposal, StringComparison.Ordinal);
        const string coreStart = "    internal static Task DisposeWorkspaceAsync(";
        if (workspace.Contains(coreStart, StringComparison.Ordinal))
        {
            RequireOnce(workspace, coreStart);
            const string helperStart = "    private List<string> GetOpenEditorTabIds()";
            var originalHelper = Scope(original, helperStart, "\n}\n");
            RequireOnce(reconstructed, originalHelper);
            var helperEnd = reconstructed.IndexOf(originalHelper, StringComparison.Ordinal) + originalHelper.Length;
            var appended = reconstructed[helperEnd..];
            Assert.IsTrue(appended.StartsWith("\n\n    /// <summary>\n", StringComparison.Ordinal));
            var coreIndex = appended.IndexOf(coreStart, StringComparison.Ordinal);
            Assert.IsTrue(coreIndex > 0);
            Assert.IsTrue(appended[..coreIndex].EndsWith("\n", StringComparison.Ordinal));
            foreach (var line in appended[2..(coreIndex - 1)].Split('\n'))
                Assert.IsTrue(line.StartsWith("    ///", StringComparison.Ordinal),
                    "No extra members or padding may precede the appended core.");
            var core = Scope(appended, coreStart, "\n    }\n");
            Assert.IsTrue(appended.EndsWith(core + "\n    }\n}\n", StringComparison.Ordinal),
                "No extra members may follow the appended core.");
            reconstructed = reconstructed[..helperEnd] + "\n}\n";
        }
        Assert.AreEqual(original, reconstructed, "Inverse must restore the complete original workspace, not selected snippets.");
        // Path-key reuse is not filesystem identity. Snapshot/null-entry seam cases do not allege
        // dictionary corruption or normal duplicate entries. No admission or publication repair here.
    }

    [TestMethod]
    public void WorkspaceCleanup_Source_PreservesPickerAndSearchLifetimeBoundaries()
    {
        var picker = ReadSource("CodeAlta.Tui/Presentation/Editing/ProjectFileOpenDialogController.cs");
        var dialog = ReadSource("CodeAlta.Tui/Presentation/Prompting/ProjectFilePickerDialog.cs");
        var service = ReadSource("CodeAlta.Catalog/ProjectFiles/ProjectFileSearchService.cs");
        var session = ReadSource("CodeAlta.Catalog/ProjectFiles/ProjectFileSearchSession.cs");
        var sessionInterface = ReadSource("CodeAlta.Catalog/ProjectFiles/IProjectFileSearchSession.cs");
        RequireOnce(picker, """
                    _dialog = new ProjectFilePickerDialog(SR.T("Arrows move · Enter open · Esc close"));
                    _dialog.QueryChanged += (_, queryText) => _ = OnDialogQueryChangedAsync(queryText);
                    _dialog.SelectionChanged += (_, selectedIndex) => OnSelectionChanged(selectedIndex);
                    _dialog.AcceptRequested += (_, _) => AcceptSelected();
                    _dialog.DismissRequested += (_, _) => _ = CloseAsync();
                    RefreshDialogLabels();
            """);
        RequireOnce(picker, "    public async ValueTask DisposeAsync()\n        => await CloseAsync();");
        RequireOnce(picker, """
                private async Task CloseAsync(bool restoreFocus = true)
                {
                    if (_dialog.IsOpen)
                    {
                        var app = _dialogIsApp();
                        _dialog.Close();
                        if (restoreFocus && _getFocusTarget() is { } focusTarget)
                        {
                            app?.Focus(focusTarget);
                        }
                    }

                    if (_session is not null)
                    {
                        _session.Updated -= OnSessionUpdated;
                        await _session.DisposeAsync();
                        _session = null;
                    }

                    _items = [];
                    _activeQuery = string.Empty;
                    _selectedRelativePath = null;
                    _selectedIndex = -1;
                    _candidateCount = 0;
                    _isRefreshing = false;
                    _projectRoot = null;
                    _dialog.SetQueryText(string.Empty);
                    _dialog.SetResults(_emptyItems, -1);
                    RefreshDialogLabels();
                }
            """);
        var show = Scope(picker, "    public async Task ShowAsync(", "    public async ValueTask DisposeAsync()");
        RequireOrdered(show, "await CloseAsync();", "_projectRoot = projectRoot;", "_dialog.Show(app);\n\n",
            "_session = await _searchService.CreateSessionAsync(", "cancellationToken);", "_session.Updated += OnSessionUpdated;", "ApplyState(_session.Current);");
        Reject(show, "_disposed", "finally", "ConfigureAwait", "Task.Run(");
        RequireOnce(service, """
                    var normalizedRoot = ProjectFilePathUtilities.NormalizeProjectRoot(options.ProjectRoot);
                    var recentTask = _usageStore.GetRecentAsync(normalizedRoot, Math.Max(1, options.RecentItemLimit), cancellationToken).AsTask();
                    var cacheTask = _snapshotCache.GetAsync(normalizedRoot, cancellationToken).AsTask();
                    await Task.WhenAll(recentTask, cacheTask).ConfigureAwait(false);

                    var session = new ProjectFileSearchSession(
                        normalizedRoot,
                        options with { ProjectRoot = normalizedRoot },
                        _snapshotCache,
                        _usageStore,
                        _traversal,
                        _scorer,
                        recentTask.Result,
                        cacheTask.Result);
                    await session.RefreshAsync(cancellationToken).ConfigureAwait(false);
                    return session;
            """);
        // The researched default host uses memory cache/usage implementations whose reads complete
        // synchronously. Those sources are research evidence, NOT additional fixture reads. A delayed
        // interface acquisition is a permitted channel, not a demonstrated default delayed creation.
        RequireOnce(sessionInterface, "public interface IProjectFileSearchSession : IAsyncDisposable");
        RequireOnce(sessionInterface, "ValueTask RefreshAsync(CancellationToken cancellationToken = default);");
        RequireOnce(picker, """
                private async Task OnDialogQueryChangedAsync(string queryText)
                {
                    _activeQuery = queryText;
                    RefreshDialogLabels();

                    if (_session is not null)
                    {
                        await _session.SetQueryAsync(queryText);
                    }
                }
            """);
        RequireOnce(picker, """
                    var selected = _items[_selectedIndex];
                    _ = AcceptSelectedAsync(selected);
                    return true;
            """);
        Assert.AreEqual("        _ = AcceptSelectedAsync(selected);", picker.Split('\n')[216]);
        RequireOnce(picker, """
                private async Task AcceptSelectedAsync(ProjectFileReferencePopupItem selected)
                {
                    ArgumentNullException.ThrowIfNull(selected);

                    var dispatcher = _getFocusTarget()?.Dispatcher;
                    await CloseAsync(restoreFocus: false);
                    if (dispatcher is not null)
                    {
                        dispatcher.Post(() => _openFile(selected.Result.Item, selected.Appearance));
                    }
                    else
                    {
                        _openFile(selected.Result.Item, selected.Appearance);
                    }

                    _ = _searchService.RecordUsageAsync(
                        new ProjectFileUsageEvent(
                            selected.Result.Item.ProjectRoot,
                            selected.Result.Item.RelativePath,
                            selected.Result.Item.Kind,
                            DateTimeOffset.UtcNow,
                            ProjectFileUsageAccessKind.PopupAccepted));
                }
            """);
        var updated = Scope(picker, "    private void OnSessionUpdated(", "    private void ApplyState(ProjectFileSearchState state)");
        RequireOrdered(updated, "if (!ReferenceEquals(sender, _session))", "var dispatcher = _getFocusTarget()?.Dispatcher;",
            "dispatcher.Post(() => ApplyStateIfCurrent(e.State));", "private void ApplyStateIfCurrent(ProjectFileSearchState state)",
            "if (!_dialog.IsOpen ||", "!string.Equals(state.Query, _activeQuery, StringComparison.Ordinal)", "ApplyState(state);");
        var queuedUpdate = Scope(updated, "    private void ApplyStateIfCurrent(", "\n    }\n");
        Reject(queuedUpdate, "ReferenceEquals", "_session", "Generation");
        RequireOnce(dialog, """
                public void Close()
                {
                    if (!_isOpen)
                    {
                        return;
                    }

                    _dialog.Close();
                    _isOpen = false;
                }
            """);
        Reject(dialog, "DisposeAsync(", "CancellationTokenSource", "Task.WhenAll(");
        RequireOnce(session, """
                public ValueTask DisposeAsync()
                {
                    lock (_gate)
                    {
                        if (_disposed)
                        {
                            return ValueTask.CompletedTask;
                        }

                        _disposed = true;
                        _refreshCancellation?.Cancel();
                        _refreshCancellation?.Dispose();
                        _pendingRankingRequest = null;
                    }

                    return ValueTask.CompletedTask;
                }
            """);
        var refresh = Scope(session, "    public ValueTask RefreshAsync(", "    public ValueTask DisposeAsync()");
        RequireOrdered(refresh, "cancellationToken.ThrowIfCancellationRequested();", "ThrowIfDisposed();",
            "_refreshCancellation?.Cancel();", "_refreshCancellation?.Dispose();",
            "linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);",
            "_refreshCancellation = linkedCancellation;", "PublishState(",
            "_ = RunRefreshAsync(generation, existingCandidates, linkedCancellation.Token);", "return ValueTask.CompletedTask;");
        var refreshWorker = Scope(session, "    private async Task RunRefreshAsync(", "    private void ScheduleRanking(");
        RequireOrdered(refreshWorker,
            "await _snapshotCache.MarkDirtyAsync(_projectRoot, ProjectFileInvalidationReason.SessionRefresh, cancellationToken).ConfigureAwait(false);",
            "var usageByPath = await _usageStore.GetUsageByRelativePathAsync(_projectRoot, cancellationToken).ConfigureAwait(false);",
            "var traversalSnapshot = await Task.Run(", "() => _traversal.Traverse(", "                cancellationToken).ConfigureAwait(false);",
            "if (cancellationToken.IsCancellationRequested)", "await _snapshotCache.SetAsync(snapshot, cancellationToken).ConfigureAwait(false);",
            "catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)");
        Reject(refreshWorker, "catch (Exception", "finally", "CodeAltaTaskMonitor");
        var ranking = Scope(session, "    private void ScheduleRanking(", "    private void PublishState(");
        RequireOrdered(ranking, "_pendingRankingRequest = new RankingRequest(",
            "_ = Task.Run(RunRankingLoopAsync);", "private void RunRankingLoopAsync()",
            "var ranked = _scorer.Rank(request.Query, request.Candidates, Math.Max(1, _options.MaximumResults));",
            "if (request.RefreshGeneration != _refreshGeneration ||", "PublishState(");
        RequireOnce(session, """
                private void PublishState(ProjectFileSearchState state)
                {
                    lock (_gate)
                    {
                        if (_disposed)
                        {
                            return;
                        }

                        _current = state;
                    }

                    Updated?.Invoke(this, new ProjectFileSearchStateChangedEventArgs(state));
                }
            """);
        // Search disposal is cancel/release, not a refresh/ranking/event traversal join. Setting
        // _disposed first can prevent retry after failure. No throwing application registration or
        // ordinary CTS-release failure is demonstrated by these exception-channel source checks.
    }

    [TestMethod]
    public void WorkspaceCleanup_Source_PreservesTabCloseAndDurableSaveBoundaries()
    {
        var tab = ReadSource("CodeAlta.Tui/Presentation/Editing/FileEditorTab.cs");
        var document = ReadSource("CodeAlta.Tui/Presentation/Editing/FileEditorTextDocument.cs");
        var codec = ReadSource("CodeAlta.Catalog/TextFileCodec.cs");
        var shellTabs = ReadSource("CodeAlta.Tui/App/IShellTabService.cs");
        var workspaceView = ReadSource("CodeAlta.Tui/Views/SessionWorkspaceView.cs");
        var tabHost = ReadSource("CodeAlta.Tui/Views/SessionTabHostView.cs");
        RequireOnce(tab, """
                public async ValueTask DisposeAsync()
                {
                    if (_disposed)
                    {
                        return;
                    }

                    _disposed = true;
                    Volatile.Write(ref _externalRefreshAttachment, null);
                    Editor.TextDocument.Changed -= OnEditorDocumentChanged;

                    if (_watcher is not null)
                    {
                        _watcher.Changed -= OnWatchedFileChanged;
                        _watcher.Created -= OnWatchedFileChanged;
                        _watcher.Deleted -= OnWatchedFileChanged;
                        _watcher.Renamed -= OnWatchedFileRenamed;
                        _watcher.Dispose();
                    }

                    await Task.CompletedTask;
                }
            """);
        RequireOnce(document, """
                public event EventHandler<TextDocumentChangedEventArgs> Changed
                {
                    add => Content.Changed += value;
                    remove => Content.Changed -= value;
                }
            """);
        var constructor = Scope(tab, "    private FileEditorTab(", "    public ProjectFileSearchItem Item");
        RequireOrdered(constructor, "_textFiles = textFiles;", "_document = document;", "Editor = CreateEditor(item, snapshot.Text);",
            "Editor.TextDocument.Changed += OnEditorDocumentChanged;", "_saveButton.Click(() => _ = SaveAsync());",
            "_reloadButton.Click(() => _ = ReloadAsync(confirmWhenDirty: true));", "Root = new EditorRoot(this, BuildRoot())",
            "_watcher = CreateWatcher(item.FullPath);", "UpdateUiState();");
        Reject(constructor, "catch", "finally", "DisposeAsync(");
        RequireOnce(tab, """
                    document ??= new TextFileDocument(item.FullPath);
                    var snapshot = await textFiles.LoadAsync(document, cancellationToken);
                    return new FileEditorTab(item, appearance, textFiles, document, snapshot, setStatus);
            """);
        RequireOnce(tab, """
                    var watcher = new FileSystemWatcher(directoryPath, fileName)
                    {
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                        IncludeSubdirectories = false,
                    };
                    watcher.Changed += OnWatchedFileChanged;
                    watcher.Created += OnWatchedFileChanged;
                    watcher.Deleted += OnWatchedFileChanged;
                    watcher.Renamed += OnWatchedFileRenamed;
                    watcher.EnableRaisingEvents = true;
                    return watcher;
            """);
        RequireOnce(tab, """
                    if (!IsDirty)
                    {
                        await closeTabAsync();
                        return true;
                    }
            """);
        var close = Scope(tab, "    public async Task<bool> RequestCloseAsync(", "    public async ValueTask DisposeAsync()");
        RequireOnce(close, "new DialogAction(SR.T(\"Discard\"), ControlTone.Error, closeTabAsync)]);");
        RequireOnce(close, """
                                    if (await SaveAsync())
                                    {
                                        await closeTabAsync();
                                    }
            """);
        Assert.IsTrue(close.EndsWith("        return false;\n    }\n\n", StringComparison.Ordinal));
        RequireOnce(tab, "button.Click(() => _ = ExecuteDialogActionAsync(dialog, action.Execute));");
        RequireOnce(tab, "var ignored = SaveAsync();");
        RequireOnce(tab, "var ignored = ReloadAsync(confirmWhenDirty: true);");
        RequireOnce(tab, """
                private Task ExecuteDialogActionAsync(Dialog? dialog, Func<Task> execute)
                {
                    ArgumentNullException.ThrowIfNull(execute);
                    CloseDialog(dialog);
                    return execute();
                }
            """);
        var save = Scope(tab, "    internal async Task<TextFileSaveResult> SaveCurrentTextAsync(", "    private async Task ReloadAsync(");
        RequireOnce(save, """
                    _pendingSaves++;
                    TextFileSaveResult result;
                    try
                    {
                        result = await _textFiles.SaveAsync(_document, GetEditorText(), _snapshot, expectedRevision);
                    }
                    finally
                    {
                        _pendingSaves--;
                    }
            """);
        RequireOrdered(save, "_snapshot = result.Snapshot;", "_sessionState.MarkSaved(_snapshot.Text, _snapshot.LastWriteTimeUtc);",
            "_sessionState.UpdateEditorText(GetEditorText());", "_setStatus(SR.T(\"Saved '{0}'.\", Item.Basename), false, StatusTone.Ready);");
        Reject(save, "_disposed", "CancellationToken", "ConfigureAwait");
        RequireOnce(tab, "if (_pendingSaves != 0) return false;");
        var reload = Scope(tab, "    private async Task ReloadAsync(", "    private void RefreshExternalState()");
        RequireOrdered(reload, "if (confirmWhenDirty && IsDirty)", "var snapshot = await _textFiles.LoadAsync(_document);",
            "ReplaceEditorDocument(snapshot.Text);", "_sessionState.MarkReloaded(snapshot.Text, snapshot.LastWriteTimeUtc);",
            "catch (Exception ex)", "_setStatus(SR.T(\"Failed to reload '{0}': {1}\", Item.Basename, ex.Message), false, StatusTone.Error);");
        Reject(reload, "_disposed", "CancellationToken", "ConfigureAwait");
        RequireOnce(tab, """
                    Root.Dispatcher.Post(
                        () =>
                        {
                            Editor.GoToLine(Editor.Line, Editor.Column);
                            (Editor.App ?? Root.App)?.Focus(Editor);
                        });
            """);
        var refresh = Scope(tab, "    public void QueueExternalStateRefresh()", "    private Task<bool> SaveAsync()");
        RequireOnce(refresh, """
                        attachment.App.Post(
                            () =>
                            {
                                Interlocked.Exchange(ref attachment.RefreshQueued, 0);
                                if (ReferenceEquals(Volatile.Read(ref _externalRefreshAttachment), attachment))
                                {
                                    RefreshExternalState();
                                }
                            });
            """);
        RequireOnce(refresh, "Volatile.Write(ref _externalRefreshAttachment, new ExternalRefreshAttachment(app));");
        RequireOnce(tab, """
                    protected override void OnDetachedFromApp(TerminalApp app)
                    {
                        Volatile.Write(ref owner._externalRefreshAttachment, null);
                        base.OnDetachedFromApp(app);
                    }
            """);
        RequireOnce(codec, "public Task<TextFileSnapshot> LoadAsync(TextFileDocument document) => LoadAsync(document, CancellationToken.None);");
        RequireOnce(codec, """
                public Task<TextFileSaveResult> SaveAsync(TextFileDocument document, string text, TextFileSnapshot format, TextFileRevision expectedRevision)
                    => SaveAsync(document, text, format, expectedRevision, CancellationToken.None);
            """);
        RequireOnce(codec, "if (document.IsReadOnly) throw new UnauthorizedAccessException(\"The skill document is read-only.\");");
        RequireOnce(codec, """
                    await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    string? stagingPath = null;
                    try
                    {
                        var current = await ReadRevisionAsync(path, cancellationToken).ConfigureAwait(false);
                        if (current != request.ExpectedRevision)
                        {
                            return new TextFileSaveResult(null, current);
                        }

                        var directory = Path.GetDirectoryName(path) ?? throw new ArgumentException("A file path with a parent directory is required.", nameof(request));
                        Directory.CreateDirectory(directory);
                        var candidate = Path.Combine(directory, $".codealta-save-{Guid.NewGuid():N}.tmp");
                        await using (var stream = CreateStagingFile(candidate, current.Exists ? path : null))
                        {
                            stagingPath = candidate; // Only remove a staging file created by this operation.
                            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                        }

                        current = await ReadRevisionAsync(path, cancellationToken).ConfigureAwait(false);
                        if (current != request.ExpectedRevision)
                        {
                            return new TextFileSaveResult(null, current);
                        }

                        if (current.Exists)
                        {
                            if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
                            {
                                throw new UnauthorizedAccessException("The text file is read-only.");
                            }

                            if (!OperatingSystem.IsWindows())
                            {
                                File.SetUnixFileMode(stagingPath, File.GetUnixFileMode(path));
                            }
                        }

                        cancellationToken.ThrowIfCancellationRequested();
                        // The owner gate covers cooperating saves, not external editors or path-link changes.
                        if (current.Exists)
                        {
                            File.Replace(stagingPath, path, destinationBackupFileName: null);
                        }
                        else
                        {
                            File.Move(stagingPath, path); // A concurrent creation must never be overwritten.
                        }

                        stagingPath = null;
                        var snapshot = new TextFileSnapshot(request.Text, encoding, request.HasByteOrderMark, File.GetLastWriteTimeUtc(path), revision);
                        return new TextFileSaveResult(snapshot, revision);
                    }
                    finally
                    {
                        try
                        {
                            if (stagingPath is not null)
                            {
                                File.Delete(stagingPath);
                            }
                        }
                        finally
                        {
                            _saveGate.Release();
                        }
                    }
            """);
        // Shared codec commits can precede UI acknowledgement/failure and disposal. This is not
        // a save/load join, durability rollback, complete hidden-acquisition repair or native
        // watcher callback join. No installed UI-binary match follows from these named sources.
        var shellClose = Scope(shellTabs, "    public async Task<bool> CloseTabAsync(", "    public bool TryGetTab(");
        RequireOrdered(shellClose, "cancellationToken.ThrowIfCancellationRequested();", "if (!entry.Descriptor.CanClose)",
            "_tabs.Remove(tabId);", "RaiseTabsChanged(openTabsChanged: true, selectedTabChanged: selectedChanged);",
            "await entry.Descriptor.OnClosedAsync(reason);", "return true;");
        Reject(shellClose, "ViewModel.Dispose", "finally", "catch", "ConfigureAwait");
        RequireOnce(workspaceView, "    public bool RemoveTabPage(string tabId)\n        => _sessionTabHostView.RemoveTabPage(tabId);");
        RequireOnce(tabHost, """
                public bool RemoveTabPage(string tabId)
                {
                    ArgumentException.ThrowIfNullOrWhiteSpace(tabId);
                    var removed = _tabPages.Remove(tabId, out var page);
                    if (removed && page is not null)
                    {
                        SessionTabControl.TryCloseTab(page);
                    }

                    var splitter = _sessionTabContentSplitters.Remove(tabId, out var cachedSplitter)
                        ? cachedSplitter
                        : page?.Content as VSplitter;
                    if (splitter is not null)
                    {
                        splitter.First = null;
                        splitter.Second = null;
                    }

                    _sessionPromptPanels.Remove(tabId);
                    _askProjectionStates.Remove(tabId);
                    if (string.Equals(_activeSessionTabContentId, tabId, StringComparison.OrdinalIgnoreCase))
                    {
                        _activeSessionTabContentId = null;
                    }

                    return removed;
                }
            """);
    }

    [TestMethod]
    public void WorkspaceCleanup_Source_PreservesQueuedWorkAndObservationBoundaries()
    {
        var app = ReadSource("CodeAlta.Tui/App/CodeAltaApp.cs");
        var queue = ReadSource("CodeAlta.Tui/App/DeferredUiActionQueue.cs");
        var diagnostics = ReadSource("CodeAlta.Tui/App/UiTaskDiagnostics.cs");
        var strip = ReadSource("CodeAlta.Tui/Presentation/Tabs/SessionTabStripCoordinator.cs");
        var context = ReadSource("CodeAlta.Tui/App/Context/SessionTabContext.cs");
        var ports = ReadSource("CodeAlta.Tui/App/SessionTabPorts.cs");
        var monitor = ReadSource("CodeAlta.Tui/CodeAltaTaskMonitor.cs");
        var reporter = ReadSource("CodeAlta.Tui/CodeAltaCrashReporter.cs");
        var logging = ReadSource("CodeAlta.Tui/CodeAltaLogging.cs");
        RequireOnce(app, """
                    _fileEditorWorkspaceCoordinator = new FileEditorWorkspaceCoordinator(
                        composition.TextFiles,
                        projectFileSearchService,
                        _shellTabService,
                        ResolvePromptRoot,
                        () => SessionInput,
                        () => _sessionWorkspaceView,
                        build => CreateComputedVisual(build),
                        DispatchToUiDeferred,
                        SyncSessionTabControl,
                        SetStatus);
            """);
        RequireOnce(app, "tabId => ObserveUiTask(() => _fileEditorWorkspaceCoordinator.CloseFileTabAsync(tabId), SR.T(\"close the file tab\"))");
        RequireOnce(app, """
                private void ObserveUiTask(Func<Task> taskFactory, string operation)
                    => _ = UiTaskDiagnostics.ObserveAsync(taskFactory, operation, SetStatus);
            """);
        RequireOnce(app, "internal void DispatchToUiDeferred(Action action) { ArgumentNullException.ThrowIfNull(action); _deferredUiActionQueue.Enqueue(action); }");
        RequireOnce(app, "    private void DrainDeferredUiActions()\n        => _deferredUiActionQueue.Drain();");
        RequireOnce(queue, """
                public int Drain(int maxActions = 256)
                {
                    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxActions);

                    var drainedActions = 0;
                    while (drainedActions < maxActions && _pendingActions.TryDequeue(out var action))
                    {
                        drainedActions++;
                        action();
                    }

                    return drainedActions;
                }
            """);
        Reject(queue, "Dispose", "Clear(", "CancellationToken", "Task");
        RequireOnce(strip, """
                        case ShellTabKind.Editor:
                            _sessionTabs.CloseFileTab(tab.TabId.Value);
                            return true;
            """);
        RequireOnce(context, """
                public void CloseFileTab(string tabId)
                {
                    ArgumentException.ThrowIfNullOrWhiteSpace(tabId);
                    _fileEditors.CloseFileTab(tabId);
                }
            """);
        RequireOnce(ports, """
                public void CloseFileTab(string tabId)
                {
                    ArgumentException.ThrowIfNullOrWhiteSpace(tabId);
                    _closeFileTab(tabId);
                }
            """);
        RequireOnce(diagnostics, """
                    var previousContext = SynchronizationContext.Current;
                    var observedContext = SafeObservedSynchronizationContext.Wrap(previousContext);
                    try
                    {
                        SynchronizationContext.SetSynchronizationContext(observedContext);
                        return taskFactory();
                    }
                    finally
                    {
                        SynchronizationContext.SetSynchronizationContext(previousContext);
                    }
            """);
        var observe = Scope(diagnostics, "    private static async Task ObserveCoreAsync(", "    private sealed class SafeObservedSynchronizationContext");
        RequireOrdered(observe, "await task;", "catch (OperationCanceledException)", "catch (Exception ex)",
            "CodeAltaApp.UiLogger.Error(ex, message);", "Terminal.Error.WriteLine($\"[CodeAlta.UI] {message} {ex}\");",
            "setStatus(SR.T(\"{0} {1}\", message, ex.Message), false, StatusTone.Error);");
        RequireOnce(diagnostics, """
                        catch (InvalidOperationException ex) when (IsDetachedDispatcherException(ex))
                        {
                            // The terminal is already shutting down. Dropping the continuation avoids surfacing a
                            // dispatcher crash after the app has exited; any useful UI update can no longer render.
                        }
            """);
        RequireOnce(diagnostics, "private const string DetachedDispatcherMessage = \"Dispatcher is not attached to a running TerminalApp.\";");
        RequireOnce(diagnostics, "=> string.Equals(exception.Message, DetachedDispatcherMessage, StringComparison.Ordinal);");
        RequireOnce(monitor, """
                    _ = task.ContinueWith(
                        static (completedTask, state) => ReportIfFaulted(completedTask, (string)state!),
                        source,
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
            """);
        RequireOnce(monitor, "CodeAltaCrashReporter.ReportFatalTaskException(source, task.Exception.Flatten());");
        RequireOnce(logging, "CodeAltaCrashReporter.Register(homeRoot);");
        RequireOnce(reporter, """
                    AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
                    TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
            """);
        RequireOnce(reporter, """
                private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
                {
                    ReportFatalTaskException("Unobserved task exception", e.Exception);
                    e.SetObserved();
                }
            """);
        RequireOnce(reporter, """
                internal static void ReportFatalTaskException(string source, Exception exception)
                {
                    ReportFatalException(source, exception);
                    TerminateProcess(source, exception);
                }
            """);
        RequireOnce(reporter, """
                private static void DefaultTerminateProcess(string source, Exception exception)
                    => Environment.FailFast($"CodeAlta fatal task failure: {source}", exception);
            """);
    }

    // All quoted production, including construction, file effects, records, scheduling and reporting,
    // is source DATA only. No application/lifetime core, logger, version, monitor or terminator runs.
    // Workspace containment cannot join discarded opening/query/accept/refresh/ranking/save/reload/
    // dialog actions, independent cancellation traversals or queued focus/update callbacks. Defaults
    // do not demonstrate throwing registrations, ordinary CTS release errors or delayed acquisition.
    // A terminal disposal result is not native watcher completion, durable rollback or process survival.
    // Normal dirty-close return need not mean the later dialog action completed. Early disposed flags
    // do not provide retry/shared completion, and no repeated/concurrent/recursive guarantee is added.
    // Noncompletion can indefinitely prevent later frontend/owned-service cleanup. Hidden construction,
    // publication/admission, controller incomplete startup joins and other lower owners/M2-M7 stay open.
    // Named source reads and existing writerless assembly logging are nonzero I/O, not isolation.
    private static string ReadSource(string relativePath)
        => File.ReadAllText(Path.Combine(SourceRoot(), relativePath))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string SourceRoot([CallerFilePath] string sourceFile = "")
        => Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(sourceFile) ?? throw new AssertFailedException("Missing fixture source directory."), ".."));

    private static string Scope(string source, string startAnchor, string endAnchor)
    {
        var start = source.IndexOf(startAnchor, StringComparison.Ordinal);
        Assert.IsTrue(start >= 0, $"Missing source anchor: {startAnchor}");
        var end = source.IndexOf(endAnchor, start + startAnchor.Length, StringComparison.Ordinal);
        Assert.IsTrue(end > start, $"Missing following source anchor: {endAnchor}");
        return source[start..end];
    }

    private static void RequireOnce(string source, string expected)
    {
        var first = source.IndexOf(expected, StringComparison.Ordinal);
        Assert.IsTrue(first >= 0, $"Missing expected wiring: {expected}");
        Assert.AreEqual(first, source.LastIndexOf(expected, StringComparison.Ordinal), $"Duplicate wiring: {expected}");
    }

    private static void RequireOrdered(string source, params string[] expected)
    {
        var previous = -1;
        foreach (var item in expected)
        {
            RequireOnce(source, item);
            var current = source.IndexOf(item, StringComparison.Ordinal);
            Assert.IsTrue(current > previous, $"Out-of-order wiring: {item}");
            previous = current;
        }
    }

    private static void Reject(string source, params string[] forbidden)
    {
        foreach (var item in forbidden)
        {
            Assert.IsFalse(source.Contains(item, StringComparison.Ordinal), $"Unexpected wiring: {item}");
        }
    }
}
