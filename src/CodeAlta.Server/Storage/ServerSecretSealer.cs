using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace CodeAlta.Server.Storage;

/// <summary>
/// Seals the few secrets the server itself must use (the client secret of the company's sign-in, the headers it sends to
/// the telemetry collector) before the database keeps them: ASP.NET Core Data Protection, whose key ring lives beside the
/// database in a folder of the data root, protected by DPAPI for the account of the server on Windows, and readable by
/// that account only elsewhere. A value is sealed for one use and one organization: it does not open as another.
/// </summary>
/// <remarks>
/// The database alone (a backup, a copy) holds no secret in clear; its key ring is needed too. A value that does not
/// open (another key ring, a database restored on another machine) reads as unset, and its owner sets it again.
/// </remarks>
public sealed class ServerSecretSealer
{
    private const string Prefix = "dp1:";
    private const string ApplicationName = "CodeAlta Server";
    private const string RootPurpose = "CodeAlta.Server.SealedSecrets.v1";

    private readonly IDataProtector _root;

    /// <summary>Initializes a new instance of the <see cref="ServerSecretSealer"/> class.</summary>
    /// <param name="provider">The protection of the server.</param>
    /// <exception cref="ArgumentNullException"><paramref name="provider"/> is null.</exception>
    public ServerSecretSealer(IDataProtectionProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _root = provider.CreateProtector(RootPurpose);
    }

    /// <summary>Gets the sealer of a key ring in a folder, made the first time a value is sealed.</summary>
    /// <param name="keysRoot">The folder of the key ring; made readable by the account of the server only.</param>
    /// <returns>The sealer.</returns>
    /// <exception cref="ArgumentException"><paramref name="keysRoot"/> is empty.</exception>
    /// <exception cref="IOException">The folder cannot be made.</exception>
    public static ServerSecretSealer Create(string keysRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keysRoot);
        var folder = OperatingSystem.IsWindows()
            ? Directory.CreateDirectory(keysRoot)
            : Directory.CreateDirectory(keysRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        // A folder made before keeps its mode: it is narrowed to the account of the server.
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(folder.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var provider = DataProtectionProvider.Create(folder, builder =>
        {
            builder.SetApplicationName(ApplicationName);
            if (OperatingSystem.IsWindows()) builder.ProtectKeysWithDpapi();
        });
        return new ServerSecretSealer(provider);
    }

    /// <summary>Gets whether a value of the database is sealed.</summary>
    /// <param name="value">The value.</param>
    /// <returns>Whether it is.</returns>
    public static bool IsSealed(string? value) => value is not null && value.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>Seals a value for one use and one organization.</summary>
    /// <param name="value">The value.</param>
    /// <param name="purpose">What it is for, such as <c>sign_in.company_client_secret</c>.</param>
    /// <param name="organizationId">The organization it belongs to.</param>
    /// <returns>The sealed value, as the database keeps it.</returns>
    /// <exception cref="ArgumentException">An argument is null or empty.</exception>
    public string Seal(string value, string purpose, string organizationId)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentException.ThrowIfNullOrEmpty(purpose);
        ArgumentException.ThrowIfNullOrEmpty(organizationId);
        return Prefix + _root.CreateProtector(purpose, organizationId).Protect(value);
    }

    /// <summary>Opens a sealed value.</summary>
    /// <param name="sealedValue">The sealed value, or null.</param>
    /// <param name="purpose">What it is for.</param>
    /// <param name="organizationId">The organization it belongs to.</param>
    /// <returns>The value; null for none, and for one that does not open with this key ring, this use or this organization.</returns>
    public string? Unseal(string? sealedValue, string purpose, string organizationId)
    {
        if (!IsSealed(sealedValue)) return null;
        try
        {
            return _root.CreateProtector(purpose, organizationId).Unprotect(sealedValue![Prefix.Length..]);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
