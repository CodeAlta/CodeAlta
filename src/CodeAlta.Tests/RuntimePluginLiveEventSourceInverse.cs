using System.Security.Cryptography;
using System.Text;

namespace CodeAlta.Tests;

/// <summary>Pure restoration of complete live-cutover inputs to the parent-verified 86102586 blobs.</summary>
/// <remarks>No checkout, artifact, Git, provider or runtime access. Both sides of every restoration
/// are whole-file authenticated; section boundaries cannot hide unexpected edits in removed code.</remarks>
internal static class RuntimePluginLiveEventSourceInverse
{
    internal const string Runtime = "CodeAlta.Orchestration/Runtime/SessionRuntimeService.cs";
    internal const string Host = "CodeAlta.Orchestration/Hosting/CodeAltaHost.cs";
    internal const string Options = "CodeAlta.Orchestration/Hosting/CodeAltaHostOptions.cs";
    internal const string Owned = "CodeAlta.Tui/App/CodeAltaOwnedServices.cs";
    internal const string Coordinator = "CodeAlta.Tui/App/SessionRuntimeEventCoordinator.cs";
    internal const string CoordinatorTests = "CodeAlta.Tests/SessionRuntimeEventCoordinatorTests.cs";
    internal const string Architecture = "CodeAlta.Tests/ArchitectureGuardrailTests.cs";
    internal const string CacheTests = "CodeAlta.Tests/RuntimeFileSearchInvalidationSourceTests.cs";
    internal const string ForwardingTests = "CodeAlta.Tests/RuntimeEventForwardingSourceTests.cs";
    internal const string OwnershipInverse = "CodeAlta.Tests/PluginAgentEventOwnershipSourceInverse.cs";
    internal const string CacheInverse = "CodeAlta.Tests/RuntimeFileSearchInvalidationSourceInverse.cs";
    internal const string DesktopProject = "CodeAlta.Desktop.Tests/CodeAlta.Desktop.Tests.csproj";
    internal const string Commands = "CodeAlta.Orchestration/Runtime/OwnedSessionCommandService.cs";
    internal const string Forwarding = "CodeAlta.Orchestration/Runtime/OwnedProviderEventForwarding.cs";
    internal const string Permissions = "CodeAlta.Orchestration/Runtime/SessionPermissionService.cs";
    internal const string UserInput = "CodeAlta.Orchestration/Runtime/SessionPermissionService.OwnedUserInput.cs";
    internal const string Queue = "CodeAlta.Orchestration/Runtime/SessionRuntimeService.OwnedQueue.cs";
    internal const string Program = "CodeAlta.Tui/Program.cs";
    internal const string WorkspaceTests = "CodeAlta.Orchestration.Tests/OwnedSessionWorkspaceTests.cs";
    internal const string Agent = "CodeAlta.Agent/Runtime/AgentSession.cs";
    internal const string Hub = "CodeAlta.Orchestration/Runtime/AgentHub.cs";
    internal const string Ask = "CodeAlta.Orchestration/Runtime/OwnedSessionAskExecution.cs";
    internal const string Deferred = "CodeAlta.Tui/Views/DeferredCodeAltaApp.cs";
    internal const string Guard = "CodeAlta.Hosting/CodeAltaSingleInstanceGuard.cs";
    internal const string StartupTests = "CodeAlta.Tests/CodeAltaStartupAdmissionSourceTests.cs";

    internal static IReadOnlyList<string> Paths => [Runtime, Host, Options, Owned, Coordinator, CoordinatorTests,
        Architecture, CacheTests, ForwardingTests, OwnershipInverse, CacheInverse, DesktopProject, Commands,
        Forwarding, Permissions, UserInput, Queue, Program, WorkspaceTests, Agent, Hub, Ask, Deferred, Guard, StartupTests];

    internal static string RestoreInput(string path, string source)
        => Paths.Contains(path, StringComparer.Ordinal) ? Restore(path, source) : source;

    // Host/Desktop already enter the ownership inverse from the cache chain; do not restore twice.
    internal static string RestoreCacheBodyInput(string path, string source)
        => path is Runtime or Coordinator or CoordinatorTests or Architecture ? Restore(path, source) : source;

    internal static string RestoreLifetimeInput(string path, string source)
        => path is Program or Guard or StartupTests or CacheTests or ForwardingTests or OwnershipInverse or WorkspaceTests
            or Hub or Forwarding or UserInput or Deferred ? Restore(path, source) : source;

    internal static string Restore(string path, string source)
    {
        source = SourceTestText.Canonicalize(source);
        Assert.IsTrue(source.EndsWith('\n'), path + ": final LF");
        var encoding = new UTF8Encoding(false, true);
        var bytes = encoding.GetBytes(source);
        var identity = CompleteIdentity(path);
        Assert.AreEqual(identity.CurrentBytes, bytes.Length, path + ": current byte count");
        Assert.AreEqual(CurrentSha256(path), Convert.ToHexString(SHA256.HashData(bytes)), path + ": current input");
        source = RestoreBody(path, source);
        bytes = encoding.GetBytes(source);
        Assert.AreEqual(identity.OriginalBytes, bytes.Length, path + ": original byte count");
        Assert.AreEqual(identity.OriginalSha256, Convert.ToHexString(SHA256.HashData(bytes)), path + ": complete original SHA256");
        Assert.AreEqual(Original(path), OwnedSessionAskSourceInverse.GitObjectId(source), path + ": complete original");
        return source;
    }

    private static (int CurrentBytes, int OriginalBytes, string OriginalSha256) CompleteIdentity(string path) => path switch
    {
        Runtime => (183882, 176513, "6C5DBD9E2D9B6002B40478E9DEF756ED500549EC498FD572EBE4AC06BFD3D0C1"),
        Host => (35893, 28025, "0CF89E0B0EFE3BCB5094CAA729C9975AC73ABE0462B5D756284830D665C31936"),
        Options => (6646, 6066, "B879CAF219546FD698D9A5A50E7F30A35B310D5E64E199732B48ED6586C0D922"),
        Owned => (20347, 17664, "3EFF650DAA261CB839D89CA7AF64AA6E0E0664425615B1EDE8EA5CA83CD7606D"),
        Coordinator => (25943, 26101, "A538779B3543D00C8A6E4CDC208B19F273B520162EE711F0717F9404E1764CFE"),
        CoordinatorTests => (47726, 47550, "F9D6B72DCA8CCBE3B827DEA93F1589327DEBBFBDBDB25A9CCF7FC66B7B47B27F"),
        Architecture => (145464, 145464, "D0C4A1A3720D0DF35AC07C3FDE470D885F85E485F3EB5B9C11FFA1310FE3438C"),
        CacheTests => (12353, 10848, "03FF76AA0C4421840EF06A06AAF4B6C0D45A47283DB8CB066CA1BA2CBAC38580"),
        ForwardingTests => (13819, 11641, "1D3C1217DFADA854BB3F75D16ADF5F17F04C7BE5A32952810EAAB5542D6A134C"),
        OwnershipInverse => (31818, 31154, "004ACD54FA61FBFA2E424713BAB5A496CAD4440A1A4853FCD5B5C72D46079234"),
        CacheInverse => (21336, 21190, "CC68F2F4B49788F0D39B13F9FACD1E866C85EED2E84786F3CAEAF39400B152A7"),
        DesktopProject => (1340, 1211, "0EF69EDDC5183FA6E306022A91F69012050F40ACAF719A62C8D121CCAE07E125"),
        Commands => (55966, 46849, "B186098FEE8E880CFB687B13DE1D4315D2ED481BF2A1500D9B301C1A99FE07A2"),
        Forwarding => (24453, 16221, "C21BAB59EF9C568383312E50B8BA70D18CA9350503B385F305003446E365A0BD"),
        Permissions => (41263, 33384, "98711908E3EC2AC8107BB3BF0539CFC1F7A54B9B780364687E871FA2C137BC8D"),
        UserInput => (8271, 8507, "98626EB162CDDAB3878CF2B3B64AFE9E94F8048A98926755791C1C7C4951893C"),
        Queue => (20833, 16100, "B8C6E925528D69FA26B06B844D00D542C9FB1E3BFEAC7648217DD4FE3E6F0FC9"),
        Program => (38041, 22780, "4A216D88432D1ABA34C75E076CE46468A2D45339D0067749DA0544089F5C5734"),
        WorkspaceTests => (12224, 12032, "011FF1DF46F2A592DFFDFBFC9387CB161B802954429D015FF536D1E9A519C657"),
        Agent => (159354, 149536, "1FF9CCB696D9DC5F1F4307183E5F8A2AB43D79B9E2D3DD7D3D11E79D63CE9EB9"),
        Hub => (43829, 33283, "CCC8B08E74E5D37694297D7F3B4C43A830877124ED6F6291473857109CF1E770"),
        Ask => (9512, 7350, "37FFBDE32CFB98F1D75CEE5A255A63152CF8359A7613240E6125A871B680F27E"),
        Deferred => (25334, 19850, "11B1EA99DBB87F5205FBFC47F25ACF8396C16BBF91EE530DAC89405B9183B9CA"),
        Guard => (15812, 9592, "BD1D471CD8D9806BDFE748E65BD789CB28A4CF15E333C02A95E4779B9A80C640"),
        StartupTests => (28829, 28733, "25F1637498D576F76BB734BF2C6BCD3B3BBBD1D1D13C07ADFE7B2B07285A77A3"),
        _ => throw new ArgumentException("Unlisted live-cutover input.", nameof(path)),
    };

    internal static string Original(string path) => path switch
    {
        Runtime => "92226b75d85c0f54f849208c7570a75314862da0", Host => "1ba13ebabf7afa427733ce1ee30bfdfb518c2ae5",
        Options => "b7a2c1c431136ce2d761064c6d524f76f548ee77", Owned => "95a483962397d4d46173282ca3cb7c82e0b29065",
        Coordinator => "d8cfa2c0e8202397fa7214da53d75d53f121c8f5", CoordinatorTests => "64c694e630ed97c5b3f3592798ecf13a957757c4",
        Architecture => "1771f48afbbdd30b4b19aec71184a8c02b4426da", CacheTests => "75f4304e339efe55c062a1df422a8ccf1c67306a",
        ForwardingTests => "7eb6724029517bf7072c404ecb834e006a98338f", OwnershipInverse => "97c9fb673f4484eded14f6a903028747db8dbd3b",
        CacheInverse => "d63cbb0d05994ff9f015ce4eb6796290f3012e59", DesktopProject => "77de775d5f07a1a9d8386926c3dfb299e71531b0",
        Commands => "e8327edbf37c5917a41a5a8dadf47454481a97c9", Forwarding => "9f24733c8eb1f9a7cc507682d52fc35599d1e2d0",
        Permissions => "62c7d8d25ee052e0eef98b3b66ffd718ee98e1a4", UserInput => "6628b1e5abbc50ea4b23d17e0d4271f9dcbcd321",
        Queue => "094e8c0e8613079006ecffefa3f35aa54eb2ca11", Program => "c32442b089461f42c7f14ba99b67e67afe8985ae",
        WorkspaceTests => "61c647c2f1f3363ef0a5b0f3a65ff5b7852893b3", Agent => "05b8e7bb3d2f5b28928c4a4f8e91d8a256896290",
        Hub => "51e192026330b3391dd0b9a916d2d5e2ff07f6e3", Ask => "4b91f59f02bcb1b17dee953b0e50259d51388c84",
        Deferred => "afe99c88c08bd51dbdc14dbcdd283e19279c1829", Guard => "c343f2fafa9c93ed9fac819922fd89581d307bce",
        StartupTests => "ed3413868d6560ad2ee38c19b40ea2f457a7e330",
        _ => throw new ArgumentException("Unlisted live-cutover input.", nameof(path)),
    };

    // Populated only from parent-read, normalized final source bytes, never from runtime artifacts.
    private static string CurrentSha256(string path) => path switch
    {
        Runtime => "EA1672FD784E93D977318B2E682976FE7B7849E2F70FFEE2CEC89E8D6207DAC3",
        Host => "E7321FDE77B6F0C0A15E79FA55C0D407F57A1632BCA4EFAEE86C5EFD6177592B",
        Options => "A395267817EA801223C37D185972B6E1BF25B9B58C5ECC4DB5DA0C9AA42A5BE2",
        Owned => "97CBD4FDBFFC9EDF1C45B4B03CC0673B9E42808DAB4AF5A160B99C06B645CDBC",
        Coordinator => "5B953D838069777B7E0D405EB06F2AB4BADD68132C908803CFCD61E9FFAEA076",
        CoordinatorTests => "A47E6044DEBBC7247D3C6BC8110079B9835EA3D8D14C63EACF17D47DB36A503E",
        Architecture => "670FEB5CC5E68ED7E96343A1DBA3B63DEE6D9030387AEAA5744FEE74E14B72E0",
        CacheTests => "C3FBA177EA6E5A25AB94B12C475C38EF750B594E4FF3DCBF59E940C904DDC51E",
        ForwardingTests => "C55BA00CB9C691A81AB48EC079CB9C946704C0DCBDB6F69E2CE4192FCA5FEC65",
        OwnershipInverse => "F1B720C02F5F913F3AE985A7DCC86AAAD10A2649E3EE4671792F50E074AB9903",
        CacheInverse => "790A9F1A511355D8410A3BF75348FD349553785FB10D502DC86861CBC50FF89A",
        DesktopProject => "83D4A5311AD2D722276717766E9C3CC0E23C1F4C461429FBDD8DF2A42AB4B2FB",
        Commands => "BCAA8DC75E652A7A2C1D230767A91A2868F4CDD8D1BFBBF947595124BFDC5212",
        Forwarding => "3032D320E1909A527792932A4D20AE6CBDC0FAEF54F489E0212F84F58BF6D59B",
        Permissions => "0F17E46374FBCAA2A0813981FCE2E7467D7B485550C61AC56323217815FE89B1",
        UserInput => "A6DCEC95B573256756F4DE23BF2BE9C501CD120935A7776AB957A5D7060A8FF6",
        Queue => "FF6BD2A6F595D1A8E6FCB55D954DEC419AEDF01DFAC8FB198FDF164D2458FE14",
        Program => "63858E4827A840DD4D46C7E13FEFE8C5523A58729F31D80C93840B7F90963713",
        WorkspaceTests => "E4D146ADB981D26D9C4C93361915579F04FBB6846C3F0ABC6367EBD0CFA6FE80",
        Agent => "0E832F315827D0ABEFFDDF24D019E117611DA2408F590F22304803BD438A1202",
        Hub => "AEFC2115BA240B7F1C26F1C511371A70239D8B284FBD5BCF6AC78CDF75ADAA98",
        Ask => "8630F1073F5EE50809C00344F1A9D541772686D709474564914909A37D613FEC",
        Deferred => "D68F8C268CCEBDB4591648496DDBAC387AAC44A8F531FECB177EEE7074324D64",
        Guard => "0FFB05C88869DC9FEA5DB20C19B291EB6BFC3008B2FD6E6C92CEB8057775A92A",
        StartupTests => "3FA7D4DFE99C3DCBE94AC24291F3B725D3729A0B16F1F48426DAAF999514E878",
        _ => throw new ArgumentException("Unlisted live-cutover input.", nameof(path)),
    };

    private static string RestoreBody(string path, string source)
    {
        switch (path)
        {
            case Architecture:
                return Replace(source, "App/SessionRuntimeEventCoordinator.cs:275:Task.Run(async () =>",
                    "App/SessionRuntimeEventCoordinator.cs:281:Task.Run(async () =>");
            case DesktopProject:
                return Replace(source, "    <Compile Include=\"../CodeAlta.Tests/RuntimePluginLiveEventSourceInverse.cs\" Link=\"RuntimePluginLiveEventSourceInverse.cs\" />\n", "");
            case StartupTests:
                return Replace(source,
                    "        var guard = RuntimePluginLiveEventSourceInverse.Restore(\"CodeAlta.Hosting/CodeAltaSingleInstanceGuard.cs\", ReadSource(\"CodeAlta.Hosting/CodeAltaSingleInstanceGuard.cs\"));\n",
                    "        var guard = ReadSource(\"CodeAlta.Hosting/CodeAltaSingleInstanceGuard.cs\");\n");
            case CoordinatorTests:
                source = Replace(source, "ApplyRuntimeEvent_DoesNotObserveAgainButDirectAgentEventStillObserves", "ApplyRuntimeEvent_ForwardsAgentEventsToPluginObserver");
                return Replace(source, "        Assert.IsNull(observer.ObservedSession);\n        Assert.IsNull(observer.ObservedEvent);\n        coordinator.HandleAgentEvent(session, tab, agentEvent);\n", "");
            case Coordinator:
                return Replace(source, "        ApplyReduction(tab, reduction);\n    }\n\n    public void HandleAgentEvent(",
                    "        if (runtimeEvent is SessionAgentEvent agentRuntimeEvent)\n        {\n\n            ObservePluginAgentEvent(session, agentRuntimeEvent.Event);\n        }\n\n        ApplyReduction(tab, reduction);\n    }\n\n    public void HandleAgentEvent(");
            case Options:
                source = Replace(source, "using CodeAlta.Orchestration.Runtime.Plugins;\n", "");
                return Section(source, "\n\n    /// <summary>Gets the optional failure policy for backend-owned live plugin observation.</summary>", "\n}", "");
            case CacheInverse:
                source = Replace(source, ": RuntimePluginLiveEventSourceInverse.RestoreInput(path, source);", ": source;");
                return Replace(source, "        source = RuntimePluginLiveEventSourceInverse.RestoreCacheBodyInput(path, source);\n", "");
            case OwnershipInverse:
                source = Replace(source, "        => path is Owned ? Restore(path, source)\n            : path == RuntimePluginLiveEventSourceInverse.Program ? RuntimePluginLiveEventSourceInverse.Restore(path, source) : source;",
                    "        => path is Owned ? Restore(path, source) : source;");
                source = Replace(source, "? Restore(path, source)\n            : path == RuntimePluginLiveEventSourceInverse.Deferred ? RuntimePluginLiveEventSourceInverse.Restore(path, source) : source;",
                    "? Restore(path, source) : source;", 3);
                source = Replace(source, "? Restore(path, source)\n            : RuntimePluginLiveEventSourceInverse.RestoreLifetimeInput(path, source);", "? Restore(path, source) : source;");
                return Replace(source, "        source = RuntimePluginLiveEventSourceInverse.RestoreInput(path, source);\n", "");
            case WorkspaceTests:
                source = Replace(source, ",\n            _ => ValueTask.CompletedTask, () => Task.CompletedTask).AsTask()", ").AsTask()", 2);
                return Replace(source, "}, _ => ValueTask.CompletedTask, () => Task.CompletedTask).AsTask()", "}).AsTask()");
            case ForwardingTests:
                source = Replace(source, "EnsureCoordinatorSessionCoreAsync(session, options, actorCancellationToken, ownedCommand, failureCapture)", "EnsureCoordinatorSessionCoreAsync(session, options, actorCancellationToken)");
                source = Replace(source, "=> _agentHub.RunAsync(sessionHandleId, sendOptions, cancellationToken);", "var runId = await _agentHub.RunAsync(sessionHandleId, sendOptions, cancellationToken).ConfigureAwait(false);");
                source = Replace(source, "private Task<AgentRunId> RunCapturedAsync(", "private async Task<AgentRunId> RunCapturedAsync(");
                source = Section(source, "        StringAssert.Contains(post, \"}).AsTask(), projectionUse.Dispose,", "        var drain = Between(runtime,", """
        Before(post, "projectionUse.Dispose, ObserveLivePluginEventAsync", "await DeliverParentNotificationAsync(");
        Before(post, "projectionUse.Dispose, ObserveLivePluginEventAsync", "await TryDrainNextQueuedPromptAsync(");
""" + "\n");
                source = Replace(source, "projectionUse.Dispose, ObserveLivePluginEventAsync", "projectionUse.Dispose();", 2);
                source = Replace(source, "_active.Add(ordinal, (work, ObserveOwnedAsync(ordinal, work, attachment), receipt));", "_active.Add(ordinal, (work, ObserveOwnedAsync(ordinal, work)));");
                source = Replace(source, "if (!unsubscribeStage.Succeeded", "if (!unsubscribe.IsCompletedSuccessfully)");
                source = Replace(source, "throw new AggregateException(failures);", "throw new AggregateException(new[] { cancellation, abort, unsubscribe }");
                return Replace(source, "new AgentDependencyRetentionException(\\\"provider forwarding\\\", stage, failures, new { Owner = this, Dependencies = dependencies })", "failure.Data[\\\"RetainedForwardingOwner\\\"] = this;");
            case CacheTests:
                source = Section(source, "        Once(runtime, \"var effectWorkingDirectory", "        var effect = Read(", """
        Once(runtime, "ArgumentNullException.ThrowIfNull(session);\n        ArgumentNullException.ThrowIfNull(@event);\n        var effectWorkingDirectory = session.WorkingDirectory;\n        await AdmitAsync(() => AppendSessionEventOwnedBodyAsync(session, @event, effectWorkingDirectory, cancellationToken)");
        Once(runtime, "await _agentSessionCatalog.InvalidateAsync(session.SessionId, cancellationToken).ConfigureAwait(false);\n        _events.TryPublish(new SessionAgentEvent(session.SessionId, @event));\n        await InvalidateFileSearchCacheAsync(@event, effectWorkingDirectory).ConfigureAwait(false);");
""" + "\n");
                source = Section(source, "        Once(runtime, \"await publication.CompleteAsync", "        var effect = Read(", """
        Once(runtime, "var projection = await actor.QueryAsync(_ =>");
        Once(runtime, "return ValueTask.FromResult((Event: sanitized, WorkingDirectory: projector.Entry.WorkingDirectory, Notifications: notifications));");
        Once(runtime, "projectionUse.Dispose();\n            await InvalidateFileSearchCacheAsync(projection.Event, projection.WorkingDirectory).ConfigureAwait(false);\n            foreach (var notification in projection.Notifications)");
        Once(runtime, "var sanitized = projector.Project(@event);");
        Once(runtime, "await DeliverParentNotificationAsync(notification).ConfigureAwait(false);");
        Once(runtime, "if (IsQueueDrainTrigger(@event))");
""" + "\n");
                return Replace(source, "Assert.IsFalse(coordinator.Contains(\"ObservePluginAgentEvent(session, agentRuntimeEvent.Event);\", StringComparison.Ordinal));", "Once(coordinator, \"ObservePluginAgentEvent(session, agentRuntimeEvent.Event);\");");
            case Owned:
                return RestoreOwned(source);
            case Ask:
                return RestoreAsk(source);
            case Deferred:
                return RestoreDeferred(source);
            case Hub:
                return RestoreHub(source);
            case Host:
                return RestoreHost(source);
            case Agent:
                return RestoreAgent(source);
            case Program:
                return RestoreProgram(source);
            case Forwarding:
                return RestoreForwarding(source);
            case Queue:
                return RestoreQueue(source);
            case Permissions:
                return RestorePermissions(source);
            case Runtime:
                return RestoreRuntime(source);
            case Commands:
                return RestoreCommands(source);
            case Guard:
                source = Section(source, "    /// <summary>Acquires the default guard while publishing", "    /// <summary>Acquires the existing guard at an explicitly", "");
                source = Section(source, "    public static CodeAltaSingleInstanceGuard Acquire(string lockFilePath)\n", "    /// <summary>Releases this guard", """
    public static CodeAltaSingleInstanceGuard Acquire(string lockFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockFilePath);

        var fullLockFilePath = Path.GetFullPath(lockFilePath);
        var directory = Path.GetDirectoryName(fullLockFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var lockStream = CreateLockFile(fullLockFilePath);

        try
        {
            WriteCurrentProcessId(lockStream);
            return new CodeAltaSingleInstanceGuard(lockStream, fullLockFilePath);
        }
        catch
        {
            lockStream.Dispose();
            throw;
        }
    }

""" + "\n");
                return Section(source, "    private static void WriteCurrentProcessId(", "    private static FileStream CreateLockFile(", """
    private static void WriteCurrentProcessId(FileStream lockStream)
    {
        lockStream.SetLength(0);
        var processId = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        using var writer = new StreamWriter(lockStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true);
        writer.WriteLine(processId);
        writer.Flush();
        lockStream.Flush(flushToDisk: true);
        lockStream.Position = 0;
    }

""" + "\n");
            case UserInput:
                source = Replace(source, "private Task JoinOwnedDeliveries() => JoinDeliveryOriginalsAsync(", "private Task JoinOwnedDeliveries() => Task.WhenAll(");
                source = Replace(source, "        internal OwnedDeliveryLifetime? Lifetime { get; set; }\n", "");
                return Section(source, "        pending.Lifetime = new OwnedDeliveryLifetime(pending);", "    private bool LiveInput(", """
        try
        {
            using var linked = new CancellationTokenSource();
            await using var operation = operationToken.UnsafeRegister(static s => ((CancellationTokenSource)s!).Cancel(), linked);
            await using var attachment = attachmentToken.UnsafeRegister(static s => ((CancellationTokenSource)s!).Cancel(), linked);
            await using var run = runToken.UnsafeRegister(static s => ((CancellationTokenSource)s!).Cancel(), linked);
            await using var request = pending.Token.UnsafeRegister(static s => ((CancellationTokenSource)s!).Cancel(), linked);
            try { return await pending.Completion.Task.WaitAsync(linked.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                await ExecuteAsync(() => CompleteInput(pending.Snapshot.Handle, null), false).ConfigureAwait(false);
                return await pending.Completion.Task.ConfigureAwait(false);
            }
        }
        catch
        {
            await ExecuteAsync(() => CompleteInput(pending.Snapshot.Handle, null), false).ConfigureAwait(false);
            throw;
        }
        finally
        {
            await ExecuteAsync(() => { pending.Execution.InputDeliveries.Remove(pending); _inputDeliveries.Remove(pending); return true; }, false).ConfigureAwait(false);
        }
    }

""" + "\n");
            default:
                throw new ArgumentException("Unlisted live-cutover input.", nameof(path));
        }
    }

    private static string RestoreRuntime(string source)
    {
        source = Section(source, "    private async Task AbortOwnedBodyAsync(", "    private static string? BuildParentNotificationGuidance(", """
    private async Task AbortOwnedBodyAsync(string sessionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (_disposed)
        {
            return;
        }

        SessionActorCommandResult result;
        RuntimeSessionEntry? capturedEntry = null;
        OwnedProviderEventForwarding.Use? handleUse = null;
        try
        {
            var actor = GetActorForWork(sessionId);
            result = await actor.ExecuteReservedAsync(
                    async actorCancellationToken =>
                    {
                        var entry = await GetEntryAsync(sessionId, actorCancellationToken).ConfigureAwait(false);
                        handleUse = entry.Attachment.TryAcquireHandleUse()
                            ?? throw new InvalidOperationException("The coordinator attachment is retiring.");
                        capturedEntry = entry;
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (result.Succeeded)
            {
                await Permissions.InvalidateOwnedAttachmentAsync(capturedEntry!.Attachment).ConfigureAwait(false);
                using var execution = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, capturedEntry!.Attachment.Cancellation.Token);
                await _agentHub.AbortAsync(capturedEntry.SessionHandleId, execution.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (_disposed && ex is ObjectDisposedException or ChannelClosedException)
        {
            return;
        }
        finally { handleUse?.Dispose(); }

        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                result.Message ?? $"Failed to abort session '{sessionId}'.",
                result.Exception);
        }
    }

""" + "\n");
        source = Section(source, "        var effectWorkingDirectory = session.WorkingDirectory;", "    /// <summary>\n    /// Gets the approximate number", """
        var effectWorkingDirectory = session.WorkingDirectory;
        await AdmitAsync(() => AppendSessionEventOwnedBodyAsync(session, @event, effectWorkingDirectory, cancellationToken), cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task AppendSessionEventOwnedBodyAsync(SessionViewDescriptor session, AgentEvent @event, string effectWorkingDirectory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(@event);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(session.SessionId, @event.SessionId, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The event session id must match the target session.", nameof(@event));
        }

        await _sessionViewCatalog.JournalStore.EnsureHeaderAsync(session, cancellationToken).ConfigureAwait(false);
        var store = _sessionViewCatalog.JournalStore.CreateSessionStore();
        await store.AppendEventsAsync(
                session.ProviderId,
                session.ResolvedProviderKey,
                session.SessionId,
                [@event],
                cancellationToken)
            .ConfigureAwait(false);
        await _agentSessionCatalog.InvalidateAsync(session.SessionId, cancellationToken).ConfigureAwait(false);
        _events.TryPublish(new SessionAgentEvent(session.SessionId, @event));
        await InvalidateFileSearchCacheAsync(@event, effectWorkingDirectory).ConfigureAwait(false);
    }

""" + "\n");
        source = Section(source, "        var notes = new AgentNotesEvent(session.ProviderId", "    /// <summary>\n    /// Gets an active session descriptor", """
        var notes = new AgentNotesEvent(session.ProviderId, session.SessionId, DateTimeOffset.UtcNow, null, kind, markdown);
        await _sessionViewCatalog.JournalStore.CreateSessionStore().AppendNotesAsync(notes, async () =>
        {
            await _agentSessionCatalog.InvalidateAsync(session.SessionId, CancellationToken.None).ConfigureAwait(false);
            _events.TryPublish(new SessionAgentEvent(session.SessionId, notes));
            committed(notes);
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(string SessionId, ModelProviderId ProviderId)> ResolveNotesSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (_entries.TryGetValue(sessionId, out var entry) && !entry.IsTerminated)
        {
            return (entry.SessionId, !string.IsNullOrWhiteSpace(entry.ProviderId.Value)
                ? entry.ProviderId
                : new ModelProviderId(entry.ProviderKey));
        }

        var metadata = await _sessionViewCatalog.JournalStore.CreateSessionStore()
            .GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var cwd = metadata?.Context?.Cwd ?? metadata?.WorkspacePath;
        if (metadata is null || string.IsNullOrWhiteSpace(metadata.ProviderKey) || string.IsNullOrWhiteSpace(cwd))
        {
            throw new SessionNotesSessionNotFoundException(sessionId);
        }

        // Same rooted project/global identity as recoverable discovery, without prompt
        // discovery, provider initialization, or trusting a caller-supplied descriptor.
        var normalizedCwd = NormalizePath(cwd);
        if (!string.Equals(normalizedCwd, NormalizePath(_catalogOptions.GlobalRoot), StringComparison.OrdinalIgnoreCase))
        {
            var projects = await _projectCatalog.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (!projects.Any(project => string.Equals(NormalizePath(project.ProjectPath), normalizedCwd, StringComparison.OrdinalIgnoreCase)))
            {
                throw new SessionNotesSessionNotFoundException(sessionId);
            }
        }

        return (metadata.SessionId, new ModelProviderId(metadata.ProviderKey.Trim()));
    }

""" + "\n");
        source = Replace(source, "        var failureCapture = new RuntimeFailureCapture();\n        try", "        try", 2);
        source = Replace(source, "EnsureCoordinatorSessionWithFailureCaptureAsync(session, options, cancellationToken, failureCapture)", "EnsureCoordinatorSessionAsync(session, options, cancellationToken)", 2);
        source = Replace(source, "            await ObserveRuntimeFailureAsync(session, failureCapture.Original ?? ex, ex).ConfigureAwait(false);", "            PublishRuntimeFailureEvent(session, ex);");
        source = Replace(source, "            Exception escaping = ex;\n", "");
        source = Replace(source, "            catch (Exception rollbackException)\n            {\n                escaping = new AggregateException(ex, rollbackException);\n            }", "            catch (Exception rollbackException) when (rollbackException is not OperationCanceledException)\n            {\n                // Preserve the original session-start failure; rollback is best effort cleanup of transient project persistence.\n            }");
        source = Replace(source, "                await ObserveRuntimeFailureAsync(session, failureCapture.Original ?? ex, escaping).ConfigureAwait(false);", "                PublishRuntimeFailureEvent(session, ex);");
        source = Replace(source, "            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(escaping);", "            throw;");
        source = Section(source, "        => await EnsureCoordinatorSessionWithFailureCaptureAsync(session, options, cancellationToken, null).ConfigureAwait(false);", "    {\n        return await AdmitAsync(async () =>", "");
        source = Replace(source, "ResolveCoordinatorEntryAsync(session, options, failureCapture: failureCapture)", "ResolveCoordinatorEntryAsync(session, options)");
        source = Replace(source, ", RuntimeFailureCapture? failureCapture = null)", ")", 2);
        source = Replace(source, "actorCancellationToken, ownedCommand, failureCapture)", "actorCancellationToken, ownedCommand)");
        source = Section(source, "            Exception? bodyFailure = null;\n            try\n            {\n                await retirement", "        }, external: false);", """
            try
            {
                await retirement.ConfigureAwait(false);
                ObjectDisposedException.ThrowIf(_forwarding.IsClosed, this);
                await CreateCoordinatorSessionAsync(session, options, ticket!, existing, prompt, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                await GetActorForWork(session.SessionId).QueryAsync(_ =>
                {
                    if (_transitions.TryGetValue(session.SessionId, out var current) && ReferenceEquals(current, ticket))
                        _transitions.TryRemove(session.SessionId, out var completedTransition);
                    return ValueTask.FromResult(true);
                }, CancellationToken.None).ConfigureAwait(false);
            }
""" + "\n");
        source = Replace(source, "        CancellationToken cancellationToken,\n        RuntimeFailureCapture? failureCapture)", "        CancellationToken cancellationToken)");
        source = Replace(source, "        catch (Exception failure)\n        {\n            failureCapture?.Capture(failure);\n            // Signal setup before joining retirement: retirement may already be awaiting this record.\n            attachment.CompleteSetup();\n            try { await _forwarding.RetireAsync(attachment).ConfigureAwait(false); }\n            catch (Exception cleanupFailure) { throw new AggregateException(failure, cleanupFailure); }\n            throw;\n        }", "        catch\n        {\n            // Signal setup before joining retirement: retirement may already be awaiting this record.\n            attachment.CompleteSetup();\n            await _forwarding.RetireAsync(attachment).ConfigureAwait(false);\n            throw;\n        }");
        source = RestoreRuntimeSend(source);
        source = RestoreRuntimeControls(source);
        return RestoreRuntimePublication(source);
    }

    private static string RestoreRuntimeSend(string source)
    {
        // The authenticated current section also owns the send cleanup/observation prerequisite receipt.
        source = Section(source, "        CancellationTokenSource? execution = null;", "    internal Task<AgentRunId> SendOwnedCommandAsync(", """
        var ownedDefaultsRejected = false;
        try
        {
            while (true)
            {
            var candidate = await ResolveCoordinatorEntryAsync(session, options, ownedCommand: ownedCommand).ConfigureAwait(false);
            GetActorForWork(session.SessionId);
            var sessionStateUpdated = false;
            var sessionHandleId = await _sessionActors.GetOrCreate(session.SessionId).QueryAsync(
                    async actorCancellationToken =>
                    {
                        await Task.CompletedTask.ConfigureAwait(false);
                        if (!_entries.TryGetValue(session.SessionId, out var current) || !ReferenceEquals(current, candidate)
                            || !candidate.Matches(options, NormalizeOptionalText(candidate.PendingAgentPromptId)
                                ?? NormalizeOptionalText(options.AgentPromptId) ?? NormalizeOptionalText(session.AgentPromptId)))
                            return default(AgentSessionHandleId);
                        handleUse = candidate.Attachment.TryAcquireHandleUse();
                        if (handleUse is null) return default(AgentSessionHandleId);
                        // Matches deliberately ignores callbacks. Reject before taking ownership of
                        // any run/start/prompt state; a different caller may already have an active run.
                        if (ownedCommand && !HasOwnedCommandDefaults(candidate))
                        {
                            ownedDefaultsRejected = true;
                            return default(AgentSessionHandleId);
                        }
                        candidate.PendingAgentPromptId = null;
                        capturedEntry = candidate;
                        session.MarkStarted(DateTimeOffset.UtcNow);
                        sessionStateUpdated = true;

                        return candidate.SessionHandleId;
                    },
                    coordinationCancellationToken)
                .ConfigureAwait(false);
            if (ownedDefaultsRejected)
                throw new InvalidOperationException("Owned command requires denying session defaults.");
            if (handleUse is null) continue;

            if (sessionStateUpdated)
            {
                PublishSessionCatalogEvent(session);
            }

            var runStartedAt = DateTimeOffset.UtcNow;
            using var execution = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, candidate.Attachment.Cancellation.Token);
            AgentRunId runId;
            try
            {
                if (permissionExecution is not null)
                {
                    if (!await Permissions.BindOwnedExecutionAsync(permissionExecution, _runtimeInstanceId,
                        candidate.Attachment, candidate.ProviderId).ConfigureAwait(false))
                        throw new OperationCanceledException("Owned permission execution cannot bind to this attachment.");
                    sendOptions = new AgentSendOptions
                    {
                        Input = sendOptions.Input,
                        AskId = sendOptions.AskId,
                        AdditionalTools = sendOptions.AdditionalTools,
                        OnPermissionRequest = Permissions.CreateOwnedCommandHandler(permissionExecution),
                        OnUserInputRequest = Permissions.CreateOwnedUserInputHandler(permissionExecution),
                        EnableUserInputTool = permissionExecution.EnableUserInput,
                        RunLifecycle = OwnedSessionAskExecution.Combine(sendOptions.RunLifecycle, Permissions.CreateOwnedRunLifecycle(permissionExecution)),
                    };
                }
                if (askExecution is not null)
                {
                    askExecution.Bind(_runtimeInstanceId, candidate.Attachment.Ordinal, candidate.ProviderId);
                    sendOptions = askExecution.Compose(sendOptions);
                }
                runId = await RunCapturedAsync(sessionHandleId, sendOptions, execution.Token).ConfigureAwait(false);
                askSubmission?.RecordRunReturned(runId);
            }
            finally
            {
                // Closes only this interaction window, not a claim that the provider is quiescent.
                // Owner-controlled deliveries finish before the linked source and handle use release.
                askExecution?.Close();
                if (permissionExecution is not null) await Permissions.CloseOwnedExecutionAsync(permissionExecution).ConfigureAwait(false);
            }
            await PublishRunSubmittedIfStillInFlightAsync(session, runId, runStartedAt, coordinationCancellationToken, candidate).ConfigureAwait(false);

            return runId;
            }
        }
        // A policy refusal fails only the owned command receipt, never another caller's run.
        catch (OperationCanceledException) when (!ownedDefaultsRejected)
        {
            var activeRunId = await ClearCapturedRunAsync(capturedEntry).ConfigureAwait(false);
            PublishRunFinishedEvent(
                session.SessionId,
                activeRunId,
                SessionLifecycleEventKind.RunAborted,
                "Runtime run cancelled.",
                DateTimeOffset.UtcNow);
            throw;
        }
        catch (Exception ex) when (!ownedDefaultsRejected && ex is not OperationCanceledException)
        {
            await ClearCapturedRunAsync(capturedEntry).ConfigureAwait(false);
            PublishRuntimeFailureEvent(session, ex);
            throw;
        }
        finally
        {
            try
            {
                askExecution?.Close();
                if (permissionExecution is not null) await Permissions.CloseOwnedExecutionAsync(permissionExecution).ConfigureAwait(false);
            }
            finally { handleUse?.Dispose(); }
        }
    }

""" + "\n");
        return Replace(source, "    private Task<AgentRunId> RunCapturedAsync(AgentSessionHandleId sessionHandleId, AgentSendOptions sendOptions, CancellationToken cancellationToken)\n        => _agentHub.RunAsync(sessionHandleId, sendOptions, cancellationToken);", "    private async Task<AgentRunId> RunCapturedAsync(AgentSessionHandleId sessionHandleId, AgentSendOptions sendOptions, CancellationToken cancellationToken)\n    {\n        var runId = await _agentHub.RunAsync(sessionHandleId, sendOptions, cancellationToken).ConfigureAwait(false);\n        return runId;\n    }");
    }

    private static string RestoreRuntimeControls(string source)
    {
        source = Replace(source, "        catch (Exception failure) when (OwnedProviderEventForwarding.HasRetention(failure))\n        {\n            handleUse?.Retain(failure);\n            _forwarding.RetainDependencies(failure, this);\n            throw;\n        }\n", "", 3);
        source = Replace(source, "            var lifetime = new RuntimeCommandLifetime(new { Runtime = this, Use = handleUse });", """
            using var execution = new CancellationTokenSource();
            await using var ownerCancellation = executionCancellationToken.Register(
                static state => ((CancellationTokenSource)state!).Cancel(), execution);
            await using var attachmentCancellation = handleUse!.Attachment.Cancellation.Token.Register(
                static state => ((CancellationTokenSource)state!).Cancel(), execution);
""", 3);
        source = Replace(source, "var returnedRun = await lifetime.RunAsync(token => SteerCapturedAsync(handle, new AgentSteerOptions", "var returnedRun = await SteerCapturedAsync(handle, new AgentSteerOptions");
        source = Replace(source, "            }, token), executionCancellationToken, handleUse!.Attachment.Cancellation.Token).ConfigureAwait(false);", "            }, execution.Token).ConfigureAwait(false);");
        source = Replace(source, "            return await lifetime.RunAsync(token => _agentHub.TryCompactWhenIdleAsync(handle.Value, token),\n                executionCancellationToken, handleUse!.Attachment.Cancellation.Token).ConfigureAwait(false);", "            return await _agentHub.TryCompactWhenIdleAsync(handle.Value, execution.Token).ConfigureAwait(false);");
        return Replace(source, "            return await lifetime.RunAsync(token => _agentHub.AbortRunAsync(handle.Value, new AgentRunId(request.ExpectedRunId), token),\n                executionCancellationToken, handleUse!.Attachment.Cancellation.Token).ConfigureAwait(false);", "            return await _agentHub.AbortRunAsync(handle.Value, new AgentRunId(request.ExpectedRunId), execution.Token).ConfigureAwait(false);");
    }

    private static string RestoreRuntimePublication(string source)
    {
        source = Section(source, "    private async Task PostAgentEventToActorCoreAsync(", "    private static bool IsQueueDrainTrigger(", """
    private async Task PostAgentEventToActorCoreAsync(
        SessionActor actor, string sessionId, EventProjector projector, AgentEvent @event,
        OwnedProviderEventForwarding.Use projectionUse)
    {
        try
        {
            var projection = await actor.QueryAsync(_ =>
                {
                    var sanitized = projector.Project(@event);
                    RefuseUnavailableOwnedQueue(sessionId);
                    var notifications = projector.Entry!.TakeParentNotifications(sanitized);
                    return ValueTask.FromResult((Event: sanitized, WorkingDirectory: projector.Entry.WorkingDirectory, Notifications: notifications));
                })
                .ConfigureAwait(false);

            projectionUse.Dispose();
            await InvalidateFileSearchCacheAsync(projection.Event, projection.WorkingDirectory).ConfigureAwait(false);
            foreach (var notification in projection.Notifications)
            {
                await DeliverParentNotificationAsync(notification).ConfigureAwait(false);
            }

            if (IsQueueDrainTrigger(@event))
            {
                await TryDrainNextQueuedPromptAsync(sessionId).ConfigureAwait(false);
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
        }
        catch (OperationCanceledException) when (_disposed)
        {
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

""" + "\n");
        source = Section(source, "            if (OwnedProviderEventForwarding.HasRetention(ex))\n            {\n                work.Use.Retain(ex);", "        }\n        finally { work.Use.Dispose(); }", "            await MarkQueuedPromptFailedAsync(work.Entry, work.Prompt!.QueueItemId, ex.Message, DateTimeOffset.UtcNow).ConfigureAwait(false);\n");
        return Section(source, "    private async Task PublishRuntimeFailureEventAsync(", "    private void PublishRunFinishedEvent(", """
    private void PublishRuntimeFailureEvent(SessionViewDescriptor session, Exception exception)
    {
        if (_disposed || string.IsNullOrWhiteSpace(session.SessionId))
        {
            return;
        }

        var timestamp = DateTimeOffset.UtcNow;
        var message = string.IsNullOrWhiteSpace(exception.Message)
            ? "Runtime request failed."
            : exception.Message;
        var ProviderId = string.IsNullOrWhiteSpace(session.ProviderId)
            ? ModelProviderIds.Codex
            : new ModelProviderId(session.ProviderId);
        _events.TryPublish(new SessionAgentEvent(
            session.SessionId,
            new AgentErrorEvent(ProviderId, session.SessionId, timestamp, message, exception)));
        _events.TryPublish(new SessionLifecycleRuntimeEvent(
            session.SessionId,
            timestamp,
            new SessionLifecycleEvent
            {
                SessionId = session.SessionId,
                Kind = SessionLifecycleEventKind.RunFailed,
                Message = message,
            }));
    }

""" + "\n");
    }

    private static string RestorePermissions(string source)
    {
        source = Replace(source, "using System.Runtime.ExceptionServices;\n", "");
        source = Section(source, "        pending.Lifetime = new OwnedDeliveryLifetime(pending);", "    internal Task CloseOwnedExecutionAsync(", """
        try
        {
            using var linked = new CancellationTokenSource();
            // Join the actual forwarding registrations before disposing their destination source;
            // a second Cancel call is not evidence that an earlier concurrent traversal has settled.
            await using var execution = executionToken.UnsafeRegister(static state => ((CancellationTokenSource)state!).Cancel(), linked);
            await using var attachment = attachmentToken.UnsafeRegister(static state => ((CancellationTokenSource)state!).Cancel(), linked);
            await using var run = runToken.UnsafeRegister(static state => ((CancellationTokenSource)state!).Cancel(), linked);
            await using var request = pending.CancellationToken.UnsafeRegister(static state => ((CancellationTokenSource)state!).Cancel(), linked);
            return await AwaitDecisionAsync(pending.Snapshot.Handle, pending.Completion.Task, linked.Token).ConfigureAwait(false);
        }
        catch
        {
            // A failed cancellation registration must not leave an untracked pending entry.
            await CancelAsync(pending.Snapshot.Handle).ConfigureAwait(false);
            throw;
        }
        finally
        {
            await ExecuteAsync(() =>
            {
                pending.OwnedExecution!.Deliveries.Remove(pending);
                _ownedDeliveries.Remove(pending);
                return true;
            }, false).ConfigureAwait(false);
        }
    }

""" + "\n");
        source = Replace(source, "            return JoinDeliveryOriginalsAsync(_ownedExecutions.Values.ToArray().Select(CloseOwned));", "            foreach (var execution in _ownedExecutions.Values.ToArray()) _ = CloseOwned(execution);\n            return JoinOwnedDeliveries();");
        source = Replace(source, "=> JoinOwnedClosureAsync(() => JoinDeliveryOriginalsAsync(_ownedExecutions.Values", "=> JoinOwnedClosureAsync(() => Task.WhenAll(_ownedExecutions.Values");
        source = Section(source, "    private async Task JoinOwnedClosureAsync(Func<Task> close)", "    private Task CloseOwned(OwnedPermissionExecution execution)", """
    private async Task JoinOwnedClosureAsync(Func<Task> close)
    {
        var completion = await ExecuteAsync(close, Task.CompletedTask).ConfigureAwait(false);
        await completion.ConfigureAwait(false);
        // A concurrently disposed service owns all remaining completions. Do not release a caller's
        // source/handle on ExecuteAsync's stopped fallback before that shutdown has actually joined.
        if (Volatile.Read(ref _disposeStarted) != 0) await _shutdown.Task.ConfigureAwait(false);
    }

""" + "\n");
        source = Section(source, "    private Task CloseOwned(OwnedPermissionExecution execution)", "    private bool Owns(", """
    private Task CloseOwned(OwnedPermissionExecution execution)
    {
        if (execution.Closure is not null) return execution.Closure;
        if (!Owns(execution)) return Task.CompletedTask;
        execution.Closed = true;
        _ownedExecutions.Remove(execution.OperationId);
        var deliveries = execution.Deliveries.ToArray();
        foreach (var pending in deliveries) Complete(pending.Snapshot.Handle, AgentPermissionDecisionKind.Cancel);
        var inputs = execution.InputDeliveries.ToArray();
        foreach (var pending in inputs) CompleteInput(pending.Snapshot.Handle, null);
        execution.Attachment = null;
        execution.ExecutionToken = default;
        execution.AttachmentToken = default;
        execution.RunToken = default;
        return execution.Closure = Task.WhenAll(deliveries.Select(pending => (Task)pending.Delivery!)
            .Concat(inputs.Select(pending => (Task)pending.Delivery!)));
    }

""" + "\n");
        source = Section(source, "        var requiredCleanupConfirmed = false;", "    private async Task<AgentPermissionDecision> AwaitDecisionAsync(", """
        try
        {
            var ownedCompletion = await _actor.AskAsync(actorCancellationToken =>
            {
                _stopped = true;
                _ownedAdmissionClosed = true;
                foreach (var execution in _ownedExecutions.Values.ToArray()) _ = CloseOwned(execution);
                var owned = JoinOwnedDeliveries();
                foreach (var entry in _pending.Values)
                {
                    entry.Completion.SetResult(new(AgentPermissionDecisionKind.Cancel));
                }

                _pending.Clear();
                return ValueTask.FromResult(owned);
            }).ConfigureAwait(false);
            try { await ownedCompletion.ConfigureAwait(false); }
            finally
            {
                try
                {
                    // Delivery cleanup uses the stopped fallback during disposal; release its bounded indexes here.
                    await _actor.AskAsync(_ =>
                    {
                        foreach (var pending in _ownedDeliveries) pending.OwnedExecution!.Deliveries.Remove(pending);
                        _ownedDeliveries.Clear();
                        foreach (var pending in _inputDeliveries) pending.Execution.InputDeliveries.Remove(pending);
                        _inputDeliveries.Clear();
                        return ValueTask.FromResult(true);
                    }).ConfigureAwait(false);
                }
                finally { await _actor.StopAsync().ConfigureAwait(false); }
            }
        }
        finally
        {
            _shutdown.TrySetResult();
        }
    }

""" + "\n");
        source = Replace(source, "        internal OwnedDeliveryLifetime? Lifetime { get; set; }\n", "");
        return Section(source, "\n\n    internal static async Task JoinDeliveryOriginalsAsync(", "\n}", "");
    }

    private static string RestoreQueue(string source)
    {
        source = Replace(source, "        item.CancellationWorker.Launch(item.CancelExecutionAsync);", "        var cancellation = item.CancelExecutionAsync();\n        var callerRegistration = cancellationToken.UnsafeRegister(static state => ((OwnedQueuedExecution)state!).Stop.TrySetResult(), item);");
        source = Replace(source, "            item.CallerRegistration = cancellationToken.UnsafeRegister(static state => ((OwnedQueuedExecution)state!).Stop.TrySetResult(), item);\n            item.CallerRegistered = true;\n", "");
        source = Replace(source, "                    item.AttachmentRegistered = true;\n", "");
        source = Section(source, "                    Task<AgentRunId>? runOriginal = null;", "                    if (lifecycle is not null && !lifecycle.WasBound)", "                    var runId = await RunCapturedAsync(work.SessionHandleId, send, item.Execution.Token).ConfigureAwait(false);\n");
        source = Section(source, "        catch (OperationCanceledException failure) when (preDispatchCancellationRefused", "    // All entry reads/mutations below", """
        catch (OperationCanceledException) when (preDispatchCancellationRefused || item.IsCancellationRequested || item.Execution.IsCancellationRequested)
        {
            result = new(OwnedSessionCommandOutcome.Cancelled, Code: "queue_cancelled");
        }
        catch (Exception)
        {
            result = new(OwnedSessionCommandOutcome.Failed, Code: "queue_failed");
        }
        finally
        {
            // Begin permission closure independently of the already-running cancellation worker.
            // Neither may be substituted for the original provider send or registration joins.
            Task permissionClose = permission is null ? Task.CompletedTask : Permissions.CloseOwnedExecutionAsync(permission);
            try { await permissionClose.ConfigureAwait(false); }
            catch { result = new(OwnedSessionCommandOutcome.Failed, Code: "queue_cleanup_failed"); }
            try
            {
                await callerRegistration.DisposeAsync().ConfigureAwait(false);
                await item.AttachmentRegistration.DisposeAsync().ConfigureAwait(false);
            }
            catch { result = new(OwnedSessionCommandOutcome.Failed, Code: "queue_cleanup_failed"); }
            item.ExecutionSettled.TrySetResult();
            try { await cancellation.ConfigureAwait(false); }
            catch { result = new(OwnedSessionCommandOutcome.Failed, Code: "queue_cancel_failed"); }
            try
            {
                if (captured is not null)
                    await capturedActor!.QueryAsync(_ =>
                    {
                        if (ReferenceEquals(captured.OwnedQueue, item)) captured.OwnedQueue = null;
                        if (work is not null) captured.CompleteQueueDrain();
                        return ValueTask.FromResult(true);
                    }, CancellationToken.None).ConfigureAwait(false);
            }
            catch { result = new(OwnedSessionCommandOutcome.Failed, Code: "queue_cleanup_failed"); }
            finally
            {
                item.Execution.Dispose();
                registrationUse?.Dispose();
                work?.Use.Dispose();
                receipt.CompleteQueueInsertion(new(false, result.Code ?? "queue_failed"));
                item.Drained.TrySetResult();
            }
        }
        return result;

    }

""" + "\n");
        source = Section(source, "    private Task CloseOwnedQueueAttachmentAsync(", "    private sealed class OwnedQueuedExecution(", """
    private async Task CloseOwnedQueueAttachmentAsync(OwnedProviderEventForwarding.Attachment attachment)
    {
        // Initiate existing invalidation before any queue join, retaining the original task.
        var permissions = Permissions.InvalidateOwnedAttachmentAsync(attachment);
        Task queue = Task.CompletedTask;
        try
        {
            if (_sessionActors.TryGet(attachment.Identity.SessionId, out var actor))
                queue = await actor.QueryAsync(_ =>
                {
                    if (_entries.TryGetValue(attachment.Identity.SessionId, out var entry)
                        && ReferenceEquals(entry.Attachment, attachment) && entry.OwnedQueue is { } item)
                    {
                        item.Stop.TrySetResult();
                        return ValueTask.FromResult(item.Drained.Task);
                    }
                    return ValueTask.FromResult(Task.CompletedTask);
                }, CancellationToken.None).ConfigureAwait(false);
        }
        finally { await Task.WhenAll(permissions, queue).ConfigureAwait(false); }
    }

""" + "\n");
        source = Section(source, "        internal OwnedSessionCommandService.DependencyReleaseDecision ReleaseDecision", "        internal OwnedTextQueueRequest Request", "");
        source = Section(source, "            if (Stop.Task.IsCompleted)\n", "    private sealed class QueueBindingException", """
            if (Stop.Task.IsCompleted) await Execution.CancelAsync().ConfigureAwait(false);
        }
    }

""" + "\n");
        return Replace(source, "            catch (Exception failure) when (!OwnedProviderEventForwarding.HasRetention(failure)) { throw new QueueBindingException(); }", "            catch { throw new QueueBindingException(); }");
    }

    private static string RestoreForwarding(string source)
    {
        source = Section(source, "using System.Runtime.ExceptionServices;", "    private async Task WaitUsesAsync(", """
namespace CodeAlta.Orchestration.Runtime;

// Runtime-specific ownership, not a provider scheduler or another hosting lifetime.
// The gate protects records only. All executable work waits on a retained asynchronous launch.
internal sealed class OwnedProviderEventForwarding
{
    private readonly object _gate = new();
    private readonly Dictionary<long, (Task Work, Task Observer)> _active = [];
    private readonly List<(long Ordinal, int Stage, Exception Error)> _failures = [];
    private readonly HashSet<Attachment> _attachments = [];
    private TaskCompletionSource _changed = NewCompletion();
    private Task? _close;
    private long _ordinal;
    private bool _closed;

    internal bool IsClosed { get { lock (_gate) return _closed; } }
    internal int ActiveWorkCount { get { lock (_gate) return _active.Count; } }

    internal Task<T> RunAsync<T>(Func<Task<T>> body, bool external = true, bool reportFailure = false)
    {
        var launch = NewCompletion();
        Task<T> work;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(external && _closed, this);
            var ordinal = ++_ordinal;
            work = ExecuteAsync(ordinal, launch.Task, body, reportFailure);
            _active.Add(ordinal, (work, ObserveOwnedAsync(ordinal, work)));
        }
        launch.TrySetResult();
        return work;
    }

    internal Task RunAsync(Func<Task> body, bool external = true, bool reportFailure = false)
        => RunAsync(async () => { await body().ConfigureAwait(false); return true; }, external, reportFailure);

    private async Task<T> ExecuteAsync<T>(long ordinal, Task launch, Func<Task<T>> body, bool reportFailure, AttachmentIdentity? identity = null)
    {
        await launch.ConfigureAwait(false);
        try { return await body().ConfigureAwait(false); }
        catch (Exception ex)
        {
            if (reportFailure) RecordFailure(ordinal, 0, ex, identity);
            throw;
        }
    }

    private async Task ObserveOwnedAsync(long ordinal, Task work)
    {
        try { await work.ConfigureAwait(false); }
        catch { /* ExecuteAsync records reportable failures; external callers receive their result. */ }
        finally
        {
            lock (_gate)
            {
                _active.Remove(ordinal);
                Pulse();
            }
        }
    }

    internal Attachment RegisterAttachment(string sessionId, string handleId, Func<Task> abort, Func<Task> stop)
    {
        Attachment attachment;
        bool retire;
        lock (_gate)
        {
            attachment = new Attachment(this, ++_ordinal, new AttachmentIdentity(sessionId, handleId), abort, stop);
            _attachments.Add(attachment);
            retire = _closed;
        }
        // A late acquisition is owned before control can run or metadata can await.
        if (retire) _ = RetireAsync(attachment);
        return attachment;
    }

    internal Task Forward(Attachment attachment, Func<Use, Task> body)
    {
        var launch = NewCompletion();
        Task work;
        lock (_gate)
        {
            if (!attachment.CallbackAdmission) return Task.CompletedTask;
            attachment.ProjectionUses++;
            var use = new Use(attachment, projection: true);
            var ordinal = ++_ordinal;
            work = ExecuteAsync(ordinal, launch.Task, async () =>
            {
                try { await body(use).ConfigureAwait(false); return true; }
                finally { use.Dispose(); }
            }, reportFailure: true, identity: attachment.Identity);
            // Observe independently of the Action callback's discarded return value.
            _active.Add(ordinal, (work, ObserveOwnedAsync(ordinal, work)));
        }
        launch.TrySetResult();
        return work;
    }

    internal Task RetireAsync(Attachment attachment)
    {
        var launch = NewCompletion();
        Task retirement;
        lock (_gate)
        {
            if (attachment.Retirement is not null) return attachment.Retirement;
            attachment.HandleAdmission = false;
            var ordinal = ++_ordinal;
            retirement = ExecuteAsync(ordinal, launch.Task, async () =>
            {
                await RetireCoreAsync(attachment).ConfigureAwait(false);
                return true;
            }, reportFailure: false);
            attachment.Retirement = retirement;
            _active.Add(ordinal, (retirement, ObserveOwnedAsync(ordinal, retirement)));
        }
        launch.TrySetResult();
        return retirement;
    }

    private async Task RetireCoreAsync(Attachment attachment)
    {
        // Neither cancellation callbacks nor abort are awaited before the other is initiated.
        var cancelStage = new RetirementStage(attachment.Cancellation.CancelAsync);
        var abortStage = new RetirementStage(attachment.Abort);
        var permissionStage = new RetirementStage(attachment.CloseOwnedPermissions ?? (() => Task.CompletedTask));
        var cancellation = cancelStage.Work;
        var abort = abortStage.Work;
        attachment.Controls = [cancellation, abort];
        cancelStage.Launch();
        abortStage.Launch();
        permissionStage.Launch();
        // A permission waiting on a provider-supplied None token must not hold a handle use forever.
        // Cancellation and abort above still start independently, before this owner-only join.
        await CaptureAsync(attachment.Ordinal, 0, permissionStage.Work, attachment.Identity).ConfigureAwait(false);
        await attachment.Setup.Task.ConfigureAwait(false);
        await WaitUsesAsync(attachment, projection: false).ConfigureAwait(false);
        await CaptureAsync(attachment.Ordinal, 1, cancellation, attachment.Identity).ConfigureAwait(false);
        await CaptureAsync(attachment.Ordinal, 2, abort, attachment.Identity).ConfigureAwait(false);

        IDisposable? subscription;
        lock (_gate)
        {
            attachment.CallbackAdmission = false;
            subscription = attachment.Subscription;
        }
        // Admission closure precedes actual IDisposable.Dispose, which may invoke callbacks/throw.
        var unsubscribeStage = new RetirementStage(() => { subscription?.Dispose(); return Task.CompletedTask; });
        var unsubscribe = unsubscribeStage.Work;
        attachment.Unsubscription = unsubscribe;
        unsubscribeStage.Launch();
        await CaptureAsync(attachment.Ordinal, 3, unsubscribe, attachment.Identity).ConfigureAwait(false);
        await WaitUsesAsync(attachment, projection: true).ConfigureAwait(false);
        if (!unsubscribe.IsCompletedSuccessfully)
        {
            // An attempted closure is not a receipt of successful unsubscription. Keep the
            // provider handle, cancellation source, attachment, and runtime dependencies alive.
            foreach (var stage in new[] { cancelStage, abortStage, permissionStage, unsubscribeStage })
                await stage.Observer.ConfigureAwait(false);
            throw new AggregateException(new[] { cancellation, abort, permissionStage.Work, unsubscribe }
                .Where(task => !task.IsCompletedSuccessfully)
                .Select(task => task.Exception?.GetBaseException() ?? new TaskCanceledException(task)));
        }
        var stopStage = new RetirementStage(attachment.Stop);
        var stop = stopStage.Work;
        attachment.StopWork = stop;
        stopStage.Launch();
        await CaptureAsync(attachment.Ordinal, 4, stop, attachment.Identity).ConfigureAwait(false);
        foreach (var stage in new[] { cancelStage, abortStage, permissionStage, unsubscribeStage, stopStage })
            await stage.Observer.ConfigureAwait(false);
        lock (_gate)
        {
            attachment.Stopped = stop.IsCompletedSuccessfully;
            // A failed unsubscribe is not declared successful. Keep its receipt/delegates/failure.
            if (attachment.Stopped && unsubscribe.IsCompletedSuccessfully) _attachments.Remove(attachment);
            Pulse();
        }
        if (attachment.Stopped) attachment.Cancellation.Dispose();
        var failed = new[] { cancellation, abort, permissionStage.Work, unsubscribe, stop }.Where(task => !task.IsCompletedSuccessfully).ToArray();
        if (failed.Length != 0)
            throw new AggregateException(failed.Select(task => task.Exception?.GetBaseException() ?? new TaskCanceledException(task)));
    }

""" + "\n");
        source = Replace(source, "_attachments.Count == 0 && _retainedDependencies.Count == 0", "_attachments.Count == 0");
        source = Replace(source, "        if (permissionStage.Original is null)\n            throw RetainedFailure(\"permission cleanup\", permissionStage);\n", "");
        source = Replace(source, "            throw RetainedFailure(\"runtime drainage\", this);", "            var failure = new AggregateException(\"Attachment retirement did not confirm dependency release.\", retainedFailures);\n            failure.Data[\"RetainedForwardingOwner\"] = this;\n            throw failure;");
        source = Replace(source, "        // A terminal ordinary returned-task error confirms unwind; a missing original or any\n        // explicit marker in the captured original graph does not authorize stream release.\n        if (actorStage.Original is null || HasRetainedDependencies())\n            throw RetainedFailure(\"actor cleanup\", actorStage);\n", "");
        source = Section(source, "        lock (_gate)\n        {\n            _failures.Add((ordinal, stage, reported));", "    internal sealed record AttachmentIdentity", "        lock (_gate) _failures.Add((ordinal, stage, reported));\n    }\n\n");
        source = Section(source, "    private sealed class WorkReceipt", "    internal sealed class AttachmentFailureException", "");
        source = Section(source, "    internal sealed class RetirementStage", "    private static async Task ObserveAsync(", """
    private sealed class RetirementStage
    {
        private readonly TaskCompletionSource _launch = NewCompletion();
        internal RetirementStage(Func<Task> body)
        {
            Work = InvokeAsync(body);
            Observer = ObserveAsync(Work);
        }
        internal Task Work { get; }
        internal Task Observer { get; }
        internal void Launch() => _launch.TrySetResult();
        private async Task InvokeAsync(Func<Task> body)
        {
            await _launch.Task.ConfigureAwait(false);
            await body().ConfigureAwait(false);
        }
    }

""" + "\n");
        source = Replace(source, "        internal RetirementStage[] Stages { get; set; } = [];\n        internal List<(Use Use, Exception Failure)> RetainedUses { get; } = [];\n", "");
        source = Replace(source, "        internal int ActiveCallbacks { get; set; }\n", "");
        source = Replace(source, "_owner._closed || _owner._retainedDependencies.Count != 0 || !HandleAdmission", "_owner._closed || !HandleAdmission");
        source = Section(source, "        internal void Retain(Use use, bool projection, Exception failure)", "        internal void Release(bool projection)", "");
        source = Replace(source, "        internal bool IsReleased => Volatile.Read(ref _released) == 1;\n", "");
        source = Section(source, "        internal void Retain(Exception failure)", "        public void Dispose()", "");
        return Replace(source, "Interlocked.CompareExchange(ref _released, 1, 0)", "Interlocked.Exchange(ref _released, 1)");
    }

    private static string RestoreProgram(string source) => Section(source, "using System.Diagnostics;", "    internal static void ReportCommandLinePluginStartup(", """
using System.Diagnostics;
using CodeAlta.Hosting;
using CodeAlta.Tui;
using CodeAlta.Tui.App;
using CodeAlta.Catalog;
using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;
using CodeAlta.Tui.Views;
using XenoAtom.Ansi;
using XenoAtom.CommandLine;
using XenoAtom.Logging;
using XenoAtom.Terminal;

var mainThreadId = Environment.CurrentManagedThreadId;
try
{
    return CodeAltaStartupAdmission.Run(
        args,
        Program.RunEarlyCommand,
        static () => CodeAltaSingleInstanceGuard.Acquire(),
        () => Program.RunAdmittedStartup(args, mainThreadId));
}
catch (Exception ex)
{
    // Admission/early-output failures must not initialize logging, crash reporting or Terminal.
    Console.Error.WriteLine(ex.Message);
    return 1;
}

internal partial class Program
{
    internal static int RunEarlyCommand(string argument)
    {
        var command = CodeAltaCliOptions.CreatePlainCommandApp(
            static _ => throw new InvalidOperationException("Early commands must not enter mutable startup."));
        return command.RunAsync([argument]).AsTask().GetAwaiter().GetResult();
    }

    internal static int RunAdmittedStartup(string[] args, int mainThreadId)
    {
        try
        {
            var homeRoot = Program.GetDefaultHomeRoot();
            CodeAltaLogging.Initialize(homeRoot);

            // Plugin runtime startup ordering: register MSBuild before any plugin build service, pipe-logger
            // event payload, or Microsoft.Build type can be touched. Safe-mode raw args/environment are
            // still read by host-owned code before dynamic plugins are built or loaded.
            // Disabled for now until https://github.com/dotnet/sdk/pull/54172 is merged
            // //CodeAltaPluginRuntimeStartup.RegisterMsBuildDefaults();
            using var session = Terminal.Open();

            _ = PluginRuntimeConfigResolver.IsSafeModeEnabled(args);
            var commandLinePluginRuntime = Program.StartPluginRuntimeForCommandLine(args, CancellationToken.None);
            try
            {
                var pluginCommandLineContributions = Program.GetPluginCommandLineContributions(commandLinePluginRuntime);
                var command = CodeAltaCliOptions.CreateCommandApp(
                    options => Program.RunAsync(options, mainThreadId, commandLinePluginRuntime),
                    pluginCommandLineContributions);
                return command.RunAsync(args).AsTask().GetAwaiter().GetResult();
            }
            finally
            {
                if (commandLinePluginRuntime is not null)
                {
                    commandLinePluginRuntime.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
            }
        }
        catch (CodeAltaAlreadyRunningException ex)
        {
            Terminal.WriteMarkupLine($"[bright-red]{AnsiMarkup.Escape(ex.Message)}[/]");
            return 1;
        }
        catch (Exception ex)
        {
            try
            {
                LogManager.GetLogger("CodeAlta.Program").Error(ex, "Top-level exception");
            }
            catch
            {
            }

            CodeAltaCrashReporter.ReportFatalException("Top-level exception", ex);
            Terminal.WriteLine(ex.ToString());
            return 1;
        }
        finally
        {
            LogManager.Shutdown();
        }
    }

    internal static async ValueTask<int> RunAsync(CodeAltaCliOptions options, int mainThreadId, PluginRuntimeManager? prestartedPluginRuntime = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.PluginsStatus)
        {
            return PrintPluginsStatus(options.PluginSafeMode);
        }

        var cancellationTokenSource = new CancellationTokenSource();

        // Defer async app startup until the terminal loop is already running so XenoAtom keeps the UI
        // bound to the process main thread. Awaiting service creation before Terminal.RunAsync can move
        // the actual UI bootstrap onto a worker continuation instead.
        await using var app = new DeferredCodeAltaApp(prestartedPluginRuntime);
        if (options.TestMode)
        {
            var logger = LogManager.GetLogger("CodeAlta.Program");
            var testDurationText = options.TestDuration!.Value.TotalSeconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
            logger.Debug($"Starting CodeAlta terminal smoke test for {testDurationText}s.");

            Terminal.WriteLine($"[CodeAlta] Starting terminal smoke test for {testDurationText}s.");
        }

        // Enter the terminal immediately after synchronous setup; DeferredCodeAltaApp finishes async
        // initialization from inside the loop instead of before Terminal.RunAsync starts.
        Program.ThrowIfCurrentThreadIsNotMainThread(mainThreadId);
        await app.RunAsync(cancellationTokenSource.Token);
        PrintUpdateAvailableMessage(app.UpdateCheckSnapshot);

        if (options.TestMode)
        {
            var logger = LogManager.GetLogger("CodeAlta.Program");
            logger.Debug("CodeAlta terminal smoke test exited cleanly.");

            Terminal.WriteLine("[CodeAlta] Terminal smoke test exited cleanly.");
        }

        return 0;
    }

    private static void PrintUpdateAvailableMessage(CodeAltaUpdateCheckSnapshot snapshot)
    {
        if (!snapshot.HasNewerVersion)
        {
            return;
        }

        Terminal.WriteLine($"A new version {snapshot.LatestVersionText} of CodeAlta is available!");
        Terminal.WriteLine($"To update: {snapshot.UpdateCommand}");
    }

    internal static PluginRuntimeManager? StartPluginRuntimeForCommandLine(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
        => StartPluginRuntimeForCommandLineAsync(args, cancellationToken).AsTask().GetAwaiter().GetResult();

    internal static async ValueTask<PluginRuntimeManager?> StartPluginRuntimeForCommandLineAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        var homeRoot = GetDefaultHomeRoot();
        Directory.CreateDirectory(homeRoot);
        CodeAltaLogging.Initialize(homeRoot);
        var currentDirectory = Environment.CurrentDirectory;
        var pluginBootstrapOptions = CodeAltaCliOptions.GetPluginBootstrapOptions(args);
        if (!CanStartPluginRuntimeBeforeConfigRecovery(homeRoot))
        {
            return null;
        }

        var runtime = new PluginRuntimeManager();
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = await runtime.StartAsync(
                new PluginRuntimeManagerOptions
                {
                    GlobalRoot = homeRoot,
                    ProjectContext = new PluginProjectContext
                    {
                        ProjectId = "current",
                        ProjectPath = currentDirectory,
                    },
                    SafeMode = pluginBootstrapOptions.PluginSafeMode,
                    IsHeadless = false,
                    StartupFeedback = new CodeAlta.Tui.Plugins.TerminalPluginStartupFeedback(),
                    AuthoringProfile = PluginAuthoringProfile.Terminal,
                    WaitForEnterAfterBuildLiveOutput = pluginBootstrapOptions.WaitForEnterAfterPluginLiveOutput,
                    RawArguments = args,
                    BuiltIns = CodeAltaBuiltInPlugins.All,
                },
                cancellationToken);
            stopwatch.Stop();
            ReportCommandLinePluginStartup(result, stopwatch.Elapsed, pluginBootstrapOptions);
            return runtime;
        }
        catch
        {
            await runtime.DisposeAsync();
            throw;
        }
    }

""" + "\n");

    private static string RestoreAgent(string source)
    {
        source = Replace(source, "    private int _activeOperationUses;\n    private readonly List<OperationUse> _retainedOperations = [];\n", "");
        source = Replace(source, "            run.Body = ExecuteRunAsync(options, run.Id, run.Cancellation, run);\n            return await run.Body.ConfigureAwait(false);", "            return await ExecuteRunAsync(options, run.Id, run.Cancellation).ConfigureAwait(false);");
        source = Section(source, "            run.Finish = FinishRunAsync(", "    private async Task<AgentRunId> ExecuteRunAsync(", """
            await FinishRunAsync(run, admitted, options.RunLifecycle, callerRegistration, failure).ConfigureAwait(false);
        }
    }

""" + "\n");
        source = Replace(source, "CancellationTokenSource linkedCts, ActiveRun run)", "CancellationTokenSource linkedCts)");
        source = Replace(source, "            if (options.RunLifecycle is { } lifecycle)\n            {\n                await run.Start.RunAsync(lifecycle, runId, linkedCts.Token).ConfigureAwait(false);\n            }", "            if (options.RunLifecycle is { } lifecycle)\n                await lifecycle.StartedAsync(runId, linkedCts.Token).ConfigureAwait(false);");
        source = Replace(source, "            var originalBodyFailure = run.Start.PublicationFailure ?? ex;\n            run.OriginalBodyFailure = originalBodyFailure;\n            run.ErrorPublication = AppendRunErrorAsync(runId, originalBodyFailure);\n            try { await run.ErrorPublication.ConfigureAwait(false); }\n            catch (Exception publicationFailure) { throw new AggregateException(ex, publicationFailure); }", "            await AppendRunErrorAsync(runId, ex).ConfigureAwait(false);");
        source = Replace(source, "EnterOperation(allowRetained: true)", "EnterOperation()", 3);
        source = Section(source, "        lock (_lifetimeGate)\n        {\n            if (_retainedOperations.Count != 0)", "        try\n        {\n            if (_turnExecutor is IAgentProviderSessionCleanup", "");
        source = Replace(source, "        catch (Exception ex)\n        {\n            failures.Add(ex);\n            throw new AgentDependencyRetentionException(SessionId, \"provider session cleanup\", failures, this);\n        }\n        _pendingSteerInputs.Clear();\n        _stateGate.Dispose();\n        _eventChannel.Writer.TryComplete();", "        catch (Exception ex) { failures.Add(ex); }\n        finally\n        {\n            _pendingSteerInputs.Clear();\n            _stateGate.Dispose();\n            _eventChannel.Writer.TryComplete();\n        }");
        source = Section(source, "    private OperationUse EnterOperation(", "    private sealed class ActiveRun", """
    private OperationUse EnterOperation()
    {
        lock (_lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_operationUses++ == 0) _operationsSettled = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return new OperationUse(this);
        }
    }

    private sealed class OperationUse(AgentSession owner) : IDisposable
    {
        public void Dispose()
        {
            lock (owner._lifetimeGate)
                if (--owner._operationUses == 0) owner._operationsSettled!.TrySetResult();
        }
    }

""" + "\n");
        source = Section(source, "        internal Task<AgentRunId>? Body { get; set; }", "        // State gate owns admission flags.", "");
        return Section(source, "        run.Release = new RunReleaseDecision(run);", "    private IReadOnlyList<AgentToolDefinition> BuildAvailableTools(", """
        // Launch Closing before dependent cancellation joins: permission delivery may itself be a
        // cancellation dependency. Capture synchronous hook failures in the returned original task.
        var closing = admitted && lifecycle is not null ? CloseLifecycleAsync(lifecycle, run.Id) : Task.CompletedTask;
        var registrationDisposal = callerRegistration.DisposeAsync().AsTask();
        var closureJoins = Task.WhenAll(closing, registrationDisposal);
        Exception? cleanupFailure = null;
        try { await closureJoins.ConfigureAwait(false); }
        catch (Exception ex) { cleanupFailure = closureJoins.Exception ?? ex; }
        // Keep trusted/disposal cancellation available while Closing is pending. Only after the hook
        // and caller forwarding registration settle can a non-cancelled run seal its worker as a no-op.
        await _stateGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            run.CancellationSealed = true;
            if (!run.CancellationReserved) run.CompleteWithoutCancellation();
        }
        finally { _stateGate.Release(); }
        try { await run.CancellationWork.ConfigureAwait(false); }
        catch (Exception ex) { cleanupFailure = cleanupFailure is null ? ex : new AggregateException(cleanupFailure, ex); }
        finally
        {
            // Sole CTS disposer, after every original traversal and the Closing hook have settled.
            run.Cancellation.Dispose();
            if (admitted)
            {
                await CompleteActiveTurnAsync(run.Id, CancellationToken.None).ConfigureAwait(false);
                await _stateGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    // Logical turn completion may already have cleared _activeRunId. Release only
                    // this exact source lifetime, after all original work and source disposal.
                    if (ReferenceEquals(_activeRun, run)) _activeRun = null;
                }
                finally { _stateGate.Release(); }
            }
        }
        if (cleanupFailure is not null)
        {
            if (bodyFailure is not null) throw new AggregateException(bodyFailure, cleanupFailure);
            ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
        }
    }

    private static async Task CloseLifecycleAsync(AgentRunLifecycle lifecycle, AgentRunId runId)
        => await lifecycle.ClosingAsync(runId).ConfigureAwait(false);

""" + "\n");
    }

    private static string RestoreHost(string source)
    {
        source = Replace(source, "using CodeAlta.Orchestration.Runtime.Plugins;\n", "");
        source = Replace(source, "    private readonly HostDisposalStage _earlyReadShutdown;\n    private readonly HostDisposalStage _earlyCommandShutdown;\n", "");
        source = Replace(source, "        _earlyReadShutdown = new HostDisposalStage(() => WorkspaceReads.DisposeAsync().AsTask());\n        _earlyCommandShutdown = new HostDisposalStage(() => Commands.DisposeAsync().AsTask());\n", "");
        source = Section(source, "            var eventFailurePolicy =", "            runtimeService = new SessionRuntimeService(", "");
        source = Replace(source, "                PluginEventObserver = eventObserver,\n                PluginEventCurrentProjectId = currentProject.Id,\n                PluginEventCurrentProjectPath = currentProject.ProjectPath,\n", "");
        source = Section(source, "            var acquisitions = new { Plugin =", "            await RollbackHostCreationAsync(", """
            await PluginEventDependencyBarrier.BeforeRollbackAsync(
                pluginRuntime ?? options.PrestartedPluginRuntime, creationFailure,
                new object?[] { pluginRuntime, runtimeService, agentHub, modelProviderRegistry, options }).ConfigureAwait(false);
""" + "\n");
        source = Section(source, "    /// <summary>Closes owned read/command admission", "    /// <summary>\n    /// Creates a lazy, single-execution host cleanup", """
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

""" + "\n");
        source = Section(source, "        var stages = new[]", "    /// <summary>\n    /// Awaits best-effort rollback", """
        List<Exception>? failures = null;
        try
        {
            await disposeRuntimeService().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            (failures ??= []).Add(exception);
        }

        try
        {
            await disposeAgentHub().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            (failures ??= []).Add(exception);
        }

        try
        {
            await disposeModelProviderRegistry().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            (failures ??= []).Add(exception);
        }

        if (ownsPluginRuntime)
        {
            try
            {
                await disposePluginRuntime().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        if (ownsLogging)
        {
            try
            {
                shutdownLogging();
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
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

""" + "\n");
        return Replace(source, "        if (OwnedProviderEventForwarding.HasRetention(creationFailure))\n            throw new AgentDependencyRetentionException(\"host creation\", \"retained inner acquisition\", [creationFailure], disposal);\n", "");
    }

    private static string RestoreHub(string source)
    {
        source = Replace(source, "using System.Runtime.ExceptionServices;\n", "");
        source = Replace(source, "    private volatile bool _disposed;\n    private readonly object _disposalGate = new();\n    private Task? _disposeTask;\n    private SessionEntry[]? _disposalEntries;", "    private bool _disposed;");
        source = Section(source, "        var original = entry.Lifetime.RecordRun(", "    /// <summary>\n    /// Steers an active run", """
        try
        {
            return await entry.Coordinator.RunAsync(sessionHandleId, options, _events, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            entry.ReleaseReference();
        }
    }

""" + "\n");
        source = Section(source, "    public async Task StopSessionAsync(", "    private async Task<AgentSessionHandle> AttachSessionAsync(", """
    public async Task StopSessionAsync(AgentSessionHandleId sessionHandleId, CancellationToken cancellationToken = default)
    {
        SessionEntry? entry = null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_sessions.TryGetValue(sessionHandleId, out entry))
            {
                _sessions.Remove(sessionHandleId);
            }
        }
        finally
        {
            _gate.Release();
        }

        if (entry is not null)
        {
            await entry.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _events.Complete();

        SessionEntry[] sessions;
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            sessions = _sessions.Values.ToArray();
            _sessions.Clear();
        }
        finally
        {
            _gate.Release();
        }

        foreach (var session in sessions)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }

        _gate.Dispose();
    }

""" + "\n");
        source = Replace(source, "            ObjectDisposedException.ThrowIf(_disposed, this);\n", "");
        source = Section(source, "    private static async ValueTask DisposeProviderRuntimeAsync(", "    private sealed class ProviderSessionRuntimeLease", """
    private static async ValueTask DisposeProviderRuntimeAsync(ProviderSessionRuntimeLease runtime)
    {
        try
        {
            await runtime.StopAsync().ConfigureAwait(false);
        }
        catch
        {
            // Ignore shutdown exceptions from provider runtimes that did not finish starting.
        }

        await runtime.DisposeAsync().ConfigureAwait(false);
    }

""" + "\n");
        source = Replace(source, "        internal SessionPermissionService.DeliveryStage? StopOriginal { get; set; }\n        internal SessionPermissionService.DeliveryStage? DisposalOriginal { get; set; }\n", "");
        source = Replace(source, "        private readonly CoordinatorFailureOwner _failureOwner;\n", "");
        source = Replace(source, "            _failureOwner = new CoordinatorFailureOwner(this);\n", "");
        source = Replace(source, "                var runId = await _failureOwner.RunAsync(invocation, () => _session.SendAsync(options, cancellationToken)).ConfigureAwait(false);", "                var runId = await _session.SendAsync(options, cancellationToken).ConfigureAwait(false);");
        source = Replace(source, "            var invocation = new OwnedSessionCommandService.OriginalInvocation();\n", "", 2);
        source = Replace(source, "(invocation.AwaitedFailure ?? ex).Message", "ex.Message");
        source = Replace(source, "await _failureOwner.AbortAsync(invocation, () => _session.AbortAsync(cancellationToken)).ConfigureAwait(false);", "await _session.AbortAsync(cancellationToken).ConfigureAwait(false);", 2);
        source = Section(source, "            await _failureOwner.DisposeAsync(_session.DisposeAsync, () =>", "    private sealed class SessionEntry", """
            _runGate.Dispose();
            _controlGate.Dispose();
            await _session.DisposeAsync().ConfigureAwait(false);
        }
    }

""" + "\n");
        return Section(source, "    private sealed class SessionEntry", "\n}", """
    private sealed class SessionEntry : IAsyncDisposable
    {
        private readonly object _sync = new();
        private readonly ProviderSessionRuntimeLease _providerRuntime;
        private readonly TaskCompletionSource _disposedCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource? _idleCompletion;
        private int _activeReferences;
        private bool _disposeStarted;

        public SessionEntry(AgentSessionCoordinator coordinator, ProviderSessionRuntimeLease providerRuntime)
        {
            Coordinator = coordinator;
            _providerRuntime = providerRuntime;
        }

        public AgentSessionCoordinator Coordinator { get; }

        public bool TryAddReference()
        {
            lock (_sync)
            {
                if (_disposeStarted)
                {
                    return false;
                }

                _activeReferences++;
                return true;
            }
        }

        public void ReleaseReference()
        {
            TaskCompletionSource? idleCompletion = null;
            lock (_sync)
            {
                if (_activeReferences <= 0)
                {
                    throw new InvalidOperationException("Session entry reference count is already zero.");
                }

                _activeReferences--;
                if (_activeReferences == 0 && _disposeStarted)
                {
                    idleCompletion = _idleCompletion;
                }
            }

            idleCompletion?.TrySetResult();
        }

        public async ValueTask DisposeAsync()
        {
            Task? disposeTask;
            Task? idleTask = null;
            lock (_sync)
            {
                if (_disposeStarted)
                {
                    disposeTask = _disposedCompletion.Task;
                }
                else
                {
                    _disposeStarted = true;
                    disposeTask = null;
                    if (_activeReferences > 0)
                    {
                        _idleCompletion ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        idleTask = _idleCompletion.Task;
                    }
                }
            }

            if (disposeTask is not null)
            {
                await disposeTask.ConfigureAwait(false);
                return;
            }

            try
            {
                if (idleTask is not null)
                {
                    try
                    {
                        await Coordinator.AbortAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Ignore best-effort abort failures while the session is being disposed.
                    }

                    await idleTask.ConfigureAwait(false);
                }

                await Coordinator.DisposeAsync().ConfigureAwait(false);
                await DisposeProviderRuntimeAsync(_providerRuntime).ConfigureAwait(false);
                _disposedCompletion.TrySetResult();
            }
            catch (Exception ex)
            {
                _disposedCompletion.TrySetException(ex);
                throw;
            }
        }
    }
""");
    }

    private static string RestoreDeferred(string source)
    {
        source = Replace(source, "using CodeAlta.Agent;\n", "");
        source = Section(source, "            disposeStartupCancellation: () => startupCancellation?.Dispose(),", "    private TerminalLoopResult OnIteration(", """
            disposeStartupCancellation: () => startupCancellation?.Dispose());
    }

""" + "\n");
        source = Section(source, "        Action disposeStartupCancellation)\n        where TServices : class, IAsyncDisposable\n", "        ArgumentNullException.ThrowIfNull(cancelStartup);", """
        Action disposeStartupCancellation)
        where TServices : class, IAsyncDisposable
    {
""" + "\n");
        source = Replace(source, "        ArgumentNullException.ThrowIfNull(beginOwnedShutdown);\n        ArgumentNullException.ThrowIfNull(quiescePlugins);\n", "");
        source = Section(source, "            Task? appDisposalOriginal = null;", "            // A new disposal request", "");
        source = Section(source, "            try { beginOwnedShutdown(); }", "            if (app is not null)\n", "");
        source = Replace(source, "                    appDisposalOriginal = app.DisposeAsync().AsTask();\n                    await appDisposalOriginal;", "                    await app.DisposeAsync();");
        source = Replace(source, "                        servicesDisposalOriginal = returnedServices.DisposeAsync().AsTask();\n                        await servicesDisposalOriginal;", "                        await returnedServices.DisposeAsync();");
        source = Replace(source, "(failures ??= []).Add(CaptureOriginalFailure(appDisposalOriginal, ex).Reported);", "(failures ??= []).Add(ex);");
        source = Replace(source, "(failures ??= []).Add(CaptureOriginalFailure(servicesDisposalOriginal, ex).Reported);", "(failures ??= []).Add(ex);");
        source = Replace(source, "                    var evidence = CaptureOriginalFailure(startupTask, ex);\n", "");
        source = Replace(source, "(failures ??= []).Add(evidence.Reported);", "(failures ??= []).Add(ex);");
        source = Replace(source, "                    if (evidence.HasRetention ||\n                        (!ReferenceEquals(ex, reportedStartupFailure) && !IsExpectedDeferredStartupCancellation(\n                            startupTask, ex, startupToken, requestedAtObservation)))", "                    if (!ReferenceEquals(ex, reportedStartupFailure) &&\n                        !IsExpectedDeferredStartupCancellation(\n                            startupTask, ex, startupToken, requestedAtObservation))");
        source = Section(source, "    // Failure evidence preserves the actual task (or missing synchronous launch)", "    /// <summary>\n    /// Recognizes only canceled startup tasks", "");
        return Replace(source, "            ThrowIfRetained();\n\n", "");
    }

    private static string RestoreAsk(string source)
    {
        source = Replace(source, "using System.Runtime.ExceptionServices;\n", "");
        source = Replace(source, "using CodeAlta.Agent.Runtime;\n", "");
        source = Section(source, "    private sealed class Combined(", "    private Task<AgentToolResult> Invoke(", """
    private sealed class Combined(AgentRunLifecycle first, AgentRunLifecycle second) : AgentRunLifecycle
    {
        public override async Task StartedAsync(AgentRunId runId, CancellationToken executionToken)
        {
            await first.StartedAsync(runId, executionToken).ConfigureAwait(false);
            await second.StartedAsync(runId, executionToken).ConfigureAwait(false);
        }
        public override async Task ClosingAsync(AgentRunId runId)
        {
            // Start both original closing obligations before joining either, including partial start.
            Task a, b;
            try { a = first.ClosingAsync(runId); } catch (Exception ex) { a = Task.FromException(ex); }
            try { b = second.ClosingAsync(runId); } catch (Exception ex) { b = Task.FromException(ex); }
            var both = Task.WhenAll(a, b);
            try { await both.ConfigureAwait(false); }
            catch { if (a.Exception is not null && b.Exception is not null) throw new AggregateException(a.Exception.InnerExceptions.Concat(b.Exception.InnerExceptions)); throw; }
        }
    }

""" + "\n");
        return Section(source, "    internal sealed class Lifecycle(", "\n}", """
    private sealed class Lifecycle(OwnedSessionAskExecution ask, AgentRunLifecycle? previous) : AgentRunLifecycle
    {
        private readonly object _gate = new();
        private Task? _closing;
        public override async Task StartedAsync(AgentRunId runId, CancellationToken executionToken)
        {
            // AgentSession retains this original start and always invokes Closing, including start failure.
            if (previous is not null) await previous.StartedAsync(runId, executionToken).ConfigureAwait(false);
            await ask.StartedAsync(runId, executionToken).ConfigureAwait(false);
        }
        public override Task ClosingAsync(AgentRunId runId)
        {
            TaskCompletionSource launch;
            Task closing;
            lock (_gate)
            {
                if (_closing is not null) return _closing;
                launch = new(TaskCreationOptions.RunContinuationsAsynchronously);
                closing = _closing = CloseOriginalAsync(runId, launch.Task);
            }
            // Independent ask closure precedes a potentially noncooperative permission join.
            try { ask.Close(); }
            finally { launch.TrySetResult(); }
            return closing;
        }
        private async Task CloseOriginalAsync(AgentRunId runId, Task launch)
        {
            await launch.ConfigureAwait(false);
            if (previous is not null) await previous.ClosingAsync(runId).ConfigureAwait(false);
        }
    }
""");
    }

    private static string RestoreOwned(string source)
    {
        source = Replace(source, "using CodeAlta.Tui.Views;\n", "");
        source = Section(source, "                        PluginAgentEventFailurePolicy =", "                    },", "");
        source = Section(source, "            var acquisitions = new { Host = sharedHost", "            await RollbackOwnedServicesCreationAsync(", """
            await PluginEventDependencyBarrier.BeforeRollbackAsync(
                sharedHost?.PluginRuntime ?? prestartedPluginRuntime, creationFailure,
                new object?[] { sharedHost, modelsDevCatalogService, prestartedPluginRuntime }).ConfigureAwait(false);
""" + "\n");
        source = Replace(source, "    // Deferred startup calls this before entering the unchanged ShellFrontendHost plugin barrier.\n    // The Host pre-owns and memoizes the actual originals; no borrowed dependency is disposed here.\n    internal void BeginShutdownControls() => _host.BeginShutdownControls();\n\n", "");
        source = Section(source, "        var stages = new[]", "    /// <summary>\n    /// Awaits best-effort rollback", """
        List<Exception>? failures = null;
        try
        {
            await disposeHost().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            (failures ??= []).Add(exception);
        }

        try
        {
            await disposeModelsDevCatalog().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            (failures ??= []).Add(exception);
        }

        if (ownsLogging)
        {
            try
            {
                shutdownLogging();
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
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

""" + "\n");
        return Replace(source, "        if (Program.StartupOwner.ContainsRetention(creationFailure))\n            throw new AgentDependencyRetentionException(\"terminal creation\", \"retained inner acquisition\", [creationFailure], disposal);\n", "");
    }

    private static string RestoreCommands(string source)
    {
        source = Replace(source, "    private bool _retained;\n", "");
        source = Replace(source, "    private readonly OriginalInvocation _permissionShutdown = new();\n    private readonly OriginalInvocation _askDrain = new();\n", "");
        source = Replace(source, "if (_closed || _retained)", "if (_closed)", 4);
        source = Replace(source, "RecordFailure(ex, cleanup: false, operation.ReleaseDecision);", "RecordFailure(ex, cleanup: false);", 7);
        source = Replace(source, "RecordFailure(ex, cleanup: true, operation.ReleaseDecision);", "RecordFailure(ex, cleanup: true);", 6);
        source = Replace(source, "catch (OperationCanceledException failure) when (operation.Execution.IsCancellationRequested)\n        {\n            RecordFailure(failure, cleanup: false, operation.ReleaseDecision);", "catch (OperationCanceledException) when (operation.Execution.IsCancellationRequested)\n        {", 3);
        source = Replace(source, "catch (NotSupportedException failure)\n        {\n            RecordFailure(failure, cleanup: false, operation.ReleaseDecision);", "catch (NotSupportedException)\n        {", 2);
        source = Replace(source, "            operation.ReleaseDecision.Complete();\n", "", 3);
        source = Replace(source, "        operation.ReleaseDecision.Complete();\n        operation.Released = operation.ReleaseDecision.Released;", "        operation.Released = true;", 2);
        source = Replace(source, "operation.RuntimeInvocation.RunAsync(() => _runtime.SteerOwnedCommandAsync(operation.Request, operation.Execution.Token))", "_runtime.SteerOwnedCommandAsync(operation.Request, operation.Execution.Token)");
        source = Replace(source, "operation.RuntimeInvocation.RunAsync(() => _runtime.CompactOwnedCommandAsync(operation.Request, operation.Execution.Token))", "_runtime.CompactOwnedCommandAsync(operation.Request, operation.Execution.Token)");
        source = Replace(source, "operation.RuntimeInvocation.RunAsync(() => _runtime.AbortRunOwnedCommandAsync(operation.Request, operation.Execution.Token))", "_runtime.AbortRunOwnedCommandAsync(operation.Request, operation.Execution.Token)");
        source = Section(source, "            Task<OwnedSessionCommandResult>? runtimeOriginal = null;", "        }\n        catch (Exception ex)", "            result = await _runtime.QueueOwnedCommandAsync(operation.Request, operation.Receipt, _reviewPermissions,\n                operation.Execution.Token, _enableUserInput).ConfigureAwait(false);\n");
        source = Replace(source, "        operation.CancellationInvocation.Launch(() => operation.SourceCancellation = operation.Execution.CancelAsync());\n        operation.CancelLaunch.TrySetResult();", "        try { operation.SourceCancellation = operation.Execution.CancelAsync(); }\n        catch (Exception ex)\n        {\n            operation.CancellationFailed = true;\n            RecordFailure(ex, cleanup: true);\n        }\n        finally { operation.CancelLaunch.TrySetResult(); }");
        source = Section(source, "            if (await operation.CancellationInvocation.Outcome.ConfigureAwait(false) is { } failure)\n            {\n                if (operation.CancellationInvocation.Original is null)\n                    throw new AgentDependencyRetentionException(\"queue command\"", "        }\n        catch (Exception ex)", "            await operation.SourceCancellation.ConfigureAwait(false);\n");
        source = Replace(source, "        operation.CancellationInvocation.Launch(() => operation.Cancellation = operation.Execution.CancelAsync());\n        operation.CancellationStarted.TrySetResult();\n        operation.ControlLaunch.TrySetResult();", "        try\n        {\n            operation.Cancellation = operation.Execution.CancelAsync();\n        }\n        catch (Exception ex)\n        {\n            operation.CancellationFailed = true;\n            RecordFailure(ex, cleanup: true);\n        }\n        finally\n        {\n            operation.CancellationStarted.TrySetResult();\n            operation.ControlLaunch.TrySetResult();\n        }");
        source = Replace(source, "            operation.PreparationInvocation.Launch(() => operation.Preparation = PrepareAsync(operation));\n            if (await operation.PreparationInvocation.Outcome.ConfigureAwait(false) is { } preparationFailure) ExceptionDispatchInfo.Throw(preparationFailure);\n            var prepared = await operation.Preparation!.ConfigureAwait(false);", "            operation.Preparation = PrepareAsync(operation);\n            var prepared = await operation.Preparation.ConfigureAwait(false);");
        source = Replace(source, "                        operation.SendInvocation.Launch(() => operation.Send = _runtime.SendOwnedCommandAsync(prepared.Session, prepared.Options, sendOptions,\n                            operation.PermissionExecution, operation.Execution.Token, operation.AskExecution, operation.AskSubmission));\n                        if (await operation.SendInvocation.Outcome.ConfigureAwait(false) is { } sendFailure) ExceptionDispatchInfo.Throw(sendFailure);\n                        var runId = await operation.Send!.ConfigureAwait(false);", "                        operation.Send = _runtime.SendOwnedCommandAsync(prepared.Session, prepared.Options, sendOptions,\n                            operation.PermissionExecution, operation.Execution.Token, operation.AskExecution, operation.AskSubmission);\n                        var runId = await operation.Send.ConfigureAwait(false);");
        source = Section(source, "        try { operation.AskExecution?.Close(); }", "        if (operation.PermissionExecution is { } permission)", "        operation.AskExecution?.Close();\n");
        source = Replace(source, "            try\n            {\n                operation.PermissionCloseInvocation.Launch(() => _runtime.Permissions.CloseOwnedExecutionAsync(permission));\n                if (await operation.PermissionCloseInvocation.Outcome.ConfigureAwait(false) is { } failure) ExceptionDispatchInfo.Throw(failure);\n            }", "            try { await _runtime.Permissions.CloseOwnedExecutionAsync(permission).ConfigureAwait(false); }");
        source = Section(source, "    private async Task RunControlAsync(SendOperation operation)", "    // _gate owns slot release", """
    private async Task RunControlAsync(SendOperation operation)
    {
        await operation.ControlLaunch.Task.ConfigureAwait(false);
        var failed = operation.CancellationFailed;
        var attached = false;

        try
        {
            // StartControl already initiated source cancellation independently. Close this exact
            // operation before preparation/provider/cancellation joins, including provider None tokens.
            if (_reviewPermissions || _enableUserInput) await _runtime.Permissions.InvalidateOwnedOperationAsync(operation.Receipt.OperationId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failed = true;
            RecordFailure(ex, cleanup: true);
        }
        try
        {
            attached = await operation.Attachment.Task.ConfigureAwait(false) is not null;
            if (attached)
            {
                // Do not wait cancellation callbacks before making the real abort route available.
                await _runtime.AbortAsync(operation.SessionId, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            failed = true;
            RecordFailure(ex, cleanup: true);
        }
        try
        {
            await operation.Cancellation.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failed = true;
            RecordFailure(ex, cleanup: true);
        }
        var result = new OwnedSessionCommandResult(
            failed ? OwnedSessionCommandOutcome.Failed : OwnedSessionCommandOutcome.Completed,
            Code: failed ? "control_failed" : attached ? null : "not_attached");
        lock (_gate)
        {
            operation.ControlResult = result;
            foreach (var receipt in operation.AbortReceipts) receipt.Complete(result);
        }
    }

""" + "\n");
        source = Section(source, "    private void RecordFailure(Exception failure, bool cleanup,", "    /// <summary>Closes admission, signals every owned operation", """
    private void RecordFailure(Exception failure, bool cleanup)
    {
        lock (_gate)
        {
            _failures.Add(failure);
            if (cleanup) _cleanupFailures.Add(failure);
        }
    }

""" + "\n");
        source = Section(source, "        try { Asks.CloseAdmission(); }", "        foreach (var operation in operations) StartControl(operation);", "        Asks.CloseAdmission();\n");
        source = Replace(source, "            steer.CancellationInvocation.Launch(() => steer.Cancellation = steer.Execution.CancelAsync());", "            try { steer.Cancellation = steer.Execution.CancelAsync(); }\n            catch (Exception ex) { RecordFailure(ex, cleanup: true); }");
        source = Replace(source, "            compact.CancellationInvocation.Launch(() => compact.Cancellation = compact.Execution.CancelAsync());", "            try { compact.Cancellation = compact.Execution.CancelAsync(); }\n            catch (Exception ex) { RecordFailure(ex, cleanup: true); }");
        source = Replace(source, "            abortRun.CancellationInvocation.Launch(() => abortRun.Cancellation = abortRun.Execution.CancelAsync());", "            try { abortRun.Cancellation = abortRun.Execution.CancelAsync(); }\n            catch (Exception ex) { RecordFailure(ex, cleanup: true); }");
        source = Section(source, "    private async Task ObserveCancellationAsync(", "    private sealed record ReceiptEntry(", """
    private async Task DisposeCoreAsync(SendOperation[] operations, SteerOperation[] steers, CompactOperation[] compacts,
        AbortRunOperation[] abortRuns, QueueOperation[] queues, Task launch)
    {
        await launch.ConfigureAwait(false);
        if (_reviewPermissions || _enableUserInput)
        {
            try { await _runtime.Permissions.CloseOwnedAdmissionAsync().ConfigureAwait(false); }
            catch (Exception ex) { RecordFailure(ex, cleanup: true); }
        }
        foreach (var operation in operations)
        {
            if (operation.Control is not null)
                await operation.CancellationStarted.Task.ConfigureAwait(false);
        }
        foreach (var operation in operations)
        {
            try { await operation.Work.ConfigureAwait(false); }
            catch (Exception ex) { RecordFailure(ex, cleanup: true); }
            if (operation.Control is { } control)
            {
                try { await control.ConfigureAwait(false); }
                catch (Exception ex) { RecordFailure(ex, cleanup: true); }
            }
            // The wrappers have joined preparation, send, abort and cancellation before this point.
            try { operation.Execution.Dispose(); }
            catch (Exception ex) { RecordFailure(ex, cleanup: true); }
        }
        foreach (var steer in steers)
        {
            try { await steer.Work.ConfigureAwait(false); }
            catch (Exception ex) { RecordFailure(ex, cleanup: true); }
            try { await steer.Cancellation.ConfigureAwait(false); }
            catch (Exception ex) { RecordFailure(ex, cleanup: true); }
            try { steer.Execution.Dispose(); }
            catch (Exception ex) { RecordFailure(ex, cleanup: true); }
        }
        foreach (var compact in compacts)
        {
            try { await compact.Work.ConfigureAwait(false); }
            catch (Exception ex) { RecordFailure(ex, cleanup: true); }
            try { await compact.Cancellation.ConfigureAwait(false); }
            catch (Exception ex) { RecordFailure(ex, cleanup: true); }
            try { compact.Execution.Dispose(); }
            catch (Exception ex) { RecordFailure(ex, cleanup: true); }
        }
        foreach (var abortRun in abortRuns)
        {
            try { await abortRun.Work.ConfigureAwait(false); }
            catch (Exception ex) { RecordFailure(ex, cleanup: true); }
            try { await abortRun.Cancellation.ConfigureAwait(false); }
            catch (Exception ex) { RecordFailure(ex, cleanup: true); }
            try { abortRun.Execution.Dispose(); }
            catch (Exception ex) { RecordFailure(ex, cleanup: true); }
        }
        foreach (var queue in queues)
        {
            try { await queue.Work.ConfigureAwait(false); }
            catch (Exception ex) { RecordFailure(ex, cleanup: true); }
            if (queue.Cancellation is { } cancellation)
            {
                try { await cancellation.ConfigureAwait(false); }
                catch (Exception ex) { RecordFailure(ex, cleanup: true); }
            }
            try { queue.Execution.Dispose(); }
            catch (Exception ex) { RecordFailure(ex, cleanup: true); }
        }
        try { await Asks.DrainAsync().ConfigureAwait(false); }
        catch (Exception ex) { RecordFailure(ex, cleanup: true); }
        Exception[] failures;
        lock (_gate) failures = [.. _cleanupFailures];
        if (failures.Length == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Length > 1) throw new AggregateException(failures);
    }

""" + "\n");
        source = Replace(source, "        internal DependencyReleaseDecision ReleaseDecision { get; } = new();\n        internal OriginalInvocation RuntimeInvocation { get; } = new();\n        internal OriginalInvocation CancellationInvocation { get; } = new();\n", "", 4);
        source = Section(source, "        internal DependencyReleaseDecision ReleaseDecision { get; } = new();", "        internal OwnedTextSendRequest Request", "");
        source = Replace(source, "        internal Task? SourceCancellation { get; set; }", "        internal Task SourceCancellation { get; set; } = Task.CompletedTask;");
        source = Replace(source, "        internal Task? Cancellation { get; set; }\n    }", "        internal Task Cancellation { get; set; } = Task.CompletedTask;\n    }", 3);
        return Replace(source, "        internal Task? Cancellation { get; set; }\n        internal bool CancelRequested", "        internal Task Cancellation { get; set; } = Task.CompletedTask;\n        internal bool CancelRequested");
    }

    private static string Replace(string source, string current, string original, int count = 1)
    {
        Assert.IsTrue(current.Length > 0);
        Assert.AreEqual(count, source.Split(current, StringSplitOptions.None).Length - 1, current);
        return source.Replace(current, original, StringComparison.Ordinal);
    }

    // Whole-input authentication above makes an unexpected change inside a removed region fail closed.
    private static string Section(string source, string start, string end, string original)
    {
        Assert.AreEqual(1, source.Split(start, StringSplitOptions.None).Length - 1, start);
        var offset = source.IndexOf(start, StringComparison.Ordinal);
        var limit = source.IndexOf(end, offset + start.Length, StringComparison.Ordinal);
        Assert.IsTrue(limit > offset, end);
        return source[..offset] + original + source[limit..];
    }
}
