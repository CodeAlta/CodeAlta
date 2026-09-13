using System.Security.Cryptography;
using System.Text;

namespace CodeAlta.Tests;

/// <summary>Literal-only newest-delta restoration against 3e893ebc Git object anchors; no content reads.</summary>
internal static class OwnedSessionAskSourceInverse
{
    private const string Host = "CodeAlta.Orchestration/Hosting/CodeAltaHost.cs";
    private const string Options = "CodeAlta.Orchestration/Hosting/CodeAltaHostOptions.cs";
    private const string Runtime = "CodeAlta.Orchestration/Runtime/SessionRuntimeService.cs";
    private const string App = "CodeAlta/Desktop/DesktopApplication.cs";
    private const string Boot = "CodeAlta/Desktop/Rpc/BootRpc.cs";
    private const string Main = "CodeAlta/frontend/src/main.tsx";
    private const string Project = "CodeAlta.Desktop.Tests/CodeAlta.Desktop.Tests.csproj";
    private const string DesktopInverse = "CodeAlta.Tests/DesktopOwnedSessionSourceInverse.cs";
    private const string RuntimeInverse = "CodeAlta.Tests/RuntimeEventForwardingSourceInverse.cs";
    internal static IReadOnlyList<string> Paths => [Host, Options, Runtime, App, Boot, Main, Project, DesktopInverse, RuntimeInverse];
    internal static string RestoreDesktopInput(string path, string source)
        => path is Host or Options or App or Boot or Main or Project ? Restore(path, source) : source;
    internal static string RestoreRuntimeInput(string path, string source)
        => path is Runtime or DesktopInverse ? Restore(path, source) : source;

    internal static string Restore(string path, string source)
    {
        source = OwnedSessionNotesSourceInverse.RestoreInput(path, source);
        source = SourceTestText.Canonicalize(source);
        foreach (var (before, after, count) in Edits(path))
        {
            Assert.IsTrue(after.Length > 0, path);
            Assert.AreEqual(count, source.Split(after, StringSplitOptions.None).Length - 1, path + ": " + after);
            source = source.Replace(after, before, StringComparison.Ordinal);
        }
        Assert.AreEqual(Original(path), GitObjectId(source), path);
        return source;
    }

    // These are literal Git blob identities obtained with read-only rev-parse, not newly calculated test results.
    internal static string Original(string path) => path switch
    {
        Host => "2e142d2be512784eece60241889c72f22110eaed",
        Options => "cc9c044e9378ac8f366ab6aed1192c801da1639a",
        Runtime => "bb53452d1015a09f551146f7ff241cb14f4e81aa",
        App => "28170fd7c4858905e5b16d6a4a56163fa47f9b87",
        Boot => "95967cd0725fc16b133f6a2247c7bdcd4743b829",
        Main => "8d3e06b200bb40c1786d7ea15a4700bcd5ed241c",
        Project => "a94c54775a6f48378a72fe891160847aa3900b5f",
        DesktopInverse => "c29de6684e87266c11c0f7e976cc4940da9caf85",
        RuntimeInverse => "8de37cc5f4140ed8cb5f1d65c774ca82c4a13274",
        _ => throw new ArgumentException("Unlisted source.", nameof(path)),
    };

    internal static string GitObjectId(string source)
    {
        var bytes = new UTF8Encoding(false, true).GetBytes(SourceTestText.Canonicalize(source));
        var header = Encoding.ASCII.GetBytes("blob " + bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\0");
        return Convert.ToHexString(SHA1.HashData([.. header, .. bytes])).ToLowerInvariant();
    }

    private static IEnumerable<(string Before, string After, int Count)> Edits(string path)
    {
        switch (path)
        {
            case Host:
                yield return ("        bool reviewOwnedCommandPermissions)\n", "        bool reviewOwnedCommandPermissions,\n        bool enableOwnedAsks)\n", 1);
                yield return ("ownedCommandReceiptCapacity, reviewOwnedCommandPermissions);", "ownedCommandReceiptCapacity, reviewOwnedCommandPermissions, enableOwnedAsks);", 1);
                yield return ("                options.ReviewOwnedCommandPermissions);\n", "                options.ReviewOwnedCommandPermissions,\n                options.EnableOwnedAsks);\n", 1);
                break;
            case Options:
                yield return ("", "    /// <summary>Gets whether owned sends expose the restricted, operation-bound ask producer. Default is false.</summary>\n    public bool EnableOwnedAsks { get; init; }\n\n", 1);
                break;
            case Runtime:
                yield return ("        SessionPermissionService.OwnedPermissionExecution? permissionExecution = null, bool ownedCommand = false)\n",
                    "        SessionPermissionService.OwnedPermissionExecution? permissionExecution = null, bool ownedCommand = false,\n        OwnedSessionAskExecution? askExecution = null, OwnedAskSubmission? askSubmission = null)\n", 1);
                yield return ("", "                        AdditionalTools = sendOptions.AdditionalTools,\n", 1);
                yield return ("RunLifecycle = Permissions.CreateOwnedRunLifecycle(permissionExecution)", "RunLifecycle = OwnedSessionAskExecution.Combine(sendOptions.RunLifecycle, Permissions.CreateOwnedRunLifecycle(permissionExecution))", 1);
                yield return ("", "                if (askExecution is not null)\n                {\n                    askExecution.Bind(_runtimeInstanceId, candidate.Attachment.Ordinal, candidate.ProviderId);\n                    sendOptions = askExecution.Compose(sendOptions);\n                }\n", 1);
                yield return ("", "                askSubmission?.RecordRunReturned(runId);\n", 1);
                yield return ("", "                askExecution?.Close();\n", 2);
                yield return ("        AgentSendOptions sendOptions, SessionPermissionService.OwnedPermissionExecution? permissionExecution, CancellationToken cancellationToken)\n        => AdmitAsync(() => SendOwnedBodyAsync(session, options, sendOptions, cancellationToken, CancellationToken.None, permissionExecution, ownedCommand: true), CancellationToken.None);\n",
                    "        AgentSendOptions sendOptions, SessionPermissionService.OwnedPermissionExecution? permissionExecution, CancellationToken cancellationToken,\n        OwnedSessionAskExecution? askExecution = null, OwnedAskSubmission? askSubmission = null)\n        => AdmitAsync(() => SendOwnedBodyAsync(session, options, sendOptions, cancellationToken, CancellationToken.None, permissionExecution,\n            ownedCommand: true, askExecution: askExecution, askSubmission: askSubmission), CancellationToken.None);\n", 1);
                break;
            case App:
                yield return ("", "        SessionAsksService? asks = null;\n", 1);
                yield return ("", "                    asks?.CloseAdmission();\n", 1);
                yield return ("", "                EnableOwnedAsks = true,\n", 1);
                yield return ("", "                asks = new SessionAsksService(host.Commands.Asks, epoch);\n", 1);
                yield return ("", "                    builder.AddSessionAsksService(asks);\n", 1);
                yield return ("", "        asks?.CloseAdmission();\n", 1);
                break;
            case Boot:
                yield return ("CommandReviewEnabled = _commandReview };", "CommandReviewEnabled = _commandReview, OwnedAsksEnabled = true };", 1);
                yield return ("", "    public bool OwnedAsksEnabled { get; init; }\n", 1);
                yield return ("", "[JsonSerializable(typeof(SessionAsksRequest))]\n[JsonSerializable(typeof(SessionAsksPage))]\n[JsonSerializable(typeof(SessionAskActionRequest))]\n[JsonSerializable(typeof(SessionAskObservationRequest))]\n[JsonSerializable(typeof(SessionAskResult))]\n", 1);
                break;
            case Main:
                yield return ("sessionOperations, type BootStatus", "sessionOperations, sessionAsks, type BootStatus", 1);
                yield return ("", "import { AskPanel } from \"./AskPanel\";\nimport { askWireRequest, createAskActions } from \"./sessionAsks\";\n", 1);
                yield return ("", "  const [askActions] = useState(() => createAskActions(\n    request => sessionAsks.answer(askWireRequest(request), { timeoutMilliseconds: 8000 }),\n    request => sessionAsks.cancel(askWireRequest(request), { timeoutMilliseconds: 8000 })));\n", 1);
                yield return ("", "          {status?.ownedAsksEnabled && status.hostEpoch && mutation?.epoch === status.hostEpoch && <AskPanel\n            key={JSON.stringify([selectedSession.id, status.hostEpoch, \"asks\"])} epoch={status.hostEpoch} sessionId={selectedSession.id}\n            actions={askActions} capability={mutation.capability} />}\n", 1);
                break;
            case Project:
                yield return ("", "    <Compile Include=\"../CodeAlta.Tests/OwnedSessionAskSourceInverse.cs\" Link=\"OwnedSessionAskSourceInverse.cs\" />\n", 1);
                break;
            case DesktopInverse:
                yield return ("    internal static string RestoreInput(string path, string source) => path is\n",
                    "    internal static string RestoreInput(string path, string source)\n        => RestoreInputCore(path, OwnedSessionAskSourceInverse.RestoreDesktopInput(path, source));\n\n    private static string RestoreInputCore(string path, string source) => path is\n", 1);
                break;
            case RuntimeInverse:
                yield return ("", "        source = OwnedSessionAskSourceInverse.RestoreRuntimeInput(path, source);\n", 1);
                break;
            default: throw new ArgumentException("Unlisted source.", nameof(path));
        }
    }
}
