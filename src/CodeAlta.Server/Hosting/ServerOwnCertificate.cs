using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CodeAlta.Server.Storage;

namespace CodeAlta.Server.Hosting;

/// <summary>
/// The certificate a server makes for itself (<c>altaserver run --own-certificate</c>): a self-signed ECDSA P-256
/// certificate, made at the first start and kept sealed with the key ring of the server in its data root, so that a server
/// on a local network is reached over TLS without a reverse proxy or a certificate authority. No browser or system trusts
/// it: its clients pin its key (<see cref="CodeAlta.Fleet.Protocol.FleetServerCertificates"/>), which the server prints.
/// </summary>
internal static class ServerOwnCertificate
{
    private const string Purpose = "tls.own_certificate";
    private const string Scope = "server";

    /// <summary>Loads the certificate of the data root, or makes it the first time.</summary>
    /// <param name="paths">The files of the server.</param>
    /// <param name="sealer">The sealer of the key ring of the server.</param>
    /// <param name="time">The clock of its dates.</param>
    /// <returns>The certificate, with its key.</returns>
    /// <exception cref="InvalidOperationException">The certificate kept there does not open with the key ring of the server.</exception>
    /// <exception cref="IOException">The file cannot be written.</exception>
    public static X509Certificate2 LoadOrCreate(ServerPaths paths, ServerSecretSealer sealer, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(sealer);
        if (File.Exists(paths.OwnCertificatePath))
        {
            // Never replaced silently: a new key would break the pin every client holds.
            var kept = sealer.Unseal(File.ReadAllText(paths.OwnCertificatePath).Trim(), Purpose, Scope)
                ?? throw new InvalidOperationException($"The certificate of the server ({paths.OwnCertificatePath}) does not open with its key ring ({paths.KeysRoot}). " +
                    "Restore the key ring, or delete the file to make a new certificate, whose new pin every device then needs.");
            return X509CertificateLoader.LoadPkcs12(Convert.FromBase64String(kept), null);
        }

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=CodeAlta Server", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        var now = time.GetUtcNow();
        // The pin is the trust, not the dates: a long life, so that the server never has to change the pin of its devices.
        using var made = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(30));
        var pfx = made.Export(X509ContentType.Pkcs12);
        try
        {
            var folder = Path.GetDirectoryName(paths.OwnCertificatePath)!;
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(folder);
            else Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var temporary = paths.OwnCertificatePath + ".tmp";
            File.WriteAllText(temporary, sealer.Seal(Convert.ToBase64String(pfx), Purpose, Scope));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, paths.OwnCertificatePath, overwrite: false);
            // Kestrel on Windows needs a key it can persist: the certificate is loaded from its PKCS #12, as it is kept.
            return X509CertificateLoader.LoadPkcs12(pfx, null);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pfx);
        }
    }
}
