using System.Security.Cryptography;
using System.Text;

namespace CodeAlta.Tests;

/// <summary>Named checkout reads only. No fixture construction, runtime, provider, plugin or Git execution.</summary>
[TestClass]
public sealed class RuntimeFileSearchInvalidationSourceTests
{
    [TestMethod]
    public void CurrentSources_WireSameBorrowedCacheAndCaptureAppendBeforeAdmission()
    {
        var host = Read("CodeAlta.Orchestration/Hosting/CodeAltaHost.cs");
        Once(host, "var projectFileSnapshotCache = new ProjectFileSnapshotCache();");
        Once(host, "FileSearchCache = projectFileSnapshotCache,");
        Once(host, "var projectFileSearchService = new ProjectFileSearchService(\n                projectFileSnapshotCache,");
        var runtime = Read("CodeAlta.Orchestration/Runtime/SessionRuntimeService.cs");
        Once(runtime, "var effectWorkingDirectory = session.WorkingDirectory;\n        var sessionId = session.SessionId;\n        var projectId = session.ProjectRef;\n        await AdmitAsync(() => AppendSessionEventOwnedBodyAsync(session, @event, sessionId, projectId, effectWorkingDirectory, cancellationToken)");
        Once(runtime, "await _agentSessionCatalog.InvalidateAsync(sessionId, cancellationToken).ConfigureAwait(false);\n        _events.TryPublish(new SessionAgentEvent(sessionId, @event));\n        mark(CapturePluginEvent(@event, sessionId, projectId, effectWorkingDirectory));");
        Once(runtime, "() => publication.Published is null ? Task.CompletedTask : InvalidateFileSearchCacheAsync(@event, effectWorkingDirectory).AsTask()");
        var effect = Read("CodeAlta.Orchestration/Runtime/SessionRuntimeService.FileSearch.cs");
        Once(effect, "internal ProjectFileSnapshotCache? FileSearchCache { get; init; }");
        Once(effect, "AgentActivityEvent { Kind: AgentActivityKind.FileChange }");
        Once(effect, "AgentSessionUpdateEvent { Kind: AgentSessionUpdateKind.DiffUpdated }");
        Once(effect, "await cache.MarkDirtyAsync(workingDirectory, ProjectFileInvalidationReason.FileSystemWrite).ConfigureAwait(false);");
        foreach (var forbidden in new[] { "Task.Run", "IProjectFileSearchService", "Action<", "CancellationToken", "Subscribe", "lock (", "Dispose", "Phase:" })
            Assert.IsFalse(effect.Contains(forbidden, StringComparison.Ordinal), forbidden);
    }

    [TestMethod]
    [Ignore("Code-shape preservation check; disabled in favor of behavioral coverage.")]
    public void CurrentSources_UsePublicationReferenceOutsideActorBeforeNotificationAndQueueTails()
    {
        var runtime = Read("CodeAlta.Orchestration/Runtime/SessionRuntimeService.cs");
        Once(runtime, "await publication.CompleteAsync(mark => actor.QueryAsync(_ =>");
        Once(runtime, "mark(CapturePluginEvent(sanitized, sessionId, projectId, workingDirectory));");
        Once(runtime, "}).AsTask(), projectionUse.Dispose, ObserveLivePluginEventAsync, () =>\n            {\n                var effects = new List<Func<Task>>();\n                if (published is not null)\n                    effects.Add(() => InvalidateFileSearchCacheAsync(published, workingDirectory));\n                foreach (var notification in notifications)\n                    effects.Add(() => DeliverParentNotificationAsync(notification));\n                if (actorChoresCompleted && IsQueueDrainTrigger(@event))\n                    effects.Add(() => TryDrainNextQueuedPromptAsync(sessionId));\n                publication.IndependentWork = new LiveEventIndependentWork(effects);\n                return publication.IndependentWork.RunAsync();");
        Once(runtime, "var sanitized = projector.Project(@event);");
        Once(runtime, "var actorChoresCompleted = false;");
        Once(runtime, "notifications = projector.Entry!.TakeParentNotifications(sanitized);\n                    actorChoresCompleted = true;");
        var publicationHelper = Read("CodeAlta.Orchestration/Runtime/SessionRuntimeService.PluginEvents.cs");
        Once(publicationHelper, "try { releasePublicationUse(); }");
        Once(publicationHelper, "IndependentOriginal = independent()");
        Assert.IsTrue(publicationHelper.IndexOf("try { releasePublicationUse(); }", StringComparison.Ordinal)
            < publicationHelper.IndexOf("IndependentOriginal = independent()", StringComparison.Ordinal));
        Once(publicationHelper, "Stages = _effects.Select(static _ => new OwnedSessionCommandService.OriginalInvocation()).ToArray();");
        Once(publicationHelper, "Stages[index].Launch(_effects[index]);\n                if (await Stages[index].Outcome.ConfigureAwait(false) is { } failure) failures.Add(failure);");
        var effect = Read("CodeAlta.Orchestration/Runtime/SessionRuntimeService.FileSearch.cs");
        Once(effect, "if (FileSearchCache is not { } cache || string.IsNullOrWhiteSpace(workingDirectory)");
        Once(effect, "catch\n        {\n            // Cache dirty marking remains best effort; this catch does not own any other event work.\n        }");
        var publisher = Read("CodeAlta.Orchestration/Runtime/SessionRuntimeEventPublisher.cs");
        Assert.IsFalse(publisher.Contains("FileSearch", StringComparison.Ordinal));
        Assert.IsFalse(publisher.Contains("Plugin", StringComparison.Ordinal));
    }

    [TestMethod]
    public void CurrentSources_RemoveAllTuiCacheEffectsButPreservePluginReplay()
    {
        var coordinator = Read("CodeAlta.Tui/App/SessionRuntimeEventCoordinator.cs");
        Assert.IsFalse(coordinator.Contains("InvalidateProjectFileSearch", StringComparison.Ordinal));
        Assert.IsFalse(coordinator.Contains("_projectFileSearchService", StringComparison.Ordinal));
        Once(coordinator, "IProjectFileSearchService projectFileSearchService,");
        Once(coordinator, "ArgumentNullException.ThrowIfNull(projectFileSearchService);");
        Assert.AreEqual(2, coordinator.Split("ObservePluginAgentEvent(session, @event);", StringSplitOptions.None).Length - 1);
        Assert.IsFalse(coordinator.Contains("ObservePluginAgentEvent(session, agentRuntimeEvent.Event);", StringComparison.Ordinal));
        Assert.AreEqual(2, coordinator.Split("if (!tab.HistoryLoading)\n        {\n            ProjectPluginSessionEvents", StringSplitOptions.None).Length - 1);
        var history = Read("CodeAlta.Tui/App/SessionHistoryCoordinator.cs");
        Once(history, "await _handleAgentEventAsync(session, tab, @event);");
        Assert.IsFalse(history.Contains("InvalidateFileSearch", StringComparison.Ordinal));
    }

    [TestMethod]
    [Ignore("Code-shape preservation check; disabled in favor of behavioral coverage.")]
    public void Preservation_RestoresTwelveParentBlobsAcrossRepresentations()
    {
        Assert.AreEqual(12, RuntimeFileSearchInvalidationSourceInverse.Paths.Count);
        foreach (var path in RuntimeFileSearchInvalidationSourceInverse.Paths)
        {
            var current = Read(path);
            foreach (var text in new[] { current, current.Replace("\n", "\r\n", StringComparison.Ordinal), Mixed(current) })
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                var restored = RuntimeFileSearchInvalidationSourceInverse.Restore(path, SourceTestText.DecodeSource(bytes));
                Assert.AreEqual(RuntimeFileSearchInvalidationSourceInverse.Original(path), OwnedSessionAskSourceInverse.GitObjectId(restored), path);
                Assert.ThrowsExactly<ArgumentException>(() => SourceTestText.DecodeSource(Encoding.UTF8.GetBytes("\uFEFF" + text)));
            }
            Assert.ThrowsExactly<AssertFailedException>(() => RuntimeFileSearchInvalidationSourceInverse.Restore(path, current + "// unexpected\n"));
        }
    }

    [TestMethod]
    public void CurrentSources_PreserveActualByteValidationBeforeHistoricalRestoration()
    {
        var reminder = Read("CodeAlta.Tests/AltaReminderLifetimeSourceTests.cs");
        Once(reminder, "return RuntimeFileSearchInvalidationSourceInverse.RestoreArchitectureInput(baseline.Path, SourceTestText.DecodeSource(bytes));");
        var models = Read("CodeAlta.Tests/ModelsDevCatalogLifetimeSourceTests.cs");
        Once(models, "var source = ReadSource(baseline);\n            // Actual checkout bytes were checked by ReadSource; compare reconstructed originals below.\n            var historicalSource = RuntimeFileSearchInvalidationSourceInverse.RestoreArchitectureInput(baseline.Path, source.Text);\n            var restored = CodeAltaStartupAdmissionSourceTests.RestoreCatalogRoute(baseline.Path, historicalSource);");
        Once(models, "AssertEncoding(source, baseline);\n        return source;");
        Once(models, "CollectionAssert.AreEqual(Encode(source.Text), source.Bytes, baseline.Path + \": actual encoding round trip\");");
        var owner = Read("CodeAlta.Tests/OwnedSessionCommandSourceInverse.cs");
        Once(owner, "internal static string RestoreLifetimeInput(string path, string source)\n        => path is Lifetime ? RestoreCurrentInput(path, source) : RuntimeFileSearchInvalidationSourceInverse.RestoreLifetimeInput(path, source);");
        var desktop = Read("CodeAlta.Tests/DesktopOwnedSessionSourceInverse.cs");
        Once(desktop, "if (path == OwnerInverse) source = RuntimeFileSearchInvalidationSourceInverse.Restore(path, source);");
    }

    [TestMethod]
    [Ignore("Code-shape preservation check; disabled in favor of behavioral coverage.")]
    public void Preservation_HistoricalGatewaysRestoreEachChangedInputExactlyOnce()
    {
        foreach (var path in new[] { "CodeAlta.Tests/ArchitectureGuardrailTests.cs", "CodeAlta.Tests/AltaReminderLifetimeSourceTests.cs" })
        {
            var source = Read(path);
            var restored = OwnedSessionCommandSourceInverse.RestoreLifetimeInput(path, source);
            Assert.AreEqual(RuntimeFileSearchInvalidationSourceInverse.Original(path), OwnedSessionAskSourceInverse.GitObjectId(restored), path);
            Assert.ThrowsExactly<AssertFailedException>(() => OwnedSessionCommandSourceInverse.RestoreLifetimeInput(path, restored));
            // Exercise the outer PluginUI reader from the same original decoded input, not the restored text.
            var uiRestored = PluginAuthoringProfileSourceInverse.RestoreUiContentInput(path,
                PluginMcpBackendSeparationSourceInverse.RestoreUiContentInput(path, source));
            var expectedUiHash = path == "CodeAlta.Tests/ArchitectureGuardrailTests.cs"
                ? "321B5BD7F0C30F83E80D8E8EC3C06185DA779F49220B22BB37891821C0ECBBFD"
                : "EAEBDD92546584DE2B50A7C2B6B22A33B432ECEDE82C54C3D4963C497A1C397D";
            Assert.AreEqual(expectedUiHash,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(SourceTestText.Canonicalize(uiRestored)))), path);
        }
        const string owner = "CodeAlta.Tests/OwnedSessionCommandSourceInverse.cs";
        // These existing inverses assert their entire original internally; no filesystem reads.
        _ = DesktopOwnedSessionSourceInverse.Restore(owner, Read(owner));
        const string desktop = "CodeAlta.Tests/DesktopOwnedSessionSourceInverse.cs";
        _ = OwnedSessionUserInputSourceInverse.Restore(desktop, Read(desktop));
    }

    [TestMethod]
    [Ignore("Code-shape preservation check; disabled in favor of behavioral coverage.")]
    public void Preservation_RetainsAllEighteenInputAnchorsAndHistoricalProjectionAssertions()
    {
        Assert.AreEqual(18, OwnedSessionUserInputSourceInverse.Paths.Count);
        foreach (var path in OwnedSessionUserInputSourceInverse.Paths)
            Assert.AreEqual(OwnedSessionUserInputSourceInverse.Original(path),
                OwnedSessionAskSourceInverse.GitObjectId(OwnedSessionUserInputSourceInverse.Restore(path, Read(path))), path);
        new PluginSessionEventProjectionSourceTests().TuiRouting_PreservesFactoryPrecedenceAndDeferredContexts();
    }

    private static void Once(string source, string text) => Assert.AreEqual(1, source.Split(text, StringSplitOptions.None).Length - 1, text);
    private static string Mixed(string source)
    {
        var lines = source.Split('\n');
        return string.Concat(lines.Select((line, i) => i == lines.Length - 1 ? line : line + (i % 2 == 0 ? "\r\n" : "\n")));
    }
    private static string Read(string path)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CodeAlta.slnx"))) directory = directory.Parent;
        Assert.IsNotNull(directory);
        return SourceTestText.DecodeSource(File.ReadAllBytes(Path.Combine(directory.FullName, path)));
    }
}
