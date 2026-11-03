using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeAlta.Plugin.Mobile;

/// <summary>A certificate the ACME authority gave.</summary>
/// <param name="ChainPem">The certificate and its chain, PEM.</param>
/// <param name="KeyPem">Its private key, PKCS#8 PEM.</param>
/// <param name="AccountUrl">The account it was asked with: the next requests use it again.</param>
internal sealed record MobileAcmeCertificate(string ChainPem, string KeyPem, string AccountUrl);

/// <summary>Why the ACME authority gave no certificate, in its words.</summary>
internal sealed class MobileAcmeException : Exception
{
    /// <summary>Creates the exception.</summary>
    public MobileAcmeException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with its cause.</summary>
    public MobileAcmeException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>
/// A client of ACME (RFC 8555), the protocol of Let's Encrypt, for one certificate of a domain name at a time: the authority checks this
/// computer with the challenge <c>tls-alpn-01</c>, which it reads with TLS on port 443 of the name (RFC 8737), and gives the
/// certificate and its key.
/// </summary>
internal sealed class MobileAcme
{
    /// <summary>The directory of Let's Encrypt.</summary>
    internal static Uri LetsEncrypt => new("https://acme-v02.api.letsencrypt.org/directory");

    // The challenge Let's Encrypt reads with TLS on port 443 of the name, ALPN acme-tls/1 (RFC 8737).
    private const string TlsAlpnChallenge = "tls-alpn-01";

    private static readonly TimeSpan Patience = TimeSpan.FromMinutes(2);

    private readonly HttpClient _http;
    private readonly Uri _directory;
    private string? _nonce;

    /// <summary>Creates the client of a directory.</summary>
    /// <param name="http">The client of the requests.</param>
    /// <param name="directory">The directory of the authority.</param>
    internal MobileAcme(HttpClient http, Uri directory)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(directory);
        _http = http;
        _directory = directory;
    }

    /// <summary>Asks a certificate for a domain name.</summary>
    /// <param name="identifier">The name (<c>pulsar.example.com</c>) the certificate is for.</param>
    /// <param name="accountKey">The key of the account, P-256; the account is made when <paramref name="accountUrl"/> is null.</param>
    /// <param name="accountUrl">The account made before, or null.</param>
    /// <param name="answer">
    /// Gives the key authorization of the challenge (its token and the thumbprint of the account): the caller answers with it until the
    /// certificate is given.
    /// </param>
    /// <param name="cancellationToken">Stops the request.</param>
    /// <returns>The certificate.</returns>
    /// <exception cref="MobileAcmeException">The authority gave no certificate; the message says why.</exception>
    internal async Task<MobileAcmeCertificate> IssueAsync(string identifier, ECDsa accountKey, string? accountUrl, Action<string> answer, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(identifier);
        ArgumentNullException.ThrowIfNull(accountKey);
        ArgumentNullException.ThrowIfNull(answer);
        try
        {
            var directory = await ReadDirectoryAsync(cancellationToken).ConfigureAwait(false);
            var signer = new Signer(accountKey, accountUrl);
            if (signer.Kid is null)
            {
                using var created = await PostAsync(directory.NewAccount, new JsonObject { ["termsOfServiceAgreed"] = true }, signer, directory, cancellationToken).ConfigureAwait(false);
                signer.Kid = created.Headers.Location?.ToString() ?? throw new MobileAcmeException("The authority made the account but did not say where.");
            }

            var request = new JsonObject { ["identifiers"] = new JsonArray(new JsonObject { ["type"] = "dns", ["value"] = identifier }) };
            using var ordered = await PostAsync(directory.NewOrder, request, signer, directory, cancellationToken).ConfigureAwait(false);
            var orderUrl = ordered.Headers.Location ?? throw new MobileAcmeException("The authority made the order but did not say where.");
            var order = await JsonAsync(ordered, cancellationToken).ConfigureAwait(false);

            foreach (var authorization in Urls(order, "authorizations"))
            {
                await AuthorizeAsync(authorization, signer, directory, answer, cancellationToken).ConfigureAwait(false);
            }

            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var csr = SigningRequest(identifier, key);
            var finalize = Url(order, "finalize");
            using (await PostAsync(finalize, new JsonObject { ["csr"] = Base64Url(csr) }, signer, directory, cancellationToken).ConfigureAwait(false))
            {
            }

            order = await WaitAsync(orderUrl, signer, directory, static state => state is "valid" or "invalid", cancellationToken).ConfigureAwait(false);
            if (Text(order, "status") != "valid") throw new MobileAcmeException("The authority refused the order" + Problem(order["error"]));
            using var certificate = await PostAsync(Url(order, "certificate"), null, signer, directory, cancellationToken, "application/pem-certificate-chain").ConfigureAwait(false);
            var chain = await certificate.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return new MobileAcmeCertificate(chain, key.ExportPkcs8PrivateKeyPem(), signer.Kid!);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new MobileAcmeException("Let's Encrypt could not be reached: " + exception.Message, exception);
        }
    }

    /// <summary>
    /// The certificate that answers the challenge <c>tls-alpn-01</c> of a name (RFC 8737): signed by itself, for the name alone, with the
    /// critical extension <c>acmeIdentifier</c> that holds the SHA-256 of the key authorization.
    /// </summary>
    internal static X509Certificate2 TlsAlpnCertificate(string identifier, string keyAuthorization)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=" + identifier, key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(identifier);
        request.CertificateExtensions.Add(names.Build());
        // id-pe-acmeIdentifier: an OCTET STRING of the 32 bytes of the hash.
        var value = new byte[34];
        value[0] = 0x04;
        value[1] = 32;
        SHA256.HashData(Encoding.UTF8.GetBytes(keyAuthorization), value.AsSpan(2));
        request.CertificateExtensions.Add(new X509Extension(new Oid(AcmeIdentifierOid), value, critical: true));
        using var made = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(7));
        return MobileTls.Persistable(made);
    }

    /// <summary>The OID of the extension <c>acmeIdentifier</c> (RFC 8737).</summary>
    internal const string AcmeIdentifierOid = "1.3.6.1.5.5.7.1.31";

    /// <summary>The thumbprint of the key of the account (RFC 7638), which the answer of a challenge ends with.</summary>
    internal static string Thumbprint(ECDsa key)
    {
        var jwk = Jwk(key);
        var canonical = $"{{\"crv\":\"{jwk["crv"]}\",\"kty\":\"{jwk["kty"]}\",\"x\":\"{jwk["x"]}\",\"y\":\"{jwk["y"]}\"}}";
        return Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    /// <summary>The public key of the account as a JWK.</summary>
    internal static JsonObject Jwk(ECDsa key)
    {
        var parameters = key.ExportParameters(false);
        return new JsonObject { ["crv"] = "P-256", ["kty"] = "EC", ["x"] = Base64Url(parameters.Q.X!), ["y"] = Base64Url(parameters.Q.Y!) };
    }

    /// <summary>Base64url without padding, as JOSE writes it.</summary>
    internal static string Base64Url(ReadOnlySpan<byte> bytes) => System.Buffers.Text.Base64Url.EncodeToString(bytes);

    // The request of the certificate: the name in the alternative names, which is what Let's Encrypt reads.
    private static byte[] SigningRequest(string identifier, ECDsa key)
    {
        var request = new CertificateRequest(new X500DistinguishedName(""), key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(identifier);
        request.CertificateExtensions.Add(names.Build());
        return request.CreateSigningRequest();
    }

    // Answers the challenge of an authorization and waits until the authority read it.
    private async Task AuthorizeAsync(Uri url, Signer signer, Directory directory, Action<string> answer, CancellationToken cancellationToken)
    {
        JsonObject authorization;
        using (var read = await PostAsync(url, null, signer, directory, cancellationToken).ConfigureAwait(false))
            authorization = await JsonAsync(read, cancellationToken).ConfigureAwait(false);
        if (Text(authorization, "status") == "valid") return;
        var challenge = (authorization["challenges"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(static item => Text(item, "type") == TlsAlpnChallenge)
            ?? throw new MobileAcmeException($"The authority offers no check of the type {TlsAlpnChallenge} for this name.");
        var token = Text(challenge, "token") ?? throw new MobileAcmeException("The challenge of the authority has no token.");
        answer(token + "." + Thumbprint(signer.Key));
        using (await PostAsync(Url(challenge, "url"), new JsonObject(), signer, directory, cancellationToken).ConfigureAwait(false))
        {
        }

        authorization = await WaitAsync(url, signer, directory, static state => state is "valid" or "invalid" or "deactivated" or "expired" or "revoked", cancellationToken).ConfigureAwait(false);
        if (Text(authorization, "status") == "valid") return;
        var failed = (authorization["challenges"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(static item => Text(item, "type") == TlsAlpnChallenge);
        throw new MobileAcmeException("Let's Encrypt could not check this computer" + Problem(failed?["error"]));
    }

    // Reads an order or an authorization until its state is one of the ends, as long as the authority says to wait.
    private async Task<JsonObject> WaitAsync(Uri url, Signer signer, Directory directory, Func<string?, bool> ended, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + Patience;
        while (true)
        {
            using var read = await PostAsync(url, null, signer, directory, cancellationToken).ConfigureAwait(false);
            var value = await JsonAsync(read, cancellationToken).ConfigureAwait(false);
            if (ended(Text(value, "status"))) return value;
            if (DateTimeOffset.UtcNow > deadline) throw new MobileAcmeException("Let's Encrypt did not answer in time.");
            var wait = read.Headers.RetryAfter?.Delta is { } delta && delta > TimeSpan.Zero && delta < TimeSpan.FromSeconds(10) ? delta : TimeSpan.FromSeconds(1);
            await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<Directory> ReadDirectoryAsync(CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(_directory, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new MobileAcmeException($"The directory of Let's Encrypt answered {(int)response.StatusCode}.");
        var directory = await JsonAsync(response, cancellationToken).ConfigureAwait(false);
        return new Directory(Url(directory, "newNonce"), Url(directory, "newAccount"), Url(directory, "newOrder"));
    }

    private async Task<string> NonceAsync(Directory directory, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _nonce, null) is { } kept) return kept;
        using var request = new HttpRequestMessage(HttpMethod.Head, directory.NewNonce);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return Nonce(response) ?? throw new MobileAcmeException("The authority gave no nonce.");
    }

    private static string? Nonce(HttpResponseMessage response) => response.Headers.TryGetValues("Replay-Nonce", out var values) ? values.FirstOrDefault() : null;

    // A request signed with the key of the account (JWS, ES256); null as payload is a POST-as-GET. A nonce the authority refuses is
    // replaced once by the one it gives with the refusal.
    private async Task<HttpResponseMessage> PostAsync(Uri url, JsonObject? payload, Signer signer, Directory directory, CancellationToken cancellationToken, string? accept = null)
    {
        for (var attempt = 0; ; attempt++)
        {
            var nonce = await NonceAsync(directory, cancellationToken).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(signer.Sign(url, nonce, payload).ToJsonString(), Encoding.UTF8) };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/jose+json");
            if (accept is not null) request.Headers.Accept.ParseAdd(accept);
            var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (Nonce(response) is { } next) _nonce = next;
            if (response.IsSuccessStatusCode) return response;
            using (response)
            {
                var problem = await ProblemAsync(response, cancellationToken).ConfigureAwait(false);
                if (attempt == 0 && Text(problem, "type") == "urn:ietf:params:acme:error:badNonce") continue;
                throw new MobileAcmeException($"Let's Encrypt refused the request{Problem(problem)}");
            }
        }
    }

    private static async Task<JsonObject?> ProblemAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try { return JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)) as JsonObject; }
        catch (JsonException) { return new JsonObject { ["detail"] = $"{(int)response.StatusCode} {response.ReasonPhrase}" }; }
    }

    // ": detail" of a problem document, with the details of its subproblems; "." without one.
    private static string Problem(JsonNode? problem)
    {
        if (problem is not JsonObject value || Text(value, "detail") is not { Length: > 0 } detail) return ".";
        var subproblems = (value["subproblems"] as JsonArray)?.OfType<JsonObject>().Select(static sub => Text(sub, "detail")).Where(static text => !string.IsNullOrEmpty(text)).ToArray() ?? [];
        return ": " + detail.TrimEnd('.') + (subproblems.Length > 0 ? " (" + string.Join("; ", subproblems) + ")" : "") + ".";
    }

    private static async Task<JsonObject> JsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        => JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)) as JsonObject
            ?? throw new MobileAcmeException("The authority answered something else than an object.");

    private static string? Text(JsonObject? value, string name) => value?[name] is JsonValue text && text.TryGetValue<string>(out var result) ? result : null;

    private static Uri Url(JsonObject value, string name)
        => Text(value, name) is { } text && Uri.TryCreate(text, UriKind.Absolute, out var url) ? url : throw new MobileAcmeException($"The authority gave no {name}.");

    private static IEnumerable<Uri> Urls(JsonObject value, string name)
        => (value[name] as JsonArray ?? []).Select(static item => item is JsonValue text && text.TryGetValue<string>(out var url) && Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri : null)
            .OfType<Uri>().ToArray();

    private sealed record Directory(Uri NewNonce, Uri NewAccount, Uri NewOrder);

    // Signs the requests with the key of the account: with the key itself until the account exists, with its URL after.
    private sealed class Signer(ECDsa key, string? kid)
    {
        internal ECDsa Key { get; } = key;

        internal string? Kid { get; set; } = kid;

        internal JsonObject Sign(Uri url, string nonce, JsonObject? payload)
        {
            var header = new JsonObject { ["alg"] = "ES256", ["nonce"] = nonce, ["url"] = url.ToString() };
            if (Kid is null) header["jwk"] = Jwk(Key);
            else header["kid"] = Kid;
            var protectedPart = Base64Url(Encoding.UTF8.GetBytes(header.ToJsonString()));
            var payloadPart = payload is null ? "" : Base64Url(Encoding.UTF8.GetBytes(payload.ToJsonString()));
            // ES256 signs with r and s side by side (IEEE P1363), which JOSE asks for.
            var signature = Key.SignData(Encoding.ASCII.GetBytes(protectedPart + "." + payloadPart), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            return new JsonObject { ["protected"] = protectedPart, ["payload"] = payloadPart, ["signature"] = Base64Url(signature) };
        }
    }
}
