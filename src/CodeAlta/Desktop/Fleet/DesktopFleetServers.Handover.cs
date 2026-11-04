using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Fleet.Server;
using XenoAtom.Logging;

namespace CodeAlta.Desktop.Fleet;

// The automations a transfer moves between people (doc/fleet/access-control.md, ownership over time): the computer of its
// owner seals what one does for the devices of the person it is given to; that person's computer takes it at Resume and
// gives its machines versions of its own; the computer of its former owner takes it off once it was taken.
internal sealed partial class DesktopFleetServers
{
    private readonly Logger _handoverLogger = LogManager.GetLogger("CodeAlta.Desktop.Fleet.Handover");
    private int _followingTransfers;

    /// <summary>Gets or sets the automations of this computer as transfers move them; null where none moves.</summary>
    internal IDesktopAutomationHandover? Handover { get; set; }

    /// <summary>
    /// Offers a resource to another owner; an automation of this computer given to a person goes with what it does, sealed
    /// here for that person's devices as the device list of the server verifies them.
    /// </summary>
    /// <param name="linkId">The link.</param>
    /// <param name="request">The offer.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The transfer.</returns>
    /// <exception cref="KeyNotFoundException">No such link.</exception>
    /// <exception cref="FleetServerException">The server refused it.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    /// <exception cref="FleetDeviceListException">The device list does not verify.</exception>
    public async Task<TransferDto> OfferTransferAsync(string linkId, OfferTransferRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var client = Client(linkId);
        if (request.ResourceKind == FleetResourceKinds.Automation && request.ToKind == FleetPrincipalKinds.Person && Handover is { } handover
            && await handover.ReadAsync(request.ResourceId, cancellationToken).ConfigureAwait(false) is { } content && client.DeviceList is { } devices)
        {
            var list = await devices.RefreshAsync(cancellationToken).ConfigureAwait(false);
            var recipients = list.Members.Values.Where(member => member.Kind == FleetDeviceKinds.Controller && member.OwnerId == request.ToId).Select(static member => member.Key).ToArray();
            if (recipients.Length > 0)
            {
                var (sealedText, wraps) = FleetTransferHandover.Seal(list.OrganizationId ?? string.Empty, Guid.NewGuid().ToString("N"), request.ResourceId, content, recipients,
                    list.Sequence, Identity());
                request = request with { Handover = new TransferHandoverDto(sealedText, [.. wraps.Select(static wrap => new GrantKeyWrapDto(wrap.DeviceId, wrap.Wrapped))]) };
            }
        }

        return await client.OfferTransferAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Resumes a resource that waits for its owner. An automation handed over to this computer's person is taken here first,
    /// opened with this device's key once its former owner's signature holds: this computer then gives it versions of its own.
    /// </summary>
    /// <param name="linkId">The link.</param>
    /// <param name="kind">The kind of resource.</param>
    /// <param name="id">Its id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Why it could not be taken here, or null once it is resumed.</returns>
    /// <exception cref="KeyNotFoundException">No such link.</exception>
    /// <exception cref="FleetServerException">The server refused it.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    /// <exception cref="FleetDeviceListException">The device list does not verify.</exception>
    public async Task<string?> ResumeResourceAsync(string linkId, string kind, string id, CancellationToken cancellationToken)
    {
        using var client = Client(linkId);
        if (kind == FleetResourceKinds.Automation && Handover is { } handover && client.DeviceList is { } devices)
        {
            var self = await client.GetAccessSelfAsync(cancellationToken).ConfigureAwait(false);
            var given = (await client.ListTransfersAsync(cancellationToken).ConfigureAwait(false)).Items
                .Where(transfer => transfer.ResourceKind == kind && transfer.ResourceId == id && transfer.State == FleetTransferStates.Accepted)
                .OrderByDescending(static transfer => transfer.DecidedAt).FirstOrDefault();
            if (given is { HasHandover: true, ToKind: FleetPrincipalKinds.Person, FromKind: FleetPrincipalKinds.Person } && given.ToId == self.Person.Id)
            {
                var copy = await client.GetTransferHandoverAsync(given.Id, cancellationToken).ConfigureAwait(false);
                var list = await devices.RefreshAsync(cancellationToken).ConfigureAwait(false);
                FleetAutomationContent content;
                try
                {
                    content = FleetTransferHandover.Open(copy.Sealed, copy.Wrapped, list.OrganizationId ?? string.Empty, id, given.FromId, Identity(), list);
                }
                catch (InvalidDataException exception)
                {
                    return $"What was handed over does not open on this computer: {exception.Message}";
                }

                if (await handover.TakeAsync(content, cancellationToken).ConfigureAwait(false) is { } problem) return problem;
            }
        }

        await client.ResumeResourceAsync(kind, id, cancellationToken).ConfigureAwait(false);
        OnChanged();
        return null;
    }

    // Takes off this computer the automations this person gave away and that their new owner took: the last transfer of each
    // decides, so that one given back later stays. One read at a time; a read asked meanwhile is folded into it.
    private async Task FollowTransfersAsync(string linkId)
    {
        if (Handover is not { } handover || Interlocked.Exchange(ref _followingTransfers, 1) != 0) return;
        try
        {
            using var client = Client(linkId);
            var self = await client.GetAccessSelfAsync(CancellationToken.None).ConfigureAwait(false);
            var latest = (await client.ListTransfersAsync(CancellationToken.None).ConfigureAwait(false)).Items
                .Where(static transfer => transfer.ResourceKind == FleetResourceKinds.Automation && transfer.State == FleetTransferStates.Accepted)
                .GroupBy(static transfer => transfer.ResourceId, StringComparer.Ordinal)
                .Select(static group => group.OrderByDescending(static transfer => transfer.DecidedAt).First());
            foreach (var transfer in latest.Where(transfer => transfer.FromKind == FleetPrincipalKinds.Person && transfer.FromId == self.Person.Id))
            {
                if (await handover.GiveUpAsync(transfer.ResourceId, CancellationToken.None).ConfigureAwait(false) && LogManager.IsInitialized)
                    _handoverLogger.Info($"The automation {transfer.ResourceId} was taken by its new owner: it leaves this computer and its machines.");
            }
        }
        catch (Exception exception) when (exception is FleetServerException or HttpRequestException or IOException or KeyNotFoundException or OperationCanceledException)
        {
            if (LogManager.IsInitialized) _handoverLogger.Info($"The transfers of {linkId} could not be read: {exception.Message}");
        }
        finally
        {
            Volatile.Write(ref _followingTransfers, 0);
        }
    }
}
