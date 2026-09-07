using System.Runtime.CompilerServices;

namespace CodeAlta.Tests;

[TestClass]
public sealed class PromptDraftPrerequisiteSourceTests
{
    [TestMethod]
    public void PromptDraftPrerequisites_SourceWiring_UsesMandatoryProductionCore()
    {
        var source = ReadSource("CodeAlta.Tui/App/SessionPromptDraftPersistenceCoordinator.cs");
        RequireOnce(source, "await JoinDraftPrerequisitesAsync(");
        RequireOnce(source, ExpectedAdapter);
        RequireOnce(source, CoreSignature);
        var persist = Scope(source, "    private async Task PersistAsync(", "    private sealed class DraftState");
        RequireOrdered(persist,
            "        try\n        {\n" + ExpectedAdapter,
            "            string? text;",
            "var result = await _save(key, text, revision).ConfigureAwait(false);",
            "        catch (Exception ex)",
            "        finally",
            "                    delay.Dispose();");
        RequireOrdered(source,
            "    private async Task PersistAsync(",
            "    private sealed class DraftState",
            CoreSignature,
            "internal sealed record PromptDraftFlushResult(");
        Reject(persist, OriginalPrerequisites, "Task.Run(", "ContinueWith(", "WaitAsync(");
        // The full inverse below forbids fallback paths, new fields/initializers, constructor
        // changes and extra code outside this adapter and the appended documented static core.
    }

    [TestMethod]
    public void PromptDraftPrerequisites_SourceCore_JoinsPreviousAfterDelayFailure()
    {
        var source = ReadSource("CodeAlta.Tui/App/SessionPromptDraftPersistenceCoordinator.cs");
        RequireOnce(source, CoreSignature);
        var core = Scope(source, CoreSignature, CoordinatorEnd);
        Assert.IsTrue(string.Equals(ExpectedCore, core, StringComparison.Ordinal),
            "The entire mandatory core, including both independent catches, must match the accepted contract.");
        RequireOrdered(core,
            "ArgumentNullException.ThrowIfNull(previous);",
            "ArgumentNullException.ThrowIfNull(waitDelay);",
            "ArgumentNullException.ThrowIfNull(isDelayCancellationRequested);",
            "return CoreAsync();",
            "async Task CoreAsync()",
            "List<Exception>? failures = null;",
            "var delayTask = waitDelay();",
            "if (delayTask is null)",
            "throw new InvalidOperationException(\"The delay operation returned a null task.\");",
            "await delayTask.ConfigureAwait(false);",
            "catch (OperationCanceledException) when (isDelayCancellationRequested())",
            "await previous.ConfigureAwait(false);",
            "System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failures[0]);",
            "throw new AggregateException(failures);");
        Reject(core, "_logger", "_store", "_save", "CancellationToken", "Cancel(", "Dispose(",
            "Task.Delay(", "Task.Run(", "ContinueWith(", "WaitAsync(", "WhenAny(", "WhenAll(",
            "IsCanceled", "IsFaulted", "IsCompleted", "Flatten(", "Distinct(", "lock (");

        var draftState = Scope(OriginalPersistence, "    private sealed class DraftState", CoordinatorEnd);
        var appended = Scope(source, draftState, CoreSignature)[draftState.Length..];
        Assert.IsTrue(appended.StartsWith("\n\n    /// <summary>\n", StringComparison.Ordinal));
        Assert.IsTrue(appended.EndsWith("\n", StringComparison.Ordinal));
        foreach (var line in appended[2..^1].Split('\n'))
            Assert.IsTrue(line.StartsWith("    ///", StringComparison.Ordinal),
                "Only XML documentation may separate the unchanged nested state and the appended core.");
        foreach (var marker in new[]
        {
            "    /// <summary>", "    /// <remarks>",
            "    /// <param name=\"previous\">", "    /// <param name=\"waitDelay\">",
            "    /// <param name=\"isDelayCancellationRequested\">",
            "    /// <exception cref=\"ArgumentNullException\">",
            "    /// <exception cref=\"InvalidOperationException\">",
            "    /// <exception cref=\"OperationCanceledException\">",
            "    /// <exception cref=\"AggregateException\">",
        }) RequireOnce(appended, marker);
        // Contract concepts, not a prescribed prose paragraph. In particular, filter exceptions
        // are NOT new retained errors, and the unchanged outer catch need not propagate the core error.
        foreach (var concept in new[]
        {
            "mandatory", "synchronous", "signature order", "inline", "null task",
            "original", "once", "predicate", "throwing", "filter", "false", "OCE",
            "previous", "terminal", "delay", "direct", "ordered", "EDI", "aggregate",
            "repeated", "flatten", "dedup", "exceptional ordering", "storage", "logging",
            "source release", "outer catch", "infrastructure", "forced", "switch",
            "noncompletion", "admission", "partial", "flush", "hidden", "setup", "shutdown",
        }) Assert.IsTrue(appended.Contains(concept, StringComparison.Ordinal), $"Missing XML qualification: {concept}");
    }

    [TestMethod]
    public void PromptDraftPersistence_Source_PreservesAcknowledgementAndRelease()
    {
        var source = ReadSource("CodeAlta.Tui/App/SessionPromptDraftPersistenceCoordinator.cs");
        var original = OriginalPersistence + "\n";
        var reconstructed = source;
        if (!string.Equals(source, original, StringComparison.Ordinal))
        {
            // There are exactly two accepted forms: the complete original, or its exact adapter
            // substitution and documented appended core. Never accept arbitrary alternative bodies.
            RequireOnce(source, ExpectedAdapter);
            RequireOnce(source, ExpectedCore);
            RequireOnce(source, CoreSignature);
            RequireOnce(source, CoordinatorEnd);
            var draftState = Scope(OriginalPersistence, "    private sealed class DraftState", CoordinatorEnd);
            RequireOnce(source, draftState);
            var start = source.IndexOf(draftState, StringComparison.Ordinal) + draftState.Length;
            var end = source.IndexOf(CoordinatorEnd, start, StringComparison.Ordinal);
            Assert.IsTrue(end > start);
            var appended = source[start..end];
            RequireOnce(appended, ExpectedCore);
            var coreStart = appended.IndexOf(CoreSignature, StringComparison.Ordinal);
            Assert.IsTrue(coreStart > 2);
            Assert.IsTrue(string.Equals(ExpectedCore, appended[coreStart..], StringComparison.Ordinal));
            var documentation = appended[..coreStart];
            Assert.IsTrue(documentation.StartsWith("\n\n    /// <summary>\n", StringComparison.Ordinal));
            Assert.IsTrue(documentation.EndsWith("\n", StringComparison.Ordinal));
            foreach (var line in documentation[2..^1].Split('\n'))
                Assert.IsTrue(line.StartsWith("    ///", StringComparison.Ordinal),
                    "The inverse may remove only appended XML and the exact accepted core.");
            reconstructed = source.Remove(start, end - start)
                .Replace(ExpectedAdapter, OriginalPrerequisites, StringComparison.Ordinal);
        }

        Assert.IsTrue(string.Equals(original, reconstructed, StringComparison.Ordinal),
            "Every original byte after CRLF-to-LF normalization, including the final newline, must reconstruct.");
        // Full-source equality protects the instance logger (not a static initializer), fields,
        // constructors, admission, partial flush, cached load/error state and both complete nested/result
        // types. The only permitted new executable text is ExpectedCore; it has no concrete initialization.
        RequireOnce(reconstructed, "private readonly Logger _logger = LogManager.GetLogger(\"CodeAlta.UI\");");
        Reject(reconstructed, "static readonly", "static SessionPromptDraftPersistenceCoordinator(");
        var persist = Scope(reconstructed, "    private async Task PersistAsync(", "    private sealed class DraftState");
        RequireOrdered(persist,
            "await previous.ConfigureAwait(false);",
            "if (delay?.IsCancellationRequested == true || version != draft.Version || !draft.IsDirty)",
            "if (draft.Revision is null)",
            "text = draft.Text;",
            "revision = draft.Revision;",
            "var result = await _save(key, text, revision).ConfigureAwait(false);",
            "if (result.IsConflict)",
            "return; // Never adopt the conflicting revision and silently retry.",
            "draft.Revision = result.Snapshot.Revision;",
            "draft.AcknowledgedVersion = version;",
            "draft.Error = null;",
            "if (draft.Version == version)",
            "draft.Text = result.Snapshot.Text;",
            "        catch (Exception ex)",
            "draft.Error = ex;",
            "_logger.Error(ex, $\"Failed to persist prompt draft '{key}'; pending text was retained.\");",
            "        finally",
            "if (ReferenceEquals(draft.Delay, delay))",
            "draft.Delay = null;",
            "delay.Dispose();");
        // This inverse is a preservation check, not a claim of unchanged exceptional behavior:
        // the future prerequisite core joins previous before the existing outer catch and finally.
    }

    [TestMethod]
    public void PromptDraftPersistence_Source_PreservesFrontendAndStorageRouting()
    {
        var ui = ReadSource("CodeAlta.Tui/App/PromptDraftUiCoordinator.cs");
        var store = ReadSource("CodeAlta.Catalog/PromptDraftStore.cs");
        var codec = ReadSource("CodeAlta.Catalog/TextFileCodec.cs");
        var revision = ReadSource("CodeAlta.Catalog/TextFileRevision.cs");
        var snapshot = ReadSource("CodeAlta.Catalog/TextFileSnapshot.cs");
        var composition = ReadSource("CodeAlta.Tui/App/CodeAltaFrontendComposition.cs");
        var app = ReadSource("CodeAlta.Tui/App/CodeAltaApp.cs");
        var shell = ReadSource("CodeAlta.Tui/App/ShellFrontendHost.cs");
        var command = ReadSource("CodeAlta.Tui/App/SessionCommandCoordinator.cs");
        var ports = ReadSource("CodeAlta.Tui/App/SessionCommandPorts.cs");
        var context = ReadSource("CodeAlta.Tui/App/Context/ShellSessionCommandContext.cs");
        var legacy = ReadSource("CodeAlta.Tui/App/LegacyPromptSessionPort.cs");
        var factory = ReadSource("CodeAlta.Tui/App/SessionStateFactory.cs");
        var sessions = ReadSource("CodeAlta.Tui/App/ShellSessionStateCoordinator.cs");
        var events = ReadSource("CodeAlta.Tui/App/Events/FrontendEventPublisher.cs");
        var frontendGate = ReadSource("CodeAlta.Tests/CodeAltaFrontendCleanupSourceTests.cs");
        var architecture = ReadSource("CodeAlta.Tests/ArchitectureGuardrailTests.cs");

        RequireOnce(composition, "new SessionPromptDraftService(frontend.LoadPromptDraft, frontend.DeletePromptDraft)");
        RequireOnce(composition, """
                var textFiles = sessionCatalog.TextFiles;
                var promptDraftUiCoordinator = new PromptDraftUiCoordinator(
                    new PromptDraftCoordinator(),
                    new PromptDraftStore(catalogOptions, textFiles),
                    () => sessionStateCoordinator.Selection,
                    frontendEvents,
                    frontend.UpdatePromptImageAttachmentsUi);
        """);
        Reject(composition, "new TextFileCodec(");
        RequireOnce(composition, "TextFiles = textFiles,");
        RequireOnce(ui, "_promptDraftPersistence = new SessionPromptDraftPersistenceCoordinator(promptDraftStore, TimeSpan.FromMilliseconds(500));");
        RequireOnce(ui, """
                public ValueTask DisposeAsync()
                    => _promptDraftPersistence.DisposeAsync();

                public Task<PromptDraftFlushResult> FlushPromptDraftsAsync()
                    => _promptDraftPersistence.FlushAsync();

                private void FlushPromptDrafts()
                    => FlushPromptDraftsAsync().GetAwaiter().GetResult().ThrowIfFailed();
            """);
        Reject(ui, "ConfigureAwait(false)");
        RequireOrdered(Scope(ui, "    public void SyncPromptText(", "    public void ClearPromptText()"),
            "FlushPromptDrafts();", "_activePromptSessionId =", "SyncPromptTextFromSession(activeState);");
        RequireOnce(ui, """
                private void DeletePersistedPromptText(PromptDraftSessionState state)
                {
                    var key = state.BoundSession is null ? state.DraftScopeKey : state.PromptSessionId;
                    var text = state.ViewModel.PromptText;
                    try
                    {
                        // Do not mutate the composer or its images until deletion is acknowledged.
                        _promptDraftPersistence.DeletePromptDraft(key);
                    }
                    catch
                    {
                        // A rejected pre-admission clear must retain the original unsent intent, not a
                        // pending tombstone that could erase it on a later retry or shutdown flush.
                        _promptDraftPersistence.ObservePromptDraft(key, text);
                        throw;
                    }
                }
            """);
        RequireOnce(ui, """
                public void ClearPrompt()
                {
                    var state = GetActivePromptState();
                    DeletePersistedPromptText(state);
                    ClearPromptText(state);
                    ClearPromptImages(state);
                }
            """);
        RequireOnce(ui, """
                private void ClearPromptText(PromptDraftSessionState state)
                {
                    var change = _promptDrafts.RememberPrompt(state.BoundSession, string.Empty, state.DraftScopeKey);
                    // The deletion was already acknowledged. Update the UI without scheduling a second
                    // delete that could fail after send/queue admission has succeeded.
                    state.SyncingPromptText = true;
                    try
                    {
                        state.ViewModel.PromptText = string.Empty;
                    }
                    finally
                    {
                        state.SyncingPromptText = false;
                    }

                    if (change.EditedStateChanged)
                    {
                        PublishSessionPromptEditedStateChanged(state.BoundSession is null ? state.DraftScopeKey : state.PromptSessionId);
                    }
                }
            """);

        // Entire Shell source protects all seven independent stages AND the outer owned-services
        // routing/error policy. App's exact adapter still discards only the view-state result.
        const string reminderImport = "using CodeAlta.LiveTool;\n";
        const string reminderField = "    private AltaReminderService? _reminders;\n";
        const string renamedTraversal =
            "    private async ValueTask DisposeFrontendAndOwnedServicesAsync()";

        RequireOnce(shell, reminderImport);
        RequireOnce(shell, reminderField);
        RequireOnce(shell, ExpectedReminderOwnership + "\n\n");
        RequireOnce(shell, ExpectedReminderAdapterAndCore + "\n\n");
        RequireOnce(shell, renamedTraversal);

        var originalShell = shell
            .Replace(reminderImport, "", StringComparison.Ordinal)
            .Replace(reminderField, "", StringComparison.Ordinal)
            .Replace(ExpectedReminderOwnership + "\n\n", "", StringComparison.Ordinal)
            .Replace(ExpectedReminderAdapterAndCore + "\n\n", "", StringComparison.Ordinal)
            .Replace(renamedTraversal,
                "    public async ValueTask DisposeAsync()", StringComparison.Ordinal);

        Assert.IsTrue(string.Equals(
            OriginalShell + "\n", originalShell, StringComparison.Ordinal));
        Assert.AreEqual(
            6_324,
            System.Text.Encoding.UTF8.GetByteCount(
                originalShell.Replace("\n", "\r\n", StringComparison.Ordinal)));
        RequireOnce(app, ExpectedFrontendAdapter);
        RequireOnce(app, "    public async ValueTask DisposeAsync()\n        => await _frontendHost.DisposeAsync();");
        RequireOnce(app, "    IAsyncDisposable? IShellFrontendHostLifecycle.OwnedServices => _ownedServices;");
        Assert.IsTrue(System.Text.Encoding.UTF8.GetByteCount(app.Replace("\n", "\r\n", StringComparison.Ordinal)) < 47_064);
        RequireOnce(app, "    internal string? LoadPromptDraft(string sessionId)\n        => _promptDraftUiCoordinator!.LoadPromptDraft(sessionId);");
        RequireOnce(app, "    internal void DeletePromptDraft(string sessionId)\n        => _promptDraftUiCoordinator!.DeletePersistedPromptDraft(sessionId);");
        RequireOnce(app, "    internal void ClearDraftPromptText()\n        => _promptDraftUiCoordinator!.ClearDraftPromptText();");
        RequireOnce(app, "    internal void ClearPromptText()\n        => _promptDraftUiCoordinator!.ClearPrompt();");

        RequireOnce(store, "_root = options.PromptDraftsRoot;\n        _files = files;");
        RequireOnce(store, "return Path.Combine(_root, $\"saved_prompt_{new string(characters)}.md\");");
        RequireOnce(store, "public Task<PromptDraftSaveResult> SaveAsync(string scopeKey, string? text, TextFileRevision expectedRevision)\n        => SaveAsync(scopeKey, text, expectedRevision, CancellationToken.None);");
        RequireOnce(store, """
                try
                {
                    var file = await _files.LoadAsync(GetPath(scopeKey), cancellationToken).ConfigureAwait(false);
                    return new PromptDraftSnapshot(file.Text, file.Revision);
                }
                catch (FileNotFoundException)
                {
                    return new PromptDraftSnapshot(null, TextFileRevision.Missing);
                }
                catch (DirectoryNotFoundException)
                {
                    return new PromptDraftSnapshot(null, TextFileRevision.Missing);
                }
        """);
        RequireOnce(store, """
            public async Task<PromptDraftSaveResult> SaveAsync(string scopeKey, string? text, TextFileRevision expectedRevision, CancellationToken cancellationToken)
            {
                ArgumentNullException.ThrowIfNull(expectedRevision);
                var path = GetPath(scopeKey);
                if (string.IsNullOrWhiteSpace(text))
                {
                    var deletion = await _files.DeleteAsync(path, expectedRevision, cancellationToken).ConfigureAwait(false);
                    return new PromptDraftSaveResult(
                        deletion.IsConflict ? null : new PromptDraftSnapshot(null, deletion.CurrentRevision),
                        deletion.CurrentRevision);
                }

                var saved = await _files.SaveAsync(new TextFileSaveRequest(path, text, Encoding.UTF8, false, expectedRevision), cancellationToken).ConfigureAwait(false);
                return new PromptDraftSaveResult(
                    saved.IsConflict ? null : new PromptDraftSnapshot(saved.Snapshot.Text, saved.CurrentRevision),
                    saved.CurrentRevision);
            }
        """);
        RequireOnce(store, "public sealed record PromptDraftSnapshot(string? Text, TextFileRevision Revision);");
        RequireOnce(store, """
            public sealed record PromptDraftSaveResult(PromptDraftSnapshot? Snapshot, TextFileRevision CurrentRevision)
            {
                /// <summary>Gets whether the expected revision did not match and nothing was committed.</summary>
                [MemberNotNullWhen(false, nameof(Snapshot))]
                public bool IsConflict => Snapshot is null;
            }
            """);
        RequireOnce(revision, "public static TextFileRevision Missing => new((string?)null);");
        RequireOnce(revision, "=> new(Convert.ToHexString(SHA256.HashData(bytes)));");
        RequireOnce(snapshot, "public sealed record TextFileDeleteResult(bool IsConflict, TextFileRevision CurrentRevision);");
        RequireOnce(snapshot, "public bool IsConflict => Snapshot is null;");

        RequireOnce(codec, "private readonly SemaphoreSlim _saveGate = new(1, 1);");
        RequireOnce(codec, "if (document.IsReadOnly) throw new UnauthorizedAccessException(\"The skill document is read-only.\");");
        var save = Scope(codec,
            "    public async Task<TextFileSaveResult> SaveAsync(TextFileSaveRequest request, CancellationToken cancellationToken)",
            "    /// <summary>Conditionally deletes a file entry under the same instance gate used for saves.</summary>");
        RequireOrdered(save,
            "await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);",
            "var current = await ReadRevisionAsync(path, cancellationToken).ConfigureAwait(false);",
            "await using (var stream = CreateStagingFile(candidate, current.Exists ? path : null))",
            "await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);",
            "await stream.FlushAsync(cancellationToken).ConfigureAwait(false);",
            "            current = await ReadRevisionAsync(path, cancellationToken).ConfigureAwait(false);",
            "if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)",
            "            cancellationToken.ThrowIfCancellationRequested();",
            "File.Replace(stagingPath, path, destinationBackupFileName: null);",
            "File.Move(stagingPath, path);",
            "            stagingPath = null;",
            "var snapshot = new TextFileSnapshot(request.Text, encoding, request.HasByteOrderMark, File.GetLastWriteTimeUtc(path), revision);",
            "return new TextFileSaveResult(snapshot, revision);",
            "File.Delete(stagingPath);",
            "_saveGate.Release();");
        // Commit can precede timestamp/result/cleanup failure; these are not rollback assertions.
        RequireOnce(codec, """
                public async Task<TextFileDeleteResult> DeleteAsync(string fullPath, TextFileRevision expectedRevision, CancellationToken cancellationToken)
                {
                    ArgumentNullException.ThrowIfNull(expectedRevision);
                    ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
                    var path = Path.GetFullPath(fullPath);
                    await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        var current = await ReadRevisionAsync(path, cancellationToken).ConfigureAwait(false);
                        if (current != expectedRevision)
                        {
                            return new TextFileDeleteResult(true, current);
                        }

                        cancellationToken.ThrowIfCancellationRequested();
                        if (current.Exists || new FileInfo(path).LinkTarget is not null)
                        {
                            File.Delete(path);
                        }

                        return new TextFileDeleteResult(false, TextFileRevision.Missing);
                    }
                    finally
                    {
                        _saveGate.Release();
                    }
                }
            """);
        Reject(store, "IDisposable", "IAsyncDisposable");
        Reject(codec, "IDisposable", "IAsyncDisposable");

        RequireOnce(command, """
                internal static Task ClearInputAndAdmitPromptAsync(
                    Action clearInput,
                    bool enqueue,
                    Action enqueuePrompt,
                    Func<Task> dispatchPrompt)
                {
                    ArgumentNullException.ThrowIfNull(clearInput);
                    ArgumentNullException.ThrowIfNull(enqueuePrompt);
                    ArgumentNullException.ThrowIfNull(dispatchPrompt);

                    // Clear is revision-conditional and can fail. Complete it before either admission
                    // path so retrying a storage failure cannot enqueue an already accepted prompt.
                    clearInput();
                    if (enqueue)
                    {
                        enqueuePrompt();
                        return Task.CompletedTask;
                    }

                    return dispatchPrompt();
                }
            """);
        var send = Scope(command, "    public async Task SendPromptAsync(", "    internal static Task ClearInputAndAdmitPromptAsync(");
        RequireOrdered(send, "await _sessionSelection.EnsureSessionHistoryLoadedAsync(session, cancellationToken);",
            "await ClearInputAndAdmitPromptAsync(",
            "hadExistingSession ? _commandContext.ClearSessionInput : _commandContext.ClearDraftInput,",
            "() => _queueCoordinator.EnqueuePrompt(tab, prompt),",
            "() => _promptDispatchCoordinator.DispatchPromptAsync(session, tab, prompt, steer, cancellationToken));");
        RequireOnce(ports, "    public void ClearDraftInput()\n        => _uiDispatcher.Invoke(_clearDraftInput);");
        RequireOnce(context, "    public void ClearDraftInput()\n        => _uiPort.ClearDraftInput();");
        RequireOnce(context, "    public void ClearSessionInput()\n        => _promptSessionPort.ClearPrompt(GetCurrentPromptSessionId());");
        RequireOnce(legacy, """
                public void ClearPrompt(PromptSessionId promptSessionId)
                {
                    ValidatePromptSessionId(promptSessionId);
                    _uiDispatcher.Invoke(_clearPrompt);
                }
            """);
        RequireOnce(composition, """
                var promptSessionPort = new LegacyPromptSessionPort(
                    uiDispatcher,
                    frontend.IsPromptTextEmpty,
                    frontend.ClearPromptText,
        """);
        RequireOnce(composition, "                    frontend.ClearDraftPromptText,");
        RequireOnce(factory, "state.Session.PromptDraftText = _promptDrafts.LoadPromptDraft(session.SessionId) ?? string.Empty;");
        RequireOnce(factory, """
                public void DeletePromptDraft(string sessionId)
                {
                    ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
                    _deletePromptDraft(sessionId);
                }
            """);

        var close = Scope(sessions, "    public async Task<TabCloseResult> CloseSessionTabAsync(", "    private string? GetNextOpenSessionTabId(");
        RequireOrdered(close,
            "_ = _historyLoader.EnsureSessionHistoryLoadedAsync(nextSession, CancellationToken.None);",
            "_openSessionStateStore.RemoveSessionTab(sessionId);",
            "_tabLifecycle.RemoveSessionTabPage(sessionId, ShellTabCloseReason.UserDetached);",
            "await PersistViewStateAsync();",
            "SyncStateStore(selectionChanged: true);");
        Reject(close, "_promptDrafts", "Flush", "Abort", "Dispose");
        foreach (var removal in new[]
        {
            Scope(sessions, "    public void RemoveDeletedSessions(", "    public void RemoveDeletedProject(ProjectDescriptor"),
            Scope(sessions, "    public void RemoveDeletedProject(string projectId", "    public OpenSessionState EnsureSessionTab("),
        }) RequireOrdered(removal, "_openSessionStateStore.RemoveSessionTab(sessionId);",
            "_promptDrafts.DeletePromptDraft(sessionId);", "_ = PersistViewStateAsync();");
        RequireOnce(Scope(sessions, "    public async Task RemoveDeletedSessionArtifactsAsync(", "    public void RemoveDeletedSession("),
            "_promptDrafts.DeletePromptDraft(sessionId);");
        RequireOnce(app, """
                internal void RemoveSessionTabPage(string sessionId, ShellTabCloseReason reason)
                {
                    _ = _shellTabService.CloseTabAsync(new ShellTabId(sessionId), reason);
                    _sessionWorkspaceView?.RemoveTabPage(sessionId);
                }
            """);
        RequireOnce(events, """
                public void Publish(ShellFrontendEvent frontendEvent)
                {
                    ArgumentNullException.ThrowIfNull(frontendEvent);

                    if (_uiDispatcher.CheckAccess())
                    {
                        PublishCore(frontendEvent);
                        return;
                    }

                    _uiDispatcher.Post(() => PublishCore(frontendEvent));
                }
            """);
        RequireOnce(events, """
                private void PublishCore(ShellFrontendEvent frontendEvent)
                {
                    foreach (var subscriber in _subscribers.ToArray())
                    {
                        subscriber(frontendEvent);
                    }
                }
            """);

        // These are named source reads of frozen gates, never calls into those fixtures. Parent
        // separately freezes their full raw hashes; no allowance or assertion evolution is authorized.
        RequireOnce(frontendGate, "// This is the complete ten-line adapter, not additional async wrappers or result policy.");
        RequireOnce(frontendGate, "\"async ()\", \"ThrowIfFailed\", \"PersistenceResult\", \"IsConflict\", \"Succeeded\", \"reportStatus: true\",");
        RequireOnce(architecture, "public void SessionDraftPersistence_UsesMachineSavedPromptsAndDeleteHooks()");
        RequireOnce(architecture, "Assert.IsFalse(persistenceSource.Contains(\"File.\", StringComparison.Ordinal));");
        RequireOnce(architecture, "public void PromptDraftAdmission_ClearsBeforeQueueOrDispatchAcceptance()");
        RequireOnce(architecture, "public void UiStatePersistence_SaveSeamsReportFailureWithoutShortCircuitingCleanup()");
        RequireOnce(architecture, "not \"App/SessionPromptDraftPersistenceCoordinator.cs\" and");
        RequireOnce(architecture, "const int BaseFacadeSizeBudgetBytes = 47_000;");
        RequireOnce(architecture, "const int NamespaceIdentityAllowanceBytes = 64;");
        RequireOnce(architecture, "appSize < appSizeBudget,");
        RequireOnce(architecture, "\"App/CodeAltaShellController.cs:73:_initializationTask = Task.Run(\",");
        RequireOnce(architecture, "\"App/CodeAltaShellController.cs:448:var startupProviderLoadTask = Task.Run(\",");
        RequireOnce(architecture, "\"App/RuntimeEventPump.cs:34:_pumpTask = Task.Run(\",");
        RequireOnce(architecture, "\"Presentation/Editing/ProjectFileOpenDialogController.cs:217:_ = AcceptSelectedAsync(selected);\",");
    }

    private const string CoreSignature = "    internal static Task JoinDraftPrerequisitesAsync(";
    private const string CoordinatorEnd = "\n}\n\ninternal sealed record PromptDraftFlushResult";

    private const string OriginalPrerequisites = """
                    if (delay is not null)
                    {
                        try
                        {
                            await Task.Delay(_saveDelay, delay.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (delay.IsCancellationRequested)
                        {
                        }
                    }

                    await previous.ConfigureAwait(false);
        """;

    private const string ExpectedAdapter = """
                    await JoinDraftPrerequisitesAsync(
                        previous,
                        delay is null ? static () => Task.CompletedTask : () => Task.Delay(_saveDelay, delay.Token),
                        delay is null ? static () => false : () => delay.IsCancellationRequested).ConfigureAwait(false);
        """;

    private const string ExpectedCore = """
            internal static Task JoinDraftPrerequisitesAsync(
                Task previous,
                Func<Task> waitDelay,
                Func<bool> isDelayCancellationRequested)
            {
                ArgumentNullException.ThrowIfNull(previous);
                ArgumentNullException.ThrowIfNull(waitDelay);
                ArgumentNullException.ThrowIfNull(isDelayCancellationRequested);
                return CoreAsync();

                async Task CoreAsync()
                {
                    List<Exception>? failures = null;
                    try
                    {
                        var delayTask = waitDelay();
                        if (delayTask is null)
                        {
                            throw new InvalidOperationException("The delay operation returned a null task.");
                        }

                        await delayTask.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (isDelayCancellationRequested())
                    {
                    }
                    catch (Exception ex)
                    {
                        (failures ??= []).Add(ex);
                    }

                    try
                    {
                        await previous.ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        (failures ??= []).Add(ex);
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
        """;

    private const string ExpectedFrontendAdapter = """
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
        """;

    // Complete 275-line LF baseline, including both type-closing braces and every existing comment.
    // The raw literal excludes only its final newline, restored explicitly by the preservation test.
    private const string OriginalPersistence = """
        using CodeAlta.Catalog;
        using XenoAtom.Logging;

        namespace CodeAlta.Tui.App;

        // Infrastructure-only state: no bindings or UI callbacks run on the owned work chain.
        internal sealed class SessionPromptDraftPersistenceCoordinator : IAsyncDisposable
        {
            private readonly Logger _logger = LogManager.GetLogger("CodeAlta.UI");
            private readonly PromptDraftStore _store;
            private readonly Func<string, string?, TextFileRevision, Task<PromptDraftSaveResult>> _save;
            private readonly TimeSpan _saveDelay;
            private readonly object _syncRoot = new();
            private readonly Dictionary<string, DraftState> _drafts = new(StringComparer.OrdinalIgnoreCase);
            private Task _work = Task.CompletedTask;
            private bool _disposed;

            public SessionPromptDraftPersistenceCoordinator(CatalogOptions catalogOptions, TimeSpan? saveDelay = null)
                : this(new PromptDraftStore(catalogOptions), saveDelay ?? TimeSpan.FromMilliseconds(500))
            {
            }

            internal SessionPromptDraftPersistenceCoordinator(
                PromptDraftStore store,
                TimeSpan saveDelay,
                Func<string, string?, TextFileRevision, Task<PromptDraftSaveResult>>? save = null)
            {
                ArgumentNullException.ThrowIfNull(store);
                if (saveDelay < TimeSpan.Zero)
                {
                    throw new ArgumentOutOfRangeException(nameof(saveDelay));
                }

                _store = store;
                _save = save ?? store.SaveAsync;
                _saveDelay = saveDelay;
            }

            public string? LoadPromptDraft(string scopeKey)
            {
                lock (_syncRoot)
                {
                    var draft = GetDraft(scopeKey);
                    if (draft.Revision is null && !draft.IsDirty)
                    {
                        throw new IOException("Failed to load prompt draft.", draft.Error);
                    }

                    return draft.Text;
                }
            }

            public bool HasPromptDraft(string scopeKey)
            {
                lock (_syncRoot)
                {
                    var draft = GetDraft(scopeKey);
                    if (draft.Revision is null && !draft.IsDirty)
                    {
                        throw new IOException("Failed to load prompt draft.", draft.Error);
                    }

                    return draft.IsDirty ? !string.IsNullOrWhiteSpace(draft.Text) : draft.Revision?.Exists == true;
                }
            }

            public void ObservePromptDraft(string scopeKey, string? promptText)
            {
                lock (_syncRoot)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    var draft = GetDraft(scopeKey);
                    draft.Text = promptText;
                    draft.Version++;
                    draft.Delay?.Cancel();
                    var delay = new CancellationTokenSource();
                    draft.Delay = delay;
                    _work = PersistAsync(_work, scopeKey, draft, draft.Version, delay);
                }
            }

            // Existing synchronous deletion callers get an acknowledgement, not cancellation-as-success.
            public void DeletePromptDraft(string scopeKey)
            {
                ObservePromptDraft(scopeKey, null);
                FlushAsync().GetAwaiter().GetResult().ThrowIfFailed();
            }

            public Task<PromptDraftFlushResult> FlushAsync()
            {
                lock (_syncRoot)
                {
                    return QueueFlush();
                }
            }

            public async ValueTask DisposeAsync()
            {
                Task<PromptDraftFlushResult> flush;
                lock (_syncRoot)
                {
                    _disposed = true; // Stop admission before capturing/joining all owned work, including deletes.
                    flush = QueueFlush();
                }

                (await flush.ConfigureAwait(false)).ThrowIfFailed();
            }

            private DraftState GetDraft(string scopeKey)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(scopeKey);
                if (_drafts.TryGetValue(scopeKey, out var draft))
                {
                    return draft;
                }

                draft = new DraftState();
                try
                {
                    var loaded = _store.Load(scopeKey);
                    draft.Text = loaded.Text;
                    draft.Revision = loaded.Revision;
                }
                catch (Exception ex)
                {
                    draft.Error = ex; // Without a baseline, never guess that the file was missing.
                }

                _drafts.Add(scopeKey, draft);
                return draft;
            }

            // Called under _syncRoot. A flush covers the edits admitted at this point, not future edits.
            private Task<PromptDraftFlushResult> QueueFlush()
            {
                var targets = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                foreach (var (key, draft) in _drafts)
                {
                    if (!draft.IsDirty)
                    {
                        continue;
                    }

                    targets.Add(key, draft.Version);
                    draft.Delay?.Cancel();
                    _work = PersistAsync(_work, key, draft, draft.Version, delay: null);
                }

                return CompleteFlushAsync(_work, targets);
            }

            private async Task<PromptDraftFlushResult> CompleteFlushAsync(Task work, Dictionary<string, long> targets)
            {
                await work.ConfigureAwait(false);
                lock (_syncRoot)
                {
                    var failures = new Dictionary<string, Exception>(StringComparer.OrdinalIgnoreCase);
                    foreach (var (key, version) in targets)
                    {
                        var draft = _drafts[key];
                        if (draft.AcknowledgedVersion < version)
                        {
                            failures.Add(key, draft.Error ?? new IOException("A newer prompt edit is still pending."));
                        }
                    }

                    return new PromptDraftFlushResult(failures);
                }
            }

            private async Task PersistAsync(Task previous, string key, DraftState draft, long version, CancellationTokenSource? delay)
            {
                try
                {
                    if (delay is not null)
                    {
                        try
                        {
                            await Task.Delay(_saveDelay, delay.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (delay.IsCancellationRequested)
                        {
                        }
                    }

                    await previous.ConfigureAwait(false);
                    string? text;
                    TextFileRevision revision;
                    lock (_syncRoot)
                    {
                        if (delay?.IsCancellationRequested == true || version != draft.Version || !draft.IsDirty)
                        {
                            return;
                        }

                        if (draft.Revision is null)
                        {
                            return;
                        }

                        text = draft.Text;
                        revision = draft.Revision;
                    }

                    // Once a write starts it is joined, never canceled to pretend it did not commit.
                    // The following operation uses only this operation's acknowledged revision.
                    var result = await _save(key, text, revision).ConfigureAwait(false);
                    lock (_syncRoot)
                    {
                        if (result.IsConflict)
                        {
                            draft.Error = new IOException($"Prompt draft '{key}' changed on disk; pending text was retained.");
                            _logger.Error(draft.Error, "Prompt draft persistence conflict.");
                            return; // Never adopt the conflicting revision and silently retry.
                        }

                        draft.Revision = result.Snapshot.Revision;
                        draft.AcknowledgedVersion = version;
                        draft.Error = null;
                        if (draft.Version == version)
                        {
                            draft.Text = result.Snapshot.Text;
                        }
                    }
                }
                catch (Exception ex)
                {
                    lock (_syncRoot)
                    {
                        draft.Error = ex;
                    }

                    _logger.Error(ex, $"Failed to persist prompt draft '{key}'; pending text was retained.");
                }
                finally
                {
                    if (delay is not null)
                    {
                        lock (_syncRoot)
                        {
                            if (ReferenceEquals(draft.Delay, delay))
                            {
                                draft.Delay = null;
                            }

                            delay.Dispose();
                        }
                    }
                }
            }

            private sealed class DraftState
            {
                public string? Text { get; set; }
                public TextFileRevision? Revision { get; set; }
                public long Version { get; set; }
                public long AcknowledgedVersion { get; set; }
                public bool IsDirty => Version != AcknowledgedVersion;
                public Exception? Error { get; set; }
                public CancellationTokenSource? Delay { get; set; }
            }
        }

        internal sealed record PromptDraftFlushResult(IReadOnlyDictionary<string, Exception> Failures)
        {
            public bool Succeeded => Failures.Count == 0;

            public void ThrowIfFailed()
            {
                if (!Succeeded)
                {
                    throw new IOException("Prompt drafts could not be flushed; pending text was retained.", new AggregateException(Failures.Values));
                }
            }
        }
        """;

    private const string ExpectedReminderOwnership = """
            internal void OwnReminders(AltaReminderService reminders)
            {
                ArgumentNullException.ThrowIfNull(reminders);
                if (_reminders is not null)
                {
                    throw new InvalidOperationException("Reminder ownership already transferred.");
                }

                _reminders = reminders;
            }
        """;

    private const string ExpectedReminderAdapterAndCore = """
            public async ValueTask DisposeAsync()
                => await DisposeRemindersThenFrontendAsync(
                    () => _reminders?.DisposeAsync() ?? ValueTask.CompletedTask,
                    DisposeFrontendAndOwnedServicesAsync);

            internal static Task DisposeRemindersThenFrontendAsync(
                Func<ValueTask> disposeReminders,
                Func<ValueTask> disposeExisting)
            {
                ArgumentNullException.ThrowIfNull(disposeReminders);
                ArgumentNullException.ThrowIfNull(disposeExisting);
                return CoreAsync();

                async Task CoreAsync()
                {
                    Exception? reminderFailure = null;
                    try
                    {
                        await disposeReminders();
                    }
                    catch (Exception ex)
                    {
                        reminderFailure = ex;
                    }

                    try
                    {
                        await disposeExisting();
                    }
                    catch (Exception ex) when (reminderFailure is not null)
                    {
                        throw new AggregateException(reminderFailure, ex);
                    }

                    if (reminderFailure is not null)
                    {
                        ExceptionDispatchInfo.Throw(reminderFailure);
                    }
                }
            }
        """;

    private const string OriginalShell = """
        using System.Runtime.ExceptionServices;
        using XenoAtom.Terminal;
        using XenoAtom.Terminal.UI;

        namespace CodeAlta.Tui.App;

        internal interface IShellFrontendHostLifecycle
        {
            void PrepareForRun();

            Visual GetRoot();

            TerminalLoopResult Tick(CancellationToken cancellationToken);

            ValueTask DisposeFrontendAsync();

            IAsyncDisposable? OwnedServices { get; }
        }

        internal sealed class ShellFrontendHost : IAsyncDisposable
        {
            private readonly IShellFrontendHostLifecycle _lifecycle;

            public ShellFrontendHost(IShellFrontendHostLifecycle lifecycle)
            {
                ArgumentNullException.ThrowIfNull(lifecycle);
                _lifecycle = lifecycle;
            }

            public async Task RunAsync(CancellationToken cancellationToken)
            {
                _lifecycle.PrepareForRun();
                var root = _lifecycle.GetRoot();
                await Terminal.RunAsync(
                    root,
                    () => Tick(cancellationToken),
                    cancellationToken);
            }

            public TerminalLoopResult Tick(CancellationToken cancellationToken)
                => _lifecycle.Tick(cancellationToken);

            public async ValueTask DisposeAsync()
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

            /// <summary>
            /// Attempts the seven existing frontend cleanup stages of a successfully returned app in order.
            /// </summary>
            /// <remarks>
            /// Validate every mandatory operation synchronously before starting the local core inline.
            /// Preserve projection, reminder UI, view-state persistence, editors, event pump, controller and
            /// prompt-draft order. The persistence adapter discards its result with status reporting disabled;
            /// this traversal handles escaped failures, not returned persistence errors or conflicts.
            /// Every stage is attempted after earlier terminal failures, retaining direct exception identities.
            /// Plain awaits preserve frontend cleanup context. This operation stores no resources and adds no
            /// caching, retries or repeated/concurrent disposal guarantee; the existing owner routing remains.
            /// There is no overall timeout or guarantee that noncooperative callbacks or lower owners terminate.
            /// A pending stage can prevent later frontend, owned-service and updater cleanup from being reached.
            /// Hidden construction acquisitions, partial surfaces and publication/preparation failures are not
            /// rolled back here; this is not complete startup, UI, plugin, network or lower-owner qualification.
            /// </remarks>
            /// <exception cref="ArgumentNullException">A mandatory operation is null.</exception>
            /// <exception cref="Exception">A lone original failure is rethrown through EDI.</exception>
            /// <exception cref="OperationCanceledException">The sole retained failure is cancellation.</exception>
            /// <exception cref="AggregateException">Multiple direct failures are reported in order without flattening.</exception>
            internal static Task DisposeFrontendResourcesAsync(
                Action disposeProjection,
                Action disposeReminderUi,
                Func<Task> persistViewState,
                Func<ValueTask> disposeFileEditors,
                Func<ValueTask> disposeRuntimeEventPump,
                Func<ValueTask> disposeShellController,
                Func<ValueTask> disposePromptDrafts)
            {
                ArgumentNullException.ThrowIfNull(disposeProjection);
                ArgumentNullException.ThrowIfNull(disposeReminderUi);
                ArgumentNullException.ThrowIfNull(persistViewState);
                ArgumentNullException.ThrowIfNull(disposeFileEditors);
                ArgumentNullException.ThrowIfNull(disposeRuntimeEventPump);
                ArgumentNullException.ThrowIfNull(disposeShellController);
                ArgumentNullException.ThrowIfNull(disposePromptDrafts);
                return CoreAsync();

                async Task CoreAsync()
                {
                    List<Exception>? failures = null;
                    try
                    {
                        disposeProjection();
                    }
                    catch (Exception ex)
                    {
                        (failures ??= []).Add(ex);
                    }

                    try
                    {
                        disposeReminderUi();
                    }
                    catch (Exception ex)
                    {
                        (failures ??= []).Add(ex);
                    }

                    try
                    {
                        await persistViewState();
                    }
                    catch (Exception ex)
                    {
                        (failures ??= []).Add(ex);
                    }

                    try
                    {
                        await disposeFileEditors();
                    }
                    catch (Exception ex)
                    {
                        (failures ??= []).Add(ex);
                    }

                    try
                    {
                        await disposeRuntimeEventPump();
                    }
                    catch (Exception ex)
                    {
                        (failures ??= []).Add(ex);
                    }

                    try
                    {
                        await disposeShellController();
                    }
                    catch (Exception ex)
                    {
                        (failures ??= []).Add(ex);
                    }

                    try
                    {
                        await disposePromptDrafts();
                    }
                    catch (Exception ex)
                    {
                        (failures ??= []).Add(ex);
                    }

                    if (failures is { Count: 1 })
                    {
                        ExceptionDispatchInfo.Throw(failures[0]);
                    }

                    if (failures is { Count: > 1 })
                    {
                        throw new AggregateException(failures);
                    }
                }
            }
        }
        """;

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
