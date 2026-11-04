using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CodeAlta.Server.Tests.SignIn;

/// <summary>
/// GitHub's device flow in the process of a test, on the loopback, as a GitHub App with "Enable Device Flow" on answers it:
/// <c>POST /login/device/code</c> gives a device code and a user code, <c>POST /login/oauth/access_token</c> answers
/// <c>authorization_pending</c> until the person decides — <see cref="Approve"/>, <see cref="Deny"/>, <see cref="Expire"/> —
/// or <c>slow_down</c> when told, and <c>GET /user</c> reads the account with the user token. It refuses any request
/// that carries a client secret, and counts the polls that reached it.
/// </summary>
internal sealed class FakeGitHubDeviceFlow : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<string, Code> _codes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, FakeAccount> _tokens = new(StringComparer.Ordinal);
    private int _polls;
    private int _slowDowns;
    private int _secrets;

    private FakeGitHubDeviceFlow(WebApplication app, string clientId)
    {
        _app = app;
        ClientId = clientId;
    }

    /// <summary>Gets the address of its pages and of its API.</summary>
    public Uri Address { get; private set; } = null!;

    /// <summary>Gets the client id of the GitHub App it accepts.</summary>
    public string ClientId { get; }

    /// <summary>Gets or sets the interval it asks, in seconds.</summary>
    public int Interval { get; set; } = 5;

    /// <summary>Gets or sets whether the device flow is off for the app.</summary>
    public bool DeviceFlowDisabled { get; set; }

    /// <summary>Gets how many polls of the token endpoint reached it.</summary>
    public int Polls => Volatile.Read(ref _polls);

    /// <summary>Gets how many requests carried a client secret (which it refused).</summary>
    public int Secrets => Volatile.Read(ref _secrets);

    /// <summary>Gets the user codes it gave, in order.</summary>
    public ConcurrentQueue<string> UserCodes { get; } = new();

    /// <summary>Gets the user tokens it gave.</summary>
    public ICollection<string> Tokens => _tokens.Keys;

    /// <summary>Starts it on the loopback.</summary>
    /// <param name="clientId">The client id of the GitHub App.</param>
    /// <returns>The fake.</returns>
    public static async Task<FakeGitHubDeviceFlow> StartAsync(string clientId = "Iv23-codealta-test")
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var fake = new FakeGitHubDeviceFlow(app, clientId);
        fake.Map();
        await app.StartAsync();
        fake.Address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().TrimEnd('/'));
        return fake;
    }

    /// <summary>The person enters the code and approves the app: the next poll gets a user token of <paramref name="account"/>.</summary>
    /// <param name="userCode">The code.</param>
    /// <param name="account">The account that signs in.</param>
    public void Approve(string userCode, FakeAccount account) => Decide(userCode, "approved", account);

    /// <summary>The person refuses: the next poll gets <c>access_denied</c>.</summary>
    /// <param name="userCode">The code.</param>
    public void Deny(string userCode) => Decide(userCode, "access_denied", null);

    /// <summary>The code expires: the next poll gets <c>expired_token</c>.</summary>
    /// <param name="userCode">The code.</param>
    public void Expire(string userCode) => Decide(userCode, "expired_token", null);

    /// <summary>The next polls get <c>slow_down</c>, which names an interval five seconds longer than <see cref="Interval"/>.</summary>
    /// <param name="count">How many.</param>
    public void SlowDown(int count = 1) => Interlocked.Add(ref _slowDowns, count);

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private void Decide(string userCode, string outcome, FakeAccount? account)
    {
        var code = _codes.Values.Single(code => code.UserCode == userCode);
        code.Account = account;
        code.Outcome = outcome;
    }

    private void Map()
    {
        _app.MapPost("/login/device/code", async (HttpContext context) =>
        {
            var form = await context.Request.ReadFormAsync();
            if (form.ContainsKey("client_secret")) { Interlocked.Increment(ref _secrets); return Error("incorrect_client_credentials"); }
            if (form["client_id"] != ClientId) return Error("incorrect_client_credentials");
            if (DeviceFlowDisabled) return Error("device_flow_disabled");
            var deviceCode = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N")[..8];
            var userCode = $"{Random.Shared.Next(0x1000, 0xFFFF):X4}-{Random.Shared.Next(0x1000, 0xFFFF):X4}";
            _codes[deviceCode] = new Code(userCode);
            UserCodes.Enqueue(userCode);
            return Json(new Dictionary<string, object>
            {
                ["device_code"] = deviceCode, ["user_code"] = userCode, ["verification_uri"] = Address.AbsoluteUri.TrimEnd('/') + "/login/device",
                ["expires_in"] = 900, ["interval"] = Interval,
            });
        });
        _app.MapPost("/login/oauth/access_token", async (HttpContext context) =>
        {
            var form = await context.Request.ReadFormAsync();
            Interlocked.Increment(ref _polls);
            if (form.ContainsKey("client_secret")) { Interlocked.Increment(ref _secrets); return Error("incorrect_client_credentials"); }
            if (form["client_id"] != ClientId) return Error("incorrect_client_credentials");
            if (form["grant_type"] != "urn:ietf:params:oauth:grant-type:device_code") return Error("unsupported_grant_type");
            if (!_codes.TryGetValue(form["device_code"].ToString(), out var code)) return Error("incorrect_device_code");
            if (Interlocked.Decrement(ref _slowDowns) >= 0)
            {
                return Json(new Dictionary<string, object> { ["error"] = "slow_down", ["interval"] = Interval + 5 });
            }

            Interlocked.Exchange(ref _slowDowns, 0);
            switch (code.Outcome)
            {
                case null: return Error("authorization_pending");
                case "approved":
                    // A device code gives one token.
                    _codes.TryRemove(form["device_code"].ToString(), out _);
                    var token = "ghu_" + Guid.NewGuid().ToString("N");
                    _tokens[token] = code.Account!;
                    return Json(new Dictionary<string, object> { ["access_token"] = token, ["token_type"] = "bearer", ["expires_in"] = 28800, ["scope"] = "" });
                default: return Error(code.Outcome);
            }
        });
        _app.MapGet("/user", (HttpContext context) =>
        {
            var header = context.Request.Headers.Authorization.ToString();
            if (!header.StartsWith("Bearer ", StringComparison.Ordinal) || !_tokens.TryGetValue(header["Bearer ".Length..], out var account)) return Results.StatusCode(401);
            return Json(new Dictionary<string, object?> { ["id"] = account.GitHubId, ["login"] = account.Subject, ["name"] = account.Name, ["email"] = account.Email });
        });
    }

    // GitHub answers the errors of the device flow with 200 and a JSON body.
    private static IResult Error(string error) => Json(new Dictionary<string, object> { ["error"] = error, ["error_description"] = error });

    private static IResult Json<T>(T value) => Results.Text(JsonSerializer.Serialize(value), "application/json");

    private sealed class Code(string userCode)
    {
        public string UserCode { get; } = userCode;

        public volatile string? Outcome;

        public FakeAccount? Account { get; set; }
    }
}
