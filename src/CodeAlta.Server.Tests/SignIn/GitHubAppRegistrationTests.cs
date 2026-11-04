using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CodeAlta.Server.Tests.SignIn;

/// <summary>
/// The registration of the CodeAlta GitHub App (<c>register-github-app.ps1</c>, <c>doc/fleet/identity.md</c>), run with
/// <c>pwsh</c> against a fake of GitHub on the loopback: the page it serves posts the manifest to the organization's "new
/// GitHub App" page, the redirect of that registration only gives it the code, and the conversion's secrets are neither
/// printed nor saved unless a file is named. Inconclusive where PowerShell 7 is not installed.
/// </summary>
[TestClass]
public sealed class GitHubAppRegistrationTests
{
    private const string ClientSecret = "fake-client-secret-0123456789";
    private const string WebhookSecret = "fake-webhook-secret-0123456789";
    private const string PrivateKey = "-----BEGIN RSA PRIVATE KEY-----\nfake\n-----END RSA PRIVATE KEY-----\n";

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task TheScriptPostsTheManifest_ConvertsTheCodeOfItsOwnRedirect_AndPrintsNoSecret()
    {
        await using var github = await FakeGitHubApps.StartAsync();
        using var run = Run(github);
        var address = await run.AddressAsync(TestContext.CancellationToken);
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });

        // The page posts the manifest to the organization's page, with the state of this registration.
        var page = await http.GetStringAsync(address, TestContext.CancellationToken);
        var action = WebUtility.HtmlDecode(Regex.Match(page, "action=\"([^\"]+)\"").Groups[1].Value);
        StringAssert.StartsWith(action, github.Address.AbsoluteUri.TrimEnd('/') + "/organizations/CodeAlta/settings/apps/new?state=");
        var state = action[(action.IndexOf("state=", StringComparison.Ordinal) + "state=".Length)..];
        Assert.IsGreaterThanOrEqualTo(40, state.Length, "an unguessable state");
        StringAssert.Contains(page, "name=\"manifest\"");
        using var manifest = JsonDocument.Parse(WebUtility.HtmlDecode(Regex.Match(page, "name=\"manifest\" value=\"([^\"]*)\"").Groups[1].Value));
        var root = manifest.RootElement;
        Assert.AreEqual(("CodeAlta", "https://codealta.github.io", true, false), (root.GetProperty("name").GetString(), root.GetProperty("url").GetString(),
            root.GetProperty("public").GetBoolean(), root.GetProperty("request_oauth_on_install").GetBoolean()));
        Assert.AreEqual(0, root.GetProperty("default_permissions").EnumerateObject().Count(), "identity only: reading /user needs no permission");
        Assert.IsFalse(root.GetProperty("hook_attributes").GetProperty("active").GetBoolean(), "no webhook");
        CollectionAssert.AreEqual(new[] { "http://127.0.0.1/callback" }, root.GetProperty("callback_urls").EnumerateArray().Select(static url => url.GetString()).ToArray());
        Assert.AreEqual(address.AbsoluteUri + "redirect", root.GetProperty("redirect_url").GetString());

        // A redirect that is not this registration's gives it no code; GitHub's does.
        Assert.AreEqual(HttpStatusCode.BadRequest, (await http.GetAsync(new Uri(address, "redirect?code=stolen&state=another"), TestContext.CancellationToken)).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await http.GetAsync(new Uri(address, $"redirect?code=temp-code-1&state={state}"), TestContext.CancellationToken)).StatusCode);
        var output = await run.ExitAsync(0, TestContext.CancellationToken);

        Assert.AreEqual("temp-code-1", github.Conversions.Single(), "the code of its own redirect, converted once");
        Assert.AreEqual("application/vnd.github+json", github.Accept.Single());
        StringAssert.Contains(output, "App id:    424242");
        StringAssert.Contains(output, "Slug:      codealta");
        StringAssert.Contains(output, "Client id: Iv23liFakeClientId");
        StringAssert.Contains(output, "Enable Device Flow");
        StringAssert.Contains(output, github.Address.AbsoluteUri.TrimEnd('/') + "/organizations/CodeAlta/settings/apps/codealta");
        StringAssert.Contains(output, "CODEALTA_SIGNIN_GITHUB_CLIENT_ID=Iv23liFakeClientId");
        foreach (var secret in new[] { ClientSecret, WebhookSecret, "PRIVATE KEY" }) Assert.IsFalse(output.Contains(secret, StringComparison.Ordinal), $"{secret} is not printed");
    }

    [TestMethod]
    public async Task TheSecretsGoOnlyToTheNewFileTheOwnerNames()
    {
        var folder = Directory.CreateTempSubdirectory("codealta-github-app-").FullName;
        try
        {
            var file = Path.Combine(folder, "codealta-github-app.json");
            await using var github = await FakeGitHubApps.StartAsync();
            using var run = Run(github, "-SaveCredentialsTo", file);
            var address = await run.AddressAsync(TestContext.CancellationToken);
            using var http = new HttpClient();
            var page = await http.GetStringAsync(address, TestContext.CancellationToken);
            var state = Regex.Match(WebUtility.HtmlDecode(page), "state=([A-Za-z0-9_-]+)").Groups[1].Value;
            Assert.AreEqual(HttpStatusCode.OK, (await http.GetAsync(new Uri(address, $"redirect?code=temp-code-2&state={state}"), TestContext.CancellationToken)).StatusCode);
            var output = await run.ExitAsync(0, TestContext.CancellationToken);

            using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(file, TestContext.CancellationToken));
            Assert.AreEqual((ClientSecret, WebhookSecret, PrivateKey, "Iv23liFakeClientId"), (saved.RootElement.GetProperty("client_secret").GetString(),
                saved.RootElement.GetProperty("webhook_secret").GetString(), saved.RootElement.GetProperty("pem").GetString(), saved.RootElement.GetProperty("client_id").GetString()));
            if (!OperatingSystem.IsWindows())
                Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file), "readable by its owner only");
            Assert.IsFalse(output.Contains(ClientSecret, StringComparison.Ordinal) || output.Contains("PRIVATE KEY", StringComparison.Ordinal), "the secrets are not printed either");

            // A file that exists is never overwritten, and nothing is registered.
            using var again = Run(github, "-SaveCredentialsTo", file);
            StringAssert.Contains(await again.ExitAsync(1, TestContext.CancellationToken), "exists");
            Assert.AreEqual(1, github.Conversions.Count);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static ScriptRun Run(FakeGitHubApps github, params string[] more)
    {
        var start = new ProcessStartInfo("pwsh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-File", Path.Combine(AppContext.BaseDirectory, "GitHubApp", "register-github-app.ps1"), "-NoBrowser",
            "-GitHubUrl", github.Address.AbsoluteUri, "-ApiUrl", github.Address.AbsoluteUri, "-TimeoutSeconds", "120" }.Concat(more))
            start.ArgumentList.Add(argument);
        Process process;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException("pwsh did not start.");
        }
        catch (Win32Exception)
        {
            Assert.Inconclusive("PowerShell 7 (pwsh) is not installed.");
            throw;
        }

        return new ScriptRun(process);
    }

    // The output of the script, line by line, and the address it serves its page on.
    private sealed class ScriptRun : IDisposable
    {
        private readonly Process _process;
        private readonly ConcurrentQueue<string> _lines = new();
        private readonly TaskCompletionSource<Uri> _address = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ScriptRun(Process process)
        {
            _process = process;
            process.OutputDataReceived += (_, line) => Add(line.Data);
            process.ErrorDataReceived += (_, line) => Add(line.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.Exited += (_, _) => _address.TrySetException(new AssertFailedException("The script ended before it listened: " + string.Join('\n', _lines)));
            process.EnableRaisingEvents = true;
        }

        public async Task<Uri> AddressAsync(CancellationToken cancellationToken) => await _address.Task.WaitAsync(TimeSpan.FromSeconds(60), cancellationToken);

        public async Task<string> ExitAsync(int expected, CancellationToken cancellationToken)
        {
            await _process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(60), cancellationToken);
            _process.WaitForExit();
            var output = string.Join('\n', _lines);
            Assert.AreEqual(expected, _process.ExitCode, output);
            return output;
        }

        public void Dispose()
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            _process.Dispose();
        }

        private void Add(string? line)
        {
            if (line is null) return;
            _lines.Enqueue(line);
            if (line.StartsWith("Listening on ", StringComparison.Ordinal)) _address.TrySetResult(new Uri(line["Listening on ".Length..].Trim()));
        }
    }

    // GitHub's POST /app-manifests/{code}/conversions: the app, with the secrets a real one returns.
    private sealed class FakeGitHubApps : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private FakeGitHubApps(WebApplication app) => _app = app;

        public Uri Address { get; private set; } = null!;

        public ConcurrentQueue<string> Conversions { get; } = new();

        public ConcurrentQueue<string> Accept { get; } = new();

        public static async Task<FakeGitHubApps> StartAsync()
        {
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            var fake = new FakeGitHubApps(app);
            app.MapPost("/app-manifests/{code}/conversions", (string code, HttpContext context) =>
            {
                fake.Conversions.Enqueue(code);
                fake.Accept.Enqueue(context.Request.Headers.Accept.ToString());
                return Results.Text(JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["id"] = 424242, ["slug"] = "codealta", ["name"] = "CodeAlta", ["client_id"] = "Iv23liFakeClientId", ["html_url"] = "https://github.com/apps/codealta",
                    ["owner"] = new Dictionary<string, object> { ["login"] = "CodeAlta" }, ["client_secret"] = ClientSecret, ["webhook_secret"] = WebhookSecret, ["pem"] = PrivateKey,
                }), "application/json", statusCode: 201);
            });
            await app.StartAsync();
            fake.Address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().TrimEnd('/') + "/");
            return fake;
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
