using System.Runtime.CompilerServices;
using System.Text;
using Inverse = CodeAlta.Tests.OwnedSessionCommandSourceInverse;

namespace CodeAlta.Tests;

/// <summary>Ten named content inputs (eight originals and two new production files); inverse calls perform no transitive content reads.</summary>
[TestClass]
public sealed class OwnedSessionCommandSourceTests
{
    private const string Owner = "CodeAlta.Orchestration/Runtime/OwnedSessionCommandService.cs";
    private const string Contracts = "CodeAlta.Orchestration/Runtime/OwnedSessionCommandContracts.cs";
    internal static IReadOnlyList<string> DirectContentPaths =>
    [
        Inverse.Host, Inverse.Options, Inverse.Builtin, Inverse.Runtime,
        Inverse.Discovery, Inverse.Desktop, Inverse.Lifetime, Inverse.Profile, Owner, Contracts,
    ];
    internal static IReadOnlyList<string> TransitiveContentPaths => [];

    [TestMethod]
    public void Requests_AreScalarOnlyAndOwnerDoesNotExposeHost()
    {
        var contracts = Read(Contracts);
        StringAssert.Contains(contracts, "record OwnedTextSendRequest(string ClientRequestId, string SessionId, string Text)");
        StringAssert.Contains(contracts, "record OwnedAbortRequest(string ClientRequestId, Guid TargetOperationId)");
        foreach (var forbidden in new[] { "SessionViewDescriptor", "SessionExecutionOptions", "CodeAltaHost", "OwnedSessionCommandPolicy" })
            Assert.IsFalse(contracts.Contains(forbidden, StringComparison.Ordinal), forbidden);
        var owner = Read(Owner);
        StringAssert.Contains(owner, "internal OwnedSessionCommandService(");
        Assert.IsFalse(owner.Contains("CreateAsync(", StringComparison.Ordinal));
        Assert.IsFalse(owner.Contains("public SessionRuntimeService", StringComparison.Ordinal));
        Assert.IsFalse(owner.Contains("CodeAltaHost", StringComparison.Ordinal));
        StringAssert.Contains(owner, "SessionExecutionPolicy.CaptureSession(");
        StringAssert.Contains(owner, "SessionExecutionPolicy.BuildOptions(");
        Before(owner, "if (!string.Equals(request.SessionId, request.SessionId.Trim(), StringComparison.Ordinal))", "_active.Add(request.SessionId, operation);");
        Before(owner, "if (session is null || !string.Equals(session.SessionId, operation.SessionId, StringComparison.OrdinalIgnoreCase)) return null;", "SessionExecutionPolicy.CaptureSession(");
    }

    [TestMethod]
    public void Host_UsesExplicitBuiltInRootWithoutChangingDefaultProviders()
    {
        var host = Read(Inverse.Host);
        RequireOnce(host, "Commands = new OwnedSessionCommandService(runtimeService, projectCatalog, catalogOptions, ownedCommandReceiptCapacity);");
        RequireOnce(host, "public OwnedSessionCommandService Commands { get; }");
        RequireOnce(host, "        _disposeTask = CreateHostDisposal(\n            DisposeCommandsAndRuntimeAsync,\n            AgentHub.DisposeAsync,");
        RequireOnce(host, "                currentProject,\n                options.OwnedCommandReceiptCapacity);");
        Before(host, "Commands = new OwnedSessionCommandService(runtimeService, projectCatalog, catalogOptions, ownedCommandReceiptCapacity);", "_disposeTask = CreateHostDisposal(");
        Before(host, "ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.OwnedCommandReceiptCapacity);", "Directory.CreateDirectory(globalRoot);");
        Before(host, "new BuiltInCodeAltaSkillRootProvider(options.BuiltInSkillRoot);", "Directory.CreateDirectory(globalRoot);");
        Before(host, "await Commands.DisposeAsync()", "await RuntimeService.DisposeAsync()");
        StringAssert.Contains(host, "throw new AggregateException(commandFailure, runtimeFailure);");
        StringAssert.Contains(Read(Inverse.Options), "public int OwnedCommandReceiptCapacity { get; init; } = 256;");
        RequireOnce(Read(Inverse.Options), Inverse.PluginEnvironmentOption);
        RequireOnce(host, Inverse.PluginOptionsSignature.Replace("private static", "internal static", StringComparison.Ordinal));
        RequireOnce(host, Inverse.ExplicitPluginEnvironmentAssignment);
        RequireOnce(host, "var pluginOperationOptions = CreatePluginOperationOptions(options, catalogOptions, currentProject);");
        var builtin = Read(Inverse.Builtin);
        StringAssert.Contains(builtin, "var rootPath = _rootPath ?? ResolveRootPath();");
        StringAssert.Contains(builtin, "SourceId = \"builtin:codealta\"");
        StringAssert.Contains(builtin, "Precedence = 4");
        Inverse.Restore(Inverse.Host, host);
        Inverse.Restore(Inverse.Builtin, builtin);
    }

    [TestMethod]
    public void Runtime_SplitsWaitAndExecutionTokensWithLiveGuardedCalls()
    {
        var runtime = Read(Inverse.Runtime);
        RequireOnce(runtime, Inverse.SendStart + Inverse.SendForwarder);
        RequireOnce(runtime, Inverse.PublicationHelper);
        RequireOnce(runtime, "var runId = await _agentHub.RunAsync(sessionHandleId, sendOptions, cancellationToken).ConfigureAwait(false);");
        RequireOnce(runtime, "await PublishRunSubmittedIfStillInFlightAsync(session, runId, runStartedAt, coordinationCancellationToken).ConfigureAwait(false);");
        RequireOnce(runtime, Inverse.SendQuery.Replace("                    cancellationToken)", "                    coordinationCancellationToken)", StringComparison.Ordinal));
        var restored = Inverse.Restore(Inverse.Runtime, runtime);
        RequireOnce(restored, Inverse.Publication);
        Assert.IsFalse(restored.Contains("coordinationCancellationToken", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Resolver_UsesDirectStoreAndExistingRecoveryHelpers()
    {
        var runtime = Read(Inverse.Runtime);
        RequireOnce(runtime, Inverse.Resolver);
        Assert.IsFalse(Inverse.Resolver.Contains("ListSessionsAsync", StringComparison.Ordinal));
        StringAssert.Contains(Inverse.Resolver, "TryCreateRecoverableSession(metadata, projects)");
        StringAssert.Contains(Inverse.Resolver, "ApplyCachedSessionLocalState(session, metadata.ViewState)");
        StringAssert.Contains(Inverse.Resolver, "ApplyPersistedSessionLocalStateAsync(session, cancellationToken)");
    }

    [TestMethod]
    public void Owner_RetainsPreparationSendAndAbortAndJoinsBeforeHostDisposal()
    {
        var owner = Read(Owner);
        Before(owner, "_active.Add(request.SessionId, operation);", "operation.Work = RunSendAsync(operation);");
        Before(owner, "operation.Work = RunSendAsync(operation);", "operation.Launch.TrySetResult();");
        StringAssert.Contains(owner, "await operation.Launch.Task.ConfigureAwait(false);");
        StringAssert.Contains(owner, "operation.Preparation = PrepareAsync(operation);");
        StringAssert.Contains(owner, "EnsureCoordinatorSessionAsync(session, options, CancellationToken.None)");
        StringAssert.Contains(owner, "operation.Execution.Token, CancellationToken.None");
        StringAssert.Contains(owner, "await _runtime.AbortAsync(operation.SessionId, CancellationToken.None)");
        StringAssert.Contains(owner, "operation.Control = RunControlAsync(operation);");
        StringAssert.Contains(owner, "operation.Cancellation = operation.Execution.CancelAsync();");
        Assert.IsFalse(owner.Contains("Task.Run(", StringComparison.Ordinal));
        Assert.IsFalse(owner.Contains("ContinueWith", StringComparison.Ordinal));
        Assert.IsFalse(owner.Contains("StreamEventsAsync", StringComparison.Ordinal));
        Before(owner, "_closed = true;", "_disposeTask = DisposeCoreAsync(");
        StringAssert.Contains(owner, "await operation.Work.ConfigureAwait(false);");
        StringAssert.Contains(owner, "await control.ConfigureAwait(false);");
    }

    [TestMethod]
    public void Preservation_RestoresAllEightWholeOriginalsAcrossNewlineRepresentations()
    {
        Assert.AreEqual(8, Inverse.Originals.Count);
        Assert.AreEqual(10, DirectContentPaths.Distinct(StringComparer.Ordinal).Count());
        Assert.AreEqual(0, TransitiveContentPaths.Count);
        Assert.AreEqual(26, Inverse.Originals.Sum(item => Inverse.Edits(item.Path).Count));
        Assert.AreEqual(26, Inverse.Originals.Sum(item => Inverse.Edits(item.Path).Sum(edit => edit.Count)));
        foreach (var (path, hash) in Inverse.Originals)
        {
            foreach (var source in Representations(Read(path)))
                Assert.AreEqual(hash, Inverse.Hash(Inverse.Restore(path, source)), path);
        }
    }

    [TestMethod]
    public void Preservation_RejectsMissingDuplicateAndUnrelatedSourceChanges()
    {
        foreach (var (path, _) in Inverse.Originals)
        {
            var source = Read(path);
            foreach (var (_, after, _) in Inverse.Edits(path))
            {
                var edit = SourceTestText.Canonicalize(after);
                Assert.ThrowsExactly<AssertFailedException>(() => Inverse.Restore(path, source.Replace(edit, "", StringComparison.Ordinal)), path);
                Assert.ThrowsExactly<AssertFailedException>(() => Inverse.Restore(path, source + edit + "\n"), path);
            }
            Assert.ThrowsExactly<AssertFailedException>(() => Inverse.Restore(path, "// unrelated drift\n" + source), path);
            var original = Inverse.Restore(path, source);
            Assert.ThrowsExactly<AssertFailedException>(() => Inverse.Restore(path, original), path);
        }
        Assert.ThrowsExactly<AssertFailedException>(() => Inverse.Restore("unknown.cs", "// unknown\n"));
    }

    [TestMethod]
    public void Preservation_NewestPreMapPreservesInheritedChains()
    {
        RequireOnce(Read(Inverse.Discovery), Inverse.RestoreStart + Inverse.DiscoveryHook);
        RequireOnce(Read(Inverse.Desktop), SessionDiscoveryScopeSourceInverse.StatisticsLink + SessionDiscoveryScopeSourceInverse.NewLinks + Inverse.OwnerLink);
        RequireOnce(Read(Inverse.Lifetime), Inverse.LifetimeHook + Inverse.LifetimeAnchor);
        foreach (var (path, _) in Inverse.Originals)
        {
            var source = Read(path);
            var restored = Inverse.Restore(path, source);
            Assert.AreEqual(path is Inverse.Host or Inverse.Options or Inverse.Runtime or Inverse.Desktop or Inverse.Profile ? restored : source,
                Inverse.RestoreDiscoveryInput(path, source), path);
            Assert.AreEqual(path is Inverse.Lifetime ? restored : source, Inverse.RestoreLifetimeInput(path, source), path);
        }
        foreach (var path in new[] { Inverse.Host, Inverse.Options, Inverse.Runtime, Inverse.Desktop, Inverse.Profile })
            SessionDiscoveryScopeSourceInverse.Restore(path, Read(path));

        var lifetime = Read(Inverse.Lifetime);
        var expected = Inverse.Restore(Inverse.Lifetime, lifetime);
        Assert.AreEqual(expected, PluginAuthoringProfileSourceInverse.RestoreUiContentInput(Inverse.Lifetime, lifetime));
        Assert.AreEqual(expected, PluginAuthoringProfileSourceInverse.RestoreFeedbackInput(Inverse.Lifetime, lifetime));
        // This consumer performs its own older lifetime inverse after the profile pre-map.
        PluginFeedbackExtractionSourceTests.Restore(Inverse.Lifetime, lifetime);
        PluginFeedbackExtractionSourceTests.Restore(Inverse.Host, Read(Inverse.Host));
        PluginMcpBackendSeparationSourceInverse.Restore(Inverse.Profile, Read(Inverse.Profile));
        PluginGitHubBackendSeparationSourceInverse.Restore(Inverse.Profile, Read(Inverse.Profile));
        const string untouched = "not a source document";
        Assert.AreSame(untouched, Inverse.RestoreDiscoveryInput("unmapped", untouched));
        Assert.AreSame(untouched, Inverse.RestoreLifetimeInput("unmapped", untouched));
    }

    private static IEnumerable<string> Representations(string source)
    {
        yield return source;
        yield return source.Replace("\n", "\r\n", StringComparison.Ordinal);
        var mixed = new StringBuilder();
        var crlf = false;
        foreach (var character in source)
        {
            if (character == '\n' && (crlf = !crlf)) mixed.Append('\r');
            mixed.Append(character);
        }
        yield return mixed.ToString();
    }

    private static void RequireOnce(string source, string fragment)
        => Assert.AreEqual(1, source.Split(SourceTestText.Canonicalize(fragment), StringSplitOptions.None).Length - 1, fragment);

    private static void Before(string source, string first, string second)
    {
        RequireOnce(source, first);
        RequireOnce(source, second);
        Assert.IsTrue(source.IndexOf(first, StringComparison.Ordinal) < source.IndexOf(second, StringComparison.Ordinal));
    }

    private static string Read(string path, [CallerFilePath] string caller = "")
        => SourceTestText.DecodeSource(File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(caller)!, "..", path)));
}
