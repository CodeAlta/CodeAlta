using System.Net;
using System.Net.Sockets;

namespace CodeAlta.Browser.Launch;

/// <summary>How to start a browser for CodeAlta.</summary>
internal sealed record BrowserLaunchOptions
{
    /// <summary>Gets the program.</summary>
    public required string Executable { get; init; }

    /// <summary>Gets the folder of the profile: created when missing.</summary>
    public required string ProfileDirectory { get; init; }

    /// <summary>Gets whether the profile is temporary, deleted when the browser ends, and private to automation.</summary>
    public bool Temporary { get; init; } = true;

    /// <summary>Gets whether the browser runs without a window.</summary>
    public bool Headless { get; init; }

    /// <summary>Gets the proxy of every request, as <c>host:port</c>, or null.</summary>
    public string? Proxy { get; init; }

    /// <summary>Gets how long the browser has to open its endpoint.</summary>
    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets the variables to set (a null value removes one) in the environment of the browser.</summary>
    public IReadOnlyDictionary<string, string?>? Environment { get; init; }
}

/// <summary>Small helpers of the launchers.</summary>
internal static class LaunchHelpers
{
    /// <summary>A port of the loopback that is free now.</summary>
    public static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}
