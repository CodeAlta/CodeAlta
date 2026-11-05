using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;
using XenoAtom.Logging;

namespace CodeAlta.Fleet.Server;

public sealed partial class FleetConnectorOptions
{
    /// <summary>Gets the file that pins the first version of the access document of the server (<see cref="FleetOrganizationAccess"/>); null pins it in memory.</summary>
    public string? AccessPinPath { get; init; }

    /// <summary>Gets the anchored access document of the server; null makes one on <see cref="AccessPinPath"/> beside the device list.</summary>
    public FleetOrganizationAccess? Access { get; init; }

    /// <summary>
    /// Gets what is told once a newer version of the access document verified (<see cref="Protocol.Api.FleetAccessMethods.Changed"/>,
    /// and at each connection): a node then stops the work of suspended people. Null tells nobody.
    /// </summary>
    public Action<FleetAccessState>? AccessChanged { get; init; }

    /// <summary>
    /// Gets what is told each time this device read access again because the server said it changed (and at each connection):
    /// a desktop then follows the transfers of its person. Null tells nobody.
    /// </summary>
    public Action? AccessRead { get; init; }
}

// The signed access document of the server, read again when the server says access changed and at each connection.
public sealed partial class FleetConnector
{
    private readonly Logger _accessLogger = LogManager.GetLogger("CodeAlta.Fleet.Access");
    private int _accessReading;
    private int _accessAgain;

    /// <summary>Gets the access document of the organization of the server, as this device verified it.</summary>
    public FleetOrganizationAccess Access { get; private set; } = null!;

    /// <summary>Gets the titles of the sessions grants of sealed content give this device, opened here; read again when access changes.</summary>
    public FleetGrantKeyring GrantKeys { get; private set; } = null!;

    /// <summary>
    /// Gets the trust a node gives the members of the organization on a relayed channel: the device list and the access
    /// document, both as this device verified them, read live at each call.
    /// </summary>
    public IDeviceTrustPolicy Trust => new FleetOrganizationTrust(() => Devices.Current, _options.Identity.DeviceId, Access);

    private void InitializeAccess()
    {
        var access = Access = _options.Access ?? new FleetOrganizationAccess(Devices, _options.AccessPinPath);
        // The keys follow the owner the signed access document names once a machine was given to another person.
        GrantKeys = new FleetGrantKeyring(Devices) { Access = () => access.Current };
        if (_options.MetadataKeys is { Access: null } keys) keys.Access = () => access.Current;
        if (_options.AccessChanged is { } changed) Access.Changed += changed;
    }

    // Read beside the reader of the connection, one at a time; a read asked while one runs is made again after it, never
    // dropped: the second access.changed of a deprovisioning (the person suspended, then the new version signed) can arrive
    // while the read the first one started still runs, and the node must not keep trusting that person.
    private void RefreshAccess(CancellationToken stop)
    {
        Volatile.Write(ref _accessAgain, 1);
        if (Interlocked.Exchange(ref _accessReading, 1) != 0) return;
        Track(Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    while (Interlocked.Exchange(ref _accessAgain, 0) != 0 && !stop.IsCancellationRequested) await ReadAccessOnceAsync(stop).ConfigureAwait(false);
                }
                finally
                {
                    Volatile.Write(ref _accessReading, 0);
                }

                // A read asked after the last pass but before the reader was released found it busy: make it now.
                if (Volatile.Read(ref _accessAgain) == 0 || stop.IsCancellationRequested || Interlocked.Exchange(ref _accessReading, 1) != 0) return;
            }
        }, CancellationToken.None));
    }

    private async Task ReadAccessOnceAsync(CancellationToken stop)
    {
        try
        {
            await Access.RefreshAsync(stop).ConfigureAwait(false);
            // A controller device reads the keys grants give it: the titles of the sessions shared with its person.
            if (_options.Client.Kind == "desktop") await GrantKeys.RefreshAsync(stop).ConfigureAwait(false);
            _options.AccessRead?.Invoke();
        }
        catch (Exception exception) when (exception is FleetAccessDocumentException or FleetDeviceListException)
        {
            // A chain that does not verify changes nothing: the version verified before still holds.
            if (LogManager.IsInitialized) _accessLogger.Warn($"The access document of {ServerAddress.ToString()} was refused: {exception.Message}");
        }
        catch (Exception exception) when (exception is FleetServerException or HttpRequestException or IOException or OperationCanceledException)
        {
            if (LogManager.IsInitialized && !stop.IsCancellationRequested) _accessLogger.Info($"The access document of {ServerAddress.ToString()} could not be read: {exception.Message}");
        }
    }
}
