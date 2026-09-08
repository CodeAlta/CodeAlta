using System.Text;

namespace CodeAlta.Tests;

/// <summary>Mandatory whole-original b069dbdb inverses; literal maps only, with no content reads.</summary>
internal static class DesktopOwnedSessionSourceInverse
{
    internal const string Project = "CodeAlta/CodeAlta.csproj";
    internal const string Cli = "CodeAlta/Desktop/DesktopCommandLine.cs";
    internal const string App = "CodeAlta/Desktop/DesktopApplication.cs";
    internal const string Boot = "CodeAlta/Desktop/Rpc/BootRpc.cs";
    internal const string Workspace = "CodeAlta/Desktop/Rpc/WorkspaceRpc.cs";
    internal const string Main = "CodeAlta/frontend/src/main.tsx";
    internal const string Styles = "CodeAlta/frontend/src/style.css";
    internal const string Architecture = "CodeAlta.Desktop.Tests/DesktopArchitectureTests.cs";
    internal const string HistoryTests = "CodeAlta.Desktop.Tests/DesktopHistorySourceTests.cs";
    internal const string WorkspaceTests = "CodeAlta.Desktop.Tests/DesktopWorkspaceSourceTests.cs";
    internal const string Host = OwnedSessionCommandSourceInverse.Host;
    internal const string OwnerInverse = "CodeAlta.Tests/OwnedSessionCommandSourceInverse.cs";
    internal const string OwnerTests = "CodeAlta.Tests/OwnedSessionCommandSourceTests.cs";
    internal const string Lifetime = OwnedSessionCommandSourceInverse.Lifetime;
    internal const string DesktopProject = OwnedSessionCommandSourceInverse.Desktop;
    internal const string OrchestrationAssemblyInfo = "CodeAlta.Orchestration/Properties/AssemblyInfo.cs";

    internal static IReadOnlyList<string> DirectContentPaths => [];
    internal static IReadOnlyList<string> TransitiveContentPaths => [];

    internal static string RestoreInput(string path, string source) => path is
        Project or Cli or App or Boot or Workspace or Main or Styles or Architecture or HistoryTests or
        WorkspaceTests or Host or OwnerInverse or OwnerTests or Lifetime or DesktopProject or OrchestrationAssemblyInfo
        ? Restore(path, source) : source;

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
        Assert.AreEqual(Originals.Single(item => item.Path == path).Hash, OwnedSessionCommandSourceInverse.Hash(source), path);
        return source;
    }

    internal static IReadOnlyList<(string Path, string Hash)> Originals =>
    [
        (Project, "364A416ADCBA4A21DBB035E6C662B8760F9F18B4D56690D7BEC4ACE8A6C5B05E"),
        (Cli, "0C9042F66652DD45E8CD79E4BCBE265F535F582562BDFA2F4D931128FABEC1C8"),
        (App, "8A8FD357955471AF0D21CECF28ECAA7FC18069BEFC6988A4628B6B22C4DBF264"),
        (Boot, "3AF6F621BBD7CEF0859A21C0F4C2D6C3796949766466E486A0CD1287F8C60585"),
        (Workspace, "76B6FD47F7712A86AC864755D6005BF502E66F5B53034804E3337DD69E357F13"),
        (Main, "3D13BE09CCEFE4DE905FA684543BFE3D81C58199A7BC1F8C0258D2EBFB309FEB"),
        (Styles, "C675A91D71770CBC17C08B2E26CAC5E89DBBDE490042F7A8A62DE9095CDB99A8"),
        (Architecture, "B6BF813CFBADC7910FEC28C397612723C8426972C3C2919A0C5D4C18334EF232"),
        (HistoryTests, "DC186E20982845C5156EB23D3BBAD621C93F3F539163138C4A15FF1D283DAAD2"),
        (WorkspaceTests, "96DF8A17D34ED89F4A28D34AD677381B77C5D1977393E8D8234E91E9EC4B192D"),
        (Host, "C4FAED1631BFD1F9D1149FA177BDF8E7823F33C9E64588A05508FB7C6357FE67"),
        (OwnerInverse, "6E036C86ED69A3867DE3DAA00E0E4694D459BB211C48D9E57C529560019162BE"),
        (OwnerTests, "53660C0AB30D990A06F2B37BFFBE21443A8BB5C0DCDE55874CA57DA307DEBA8E"),
        (Lifetime, "B085B7A68EE2996E2D035AD29892BD619C1334962B88C79C2EED5B566A388277"),
        (DesktopProject, "7C377BD9BCD03F5AEE3C1121B765B120F54C892C5DA701D3EAEA4A3544D26293"),
        (OrchestrationAssemblyInfo, "DBE93A6F75D798248ED4993A59285A48EA259F93B904131BAF5298D5CCCC6844"),
    ];

    internal static IReadOnlyList<(string Before, string After, int Count)> Edits(string path) => path switch
    {
        Project => [(CatalogLink, CatalogLink + AgentLink + HostLinks, 1)],
        Cli =>
        [
            ("internal sealed record DesktopLaunchOptions(string DataRoot, string? CatalogRoot);\n", OwnedRoots, 1),
            ("            return 0;\n        }\n\n        if (args is [\"--version\"])\n", OwnedHelp + "            return 0;\n        }\n\n        if (args is [\"--version\"])\n", 1),
            ("        var allowCache = false;\n", "        var allowCache = false;\n        var allowOwned = false;\n        string? project = null, home = null, instructions = null, builtin = null;\n", 1),
            ("                default: return false;\n", OwnedCases + "                default: return false;\n", 1),
            ("        options = new DesktopLaunchOptions(data, catalog);\n", "        options = new DesktopLaunchOptions(data, catalog);\n" + OwnedRootValidation, 1),
        ],
        App =>
        [
            ("using CodeAlta.Desktop.Rpc;\n", AppUsings, 1),
            ("    internal int ExitCode { get; private set; } = 1;\n", AppFields + "    internal int ExitCode { get; private set; } = 1;\n", 1),
            ("        Directory.CreateDirectory(options.DataRoot);\n        var desktop = new DesktopApplication(options);\n", "        if (options.Owned is not null) return RunOwned(options);\n        Directory.CreateDirectory(options.DataRoot);\n        var desktop = new DesktopApplication(options);\n", 1),
            ("    private async ValueTask RunAsync(NeoApplication application)\n", OwnedLifecycle + "    private async ValueTask RunAsync(NeoApplication application)\n", 1),
        ],
        Boot =>
        [
            ("    [NeoRpcMethod(\"status\")]\n", BootFields + "    [NeoRpcMethod(\"status\")]\n", 1),
            (OldBootStatus, NewBootStatus, 1),
            ("internal sealed record BootStatus(string State, string ProductName, string Version, bool HostAvailable);\n", BootRecord, 1),
            ("[JsonSerializable(typeof(HistoryResponse))]\n", "[JsonSerializable(typeof(HistoryResponse))]\n" + ReceiptMetadata, 1),
        ],
        Workspace => [("    internal WorkspaceService(string? catalogRoot)\n", OwnedWorkspace + "    internal WorkspaceService(string? catalogRoot)\n", 1)],
        Main =>
        [
            ("import type { HistoryRequest } from \"#neoastra\";\n", "import type { HistoryRequest } from \"#neoastra\";\nimport type { SessionSendRequest } from \"#neoastra\";\nimport { OwnedSessionPanel } from \"./OwnedSessionPanel\";\nimport { createMutationCapability } from \"./sessionOperations\";\n", 1),
            ("  const [sessionId, setSessionId] = useState<string | null>(null);\n", "  const [sessionId, setSessionId] = useState<string | null>(null);\n  const [submissions] = useState(() => new Map<string, SessionSendRequest>());\n  const [mutation, setMutation] = useState<{ epoch: string; capability: ReturnType<typeof createMutationCapability> }>();\n", 1),
            ("      .then(value => { if (!abort.signal.aborted) setStatus(value); })\n", BootCapability, 1),
            (LegacyDescription, OwnedDescription, 1),
            (LegacyReadNotice, OwnedReadNotice, 1),
            ("          <History key={selectedSession.id} sessionId={selectedSession.id} />\n", OwnedMount, 1),
        ],
        Styles => [(".history-controls { display: flex; gap: 1rem; }\n", ".history-controls { display: flex; gap: 1rem; }\n" + OwnedStyles, 1)],
        Architecture =>
        [
            ("                Assert.IsTrue(reference.Name is \"CodeAlta.Catalog\" or \"CodeAlta.Agent\", reference.Name);\n", "                Assert.IsTrue(reference.Name is \"CodeAlta.Catalog\" or \"CodeAlta.Agent\" or \"CodeAlta.Hosting\" or \"CodeAlta.Orchestration\", reference.Name);\n", 1),
            ("        CollectionAssert.AreEqual(new[] { \"../CodeAlta.Catalog/CodeAlta.Catalog.csproj\" },\n", "        CollectionAssert.AreEqual(new[] { \"../CodeAlta.Catalog/CodeAlta.Catalog.csproj\", \"../CodeAlta.Agent/CodeAlta.Agent.csproj\", \"../CodeAlta.Hosting/CodeAlta.Hosting.csproj\", \"../CodeAlta.Orchestration/CodeAlta.Orchestration.csproj\" },\n", 1),
        ],
        HistoryTests => [(OldHistoryRead, NewHistoryRead, 1)],
        WorkspaceTests => [(WorkspaceReadAnchor, WorkspaceReadAnchor + WorkspaceReadMap, 1)],
        Host =>
        [
            ("        Commands = new OwnedSessionCommandService(runtimeService, projectCatalog, catalogOptions, ownedCommandReceiptCapacity);\n", "        Commands = new OwnedSessionCommandService(runtimeService, projectCatalog, catalogOptions, ownedCommandReceiptCapacity);\n        WorkspaceReads = new OwnedSessionWorkspace(projectCatalog, sessionViewCatalog.JournalStore);\n", 1),
            ("    public OwnedSessionCommandService Commands { get; }\n", "    public OwnedSessionCommandService Commands { get; }\n\n    /// <summary>Gets host-owned admission and drainage for up to eight actual cached workspace reads.</summary>\n    public OwnedSessionWorkspace WorkspaceReads { get; }\n", 1),
            (OwnedSessionCommandSourceInverse.HostCleanup, HostCleanup, 1),
            ("    /// Joins owned commands, then attempts runtime, hub, registry, owned plugin and owned logging cleanup in order, even after a stage fails.\n", "    /// Joins owned commands and workspace reads, then attempts runtime, hub, registry, owned plugin and owned logging cleanup in order, even after a stage fails.\n", 1),
        ],
        OwnerInverse =>
        [
            (OldDiscoveryGateway, OldDiscoveryGateway.Replace("? Restore(path, source)", "? RestoreCurrentInput(path, source)", StringComparison.Ordinal), 1),
            (OldLifetimeGateway, OldLifetimeGateway.Replace("? Restore(path, source)", "? RestoreCurrentInput(path, source)", StringComparison.Ordinal), 1),
            ("    internal static string Restore(string path, string source)\n    {\n", CurrentGateway + "    internal static string Restore(string path, string source)\n    {\n", 1),
        ],
        OwnerTests =>
        [
            ("            var restored = Inverse.Restore(path, source);\n", "            var restored = Inverse.Restore(path, source);\n            source = ReadCurrent(path);\n", 1),
            ("            SessionDiscoveryScopeSourceInverse.Restore(path, Read(path));\n", "            SessionDiscoveryScopeSourceInverse.Restore(path, ReadCurrent(path));\n", 1),
            ("        var expected = Inverse.Restore(Inverse.Lifetime, lifetime);\n", "        var expected = Inverse.Restore(Inverse.Lifetime, lifetime);\n        lifetime = ReadCurrent(Inverse.Lifetime);\n", 1),
            ("        PluginFeedbackExtractionSourceTests.Restore(Inverse.Host, Read(Inverse.Host));\n", "        PluginFeedbackExtractionSourceTests.Restore(Inverse.Host, ReadCurrent(Inverse.Host));\n", 1),
            (OwnerReadAnchor, OwnerReadAnchor + OwnerReadMap, 1),
        ],
        Lifetime => [(OwnedSessionCommandSourceInverse.LifetimeHook, OwnedSessionCommandSourceInverse.LifetimeHook.Replace(".Restore(", ".RestoreCurrentInput(", StringComparison.Ordinal), 1)],
        DesktopProject =>
        [
            (OwnedSessionCommandSourceInverse.OwnerLink, OwnedSessionCommandSourceInverse.OwnerLink + NewLink, 1),
            (ScopeInverseLink + OwnedSessionCommandSourceInverse.DiscoveryLink, ScopeInverseLink, 1),
        ],
        OrchestrationAssemblyInfo =>
        [
            ("[assembly: InternalsVisibleTo(\"CodeAlta.Tests\")]\n", "[assembly: InternalsVisibleTo(\"CodeAlta.Tests\")]\n[assembly: InternalsVisibleTo(\"CodeAlta.Desktop.Tests\")]\n", 1),
        ],
        _ => throw new AssertFailedException("Unknown desktop-owned inverse path: " + path),
    };

    internal const string NewLink = "    <Compile Include=\"../CodeAlta.Tests/DesktopOwnedSessionSourceInverse.cs\" Link=\"DesktopOwnedSessionSourceInverse.cs\" />\n";
    private const string ScopeInverseLink = "    <Compile Include=\"../CodeAlta.Tests/SessionDiscoveryScopeSourceInverse.cs\" Link=\"SessionDiscoveryScopeSourceInverse.cs\" />\n";
    private const string CatalogLink = "    <ProjectReference Include=\"../CodeAlta.Catalog/CodeAlta.Catalog.csproj\" />\n";
    private const string AgentLink = "    <ProjectReference Include=\"../CodeAlta.Agent/CodeAlta.Agent.csproj\" />\n";
    private const string HostLinks = "    <ProjectReference Include=\"../CodeAlta.Hosting/CodeAlta.Hosting.csproj\" />\n    <ProjectReference Include=\"../CodeAlta.Orchestration/CodeAlta.Orchestration.csproj\" />\n";
    private const string OwnedRoots = "internal sealed record DesktopLaunchOptions(string DataRoot, string? CatalogRoot)\n{\n    internal OwnedDesktopRoots? Owned { get; init; }\n}\ninternal sealed record OwnedDesktopRoots(string Project, string Home, string Instructions, string Builtin);\n";
    private const string OwnedHelp = "            output.WriteLine(\"Separate owned mode additionally requires --allow-owned-host --project-root <existing absolute directory> --discovery-home <existing absolute directory> --instruction-root <existing absolute project ancestor> --builtin-skill-root <existing absolute directory>. This consents to lock/project-catalog/journal/cache/provider-state writes, configured-provider registration (including declared credential environment names and shipped defaults), and provider authentication/storage/network on submission. Plugins and probes stay disabled; permissions denied and user input cancelled. No default-profile/HOME substitution; discovery roots do not sandbox providers, copied-cache external journal paths or reparse points. Only task-owned roots are admitted; this is not production/shared-profile qualification.\");\n";
    private const string OwnedCases = "                case \"--allow-owned-host\" when !allowOwned: allowOwned = true; break;\n                case \"--project-root\" when project is null && i + 1 < args.Length: project = args[++i]; break;\n                case \"--discovery-home\" when home is null && i + 1 < args.Length: home = args[++i]; break;\n                case \"--instruction-root\" when instructions is null && i + 1 < args.Length: instructions = args[++i]; break;\n                case \"--builtin-skill-root\" when builtin is null && i + 1 < args.Length: builtin = args[++i]; break;\n";
    private const string OwnedRootValidation = """
            if (allowOwned || project is not null || home is not null || instructions is not null || builtin is not null)
            {
                options = null;
                if (!allowOwned || catalog is null || !allowCache) return false;
                var roots = new[] { project, home, instructions, builtin };
                if (roots.Any(path => string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || ContainsAlta(path))) return false;
                var normalized = roots.Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path!))).ToArray();
                if (normalized.Any(path => ContainsAlta(path) || !directoryExists(path) || fileExists(path))) return false;
                if (new[] { normalized[0], normalized[1], normalized[3] }.Any(path => Overlap(data, path))) return false;
                var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                var ancestor = Path.EndsInDirectorySeparator(normalized[2]) ? normalized[2] : normalized[2] + Path.DirectorySeparatorChar;
                if (!string.Equals(normalized[0], normalized[2], comparison) && !normalized[0].StartsWith(ancestor, comparison)) return false;
                options = new DesktopLaunchOptions(data, catalog) { Owned = new(normalized[0], normalized[1], normalized[2], normalized[3]) };
            }
    """ + "\n";
    private const string AppUsings = "using CodeAlta.Desktop.Rpc;\nusing System.Collections.Frozen;\nusing CodeAlta.Catalog;\nusing CodeAlta.Hosting;\nusing CodeAlta.Orchestration.Hosting;\nusing CodeAlta.Orchestration.Runtime;\n";
    private const string AppFields = "    private CodeAltaSingleInstanceGuard? _lease;\n    private Task<CodeAltaHost>? _hostCreation;\n    private Task? _hostDisposal;\n    private Task? _closeFlow;\n    private bool _nativeConfirmed;\n    private readonly List<Task> _diagnosticWaits = [];\n";
    private const string BootFields = "    private readonly string? _epoch;\n    internal BootService() { }\n    internal BootService(string epoch) { _epoch = epoch; }\n";
    private const string OldBootStatus = "    public BootStatus Status(BootRequest request) => new(\"in-development\", \"CodeAlta\", DesktopCommandLine.Version, false);\n";
    private const string NewBootStatus = "    public BootStatus Status(BootRequest request) => _epoch is null\n        ? new(\"in-development\", \"CodeAlta\", DesktopCommandLine.Version, false)\n        : new(\"owned-text-only\", \"CodeAlta\", DesktopCommandLine.Version, true) { HostEpoch = _epoch };\n";
    private const string BootRecord = "internal sealed record BootStatus(string State, string ProductName, string Version, bool HostAvailable)\n{\n    public string? HostEpoch { get; init; }\n}\n";
    private const string ReceiptMetadata = "[JsonSerializable(typeof(SessionSendRequest))]\n[JsonSerializable(typeof(SessionAbortRequest))]\n[JsonSerializable(typeof(SessionReceiptRequest))]\n[JsonSerializable(typeof(SessionAdmission))]\n[JsonSerializable(typeof(SessionReceiptPage))]\n";
    private const string OwnedWorkspace = """
        internal WorkspaceService(CodeAlta.Orchestration.Runtime.OwnedSessionWorkspace reads)
        {
            ArgumentNullException.ThrowIfNull(reads);
            _readHistory = reads.ReadHistoryPageAsync;
            _read = async token =>
            {
                var actual = reads.ReadSnapshotAsync(token);
                var snapshot = await actual.ConfigureAwait(false);
                return ProjectSnapshot(snapshot.Projects, snapshot.Sessions);
            };
        }

    """ + "\n";
    private const string LegacyDescription = "    <p>Browse persisted workspace metadata from a trusted task-owned <strong>COPY</strong>. Read-only persisted history is available on selection. This is not live session state: sending, resuming and live events are not connected.</p>\n";
    private const string OwnedDescription = "    {status?.hostAvailable\n      ? <p>Explicit owned-host mode: text submission and receipt observation for existing sessions. No live events or completed-run claim. Configured providers may use authentication/storage/network; discovery roots are not a sandbox.</p>\n      : <p>Browse persisted workspace metadata from a trusted task-owned <strong>COPY</strong>. Read-only persisted history is available on selection. This is not live session state: sending, resuming and live events are not connected.</p>}\n";
    private const string LegacyReadNotice = "    <p className=\"detail\">The shared session catalog retains its snapshot; this screen does not refresh or invalidate it. Close and relaunch to reload. Response limits do not bound the underlying scan. Canceling a request or closing this window does not guarantee stopping the catalog/cache load.</p>\n";
    private const string OwnedReadNotice = "    {status?.hostAvailable\n      ? <p className=\"detail\">Owned mode reads the host-shared cached store directly, with eight actual reads maximum. Full scans are not bounded by display limits. Shutdown joins actual reads and commands before dependencies; pending or unconfirmed cleanup keeps the lease.</p>\n      : <p className=\"detail\">The shared session catalog retains its snapshot; this screen does not refresh or invalidate it. Close and relaunch to reload. Response limits do not bound the underlying scan. Canceling a request or closing this window does not guarantee stopping the catalog/cache load.</p>}\n";
    private const string BootCapability = "      .then(value => {\n        if (abort.signal.aborted) return;\n        setStatus(value);\n        setMutation(current => current?.epoch === value.hostEpoch ? current\n          : value.hostEpoch ? { epoch: value.hostEpoch, capability: createMutationCapability(value.hostEpoch) } : undefined);\n      })\n";
    private const string OwnedMount = "          {status?.hostAvailable && status.hostEpoch && mutation?.epoch === status.hostEpoch\n            ? <OwnedSessionPanel key={JSON.stringify([selectedSession.id, status.hostEpoch])} sessionId={selectedSession.id} epoch={status.hostEpoch} drafts={submissions} capability={mutation.capability} />\n            : <History key={selectedSession.id} sessionId={selectedSession.id} />}\n";
    private const string OwnedStyles = ".owned-session textarea { display: block; box-sizing: border-box; width: 100%; min-height: 8rem; font: inherit; margin-block: .5rem; }\n.owned-session button { font: inherit; margin-block: .5rem; }\n";
    private const string OldHistoryRead = "    private static string Read(string path) => SourceTestText.DecodeSource(File.ReadAllBytes(Path.Combine(DesktopArchitectureTests.SourceRoot, path)));\n";
    private const string NewHistoryRead = "    private static string Read(string path) => DesktopOwnedSessionSourceInverse.RestoreInput(path, SourceTestText.DecodeSource(File.ReadAllBytes(Path.Combine(DesktopArchitectureTests.SourceRoot, path))));\n";
    private const string WorkspaceReadAnchor = "        var source = SourceTestText.DecodeSource(File.ReadAllBytes(Path.Combine(DesktopArchitectureTests.SourceRoot, path)));\n";
    private const string WorkspaceReadMap = "        // This project already enters the inherited current-input gateway; never restore it twice.\n        source = path switch\n        {\n            \"CodeAlta.Desktop.Tests/CodeAlta.Desktop.Tests.csproj\" => source,\n            _ => DesktopOwnedSessionSourceInverse.RestoreInput(path, source),\n        };\n";
    private const string OldDiscoveryGateway = "    internal static string RestoreDiscoveryInput(string path, string source)\n        => path is Host or Options or Runtime or Desktop or Profile ? Restore(path, source) : source;\n";
    private const string OldLifetimeGateway = "    internal static string RestoreLifetimeInput(string path, string source)\n        => path is Lifetime ? Restore(path, source) : source;\n";
    private const string CurrentGateway = "    internal static string RestoreCurrentInput(string path, string source)\n        => Restore(path, DesktopOwnedSessionSourceInverse.RestoreInput(path, source));\n\n";
    private const string OwnerReadAnchor = "    private static string Read(string path, [CallerFilePath] string caller = \"\")\n";
    private const string OwnerReadMap = "        => DesktopOwnedSessionSourceInverse.RestoreInput(path, ReadCurrent(path, caller));\n\n    private static string ReadCurrent(string path, [CallerFilePath] string caller = \"\")\n";
    private const string HostCleanup = """
        private async ValueTask DisposeCommandsAndRuntimeAsync()
        {
            await DisposeOwnedWorkAsync(
                () => Commands.DisposeAsync().AsTask(),
                () => WorkspaceReads.DisposeAsync().AsTask(),
                RuntimeService.DisposeAsync).ConfigureAwait(false);
        }

        // Mandatory callback seam for this existing host stage, not a replacement lifetime owner.
        internal static async ValueTask DisposeOwnedWorkAsync(
            Func<Task> disposeCommands, Func<Task> disposeReads, Func<ValueTask> disposeRuntime)
        {
            ArgumentNullException.ThrowIfNull(disposeCommands);
            ArgumentNullException.ThrowIfNull(disposeReads);
            ArgumentNullException.ThrowIfNull(disposeRuntime);
            // Close read admission first, then signal commands; retain both before either await.
            var reads = Start(disposeReads);
            var commands = Start(disposeCommands);
            var failures = new List<Exception>();
            try { await commands.ConfigureAwait(false); } catch (Exception ex) { failures.Add(ex); }
            try { await reads.ConfigureAwait(false); } catch (Exception ex) { failures.Add(ex); }
            try
            {
                var runtime = disposeRuntime();
                await runtime.ConfigureAwait(false);
            }
            catch (Exception ex) { failures.Add(ex); }
            if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
            if (failures.Count > 1) throw new AggregateException(failures);

            static Task Start(Func<Task> operation)
            {
                try { return operation(); }
                catch (Exception ex) { return Task.FromException(ex); }
            }
        }
    """ + "\n";

    private const string OwnedLifecycle = """
        private static int RunOwned(DesktopLaunchOptions options)
        {
            var desktop = new DesktopApplication(options);
            try
            {
                desktop._lease = CodeAltaSingleInstanceGuard.Acquire(Path.Combine(options.CatalogRoot!, "alta.lock"));
                Directory.CreateDirectory(options.DataRoot);
                var result = NeoApplication.Run(new NeoApplicationOptions
                {
                    ApplicationName = "CodeAlta", ShutdownMode = NeoApplicationShutdownMode.Explicit,
                }, desktop.RunOwnedAsync);
                // A native loop ending externally is NOT evidence that host work terminated.
                if (CanReleaseOwnedLease(desktop._hostCreation, desktop._hostDisposal, desktop._nativeConfirmed)) desktop._lease.Dispose();
                else Console.Error.WriteLine("Owned shutdown unconfirmed; the application did not release its lease.");
                return result == 0 ? desktop.ExitCode : result;
            }
            catch (Exception)
            {
                Console.Error.WriteLine("Owned desktop startup or native lifetime failed; termination is not confirmed.");
                return 1;
            }
            finally { GC.KeepAlive(desktop); } // Strong owner/lease lifetime across the synchronous native loop.
        }

        internal static bool CanReleaseOwnedLease(Task? creation, Task? disposal, bool nativeConfirmed)
            => nativeConfirmed && ((creation is null && disposal is null) ||
                (creation?.IsCompletedSuccessfully == true && disposal?.IsCompletedSuccessfully == true));

        private async Task AwaitOwnedAsync(Task task, NeoWindow window)
        {
            var diagnostic = task.WaitAsync(TimeSpan.FromSeconds(5));
            _diagnosticWaits.Add(diagnostic);
            try { await diagnostic; }
            catch (TimeoutException) { window.Title = "CodeAlta — owned work pending; lease retained"; }
            await task; // The timeout above never substitutes for joining the actual work.
        }

        private async ValueTask RunOwnedAsync(NeoApplication application)
        {
            var roots = options.Owned!;
            var closeRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var allowClose = false;
            var shutdownUnconfirmed = false;
            SessionOperationsService? operations = null;
            NeoWindow? window = null;
            IAsyncDisposable? environmentLifetime = null, rpcLifetime = null, viewLifetime = null, bindingLifetime = null;
            var bodyFailed = false;
            try
            {
                window = application.CreateWindow(new NeoWindowOptions
                {
                    Label = "main", Title = "CodeAlta — starting owned text-only host", Width = 1000, Height = 760, IsVisible = false,
                });
                application.MainWindow = window;
                window.Closed += (_, _) => closed.TrySetResult();
                window.CloseRequested += request =>
                {
                    if (!allowClose)
                    {
                        request.Cancel();
                        operations?.CloseAdmission();
                        closeRequested.TrySetResult();
                        if (!shutdownUnconfirmed) window.Title = "CodeAlta — shutdown pending; lease retained";
                    }
                    return ValueTask.CompletedTask; // Never await host cleanup inside the native deadline.
                };
                window.Show();
                var catalog = new CatalogOptions { GlobalRoot = options.CatalogRoot! };
                _hostCreation = CodeAltaHost.CreateAsync(new CodeAltaHostOptions
                {
                    GlobalRoot = options.CatalogRoot, CurrentProjectPath = roots.Project,
                    DiscoveryScope = new SessionDiscoveryScope(roots.Home, roots.Instructions), BuiltInSkillRoot = roots.Builtin,
                    OwnedCommandReceiptCapacity = 256, PluginEnvironment = FrozenDictionary<string, string?>.Empty,
                    StartPlugins = false, OwnsLogging = false, IsHeadless = true,
                    ConfigureModelProviders = registry => ConfiguredModelProviderRegistryBuilder.RegisterConfiguredProviders(
                        registry, new CodeAltaConfigStore(catalog), options.CatalogRoot!),
                }, CancellationToken.None);
                // The retained application flow reacts even while a native acquisition is awaiting.
                // No native callback awaits this work; a close during host creation waits its actual result.
                _closeFlow = CloseOwnedHostWhenRequestedAsync(closeRequested.Task, _hostCreation);
                await AwaitOwnedAsync(_hostCreation, window);
                var host = await _hostCreation;
                if (!closeRequested.Task.IsCompleted)
                {
                    var epoch = Guid.NewGuid().ToString("D");
                    operations = new SessionOperationsService(host.Commands, epoch);
                    var assets = Path.Combine(AppContext.BaseDirectory, "assets");
                    var manifest = NeoAssetManifest.Load(Path.Combine(assets, "neoastra-assets.json"));
                    var creatingEnvironment = application.CreateEnvironmentAsync(new NeoEnvironmentOptions
                    {
                        UserDataRoot = Path.Combine(options.DataRoot, "webview"),
                        CustomSchemes = [NeoCustomScheme.Application("app", new NeoManifestResourceProvider(assets, manifest))],
                    });
                    var environment = await creatingEnvironment;
                    environmentLifetime = environment;
                    if (!closeRequested.Task.IsCompleted)
                    {
                        // Owned-only host-wide inbound UTF-8 framing cap, not a per-method/response limit.
                        var builder = new NeoRpcBuilder(new NeoRpcOptions
                        {
                            ContractHash = NeoRpcGeneratedContract.Hash, Release = true, MaximumFrameBytes = 208 * 1024,
                        });
                        builder.AddBootService(new BootService(epoch));
                        builder.AddWorkspaceService(new WorkspaceService(host.WorkspaceReads));
                        builder.AddSessionOperationsService(operations);
                        var rpc = builder.Build();
                        rpcLifetime = rpc;
                        var creatingView = environment.CreateWebViewAsync(NeoAstraHost.FillWindow(window), new NeoAstraOptions
                        {
                            ViewLabel = "main",
                            BridgePolicy = OperatingSystem.IsLinux() ? NeoBridgePolicy.TrustEntireView : NeoBridgePolicy.TrustedOrigins,
                            BridgeOrigins = OperatingSystem.IsLinux() ? [] : ["app://codealta"],
                        });
                        var view = await creatingView;
                        viewLifetime = view;
                        if (!closeRequested.Task.IsCompleted)
                        {
                            view.NavigationRequested = request => ValueTask.FromResult(new NeoNavigationDecision(
                                IsApplicationDocument(request.Uri) ? NeoDecisionAction.Allow : NeoDecisionAction.Cancel));
                            view.NewWindowRequested = static _ => ValueTask.FromResult(new NeoNewWindowDecision(NeoDecisionAction.Cancel));
                            bindingLifetime = NeoRpcViewBinding.Bind(rpc, view);
                            var navigation = view.NavigateAsync(new Uri("app://codealta/index.html"));
                            await navigation;
                        }
                    }
                }
                await closeRequested.Task;
            }
            catch (Exception)
            {
                bodyFailed = true;
                closeRequested.TrySetResult();
            }
            operations?.CloseAdmission();
            if (_closeFlow is not null)
            {
                try
                {
                    if (window is not null) await AwaitOwnedAsync(_closeFlow, window);
                    else await _closeFlow;
                }
                catch (Exception) { bodyFailed = true; }
            }
            var hostConfirmed = _hostCreation is null ||
                (_hostCreation.IsCompletedSuccessfully && _hostDisposal?.IsCompletedSuccessfully == true);
            if (!hostConfirmed)
            {
                shutdownUnconfirmed = true;
                if (window is not null) window.Title = "CodeAlta — shutdown unconfirmed; native resources and lease retained";
                Console.Error.WriteLine("Owned host termination is unconfirmed. Admission is closed; ordinary close is blocked. External termination is not confirmed cleanup.");
                if (window is not null) await closed.Task;
                GC.KeepAlive(bindingLifetime);
                GC.KeepAlive(viewLifetime);
                GC.KeepAlive(rpcLifetime);
                GC.KeepAlive(environmentLifetime);
                GC.KeepAlive(window);
                return; // No native-resource disposal, lease release or ForceShutdown on this path.
            }
            var nativeFailed = false;
            foreach (var resource in new[] { bindingLifetime, viewLifetime, rpcLifetime, environmentLifetime })
            {
                if (resource is null) continue;
                try
                {
                    var disposal = resource.DisposeAsync();
                    await disposal;
                }
                catch (Exception) { nativeFailed = true; }
            }
            if (nativeFailed)
            {
                shutdownUnconfirmed = true;
                if (window is not null) window.Title = "CodeAlta — native shutdown unconfirmed; lease retained";
                Console.Error.WriteLine("Native resource cleanup failed after host joining; lease release is not confirmed.");
                if (window is not null) await closed.Task;
                GC.KeepAlive(bindingLifetime);
                GC.KeepAlive(viewLifetime);
                GC.KeepAlive(rpcLifetime);
                GC.KeepAlive(environmentLifetime);
                GC.KeepAlive(window);
                return;
            }
            if (window is not null)
            {
                allowClose = true;
                window.Close();
                await closed.Task;
                try { var disposal = window.DisposeAsync(); await disposal; }
                catch (Exception) { Console.Error.WriteLine("Native window disposal failed; lease retained."); return; }
            }
            ExitCode = bodyFailed ? 1 : 0;
            _nativeConfirmed = true;
            application.ForceShutdown(); // Only after confirmed host and native-resource disposal.
        }

        private async Task CloseOwnedHostWhenRequestedAsync(Task closeRequested, Task<CodeAltaHost> creation)
        {
            await closeRequested;
            var host = await creation;
            _hostDisposal = host.DisposeAsync().AsTask();
            await _hostDisposal;
        }

    """ + "\n";
}
