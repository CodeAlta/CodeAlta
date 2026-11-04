using System.Buffers.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;

namespace CodeAlta.Fleet.Server;

/// <summary>A next version of the policy, ready to sign: what it says, and what it changes as this device reads it.</summary>
/// <param name="Document">The version, with this device as its signer.</param>
/// <param name="Changes">What it changes from the policy this device verified, for people.</param>
public sealed record FleetPolicyDraft(FleetPolicyDocument Document, IReadOnlyList<string> Changes);

/// <summary>
/// The signed policy of the organization of one server link, as this device last verified it (<c>doc/fleet/security-and-privacy.md</c>,
/// policies and the kill switch): the chain read from the server and replayed against the device list and the access document
/// this device anchored. The first read pins the first version; every accepted read moves a checkpoint a later chain must
/// extend, and keeps the policy it verified in the folder of the link, so that a machine applies it, its kill switch first,
/// while the server is unreachable and after a restart. A chain that does not verify changes nothing.
/// </summary>
public sealed class FleetOrganizationPolicy
{
    /// <summary>The name of the file of the pin and of the policy kept, in the folder of a link.</summary>
    public const string PinFileName = "policy.json";

    private readonly string? _path;
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private FleetPolicyState? _current;
    private bool _unreadable;
    // Whether the state was verified in this process: a state read from the file is extended only after a whole chain verified.
    private bool _verifiedHere;

    /// <summary>Initializes a new instance of the <see cref="FleetOrganizationPolicy"/> class, with the policy this device kept.</summary>
    /// <param name="access">The anchored access document of the same link, which says who signs.</param>
    /// <param name="path">The file of the pin and of the policy kept; null keeps them in memory.</param>
    /// <exception cref="ArgumentNullException"><paramref name="access"/> is null.</exception>
    public FleetOrganizationPolicy(FleetOrganizationAccess access, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(access);
        Access = access;
        _path = path;
        _current = Read(out _unreadable);
    }

    /// <summary>Gets the anchored access document of the link.</summary>
    public FleetOrganizationAccess Access { get; }

    /// <summary>Gets the anchored device list of the link.</summary>
    public FleetOrganizationDevices Devices => Access.Devices;

    /// <summary>
    /// Gets the policy as last verified, kept across restarts; null before the first read, and <see cref="FleetPolicyState.Empty"/>
    /// while the organization signed none.
    /// </summary>
    public FleetPolicyState? Current => Volatile.Read(ref _current);

    /// <summary>Raised after a read verified a newer version than the one held.</summary>
    public event Action<FleetPolicyState>? Changed;

    /// <summary>
    /// Reads the chain from the server again and verifies it against the device list and the access document (read again
    /// first), and checks it extends the version this device verified before.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The verified policy.</returns>
    /// <exception cref="FleetPolicyDocumentException">The chain does not verify, or does not extend the one verified before.</exception>
    /// <exception cref="FleetAccessDocumentException">The access document does not verify.</exception>
    /// <exception cref="FleetDeviceListException">The device list does not verify.</exception>
    /// <exception cref="FleetServerException">The server refused the read.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<FleetPolicyState> RefreshAsync(CancellationToken cancellationToken)
    {
        await _refresh.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Repaired or deleted meanwhile: read again; still unreadable: refused.
            if (_unreadable && Read(out _unreadable) is var again && !_unreadable) Volatile.Write(ref _current, again);
            if (_unreadable)
                throw new FleetPolicyDocumentException(0, $"the policy this device kept for this server does not read ({_path}), and with it the checkpoint of its chain: "
                    + "repair or delete that file to read the policy of the server again");
            var access = await Access.RefreshAsync(cancellationToken).ConfigureAwait(false);
            var devices = Devices.Current ?? await Devices.RefreshAsync(cancellationToken).ConfigureAwait(false);
            using var client = Devices.CreateClient();
            var held = Volatile.Read(ref _current);
            var organization = devices.OrganizationId ?? access.OrganizationId ?? string.Empty;
            // Verified before in this process: only the versions after it are read, each checked to follow the one before it.
            if (_verifiedHere && held is { Number: > 0 })
            {
                var after = await client.ListPolicyDocumentsAsync(held.Number, cancellationToken).ConfigureAwait(false);
                if (after.Items.Count == 0) return held;
                if (FleetPolicyDocuments.TryParse(after.Items[^1].Document.Payload, out var newest) && newest.DeviceListSequence > devices.Sequence)
                    devices = await Devices.RefreshAsync(cancellationToken).ConfigureAwait(false);
                var extended = held;
                var taken = new List<(FleetSignedPolicyDocument, FleetPolicyState)>(after.Items.Count);
                foreach (var item in after.Items)
                {
                    if (!FleetPolicyDocuments.TryAppend(extended, item.Document, devices, Access.At, organization, out var next, out var reason))
                        throw new FleetPolicyDocumentException(extended.Number + 1, reason);
                    extended = next;
                    taken.Add((item.Document, next));
                }

                return AcceptEndorsed(held, taken, devices);
            }

            var listed = await client.ListPolicyDocumentsAsync(0, cancellationToken).ConfigureAwait(false);
            var needed = listed.Items.Count == 0 ? 0 : FleetPolicyDocuments.TryParse(listed.Items[^1].Document.Payload, out var last) ? last.DeviceListSequence : long.MaxValue;
            if (devices.Sequence < needed) devices = await Devices.RefreshAsync(cancellationToken).ConfigureAwait(false);
            // Replayed one version at a time, so that the versions after the checkpoint can be held to the authority of now.
            var state = FleetPolicyState.Empty;
            var replayed = new List<(FleetSignedPolicyDocument, FleetPolicyState)>(listed.Items.Count);
            foreach (var item in listed.Items)
            {
                if (!FleetPolicyDocuments.TryAppend(state, item.Document, devices, Access.At, organization, out var next, out var reason))
                    throw new FleetPolicyDocumentException(state.Number + 1, reason);
                state = next;
                replayed.Add((item.Document, next));
            }

            if (held is { Number: > 0 })
            {
                // The chain held before is the anchor: the same first version, and the version held at the same place.
                if (state.Number < held.Number)
                    throw new FleetPolicyDocumentException(state.Number + 1, $"it is shorter than the {held.Number} versions this device verified: versions were taken away");
                if (!string.Equals(state.FirstHash, held.FirstHash, StringComparison.Ordinal))
                    throw new FleetPolicyDocumentException(1, "it starts with another version than the one this device trusted for this server");
                if (!string.Equals(FleetPolicyDocuments.Hash(listed.Items[(int)held.Number - 1].Document), held.LastHash, StringComparison.Ordinal))
                    throw new FleetPolicyDocumentException(held.Number, "it goes another way than the chain this device verified before");
            }

            _verifiedHere = true;
            // Without a checkpoint, the first read trusts the chain as it is (as the first read of the device list does); with
            // one, the versions after it are taken for the first time.
            return held is { Number: > 0 } ? AcceptEndorsed(held, replayed.Skip((int)held.Number).ToList(), devices) : Accept(held, state);
        }
        finally
        {
            _refresh.Release();
        }
    }

    /// <summary>
    /// Prepares the next version for this controller device to sign: the rules and the stop it sets, after the policy this
    /// device verified, at the device list entry and the access document version this device verified, and what it changes as
    /// this device reads it.
    /// </summary>
    /// <param name="rules">The rules of the next version.</param>
    /// <param name="stop">The kill switch of the next version, or null to let the work of the fleet go on.</param>
    /// <param name="nameOf">Gives the name of a machine or a team by its id, for the changes people read; null shows ids.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The draft; its changes are empty when it changes nothing.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="rules"/> is null.</exception>
    /// <exception cref="ArgumentException">The version does not hold (a rule that does not read, a stop without what it stops).</exception>
    /// <exception cref="FleetPolicyDocumentException">The chain of the server does not verify.</exception>
    /// <exception cref="FleetServerException">The server refused the read.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<FleetPolicyDraft> PrepareAsync(FleetPolicyRules rules, FleetPolicyStop? stop, Func<string, string?>? nameOf, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var state = await RefreshAsync(cancellationToken).ConfigureAwait(false);
        var devices = Devices.Current ?? await Devices.RefreshAsync(cancellationToken).ConfigureAwait(false);
        var access = Access.Current ?? FleetAccessState.Empty;
        var document = new FleetPolicyDocument(FleetPolicyDocument.CurrentVersion, devices.OrganizationId ?? access.OrganizationId ?? string.Empty, state.Number + 1, state.LastHash,
            Math.Max(devices.Sequence, state.DeviceListSequence), Math.Max(access.Number, state.AccessNumber), rules, stop, Devices.Identity.DeviceId, DateTimeOffset.UtcNow);
        if (FleetPolicyDocuments.Validate(document) is { } problem) throw new ArgumentException($"The policy does not hold: {problem}.", nameof(rules));
        return new FleetPolicyDraft(document, FleetPolicyDocuments.Describe(state, document, nameOf ?? (static _ => null)));
    }

    /// <summary>Signs a version this device prepared and gives it to the server, then reads the chain again.</summary>
    /// <param name="draft">The version, from <see cref="PrepareAsync"/>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The verified policy, with the new version.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="draft"/> is null.</exception>
    /// <exception cref="FleetServerException">The server refused it (this person does not sign, or another version came first: prepare again).</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<FleetPolicyState> SignAsync(FleetPolicyDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var text = FleetPolicyDocuments.Serialize(draft.Document with { SignerDeviceId = Devices.Identity.DeviceId });
        var signed = new FleetSignedPolicyDocument(text, Base64Url.EncodeToString(Devices.Identity.SignData(FleetPolicyDocuments.GetSignedBytes(text))));
        using (var client = Devices.CreateClient())
            await client.AppendPolicyDocumentAsync(signed, cancellationToken).ConfigureAwait(false);
        return await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    // Versions taken for the first time stand only up to the last one whose signer holds authority now (the device list and the
    // access document as this device holds them now): a later signer who holds it vouches for the versions before it; after
    // it, a version a revoked signer made against their past authority is refused, and the policy stays as that last one left it.
    private FleetPolicyState AcceptEndorsed(FleetPolicyState held, IReadOnlyList<(FleetSignedPolicyDocument Document, FleetPolicyState State)> taken, FleetDeviceListState devices)
    {
        var endorsed = held;
        string? refusal = null;
        var access = Access.Current;
        foreach (var (document, state) in taken)
        {
            if (FleetPolicyDocuments.CheckCurrentSigner(document, devices, access) is { } why)
            {
                refusal ??= why;
            }
            else
            {
                endorsed = state;
                refusal = null;
            }
        }

        var accepted = Accept(held, endorsed);
        return refusal is null ? accepted : throw new FleetPolicyDocumentException(endorsed.Number + 1, refusal);
    }

    private FleetPolicyState Accept(FleetPolicyState? held, FleetPolicyState state)
    {
        if (held is not null && held.Number == state.Number && held.LastHash == state.LastHash) return held;
        Write(state);
        Volatile.Write(ref _current, state);
        Changed?.Invoke(state);
        return state;
    }

    /// <summary>
    /// Gets whether the policy this device kept for the link does not read: its rules, its stop and the checkpoint of its chain
    /// are lost, so the work of the fleet stops (<see cref="Current"/> holds a stop that says so) and the chain of the server is
    /// not trusted again until the file is repaired or deleted.
    /// </summary>
    public bool KeptPolicyUnreadable => Volatile.Read(ref _unreadable);

    // The policy this device verified, read back as it wrote it: the device trusts its own folder as it trusts its pins. A
    // file that exists but does not read fails closed: established restrictions never silently become "no policy".
    private FleetPolicyState? Read(out bool unreadable)
    {
        unreadable = false;
        if (_path is null) return null;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (JsonSerializer.Deserialize(File.ReadAllBytes(_path), FleetPolicyKeptJson.Default.PolicyKept) is not { Root.Length: > 0 } kept || kept.Rules is null)
                    return Unreadable(out unreadable);
                return new FleetPolicyState(kept.Organization, kept.Number, kept.Tip, kept.List, kept.Access, kept.Rules, kept.Stop, kept.Signer, kept.Time) { FirstHash = kept.Root };
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                return null;
            }
            catch (JsonException)
            {
                return Unreadable(out unreadable);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Another instance of the link may be replacing it: read again, then fail closed.
                if (attempt >= 2) return Unreadable(out unreadable);
                Thread.Sleep(50);
            }
        }
    }

    private FleetPolicyState Unreadable(out bool unreadable)
    {
        unreadable = true;
        return FleetPolicyState.Empty with
        {
            Stop = new FleetPolicyStop(FleetPolicyStopScopes.Fleet, [], [], $"the policy this machine kept for this server does not read ({_path}): repair or delete that file"),
        };
    }

    // The checkpoint only moves forward, also when another instance of the same link wrote it meanwhile.
    private void Write(FleetPolicyState state)
    {
        if (_path is null || state.Number == 0) return;
        try
        {
            if (Read(out _) is { } stored && stored.Number > state.Number) return;
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            FleetFiles.WriteAtomically(_path, JsonSerializer.SerializeToUtf8Bytes(new PolicyKept(state.FirstHash!, state.Number, state.LastHash!, state.OrganizationId, state.DeviceListSequence,
                state.AccessNumber, state.Rules, state.Stop, state.SignerDeviceId, state.Time), FleetPolicyKeptJson.Default.PolicyKept));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Held in memory meanwhile; the next accepted read writes it.
        }
    }
}

/// <summary>The policy a device verified, kept in the folder of a link: the anchor of the chain and what it says.</summary>
internal sealed record PolicyKept(string Root, long Number, string Tip, string? Organization, long List, long Access, FleetPolicyRules Rules, FleetPolicyStop? Stop, string? Signer,
    DateTimeOffset? Time);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(PolicyKept))]
internal sealed partial class FleetPolicyKeptJson : JsonSerializerContext;
