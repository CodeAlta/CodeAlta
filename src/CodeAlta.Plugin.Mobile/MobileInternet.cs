using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeAlta.Plugin.Mobile;

/// <summary>What the provider of the Internet needs beside the settings: Let's Encrypt, and how a name is looked up. The tests give theirs.</summary>
/// <param name="AcmeDirectory">The directory of the ACME authority.</param>
/// <param name="AcmeHttp">The client of the requests to the authority.</param>
/// <param name="Resolve">Looks a name up on the Internet: its addresses, none when it is unknown.</param>
internal sealed record MobileInternetOptions(Uri AcmeDirectory, HttpClient AcmeHttp, Func<string, CancellationToken, Task<IPAddress[]>> Resolve)
{
    /// <summary>The options of this computer: Let's Encrypt, and the resolver of the system.</summary>
    internal static MobileInternetOptions System => new(MobileAcme.LetsEncrypt, new HttpClient { Timeout = TimeSpan.FromSeconds(30) }, ResolveAsync);

    private static async Task<IPAddress[]> ResolveAsync(string name, CancellationToken cancellationToken)
    {
        try { return await Dns.GetHostAddressesAsync(name, cancellationToken).ConfigureAwait(false); }
        catch (SocketException) { return []; }
    }
}

/// <summary>
/// The provider of the Internet: the server serves HTTPS itself, on every address of this computer, at a domain name of the user that
/// points at the router, whose port 443 the user forwarded to the port of the server. Its certificate comes from Let's Encrypt, which
/// checks the name with the challenge <c>tls-alpn-01</c> on that same port 443: the server answers it, nothing else is opened. The
/// certificate is kept in the folder of the plugin and renewed a month before it ends.
/// </summary>
internal sealed class MobileInternetProvider : IMobileProvider, IMobileServerTls
{
    /// <summary>The id of the provider in the settings.</summary>
    internal const string ProviderId = "internet";

    private static readonly TimeSpan RenewalCheck = TimeSpan.FromHours(6);

    private readonly IMobileProviderHost _host;
    private readonly MobileInternetOptions _options;
    private readonly Lock _gate = new();
    private (X509Certificate2 Certificate, X509Certificate2Collection Chain)? _certificate;
    private (string Host, X509Certificate2 Certificate)? _challenge;
    private CancellationTokenSource? _work;

    /// <summary>Creates the provider.</summary>
    internal MobileInternetProvider(IMobileProviderHost host, MobileInternetOptions options)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public string Id => ProviderId;

    /// <inheritdoc />
    public MobileListen Listen(MobileSettings settings) => new(settings.Port, this);

    /// <inheritdoc />
    public string? Refusal(MobileSettings settings)
    {
        if (settings.Host is not { } name) return "Give the domain name phones open, such as pulsar.example.com.";
        return IsDomainName(name) ? null : $"{name} is not a domain name: give one such as pulsar.example.com, that points at your router.";
    }

    /// <summary>Whether a text is a domain name of the Internet: letters, digits and hyphens, with a dot, and no IP address.</summary>
    internal static bool IsDomainName(string name)
        => Uri.CheckHostName(name) == UriHostNameType.Dns && name.Contains('.', StringComparison.Ordinal) && !IPAddress.TryParse(name, out _)
            && name.Split('.').All(static label => label.Length is > 0 and <= 63 && !label.StartsWith('-') && !label.EndsWith('-'));

    /// <summary>The address phones open: the name, on port 443, which goes without saying.</summary>
    internal static string Url(string name) => "https://" + name;

    /// <inheritdoc />
    public async Task<MobileProviderStatus> PublishAsync(CancellationToken cancellationToken)
    {
        await StopWorkAsync().ConfigureAwait(false);
        var name = _host.Settings.Host;
        if (name is null || !IsDomainName(name)) return new("address-missing", null, "Give the domain name phones open in Settings > Extensions > Mobile.");
        var addresses = await _options.Resolve(name, cancellationToken).ConfigureAwait(false);
        if (addresses.Length == 0)
            return new("dns-missing", null, $"{name} is not known on the Internet: create it at your DNS provider, pointing at the public address of your router.");

        var work = _work = new CancellationTokenSource();
        if (Load(name) is { } kept)
        {
            lock (_gate) _certificate = kept;
            _host.Run("mobile-internet-renew", "Renews the certificate of the mobile view.", ct => RenewLoopAsync(name, work, ct));
            return Serving(name, kept.Certificate);
        }

        // Let's Encrypt takes a while: the certificate is asked in the background, and its outcome reported.
        _host.Run("mobile-internet-certificate", "Gets the certificate of the mobile view from Let's Encrypt.", async _ =>
        {
            var outcome = await IssueAsync(name, work.Token).ConfigureAwait(false);
            if (work.IsCancellationRequested) return;
            await _host.ReportAsync(outcome).ConfigureAwait(false);
            if (outcome.State == "serving") await RenewLoopAsync(name, work, CancellationToken.None).ConfigureAwait(false);
        });
        return MobileProviderStatus.Pending("Getting a certificate from Let's Encrypt…");
    }

    /// <inheritdoc />
    public async Task UnpublishAsync(CancellationToken cancellationToken) => await StopWorkAsync().ConfigureAwait(false);

    /// <inheritdoc />
    public Task<bool> ActAsync(string action, CancellationToken cancellationToken) => Task.FromResult(false);

    /// <inheritdoc />
    public (X509Certificate2 Certificate, X509Certificate2Collection Chain)? Certificate(string? serverName, bool acme)
    {
        lock (_gate)
        {
            if (acme) return _challenge is { } challenge ? (challenge.Certificate, []) : null;
            return _certificate;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await StopWorkAsync().ConfigureAwait(false);

    private async Task StopWorkAsync()
    {
        if (Interlocked.Exchange(ref _work, null) is { } work) await work.CancelAsync().ConfigureAwait(false);
        lock (_gate) _challenge = null;
    }

    private MobileProviderStatus Serving(string name, X509Certificate2 certificate)
    {
        var url = Url(name);
        return new("serving", url, $"Reachable at {url}, with a certificate of Let's Encrypt valid until {certificate.NotAfter.ToUniversalTime():yyyy-MM-dd}.");
    }

    // Renews the certificate a month before it ends: Let's Encrypt checks the name again, on the same port, and the server serves the new
    // certificate at its next handshake.
    private async ValueTask RenewLoopAsync(string name, CancellationTokenSource work, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(work.Token, cancellationToken);
        try
        {
            while (!linked.IsCancellationRequested)
            {
                await Task.Delay(RenewalCheck, linked.Token).ConfigureAwait(false);
                X509Certificate2? current;
                lock (_gate) current = _certificate?.Certificate;
                if (current is not null && !EndsSoon(current)) continue;
                var outcome = await IssueAsync(name, linked.Token).ConfigureAwait(false);
                if (outcome.State != "serving") _host.Log($"Mobile: the certificate of {name} could not be renewed: {outcome.Detail}");
            }
        }
        catch (OperationCanceledException)
        {
            // The settings changed, or the plugin stops.
        }
    }

    /// <summary>Whether a certificate ends within a month: Let's Encrypt advises to renew then.</summary>
    internal static bool EndsSoon(X509Certificate2 certificate) => certificate.NotAfter.ToUniversalTime() - DateTime.UtcNow < TimeSpan.FromDays(30);

    // Asks Let's Encrypt for a certificate: the server answers the challenge tls-alpn-01 with a certificate of its own while it is checked.
    private async Task<MobileProviderStatus> IssueAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            using var account = Account(out var accountUrl);
            var acme = new MobileAcme(_options.AcmeHttp, _options.AcmeDirectory);
            var issued = await acme.IssueAsync(name, account, accountUrl, MobileAcme.TlsAlpnChallenge, (_, keyAuthorization) =>
            {
                var answer = MobileAcme.TlsAlpnCertificate(name, keyAuthorization);
                lock (_gate) _challenge = (name, answer);
            }, cancellationToken).ConfigureAwait(false);
            Save(name, account, issued);
            var loaded = Load(name) ?? throw new MobileAcmeException("The certificate of Let's Encrypt could not be read.");
            lock (_gate) _certificate = loaded;
            _host.Log($"Mobile: Let's Encrypt gave a certificate for {name}.");
            return Serving(name, loaded.Certificate);
        }
        catch (MobileAcmeException exception)
        {
            _host.Log($"Mobile: no certificate for {name}: {exception.Message}");
            return new("certificate-failed", null,
                $"{exception.Message} Check that {name} points at the public address of your router, and that port 443 of the router is forwarded to port {_host.Settings.Port} of this computer.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException)
        {
            return new("certificate-failed", null, "The certificate could not be kept: " + exception.Message);
        }
        finally
        {
            lock (_gate) _challenge = null;
        }
    }

    private string Folder => Path.Combine(_host.Folder, ProviderId);

    private string File(string name) => Path.Combine(Folder, name);

    // The certificate kept for the name, when it is valid for it and far from its end; null otherwise.
    private (X509Certificate2 Certificate, X509Certificate2Collection Chain)? Load(string name)
    {
        var chainPath = File(name + ".pem");
        var keyPath = File(name + ".key");
        if (!global::System.IO.File.Exists(chainPath) || !global::System.IO.File.Exists(keyPath)) return null;
        try
        {
            using var pem = X509Certificate2.CreateFromPemFile(chainPath, keyPath);
            // A key read from PEM is ephemeral, which the TLS of Windows refuses: the certificate is carried through PKCS#12 once.
            var certificate = X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), null);
            if (!certificate.MatchesHostname(name) || EndsSoon(certificate) || DateTime.UtcNow < certificate.NotBefore.ToUniversalTime())
            {
                certificate.Dispose();
                return null;
            }

            var all = new X509Certificate2Collection();
            all.ImportFromPemFile(chainPath);
            var chain = new X509Certificate2Collection();
            foreach (var other in all)
            {
                if (other.RawDataMemory.Span.SequenceEqual(certificate.RawDataMemory.Span)) other.Dispose();
                else chain.Add(other);
            }

            return (certificate, chain);
        }
        catch (Exception exception) when (exception is CryptographicException or IOException or ArgumentException)
        {
            _host.Log($"Mobile: the certificate kept for {name} could not be read: {exception.Message}");
            return null;
        }
    }

    // The key of the account of Let's Encrypt kept, or a new one.
    private ECDsa Account(out string? url)
    {
        url = null;
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        try
        {
            if (JsonNode.Parse(global::System.IO.File.ReadAllText(File("account.json"))) is JsonObject saved && saved["key"] is JsonValue text && text.TryGetValue<string>(out var pkcs8))
            {
                key.ImportPkcs8PrivateKey(Convert.FromBase64String(pkcs8), out _);
                url = saved["url"] is JsonValue kept && kept.TryGetValue<string>(out var value) ? value : null;
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException or CryptographicException or FormatException)
        {
            // No account yet, or one that cannot be read: a new one is made.
        }

        return key;
    }

    // The certificate, its key and the account, in files readable by the user only on Linux and macOS.
    private void Save(string name, ECDsa account, MobileAcmeCertificate issued)
    {
        Directory.CreateDirectory(Folder);
        Write("account.json", new JsonObject { ["key"] = Convert.ToBase64String(account.ExportPkcs8PrivateKey()), ["url"] = issued.AccountUrl }.ToJsonString());
        Write(name + ".key", issued.KeyPem);
        Write(name + ".pem", issued.ChainPem);
    }

    private void Write(string name, string content)
    {
        var path = File(name);
        var temporary = path + ".tmp";
        global::System.IO.File.WriteAllText(temporary, content);
        if (!OperatingSystem.IsWindows()) global::System.IO.File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        global::System.IO.File.Move(temporary, path, overwrite: true);
    }
}
