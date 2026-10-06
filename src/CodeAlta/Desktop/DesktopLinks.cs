using System.ComponentModel;
using System.Diagnostics;

namespace CodeAlta.Desktop;

/// <summary>Opens an address of the web in the browser of the system.</summary>
internal static class DesktopLinks
{
    /// <summary>The longest address that is opened.</summary>
    internal const int MaximumLength = 2048;

    /// <summary>The address as it is opened when it is an absolute <c>http</c> or <c>https</c> address; null for anything else.</summary>
    internal static string? WebAddress(string? address)
    {
        if (address is not { Length: > 0 and <= MaximumLength } || address.Any(char.IsControl)) return null;
        return Uri.TryCreate(address, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) && uri.Host.Length > 0
            ? uri.AbsoluteUri
            : null;
    }

    /// <summary>Opens an address <see cref="WebAddress"/> returned.</summary>
    /// <returns>False when the system could not open it.</returns>
    internal static bool Open(string address)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo { FileName = address, UseShellExecute = true });
            return true;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            return false;
        }
    }
}
