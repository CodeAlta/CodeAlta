using System.Text.Json;
using System.Text.Json.Serialization;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;

namespace CodeAlta.Fleet.Server;

/// <summary>A next version of the access document, ready to sign: what it says, and what it changes as this device reads it.</summary>
/// <param name="Document">The version, with this device as its signer.</param>
/// <param name="Changes">What it changes from the version this device verified, for people.</param>
public sealed record FleetAccessDraft(FleetAccessDocument Document, IReadOnlyList<string> Changes);

/// <summary>
/// The signed access document of the organization of one server link, as this device last verified it
/// (<c>doc/fleet/access-control.md</c>, what the node decides): the chain read from the server and replayed against the device
/// list this device anchored, itself anchored in the folder of the link. The first read pins the first version; every accepted
/// read moves a checkpoint a later chain must extend, so a server can neither give a machine another chain nor take a version
/// back (a suspension, a team member removed).
/// </summary>
/// <remarks>
/// Machines decide from <see cref="Current"/> who may use a team machine and whose devices and automations they refuse; consoles
/// of signers prepare and sign the next version with <see cref="PrepareAsync"/> and <see cref="SignAsync"/>, saying what it
/// changes from what they verified, never from what the server says changes.
/// </remarks>
public sealed class FleetOrganizationAccess
{
    /// <summary>The name of the file of the pin in the folder of a link.</summary>
    public const string PinFileName = "access-document.json";

    private readonly string? _pinPath;
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private FleetAccessState? _current;
    private AccessPin? _checkpoint;
    private bool _verifiedHere;
    // Each version verified in this process, by number: the policy names the version its signer read, and is checked against it.
    private System.Collections.Immutable.ImmutableDictionary<long, FleetAccessState> _history = System.Collections.Immutable.ImmutableDictionary<long, FleetAccessState>.Empty;

    /// <summary>Initializes a new instance of the <see cref="FleetOrganizationAccess"/> class.</summary>
    /// <param name="devices">The anchored device list of the same link.</param>
    /// <param name="pinPath">The file of the pin; null keeps it in memory.</param>
    /// <exception cref="ArgumentNullException"><paramref name="devices"/> is null.</exception>
    public FleetOrganizationAccess(FleetOrganizationDevices devices, string? pinPath = null)
    {
        ArgumentNullException.ThrowIfNull(devices);
        Devices = devices;
        _pinPath = pinPath;
        _current = Kept();
    }

    /// <summary>Gets the anchored device list of the link.</summary>
    public FleetOrganizationDevices Devices { get; }

    /// <summary>Gets the document as last verified; null before the first read, and <see cref="FleetAccessState.Empty"/> while the organization signed none.</summary>
    public FleetAccessState? Current => Volatile.Read(ref _current);

    /// <summary>Gets the document at a version this device verified in this process, or null.</summary>
    /// <param name="number">The number of the version.</param>
    /// <returns>The document as that version left it, or null.</returns>
    public FleetAccessState? At(long number) => Volatile.Read(ref _history).GetValueOrDefault(number);

    /// <summary>Raised after a read verified a newer version than the one held.</summary>
    public event Action<FleetAccessState>? Changed;

    /// <summary>
    /// Reads the chain from the server again, verifies it against the device list (read again first when this device holds
    /// none), and checks it extends the version this device verified before.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The verified document.</returns>
    /// <exception cref="FleetAccessDocumentException">The chain does not verify, or does not extend the one verified before.</exception>
    /// <exception cref="FleetDeviceListException">The device list does not verify.</exception>
    /// <exception cref="FleetServerException">The server refused the read.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<FleetAccessState> RefreshAsync(CancellationToken cancellationToken)
    {
        await _refresh.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var client = Devices.CreateClient();
            // Verified before in this process: only the versions after it are read, each checked to follow the one before it. An
            // answer that does not start right after it (a gap, a fork, a server that lists from the start) is verified whole below.
            // A version kept in the pin decides before the first read, which verifies the whole chain: the policy needs its history.
            if (_verifiedHere && Volatile.Read(ref _current) is { Number: > 0, OrganizationId: { } known } held
                && await client.ListAccessDocumentsAsync(held.Number, cancellationToken).ConfigureAwait(false) is var after
                && (after.Items.Count == 0 || FleetAccessDocuments.TryParse(after.Items[0].Document.Payload, out var first) && first.Number == held.Number + 1))
            {
                if (after.Items.Count == 0) return held;
                var needs = FleetAccessDocuments.TryParse(after.Items[^1].Document.Payload, out var newest) ? newest.DeviceListSequence : long.MaxValue;
                var list = Devices.Current is { } current && current.Sequence >= needs ? current : await Devices.RefreshAsync(cancellationToken).ConfigureAwait(false);
                var extended = held;
                foreach (var item in after.Items)
                {
                    if (!FleetAccessDocuments.TryAppend(extended, item.Document, list, known, out var next, out var reason))
                        throw new FleetAccessDocumentException(extended.Number + 1, reason);
                    extended = next;
                    Volatile.Write(ref _history, Volatile.Read(ref _history).SetItem(next.Number, next));
                }

                WritePin(new AccessPin(extended.FirstHash!, extended.Number, extended.LastHash!) { Last = after.Items[^1].Document });
                Volatile.Write(ref _current, extended);
                Changed?.Invoke(extended);
                return extended;
            }

            var listed = await client.ListAccessDocumentsAsync(0, cancellationToken).ConfigureAwait(false);
            var devices = Devices.Current;
            var needed = listed.Items.Count == 0 ? 0 : FleetAccessDocuments.TryParse(listed.Items[^1].Document.Payload, out var last) ? last.DeviceListSequence : long.MaxValue;
            if (devices is null || devices.Sequence < needed) devices = await Devices.RefreshAsync(cancellationToken).ConfigureAwait(false);
            var organization = devices.OrganizationId ?? (listed.Items.Count == 0 ? string.Empty : throw new FleetAccessDocumentException(1, "the device list names no organization yet"));
            // Replayed one version at a time, so that each one is kept for the policy that names it.
            var state = FleetAccessState.Empty;
            var history = System.Collections.Immutable.ImmutableDictionary.CreateBuilder<long, FleetAccessState>();
            foreach (var item in listed.Items)
            {
                if (!FleetAccessDocuments.TryAppend(state, item.Document, devices, organization, out var next, out var reason))
                    throw new FleetAccessDocumentException(state.Number + 1, reason);
                state = next;
                history[state.Number] = state;
            }

            var anchor = Newer(ReadPin(), _checkpoint);
            if (anchor is not null && anchor.Number > 0)
            {
                if (state.Number < anchor.Number)
                    throw new FleetAccessDocumentException(state.Number + 1, $"it is shorter than the {anchor.Number} versions this device verified: versions were taken away");
                if (!string.Equals(state.FirstHash, anchor.Root, StringComparison.Ordinal))
                    throw new FleetAccessDocumentException(1, "it starts with another version than the one this device trusted for this server");
                if (!string.Equals(FleetAccessDocuments.Hash(listed.Items[(int)anchor.Number - 1].Document), anchor.Tip, StringComparison.Ordinal))
                    throw new FleetAccessDocumentException(anchor.Number, "it goes another way than the chain this device verified before");
            }

            var previous = Volatile.Read(ref _current);
            var firstHere = !_verifiedHere;
            if (state.Number > 0) WritePin(new AccessPin(state.FirstHash!, state.Number, state.LastHash!) { Last = listed.Items[^1].Document });
            Volatile.Write(ref _history, history.ToImmutable());
            Volatile.Write(ref _current, state);
            _verifiedHere = true;
            if (previous is null || firstHere || previous.Number != state.Number || previous.LastHash != state.LastHash) Changed?.Invoke(state);
            return state;
        }
        finally
        {
            _refresh.Release();
        }
    }

    /// <summary>
    /// Prepares the next version for this controller device to sign: the server's draft, checked to follow the version this
    /// device verified, signed at the entry of the device list this device verified, and what it changes as this device reads it.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The draft, or null when it changes nothing.</returns>
    /// <exception cref="FleetAccessDocumentException">The draft does not follow the version this device verified.</exception>
    /// <exception cref="FleetServerException">The server refused the read (this device's person is not a signer).</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<FleetAccessDraft?> PrepareAsync(CancellationToken cancellationToken)
    {
        var state = await RefreshAsync(cancellationToken).ConfigureAwait(false);
        using var client = Devices.CreateClient();
        var draft = await client.GetAccessDraftAsync(cancellationToken).ConfigureAwait(false);
        var document = draft.Document;
        if (document.Number != state.Number + 1 || !string.Equals(document.Previous, state.LastHash, StringComparison.Ordinal) || document.OrganizationId != state.OrganizationId
            && state.Number > 0)
            throw new FleetAccessDocumentException(document.Number, "the draft of the server does not follow the version this device verified");
        if (state.Number > 0 && FleetAccessDocuments.SaySame(state, document)) return null;
        var devices = Devices.Current ?? await Devices.RefreshAsync(cancellationToken).ConfigureAwait(false);
        var signed = document with { DeviceListSequence = Math.Max(devices.Sequence, state.DeviceListSequence), SignerDeviceId = Devices.Identity.DeviceId, Time = DateTimeOffset.UtcNow };
        var names = draft.Names;
        var changes = FleetAccessDocuments.Describe(state, signed, id => names.TryGetValue(id, out var name) ? name : null);
        return new FleetAccessDraft(signed, changes.Count == 0 ? ["The first version of the access document"] : changes);
    }

    /// <summary>Signs a version this device prepared and gives it to the server, then reads the chain again.</summary>
    /// <param name="draft">The version, from <see cref="PrepareAsync"/>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The verified document, with the new version.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="draft"/> is null.</exception>
    /// <exception cref="FleetServerException">The server refused it (access changed meanwhile: prepare again).</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public async Task<FleetAccessState> SignAsync(FleetAccessDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var text = FleetAccessDocuments.Serialize(draft.Document with { SignerDeviceId = Devices.Identity.DeviceId });
        var signed = new FleetSignedAccessDocument(text, System.Buffers.Text.Base64Url.EncodeToString(Devices.Identity.SignData(FleetAccessDocuments.GetSignedBytes(text))));
        using (var client = Devices.CreateClient())
            await client.AppendAccessDocumentAsync(signed, cancellationToken).ConfigureAwait(false);
        return await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    // The version the pin keeps, as this device verified it: it decides while the server is unreachable after a restart (who
    // is suspended, the team of this machine), until the first read verifies the chain again. One that does not hash to the tip
    // of the pin is not trusted.
    private FleetAccessState? Kept()
    {
        try
        {
            if (ReadPin() is not { Number: > 0, Last: { } last } pin || FleetAccessDocuments.Hash(last) != pin.Tip
                || !FleetAccessDocuments.TryParse(last.Payload, out var document) || document.Number != pin.Number)
                return null;
            var state = FleetAccessDocuments.StateOf(document, pin.Tip, pin.Root);
            Volatile.Write(ref _history, Volatile.Read(ref _history).SetItem(state.Number, state));
            return state;
        }
        catch (FleetAccessDocumentException)
        {
            return null;
        }
    }

    private static AccessPin? Newer(AccessPin? stored, AccessPin? held)
        => stored is null ? held : held is null || stored.Number >= held.Number ? stored : held;

    private AccessPin? ReadPin()
    {
        if (_pinPath is null) return null;
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllBytes(_pinPath), FleetAccessPinJson.Default.AccessPin) is { Root.Length: > 0 } pin ? pin : null;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (JsonException)
        {
            // A pin that does not read trusts no chain from its first version again.
            throw new FleetAccessDocumentException(0, "the anchor of the access document of this server does not read");
        }
    }

    // The checkpoint only moves forward, also when another instance of the same link wrote it meanwhile.
    private void WritePin(AccessPin pin)
    {
        _checkpoint = pin;
        if (_pinPath is null) return;
        try
        {
            if (ReadPin() is { } stored && (stored.Number > pin.Number || stored.Number == pin.Number && stored.Last is not null)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(_pinPath)!);
            FleetFiles.WriteAtomically(_pinPath, JsonSerializer.SerializeToUtf8Bytes(pin, FleetAccessPinJson.Default.AccessPin));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Held in memory meanwhile; the next accepted read writes it.
        }
    }
}

/// <summary>The anchor of the access document in the folder of a link.</summary>
/// <param name="Root">The hash of its first version.</param>
/// <param name="Number">The number of the last version accepted.</param>
/// <param name="Tip">Its hash.</param>
internal sealed record AccessPin(string Root, long Number, string Tip)
{
    /// <summary>Gets the version at the tip, signed, as this device verified it; null in a pin written before it was kept.</summary>
    public FleetSignedAccessDocument? Last { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(AccessPin))]
internal sealed partial class FleetAccessPinJson : JsonSerializerContext;
