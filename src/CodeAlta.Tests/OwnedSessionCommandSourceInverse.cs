using System.Security.Cryptography;
using System.Text;

namespace CodeAlta.Tests;

/// <summary>Mandatory whole-original inverses of the eight parent-anchored 9d17c067 inputs; no filesystem access.</summary>
internal static class OwnedSessionCommandSourceInverse
{
    internal const string Host = "CodeAlta.Orchestration/Hosting/CodeAltaHost.cs";
    internal const string Options = "CodeAlta.Orchestration/Hosting/CodeAltaHostOptions.cs";
    internal const string Builtin = "CodeAlta.Catalog/Skills/BuiltInSkillRootProviders.cs";
    internal const string Runtime = "CodeAlta.Orchestration/Runtime/SessionRuntimeService.cs";
    internal const string Discovery = "CodeAlta.Tests/SessionDiscoveryScopeSourceInverse.cs";
    internal const string Desktop = "CodeAlta.Desktop.Tests/CodeAlta.Desktop.Tests.csproj";
    internal const string Lifetime = "CodeAlta.Tests/CodeAltaHostLifetimeTests.cs";
    internal const string Profile = "CodeAlta.Tests/PluginAuthoringProfileSourceInverse.cs";

    internal static string RestoreDiscoveryInput(string path, string source)
        => path is Host or Options or Runtime or Desktop or Profile ? Restore(path, source) : source;

    internal static string RestoreLifetimeInput(string path, string source)
        => path is Lifetime ? Restore(path, source) : source;

    internal static string Restore(string path, string source)
    {
        source = SourceTestText.DecodeSource(new UTF8Encoding(false, true).GetBytes(source));
        foreach (var (before, after, count) in Edits(path))
        {
            var expected = SourceTestText.Canonicalize(after);
            Assert.IsTrue(expected.Length > 0, path);
            Assert.AreEqual(count, source.Split(expected, StringSplitOptions.None).Length - 1, path + ": " + expected);
            source = source.Replace(expected, SourceTestText.Canonicalize(before), StringComparison.Ordinal);
        }
        source = SourceTestText.DecodeSource(new UTF8Encoding(false, true).GetBytes(source));
        Assert.AreEqual(Originals.Single(item => item.Path == path).Hash, Hash(source), path);
        return source;
    }

    internal static string Hash(string source)
        => Convert.ToHexString(SHA256.HashData(new UTF8Encoding(false, true).GetBytes(source)));

    internal static IReadOnlyList<(string Path, string Hash)> Originals =>
    [
        (Host, "B81F172AD9449EDF4A1BCB2F750E864054E282761ED172421C8C140AD12CCABB"),
        (Options, "7B8B21EE927C4BC076EDCB9B21B6740D28409B05405D7D51FEEEA3F77AEBECCB"),
        (Builtin, "0386AA47B6621155321147709DFDF33C2E1C4EF460D2F049A16EA7909FBA3C19"),
        (Runtime, "3FFCA7536250F5AC042B6A4AA78F070D46328CD58458228AA9395BBF0F613FA1"),
        (Discovery, "5ED820331DA3382C1CC121536C07430AE3D96AFD2566BB71470DE65AE6E8E967"),
        (Desktop, "E1D4DB29F594EC7D4B9889A7EB6F9ECA378AC97AE3E457AE911FD392029D099A"),
        (Lifetime, "AC94374A336C80B9010482E5663B38221474FCC486F6E9A10F55248A6B421BDD"),
        (Profile, "45056627182F4FC61577896076E58EFE40C617F8C2A135C4C4832DB681FA4268"),
    ];

    // Every tuple includes unchanged local context and has exactly one expected occurrence.
    internal static IReadOnlyList<(string Before, string After, int Count)> Edits(string path) => path switch
    {
        Host =>
        [
            ("        ProjectDescriptor currentProject)\n", "        ProjectDescriptor currentProject,\n        int ownedCommandReceiptCapacity)\n", 1),
            ("        CurrentProject = currentProject;\n", "        CurrentProject = currentProject;\n        Commands = new OwnedSessionCommandService(runtimeService, projectCatalog, catalogOptions, ownedCommandReceiptCapacity);\n", 1),
            ("        _disposeTask = CreateHostDisposal(\n            RuntimeService.DisposeAsync,\n", "        _disposeTask = CreateHostDisposal(\n            DisposeCommandsAndRuntimeAsync,\n", 1),
            ("    public SessionRuntimeService RuntimeService { get; }\n", "    public SessionRuntimeService RuntimeService { get; }\n" + CommandsProperty, 1),
            (HostScopeValidation, HostScopeValidation + HostValidation, 1),
            ("                new UserCommonSkillRootProvider(),\n                new BuiltInCodeAltaSkillRootProvider(),\n", "                new UserCommonSkillRootProvider(),\n                builtInSkillRootProvider,\n", 1),
            ("                currentProject);\n", "                currentProject,\n                options.OwnedCommandReceiptCapacity);\n", 1),
            (HostDispose, HostDispose + "\n" + HostCleanup, 1),
            (OldHostExceptions, NewHostExceptions, 1),
            ("    /// Attempts runtime, hub, registry, owned plugin and owned logging cleanup in order, even after a stage fails.\n", "    /// Joins owned commands, then attempts runtime, hub, registry, owned plugin and owned logging cleanup in order, even after a stage fails.\n", 1),
            (PluginOptionsSignature, PluginOptionsSignature.Replace("private static", "internal static", StringComparison.Ordinal), 1),
            (PluginEnvironmentAssignment, ExplicitPluginEnvironmentAssignment, 1),
        ],
        Options =>
        [
            ("    public int OwnedCommandReceiptCapacity { get; init; } = 256;\n", "    public int OwnedCommandReceiptCapacity { get; init; } = 256;\n" + PluginEnvironmentOption, 1),
            ("    public SessionDiscoveryScope? DiscoveryScope { get; init; }\n", "    public SessionDiscoveryScope? DiscoveryScope { get; init; }\n" + HostOptions, 1),
        ],
        Builtin =>
        [
            (BuiltinHeader, BuiltinHeader + BuiltinConstructors, 1),
            ("        ArgumentNullException.ThrowIfNull(context);\n        cancellationToken.ThrowIfCancellationRequested();\n        var rootPath = ResolveRootPath();\n", "        ArgumentNullException.ThrowIfNull(context);\n        cancellationToken.ThrowIfCancellationRequested();\n        var rootPath = _rootPath ?? ResolveRootPath();\n", 1),
        ],
        Runtime =>
        [
            (SendStart, SendStart + SendForwarder, 1),
            (SendQuery, SendQuery.Replace("                    cancellationToken)", "                    coordinationCancellationToken)", StringComparison.Ordinal), 1),
            (RunCall + Publication, RunCall + "            await PublishRunSubmittedIfStillInFlightAsync(session, runId, runStartedAt, coordinationCancellationToken).ConfigureAwait(false);\n", 1),
            (ResolverAnchor, Resolver + ResolverAnchor, 1),
            (SendAnchor, PublicationHelper + SendAnchor, 1),
        ],
        Discovery => [(RestoreStart, RestoreStart + DiscoveryHook, 1)],
        Desktop => [(DiscoveryLink, DiscoveryLink + OwnerLink, 1)],
        Lifetime => [(LifetimeAnchor, LifetimeHook + LifetimeAnchor, 1)],
        Profile =>
        [
            (UiMap, UiMap.Replace(": source;", ": OwnedSessionCommandSourceInverse.RestoreLifetimeInput(path, source);", StringComparison.Ordinal), 1),
            (FeedbackMap, FeedbackMap.Replace(": source;", ": OwnedSessionCommandSourceInverse.RestoreLifetimeInput(path, source);", StringComparison.Ordinal), 1),
        ],
        _ => throw new AssertFailedException("Unknown owned-command inverse path: " + path),
    };

    internal const string RestoreStart = "    internal static string Restore(string path, string source)\n    {\n";
    internal const string DiscoveryHook = "        source = OwnedSessionCommandSourceInverse.RestoreDiscoveryInput(path, source);\n";
    internal const string DiscoveryLink = "    <Compile Include=\"../CodeAlta.Orchestration/Runtime/SessionDiscoveryScope.cs\" Link=\"SessionDiscoveryScope.cs\" />\n";
    internal const string OwnerLink = "    <Compile Include=\"../CodeAlta.Tests/OwnedSessionCommandSourceInverse.cs\" Link=\"OwnedSessionCommandSourceInverse.cs\" />\n";
    internal const string LifetimeAnchor = "        var fields = Scope(source, \"public sealed class CodeAltaHost : IAsyncDisposable\", \"    private CodeAltaHost(\");\n";
    internal const string LifetimeHook = "        source = OwnedSessionCommandSourceInverse.Restore(\"CodeAlta.Orchestration/Hosting/CodeAltaHost.cs\", source);\n";
    internal const string HostScopeValidation = "        options.DiscoveryScope?.ValidateHostRoots(options.GlobalRoot, options.CurrentProjectPath);\n";
    internal const string HostDispose = "    public ValueTask DisposeAsync() => new(_disposeTask.Value);\n";
    internal const string BuiltinHeader = "public sealed class BuiltInCodeAltaSkillRootProvider : ISkillRootProvider\n{\n";
    internal const string ResolverAnchor = "    /// <summary>\n    /// Lists recoverable user-facing sessions from the session catalog.\n";
    internal const string SendAnchor = "    /// <summary>\n    /// Sends input to the coordinator session for a session.\n";
    internal const string RunCall = "            var runId = await _agentHub.RunAsync(sessionHandleId, sendOptions, cancellationToken).ConfigureAwait(false);\n";
    internal const string OldHostExceptions = "    /// <exception cref=\"ArgumentOutOfRangeException\">Thrown when the plugin authoring profile is invalid, before host acquisition.</exception>\n    /// <exception cref=\"ArgumentException\">Scoped host roots are missing, not absolute, or the project is outside the instruction boundary.</exception>\n";
    internal const string NewHostExceptions = OldHostExceptions + "    /// <exception cref=\"ArgumentOutOfRangeException\">Owned receipt capacity is not positive, before host acquisition.</exception>\n    /// <exception cref=\"ArgumentException\">An explicit builtin skill root is blank or not fully qualified.</exception>\n";

    internal const string CommandsProperty = "\n    /// <summary>Gets the host-owned text-send and abort admission service; completion is not transcript completion.</summary>\n    public OwnedSessionCommandService Commands { get; }\n";
    internal const string HostOptions = "\n    /// <summary>Gets an explicit absolute builtin skill root; null preserves application/source discovery.</summary>\n    public string? BuiltInSkillRoot { get; init; }\n\n    /// <summary>Gets the positive owner-lifetime receipt limit; full owners reject new requests without evicting retry protection.</summary>\n    public int OwnedCommandReceiptCapacity { get; init; } = 256;\n";
    internal const string HostValidation = "        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.OwnedCommandReceiptCapacity);\n        var builtInSkillRootProvider = options.BuiltInSkillRoot is null\n            ? new BuiltInCodeAltaSkillRootProvider()\n            : new BuiltInCodeAltaSkillRootProvider(options.BuiltInSkillRoot);\n";
    internal const string PluginEnvironmentOption = "\n    /// <summary>Gets an optional environment map copied for host-created plugin adapter operation options; null preserves the original ambient snapshot.</summary>\n    /// <remarks>This does not isolate the process or provider environment.</remarks>\n    public IReadOnlyDictionary<string, string?>? PluginEnvironment { get; init; }\n";
    internal const string PluginOptionsSignature = "    private static PluginAdapterOperationOptions CreatePluginOperationOptions(\n        CodeAltaHostOptions options,\n        CatalogOptions catalogOptions,\n        ProjectDescriptor currentProject)\n        => new()\n";
    internal const string PluginEnvironmentAssignment = """
                ConfigurationPaths = [Path.Combine(catalogOptions.GlobalRoot, "config.toml")],
                Environment = Environment.GetEnvironmentVariables()
                    .Cast<System.Collections.DictionaryEntry>()
                    .Where(static entry => entry.Key is string)
                    .ToDictionary(static entry => (string)entry.Key, static entry => entry.Value?.ToString(), StringComparer.OrdinalIgnoreCase),
    """ + "\n";
    internal const string ExplicitPluginEnvironmentAssignment = """
                ConfigurationPaths = [Path.Combine(catalogOptions.GlobalRoot, "config.toml")],
                Environment = options.PluginEnvironment is not null
                    ? new Dictionary<string, string?>(options.PluginEnvironment, StringComparer.OrdinalIgnoreCase)
                    : Environment.GetEnvironmentVariables()
                    .Cast<System.Collections.DictionaryEntry>()
                    .Where(static entry => entry.Key is string)
                    .ToDictionary(static entry => (string)entry.Key, static entry => entry.Value?.ToString(), StringComparer.OrdinalIgnoreCase),
    """ + "\n";

    internal const string HostCleanup = """
        private async ValueTask DisposeCommandsAndRuntimeAsync()
        {
            Exception? commandFailure = null;
            try
            {
                await Commands.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                commandFailure = ex;
            }

            try
            {
                await RuntimeService.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception runtimeFailure)
            {
                if (commandFailure is not null)
                {
                    throw new AggregateException(commandFailure, runtimeFailure);
                }
                throw;
            }

            if (commandFailure is not null)
            {
                ExceptionDispatchInfo.Capture(commandFailure).Throw();
            }
        }
    """ + "\n";

    internal const string BuiltinConstructors = """
        private readonly string? _rootPath;

        /// <summary>Uses the unchanged application/source builtin discovery route.</summary>
        public BuiltInCodeAltaSkillRootProvider()
        {
        }

        /// <summary>Uses one explicit absolute builtin root without ancestor discovery or existence probes.</summary>
        /// <exception cref="ArgumentException">The root is blank or not fully qualified.</exception>
        public BuiltInCodeAltaSkillRootProvider(string rootPath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
            if (!Path.IsPathFullyQualified(rootPath))
            {
                throw new ArgumentException("The builtin skill root must be fully qualified.", nameof(rootPath));
            }
            _rootPath = Path.GetFullPath(rootPath);
        }

    """ + "\n";

    internal const string SendStart = """
        public async Task<AgentRunId> SendAsync(
            SessionViewDescriptor session,
            SessionExecutionOptions options,
            AgentSendOptions sendOptions,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(session);
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(sendOptions);
    """ + "\n";

    internal const string SendForwarder = """

            return await SendAsync(session, options, sendOptions, cancellationToken, cancellationToken).ConfigureAwait(false);
        }

        internal async Task<AgentRunId> SendAsync(
            SessionViewDescriptor session,
            SessionExecutionOptions options,
            AgentSendOptions sendOptions,
            CancellationToken cancellationToken,
            CancellationToken coordinationCancellationToken)
        {
            ArgumentNullException.ThrowIfNull(session);
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(sendOptions);
    """ + "\n";

    internal const string SendQuery = """
                            return ensuredHandleId;
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
    """ + "\n";

    internal const string Publication = """
                if (await MarkActiveRunIfStillInFlightAsync(session.SessionId, runId, runStartedAt, cancellationToken).ConfigureAwait(false))
                {
                    PublishRunSubmittedEvent(session.SessionId, runId, runStartedAt);
                }
    """ + "\n";

    internal const string PublicationHelper = """
        private async Task PublishRunSubmittedIfStillInFlightAsync(
            SessionViewDescriptor session,
            AgentRunId runId,
            DateTimeOffset runStartedAt,
            CancellationToken cancellationToken)
        {
            if (await MarkActiveRunIfStillInFlightAsync(session.SessionId, runId, runStartedAt, cancellationToken).ConfigureAwait(false))
            {
                PublishRunSubmittedEvent(session.SessionId, runId, runStartedAt);
            }
        }

    """ + "\n";

    internal const string Resolver = """
        internal async Task<SessionViewDescriptor?> ResolveOwnedSessionAsync(string sessionId, CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
            ObjectDisposedException.ThrowIf(_disposed, this);
            var metadata = await _sessionViewCatalog.JournalStore.CreateSessionStore()
                .GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
            if (metadata is null)
            {
                return null;
            }

            var projects = await _projectCatalog.LoadAsync(cancellationToken).ConfigureAwait(false);
            var session = TryCreateRecoverableSession(metadata, projects);
            if (session is not null)
            {
                if (metadata.ViewState is not null)
                {
                    ApplyCachedSessionLocalState(session, metadata.ViewState);
                }
                else
                {
                    await ApplyPersistedSessionLocalStateAsync(session, cancellationToken).ConfigureAwait(false);
                }
            }
            return session;
        }

    """ + "\n";

    internal const string UiMap = """
        internal static string RestoreUiContentInput(string path, string source) => path is
            "CodeAlta.Plugins/PluginRootBuildFiles.cs" or "CodeAlta.Plugins/PluginAssemblyLoading.cs" or
            "CodeAlta.Plugins/PluginRuntimeManager.cs" or "CodeAlta.Orchestration/Hosting/CodeAltaHostOptions.cs" or
            "CodeAlta.Orchestration/Hosting/CodeAltaHost.cs" or "CodeAlta.Tui/Program.cs" or
            "CodeAlta.Tui/App/CodeAltaOwnedServices.cs" or "CodeAlta.Tests/PluginFeedbackExtractionSourceTests.cs"
            ? Restore(path, source) : source;
    """;

    internal const string FeedbackMap = """
        internal static string RestoreFeedbackInput(string path, string source) => path is
            "CodeAlta.Plugins/PluginRuntimeManager.cs" or "CodeAlta.Orchestration/Hosting/CodeAltaHostOptions.cs" or
            "CodeAlta.Orchestration/Hosting/CodeAltaHost.cs" or "CodeAlta.Tui/Program.cs" or "CodeAlta.Tui/App/CodeAltaOwnedServices.cs"
            ? Restore(path, source) : source;
    """;
}
