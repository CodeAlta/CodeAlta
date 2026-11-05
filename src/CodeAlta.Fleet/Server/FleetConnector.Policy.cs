using CodeAlta.Fleet.Protocol;
using XenoAtom.Logging;

namespace CodeAlta.Fleet.Server;

public sealed partial class FleetConnectorOptions
{
    /// <summary>Gets the file that pins the policy of the server and keeps the version this device verified (<see cref="FleetOrganizationPolicy"/>); null keeps them in memory.</summary>
    public string? PolicyPath { get; init; }

    /// <summary>
    /// Gets what is told once a newer version of the policy verified (<see cref="FleetPolicyMethods.Changed"/>, and at each
    /// connection), and once at the start with the version this device kept: a machine then stops the work of the fleet a
    /// stop names. Null tells nobody.
    /// </summary>
    public Action<FleetPolicyState>? PolicyChanged { get; init; }
}

// The signed policy of the server and its kill switch, read again when the server says it changed and at each connection; the
// version this device verified holds while the server is unreachable.
public sealed partial class FleetConnector
{
    private readonly Logger _policyLogger = LogManager.GetLogger("CodeAlta.Fleet.Policy");
    private int _policyReading;
    private int _policyAgain;

    /// <summary>Gets the policy of the organization of the server, as this device verified it.</summary>
    public FleetOrganizationPolicy Policy { get; private set; } = null!;

    private void InitializePolicy()
    {
        Policy = new FleetOrganizationPolicy(Access, _options.PolicyPath);
        if (_options.PolicyChanged is not { } changed) return;
        Policy.Changed += changed;
        // What this device kept applies from the start, before the server answers, or when it never does.
        if (Policy.Current is { Number: > 0 } kept) changed(kept);
    }

    // Read beside the reader of the connection, one at a time; a read asked while one runs is made again after it.
    private void RefreshPolicy(CancellationToken stop)
    {
        Volatile.Write(ref _policyAgain, 1);
        if (Interlocked.Exchange(ref _policyReading, 1) != 0) return;
        Track(Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    while (Interlocked.Exchange(ref _policyAgain, 0) != 0 && !stop.IsCancellationRequested)
                    {
                        try
                        {
                            await Policy.RefreshAsync(stop).ConfigureAwait(false);
                        }
                        catch (Exception exception) when (exception is FleetPolicyDocumentException or FleetAccessDocumentException or FleetDeviceListException)
                        {
                            // A chain that does not verify changes nothing: the version verified before still holds.
                            if (LogManager.IsInitialized) _policyLogger.Warn($"The policy of {ServerAddress.ToString()} was refused: {exception.Message}");
                        }
                        catch (Exception exception) when (exception is FleetServerException or HttpRequestException or IOException or OperationCanceledException)
                        {
                            if (LogManager.IsInitialized && !stop.IsCancellationRequested) _policyLogger.Info($"The policy of {ServerAddress.ToString()} could not be read: {exception.Message}");
                        }
                    }
                }
                finally
                {
                    Volatile.Write(ref _policyReading, 0);
                }

                // A read asked after the last pass but before the reader was released found it busy: make it now (a stop must
                // never wait for the next connection).
                if (Volatile.Read(ref _policyAgain) == 0 || stop.IsCancellationRequested || Interlocked.Exchange(ref _policyReading, 1) != 0) return;
            }
        }, CancellationToken.None));
    }
}
