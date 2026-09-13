namespace CodeAlta.Tests;

/// <summary>Literal-only input-delta restoration against the eighteen parent-frozen 74ddbb41 blobs; no reads.</summary>
internal static class OwnedSessionUserInputSourceInverse
{
    private const string Send = "CodeAlta.Agent/AgentSendOptions.cs", Agent = "CodeAlta.Agent/Runtime/AgentSession.cs";
    private const string Permission = "CodeAlta.Orchestration/Runtime/SessionPermissionService.cs", Commands = "CodeAlta.Orchestration/Runtime/OwnedSessionCommandService.cs";
    private const string Runtime = "CodeAlta.Orchestration/Runtime/SessionRuntimeService.cs", Queue = "CodeAlta.Orchestration/Runtime/SessionRuntimeService.OwnedQueue.cs";
    private const string Ask = "CodeAlta.Orchestration/Runtime/OwnedSessionAskExecution.cs", Options = "CodeAlta.Orchestration/Hosting/CodeAltaHostOptions.cs";
    private const string Host = "CodeAlta.Orchestration/Hosting/CodeAltaHost.cs", Cli = "CodeAlta/Desktop/DesktopCommandLine.cs";
    private const string App = "CodeAlta/Desktop/DesktopApplication.cs", Boot = "CodeAlta/Desktop/Rpc/BootRpc.cs", Main = "CodeAlta/frontend/src/main.tsx";
    private const string NotesInverse = "CodeAlta.Tests/OwnedSessionNotesSourceInverse.cs", DesktopInverse = "CodeAlta.Tests/DesktopOwnedSessionSourceInverse.cs";
    private const string Project = "CodeAlta.Desktop.Tests/CodeAlta.Desktop.Tests.csproj";
    private const string Factory = "CodeAlta.Agent/Runtime/Tools/AgentBuiltInToolFactory.cs", ToolOptions = "CodeAlta.Agent/Runtime/Tools/AgentBuiltInToolOptions.cs";
    internal static IReadOnlyList<string> Paths => [Send, Agent, Permission, Commands, Runtime, Queue, Ask, Options, Host, Cli, App, Boot, Main, NotesInverse, DesktopInverse, Project, Factory, ToolOptions];

    // CLI has exactly one special entry in DesktopOwnedSessionSourceInverse, never this generic dispatch.
    internal static string RestoreInput(string path, string source) => path != Cli && Paths.Contains(path) ? Restore(path, source) : source;
    internal static string Restore(string path, string source)
    {
        source = SourceTestText.Canonicalize(source);
        foreach (var (before, after, count) in Edits(path))
        {
            Assert.IsTrue(after.Length > 0, path);
            Assert.AreEqual(count, source.Split(after, StringSplitOptions.None).Length - 1, path + ": " + after);
            source = source.Replace(after, before, StringComparison.Ordinal);
        }
        Assert.AreEqual(Original(path), OwnedSessionAskSourceInverse.GitObjectId(source), path);
        return source;
    }
    internal static string Original(string path) => path switch
    {
        Send => "1e5e79d103ca749697a34311d0b78c173f7853c1", Agent => "775f266f24caf94d166d0414964809a677e47211",
        Permission => "b517b15efdd90df99fb27c56f1accd067b05c274", Commands => "f07b852db27f22039c047e2f6ed547ddf1a0be14",
        Runtime => "9d80fd730f36bfdfc5456cb7b91a9738612505dc", Queue => "1eb7f346269e0109475fbff5eb5653df1993c864",
        Ask => "2eb4a32759acfbb214651ac5c9bcd335649687bc", Options => "4a474f52947399489e72b30b5ad16684b16bc793",
        Host => "0848afce81d87b89d7a52727a0f85adf4df62a05", Cli => "cd3781d6890354460dab73468eca90611411295b",
        App => "91d2c7285d6a2ae6f42829446a9b927082625db4", Boot => "87d0e4ecf407db5f82bf5d6e1cd85d2e3b55b7fd",
        Main => "8370e83738530b52331148e1151433fe2f7bb2f3", NotesInverse => "c91e8ee82aae5f3426444bd413e284917a0da277",
        DesktopInverse => "c88fe3e0af33eec2bc95e53da892a38524b68e81", Project => "d518c24d0ac46cc120824b0970cc92480728671e",
        Factory => "0ba93b51c5b29dc840f574c2d2cad6fdb5897460", ToolOptions => "467959356231f420bce0bc00e6a4df6074fe1b6e",
        _ => throw new ArgumentException("Unlisted source.", nameof(path)),
    };
    private static IEnumerable<(string Before, string After, int Count)> Edits(string path)
    {
        switch (path)
        {
            case Send:
                yield return ("", InputOption + "\n", 1);
                yield return ("", SendActivation + "\n", 1); break;
            case Agent:
                yield return ("    private volatile bool _disposed;\n\n", "    private volatile bool _disposed;\n\n" + BorrowedToolClient + "\n\n", 1);
                yield return ("                Provider = Provider,\n            });\n        return _options.Tools", "                Provider = Provider,\n                HttpClient = BuiltInToolHttpClient,\n            });\n        return _options.Tools", 1);
                yield return ("            var allTools = AppendSendTools(BuildAvailableTools(options.OnPermissionRequest ?? _options.OnPermissionRequest), options.AdditionalTools);\n", "            var allTools = AppendSendTools(BuildAvailableTools(options.OnPermissionRequest ?? _options.OnPermissionRequest,\n                options.OnUserInputRequest ?? _options.OnUserInputRequest, options.EnableUserInputTool), options.AdditionalTools);\n", 1);
                yield return ("    private IReadOnlyList<AgentToolDefinition> BuildAvailableTools(AgentPermissionRequestHandler permissionRequestHandler)\n", "    private IReadOnlyList<AgentToolDefinition> BuildAvailableTools(AgentPermissionRequestHandler permissionRequestHandler,\n        AgentUserInputRequestHandler? userInputRequestHandler, bool enableUserInputTool)\n", 1);
                yield return ("                OnUserInputRequest = _options.OnUserInputRequest,\n", "                OnUserInputRequest = userInputRequestHandler,\n                EnableUserInputTool = enableUserInputTool,\n", 1); break;
            case Factory:
                yield return ("", FactoryActivation + "\n", 1);
                yield return ("                (invocation, cancellationToken) => ApplyPatchAsync(options, invocation, cancellationToken)),\n            // Intentionally not registered yet: the local raw-API host does not currently expose\n            // the structured UI feedback loop needed to pause for request_user_input safely.\n            // Keep the implementation around so the tool can be enabled once the host supports it.\n        ];\n", "                (invocation, cancellationToken) => ApplyPatchAsync(options, invocation, cancellationToken)),\n        ];\n", 1); break;
            case ToolOptions:
                yield return ("", BuiltInActivation + "\n", 1); break;
            case Permission:
                yield return ("public sealed class SessionPermissionService : IAsyncDisposable", "public sealed partial class SessionPermissionService : IAsyncDisposable", 1);
                yield return ("", "        internal bool ReviewCommands { get; init; }\n        internal bool EnableUserInput { get; init; }\n        internal HashSet<PendingUserInput> InputDeliveries { get; } = [];\n", 1);
                yield return ("", "        => CreateOwnedExecutionAsync(operationId, sessionId, token, true, false);\n\n    internal ValueTask<OwnedPermissionExecution?> CreateOwnedExecutionAsync(Guid operationId, string sessionId,\n        CancellationToken token, bool reviewCommands, bool enableUserInput)\n", 1);
                yield return ("            var execution = new OwnedPermissionExecution(this, operationId, sessionId, token);\n", "            var execution = new OwnedPermissionExecution(this, operationId, sessionId, token)\n            { ReviewCommands = reviewCommands, EnableUserInput = enableUserInput };\n", 1);
                yield return ("execution.Deliveries.Count != 0\n", "execution.Deliveries.Count != 0 || execution.InputDeliveries.Count != 0\n", 1);
                yield return ("            if (!CanUse(execution) || cancellationToken.IsCancellationRequested || !Eligible(execution, request)\n                || _ownedDeliveries.Count >= OwnedPendingLimit || execution.Deliveries.Count >= OwnedPendingPerExecutionLimit) return null;\n", "            if (!execution.ReviewCommands || !CanUse(execution) || cancellationToken.IsCancellationRequested || !Eligible(execution, request)\n                || !HasOwnedDeliveryCapacity(execution)) return null;\n", 1);
                yield return ("            return Task.WhenAll(_ownedDeliveries.Select(pending => pending.Delivery!));\n", "            return JoinOwnedDeliveries();\n", 1);
                yield return ("", "        var inputs = execution.InputDeliveries.ToArray();\n        foreach (var pending in inputs) CompleteInput(pending.Snapshot.Handle, null);\n", 1);
                yield return ("        return execution.Closure = Task.WhenAll(deliveries.Select(pending => pending.Delivery!));\n", "        return execution.Closure = Task.WhenAll(deliveries.Select(pending => (Task)pending.Delivery!)\n            .Concat(inputs.Select(pending => (Task)pending.Delivery!)));\n", 1);
                yield return ("                var owned = Task.WhenAll(_ownedDeliveries.Select(pending => pending.Delivery!));\n", "                var owned = JoinOwnedDeliveries();\n", 1);
                yield return ("", "                        foreach (var pending in _inputDeliveries) pending.Execution.InputDeliveries.Remove(pending);\n                        _inputDeliveries.Clear();\n", 1); break;
            case Commands:
                yield return ("", "    private readonly bool _enableUserInput;\n", 1);
                yield return ("bool enableAsks = false)", "bool enableAsks = false, bool enableUserInput = false)", 1);
                yield return ("", "        _enableUserInput = enableUserInput;\n", 1);
                yield return ("                operation.Execution.Token).ConfigureAwait(false);\n", "                operation.Execution.Token, _enableUserInput).ConfigureAwait(false);\n", 1);
                yield return ("if (_reviewPermissions)", "if (_reviewPermissions || _enableUserInput)", 3);
                yield return ("operation.Execution.Token).ConfigureAwait(false);\n                    if (_reviewPermissions && operation.PermissionExecution is null)", "operation.Execution.Token, _reviewPermissions, _enableUserInput).ConfigureAwait(false);\n                    if ((_reviewPermissions || _enableUserInput) && operation.PermissionExecution is null)", 1); break;
            case Runtime:
                yield return ("    private bool _disposed;\n\n", "    private bool _disposed;\n\n" + BorrowedPromptCatalog + "\n\n", 1);
                yield return ("        return new AgentPromptCatalog().ListEffectivePrompts(query)\n            .Any(prompt =>", "        return (PromptCatalog ?? new AgentPromptCatalog()).ListEffectivePrompts(query)\n            .Any(prompt =>", 1);
                yield return ("        var descriptor = new AgentPromptCatalog().ResolvePrompt(query, promptName);\n        if (descriptor is null)", "        var descriptor = (PromptCatalog ?? new AgentPromptCatalog()).ResolvePrompt(query, promptName);\n        if (descriptor is null)", 1);
                yield return ("", "                        OnUserInputRequest = Permissions.CreateOwnedUserInputHandler(permissionExecution),\n                        EnableUserInputTool = permissionExecution.EnableUserInput,\n", 1); break;
            case Queue:
                yield return ("        => AdmitAsync(() => QueueOwnedBodyAsync(request, receipt, reviewPermissions, cancellationToken), CancellationToken.None);\n", QueueOverload + "\n", 1);
                yield return ("    private async Task<OwnedSessionCommandResult> QueueOwnedBodyAsync(OwnedTextQueueRequest request,\n        OwnedSessionCommandReceipt receipt, bool reviewPermissions, CancellationToken cancellationToken)\n", "    private async Task<OwnedSessionCommandResult> QueueOwnedBodyAsync(OwnedTextQueueRequest request,\n        OwnedSessionCommandReceipt receipt, bool reviewPermissions, CancellationToken cancellationToken, bool enableUserInput)\n", 1);
                yield return ("if (reviewPermissions)", "if (reviewPermissions || enableUserInput)", 1);
                yield return ("item.Execution.Token).ConfigureAwait(false);\n                        if (permission is null", "item.Execution.Token, reviewPermissions, enableUserInput).ConfigureAwait(false);\n                        if (permission is null", 1);
                yield return ("                        send = new() { Input = send.Input, OnPermissionRequest = lifecycle.HandleAsync, RunLifecycle = lifecycle };\n", "                        send = new() { Input = send.Input, OnPermissionRequest = lifecycle.HandleAsync, RunLifecycle = lifecycle,\n                            OnUserInputRequest = Permissions.CreateOwnedUserInputHandler(permission), EnableUserInputTool = permission.EnableUserInput };\n", 1); break;
            case Ask:
                yield return ("", "        OnUserInputRequest = options.OnUserInputRequest,\n        EnableUserInputTool = options.EnableUserInputTool,\n", 1); break;
            case Options:
                yield return ("", HostOption + "\n", 1); break;
            case Host:
                yield return ("        bool enableOwnedAsks)\n", "        bool enableOwnedAsks,\n        bool enableOwnedUserInput)\n", 1);
                yield return ("ownedCommandReceiptCapacity, reviewOwnedCommandPermissions, enableOwnedAsks);\n", "ownedCommandReceiptCapacity, reviewOwnedCommandPermissions, enableOwnedAsks, enableOwnedUserInput);\n", 1);
                yield return ("                options.EnableOwnedAsks);\n", "                options.EnableOwnedAsks,\n                options.EnableOwnedUserInput);\n", 1); break;
            case Cli:
                yield return ("", "    internal bool EnableOwnedUserInput { get; init; }\n", 1);
                yield return ("", "            output.WriteLine(\"Owned mode only: --enable-owned-user-input independently enables manual nonsecret provider forms. Not credential entry or command approval; answers may persist in provider tool results/history. Default remains cancelled. Refresh manually; lost decisions cannot be recovered or replayed safely.\");\n", 1);
                yield return ("", "        var enableUserInput = false;\n", 1);
                yield return ("", "                case \"--enable-owned-user-input\" when !enableUserInput: enableUserInput = true; break;\n", 1);
                yield return ("allowOwned || reviewCommands || project", "allowOwned || reviewCommands || enableUserInput || project", 1);
                yield return ("ReviewOwnedCommandPermissions = reviewCommands };", "ReviewOwnedCommandPermissions = reviewCommands, EnableOwnedUserInput = enableUserInput };", 1); break;
            case App:
                yield return ("", "                EnableOwnedUserInput = options.EnableOwnedUserInput,\n", 1);
                yield return ("new BootService(epoch, options.ReviewOwnedCommandPermissions)", "new BootService(epoch, options.ReviewOwnedCommandPermissions, options.EnableOwnedUserInput)", 1);
                yield return ("", "                    builder.AddSessionUserInputService(new SessionUserInputService(host.RuntimeService.Permissions, epoch, options.EnableOwnedUserInput));\n", 1); break;
            case Boot:
                yield return ("", "    private readonly bool _userInput;\n", 1);
                yield return ("", "    internal BootService(string epoch, bool commandReview, bool userInput) : this(epoch, commandReview) { _userInput = userInput; }\n", 1);
                yield return ("OwnedAsksEnabled = true };", "OwnedAsksEnabled = true, OwnedUserInputEnabled = _userInput };", 1);
                yield return ("", "    public bool OwnedUserInputEnabled { get; init; }\n", 1);
                yield return ("", "[JsonSerializable(typeof(UserInputListRequest))]\n[JsonSerializable(typeof(UserInputPage))]\n[JsonSerializable(typeof(UserInputResolveRequest))]\n[JsonSerializable(typeof(UserInputCancelRequest))]\n[JsonSerializable(typeof(UserInputResult))]\n", 1); break;
            case Main:
                yield return ("sessionNotes, type BootStatus", "sessionNotes, sessionUserInput, type BootStatus", 1);
                yield return ("", "import { createUserInputReviewer } from \"./sessionUserInput\";\nimport { UserInputPanel } from \"./UserInputPanel\";\n", 1);
                yield return ("", MainReviewer + "\n", 1);
                yield return ("", MainPanel + "\n", 1); break;
            case NotesInverse:
                yield return ("? Restore(path, source) : source;", "? Restore(path, source) : OwnedSessionUserInputSourceInverse.RestoreInput(path, source);", 1);
                yield return ("", "        source = OwnedSessionUserInputSourceInverse.RestoreInput(path, source);\n", 1); break;
            case DesktopInverse:
                yield return ("", "        if (path == Cli) source = OwnedSessionUserInputSourceInverse.Restore(path, source);\n", 1); break;
            case Project:
                yield return ("", "    <Compile Include=\"../CodeAlta.Tests/OwnedSessionUserInputSourceInverse.cs\" Link=\"OwnedSessionUserInputSourceInverse.cs\" />\n", 1); break;
        }
    }

    private const string FactoryActivation = """
        if (options.EnableUserInputTool && options.OnUserInputRequest is not null)
        {
            tools = [.. tools, new AgentToolDefinition(
                new AgentToolSpec("request_user_input", "Request structured user input.", RequestUserInputSchema),
                (invocation, cancellationToken) => RequestUserInputAsync(options, invocation, cancellationToken))];
        }

""";
    private const string BuiltInActivation = """

    /// <summary>Gets explicit activation of the user-input built-in; defaults to false.</summary>
    /// <remarks>A selected callback is also required. Provider profiles may opt out but cannot activate
    /// this tool without this flag. This grants no command/file permission or lifecycle authority.</remarks>
    public bool EnableUserInputTool { get; init; }
""";
    private const string BorrowedToolClient = """
    /// <summary>Gets the optional borrowed client for this session's built-in tools.</summary>
    /// <remarks>Null preserves factory fallback acquisition. The supplying owner must retain the client
    /// through all original tool invocations and session cleanup; this session never disposes it.</remarks>
    internal HttpClient? BuiltInToolHttpClient { get; init; }
""";
    private const string BorrowedPromptCatalog = """
    /// <summary>Gets the optional borrowed catalog used for agent-prompt metadata lookup.</summary>
    /// <remarks>Null preserves fresh default-catalog construction at each existing lookup site.
    /// The supplying owner retains the catalog and its locator through runtime cleanup.</remarks>
    internal AgentPromptCatalog? PromptCatalog { get; init; }
""";
    private const string SendActivation = """

    /// <summary>Gets explicit activation of the user-input built-in for this send; defaults to false.</summary>
    /// <remarks>Requires a selected callback and respects provider profile opt-out. Neither callback presence
    /// nor a profile override activates the tool. Other providers must explicitly support this option.</remarks>
    public bool EnableUserInputTool { get; init; }
""";
    private const string InputOption = """

    /// <summary>Gets the optional user-input callback selected when constructing this send's built-in tools.</summary>
    /// <remarks>Null falls back to the session callback. Retained tools retain this selection; custom
    /// tools are unchanged. Other providers must explicitly support this option and the run lifecycle.
    /// Selection alone confers no authority, cancellation or recovery guarantee.</remarks>
    public AgentUserInputRequestHandler? OnUserInputRequest { get; init; }
""";
    private const string HostOption = """

    /// <summary>Gets explicit opt-in to bounded nonsecret owned provider input. Default false, independent of asks and command review.</summary>
    /// <remarks>Requires per-send callbacks and actual Started binding. Session defaults still cancel.
    /// Answers may enter provider tool results and persisted history; this is not credential entry or command permission.</remarks>
    public bool EnableOwnedUserInput { get; init; }
""";
    private const string QueueOverload = """
        => QueueOwnedCommandAsync(request, receipt, reviewPermissions, cancellationToken, false);

    internal Task<OwnedSessionCommandResult> QueueOwnedCommandAsync(OwnedTextQueueRequest request,
        OwnedSessionCommandReceipt receipt, bool reviewPermissions, CancellationToken cancellationToken, bool enableUserInput)
        => AdmitAsync(() => QueueOwnedBodyAsync(request, receipt, reviewPermissions, cancellationToken, enableUserInput), CancellationToken.None);
""";
    private const string MainReviewer = """
  const [inputReviewer] = useState(() => createUserInputReviewer(
    request => sessionUserInput.list(request, { timeoutMilliseconds: 8000 }),
    request => sessionUserInput.resolve({ ...request, answers: request.answers.map(answer => ({ ...answer })) }, { timeoutMilliseconds: 8000 }),
    request => sessionUserInput.cancel(request, { timeoutMilliseconds: 8000 })));
""";
    private const string MainPanel = """
          {status?.ownedUserInputEnabled && status.hostEpoch && mutation?.epoch === status.hostEpoch && <UserInputPanel
            key={JSON.stringify([selectedSession.id, status.hostEpoch, "input"])} epoch={status.hostEpoch} sessionId={selectedSession.id}
            reviewer={inputReviewer} capability={mutation.capability} />}
""";
}
