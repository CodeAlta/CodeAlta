using System.Runtime.Versioning;
using System.ServiceProcess;

namespace CodeAlta.Fleet.Update.Service;

/// <summary>
/// Runs <c>altanode run</c> or <c>altaserver run</c> as a Windows service (<c>run --service</c>, what the service that
/// <c>install --system</c> registers starts): the process answers the Service Control Manager, and a stop or a shutdown of
/// Windows becomes the graceful stop of the node or the server, as SIGTERM does under launchd and systemd.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsServiceHost
{
    /// <summary>Runs the body as the service of this process, until it ends or the Service Control Manager stops it.</summary>
    /// <param name="serviceName">The name of the service.</param>
    /// <param name="body">What the service runs, given the token of its stop; returns its exit code.</param>
    /// <returns>The exit code of the body.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="InvalidOperationException">This process was not started by the Service Control Manager.</exception>
    public static int Run(string serviceName, Func<CancellationToken, Task<int>> body)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        ArgumentNullException.ThrowIfNull(body);
        using var service = new LauncherService(serviceName, body);
        ServiceBase.Run(service);
        return service.Result;
    }

    private sealed class LauncherService : ServiceBase
    {
        private readonly Func<CancellationToken, Task<int>> _body;
        private readonly CancellationTokenSource _stop = new();
        private Task<int>? _running;
        private int _stopping;

        public LauncherService(string name, Func<CancellationToken, Task<int>> body)
        {
            ServiceName = name;
            _body = body;
            CanStop = true;
            CanShutdown = true;
            AutoLog = false;
        }

        public int Result { get; private set; }

        protected override void OnStart(string[] args)
        {
            _running = Task.Run(() => _body(_stop.Token));
            // A body that ends by itself ends the service, with its exit code: a failure lets the recovery of the service start it again.
            _ = _running.ContinueWith(task =>
            {
                Result = task.IsCompletedSuccessfully ? task.Result : 1;
                ExitCode = Result;
                if (Interlocked.Exchange(ref _stopping, 1) == 0) Stop();
            }, TaskScheduler.Default);
        }

        protected override void OnStop()
        {
            Interlocked.Exchange(ref _stopping, 1);
            // The node gives its turns 30 s, then its host 15 s: the stop is waited for as long as a service manager gives it.
            RequestAdditionalTime((ServiceDefinition.StopTimeoutSeconds + 10) * 1000);
            _stop.Cancel();
            try
            {
                _running?.Wait(TimeSpan.FromSeconds(ServiceDefinition.StopTimeoutSeconds + 5));
            }
            catch (AggregateException)
            {
                // Its failure is its exit code (OnStart's continuation).
            }

            if (_running is { IsCompletedSuccessfully: true } done) ExitCode = Result = done.Result;
        }

        protected override void OnShutdown() => OnStop();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _stop.Dispose();
            base.Dispose(disposing);
        }
    }
}
