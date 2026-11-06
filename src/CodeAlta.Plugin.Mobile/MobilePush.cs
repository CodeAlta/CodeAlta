using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using CodeAlta.Plugins.Abstractions;
using Lib.Net.Http.WebPush;
using Lib.Net.Http.WebPush.Authentication;

namespace CodeAlta.Plugin.Mobile;

/// <summary>Web Push subscriptions of paired phones, with one persistent VAPID identity for the app.</summary>
internal sealed class MobilePush : IDisposable
{
    internal const string FileName = "mobile-push.json";
    private readonly IPluginStateStore _state;
    private readonly MobileAccess _access;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _saving = new(1, 1);
    private readonly HttpClient _http;
    private readonly VapidAuthentication _authentication;
    private readonly PushServiceClient _client;
    private readonly MobilePushFile _file;

    private MobilePush(IPluginStateStore state, MobileAccess access, MobilePushFile file, HttpMessageHandler? handler)
    {
        _state = state;
        _access = access;
        _file = file;
        // A subscription is supplied by a phone: never follow redirects from a push service to another host.
        _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10) };
        _authentication = new VapidAuthentication(file.PublicKey, file.PrivateKey) { Subject = "https://codealta.github.io" };
        _client = new PushServiceClient(_http) { DefaultAuthentication = _authentication, DefaultTimeToLive = 600, AutoRetryAfter = false };
    }

    internal string PublicKey => _file.PublicKey;

    internal static async Task<MobilePush> LoadAsync(IPluginStateStore state, MobileAccess access, CancellationToken cancellationToken, HttpMessageHandler? handler = null)
    {
        var saved = await state.ReadJsonAsync<MobilePushFile>(PluginStateScope.User, FileName, cancellationToken).ConfigureAwait(false);
        if (saved is null)
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var parameters = key.ExportParameters(true);
            saved = new MobilePushFile
            {
                PublicKey = Base64Url.EncodeToString([4, .. parameters.Q.X!, .. parameters.Q.Y!]),
                PrivateKey = Base64Url.EncodeToString(parameters.D!),
            };
            await state.WriteJsonAsync(PluginStateScope.User, FileName, saved, cancellationToken).ConfigureAwait(false);
        }

        saved = saved with { Phones = [.. saved.Phones.Where(phone => access.IsPhoneHash(phone.Phone) && ValidEndpoint(phone.Endpoint)
            && ValidKeys(phone.PublicKey, phone.Auth)).TakeLast(MobileAccess.MaximumPhones)] };
        return new MobilePush(state, access, saved, handler);
    }

    /// <summary>Accepts only public push-service endpoints, not arbitrary URLs a paired client could make the host call.</summary>
    internal static bool ValidEndpoint(string? endpoint)
    {
        if (endpoint is not { Length: > 0 and <= 4096 } || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            || uri.Scheme != "https" || uri.Port != 443 || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0) return false;
        var host = uri.IdnHost;
        return host is "fcm.googleapis.com" or "updates.push.services.mozilla.com" or "web.push.apple.com"
            || host.EndsWith(".notify.windows.com", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool ValidKeys(string? publicKey, string? auth)
    {
        if (publicKey is not { Length: >= 86 and <= 88 } || auth is not { Length: >= 22 and <= 24 }) return false;
        try
        {
            var point = Base64Url.DecodeFromChars(publicKey ?? "");
            if (point.Length != 65 || point[0] != 4 || Base64Url.DecodeFromChars(auth ?? "").Length != 16) return false;
            using var key = ECDiffieHellman.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = point[1..33], Y = point[33..] } });
            return true;
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException or ArgumentException) { return false; }
    }

    /// <summary>Replaces this phone's subscription. No phone key is persisted, only the hash used by access checks.</summary>
    internal async Task<bool> SubscribeAsync(string phone, JsonObject? body, CancellationToken cancellationToken)
    {
        var endpoint = MobileAlta.Text(body, "endpoint");
        var keys = body?["keys"] as JsonObject;
        var publicKey = MobileAlta.Text(keys, "p256dh");
        var auth = MobileAlta.Text(keys, "auth");
        if (!_access.IsPhone(phone) || !ValidEndpoint(endpoint) || !ValidKeys(publicKey, auth)) return false;
        lock (_gate)
        {
            _file.Phones.RemoveAll(subscription => !_access.IsPhoneHash(subscription.Phone));
            var hash = MobileAccess.Hash(phone);
            _file.Phones.RemoveAll(subscription => subscription.Phone == hash);
            _file.Phones.Add(new MobilePushPhone { Phone = hash, Endpoint = endpoint!, PublicKey = publicKey!, Auth = auth! });
        }

        await SaveAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    internal async Task UnsubscribeAsync(string phone, CancellationToken cancellationToken)
    {
        lock (_gate) _file.Phones.RemoveAll(subscription => subscription.Phone == MobileAccess.Hash(phone));
        await SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Delivers an encrypted session link, with no prompt, answer, title, command or other session content.</summary>
    internal async Task SendAsync(MobilePushNotice notice, MobileSessions sessions, CancellationToken cancellationToken)
    {
        MobilePushPhone[] phones;
        lock (_gate) phones = [.. _file.Phones.Where(phone => _access.IsPhoneHash(phone.Phone))];
        if (phones.Length == 0 || await sessions.GetAsync(notice.SessionId, cancellationToken).ConfigureAwait(false) is null) return;
        var payload = new JsonObject { ["sessionId"] = notice.SessionId, ["kind"] = notice.Kind }.ToJsonString();
        foreach (var phone in phones)
        {
            if (!_access.IsPhoneHash(phone.Phone)) continue;
            lock (_gate) { if (!_file.Phones.Contains(phone)) continue; }
            var subscription = new PushSubscription { Endpoint = phone.Endpoint, Keys = new Dictionary<string, string> { ["p256dh"] = phone.PublicKey, ["auth"] = phone.Auth } };
            try
            {
                await _client.RequestPushMessageDeliveryAsync(subscription, new PushMessage(payload) { Urgency = PushMessageUrgency.High }, cancellationToken).ConfigureAwait(false);
            }
            catch (PushServiceClientException exception) when (exception.StatusCode is HttpStatusCode.Gone or HttpStatusCode.NotFound)
            {
                lock (_gate) _file.Phones.Remove(phone);
                await SaveAsync(cancellationToken).ConfigureAwait(false);
            }
            // A phone or its push service being unavailable must not stop delivery to the others. Never log the exception:
            // push errors include the endpoint and keys. No retry loop: the next notice can reach a recovered service.
            catch (Exception exception) when (exception is HttpRequestException or PushServiceClientException or CryptographicException
                || exception is OperationCanceledException && !cancellationToken.IsCancellationRequested) { }
        }
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        await _saving.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            MobilePushFile snapshot;
            lock (_gate) snapshot = _file with { Phones = [.. _file.Phones] };
            await _state.WriteJsonAsync(PluginStateScope.User, FileName, snapshot, CancellationToken.None).ConfigureAwait(false);
        }
        finally { _saving.Release(); }
    }

    public void Dispose()
    {
        _http.Dispose();
        _authentication.Dispose();
    }
}

internal sealed record MobilePushFile
{
    public required string PublicKey { get; init; }
    public required string PrivateKey { get; init; }
    public List<MobilePushPhone> Phones { get; init; } = [];
}

internal sealed record MobilePushPhone
{
    public required string Phone { get; init; }
    public required string Endpoint { get; init; }
    public required string PublicKey { get; init; }
    public required string Auth { get; init; }
}
