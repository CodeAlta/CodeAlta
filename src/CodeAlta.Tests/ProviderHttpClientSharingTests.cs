using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Anthropic;
using CodeAlta.Agent.Copilot;
using CodeAlta.Agent.GoogleGenAI;
using CodeAlta.Agent.Mistral;
using CodeAlta.Agent.OpenAI;
using CodeAlta.Agent.Xai;
using CodeAlta.Catalog;
using CodeAlta.Hosting;

namespace CodeAlta.Tests;

/// <summary>
/// The runtimes a registration creates (one for each session) share one HTTP client, where a client was made for each
/// request or each session, and Copilot and xAI one credential manager; the credential files are replaced, never
/// missing, while they are written.
/// </summary>
[TestClass]
public sealed class ProviderHttpClientSharingTests
{
    private static readonly TimeSpan Hang = TimeSpan.FromSeconds(30);

    [TestMethod]
    [DataRow("mistral")]
    [DataRow("anthropic")]
    [DataRow("google-genai")]
    [DataRow("codex")]
    public void Registration_GivesItsRuntimesOneHttpClient_AndAnotherRegistrationItsOwn(string providerType)
    {
        using var temp = TestTempDirectory.Create();
        var (first, second) = CreateTwoRuntimes(providerType, temp.Path);
        var (other, _) = CreateTwoRuntimes(providerType, temp.Path);

        var firstTransport = GetSharedTransport(providerType, first);
        Assert.IsNotNull(firstTransport);
        Assert.AreSame(firstTransport, GetSharedTransport(providerType, second));
        Assert.AreNotSame(firstTransport, GetSharedTransport(providerType, other));
    }

    [TestMethod]
    [DataRow("copilot")]
    [DataRow("xai")]
    public void Registration_GivesItsSessionsOneHttpClientAndOneCredentialManager(string providerType)
    {
        using var temp = TestTempDirectory.Create();
        var (first, second) = CreateTwoRuntimes(providerType, temp.Path);
        var (other, _) = CreateTwoRuntimes(providerType, temp.Path);
        var firstExecutor = ((IAgentModelProviderRuntime)first).CreateTurnExecutor();
        var secondExecutor = ((IAgentModelProviderRuntime)second).CreateTurnExecutor();
        var otherExecutor = ((IAgentModelProviderRuntime)other).CreateTurnExecutor();

        Assert.AreSame(GetField<HttpClient>(firstExecutor, "_httpClient"), GetField<HttpClient>(secondExecutor, "_httpClient"));
        Assert.AreSame(GetField<object>(firstExecutor, "_authManager"), GetField<object>(secondExecutor, "_authManager"));
        Assert.AreNotSame(GetField<object>(firstExecutor, "_authManager"), GetField<object>(otherExecutor, "_authManager"));

        // The shared manager reads the credential of the state root, as the one each session made did.
        var store = FindReachable(GetField<object>(firstExecutor, "_authManager"), static value => value is CopilotDirectCredentialStore or XaiDirectCredentialStore);
        var path = Assert.IsInstanceOfType<string>(GetField<object>(store!, "_path"));
        Assert.IsTrue(path.StartsWith(Path.Combine(temp.Path, "auth"), StringComparison.Ordinal), path);
    }

    [TestMethod]
    public void SharedClients_KeepTheSettingsOfTheClientsTheyReplace()
    {
        var perRequestClient = ProviderHttpClients.CreatePerRequestReplacement();
        var perRequest = HandlerOf(perRequestClient);
        Assert.AreEqual(TimeSpan.FromSeconds(100), perRequestClient.Timeout);
        Assert.AreEqual(DecompressionMethods.None, perRequest.AutomaticDecompression);
        Assert.IsFalse(perRequest.UseCookies);
        Assert.IsTrue(perRequest.UseProxy);
        Assert.IsTrue(perRequest.AllowAutoRedirect);
        Assert.AreEqual(ProviderHttpClients.PooledConnectionLifetime, perRequest.PooledConnectionLifetime);
        Assert.AreEqual(0, perRequestClient.DefaultRequestHeaders.Count());
        Assert.IsNull(perRequestClient.BaseAddress);

        var perSessionClient = ProviderHttpClients.CreatePerSessionReplacement();
        var perSession = HandlerOf(perSessionClient);
        Assert.AreEqual(TimeSpan.FromSeconds(100), perSessionClient.Timeout);
        Assert.IsTrue(perSession.UseCookies);

        var anthropicClient = ProviderHttpClients.CreateForAnthropicSdk();
        var anthropic = HandlerOf(anthropicClient);
        Assert.AreEqual(Timeout.InfiniteTimeSpan, anthropicClient.Timeout);
        Assert.AreEqual(DecompressionMethods.GZip, anthropic.AutomaticDecompression);
        Assert.IsFalse(anthropic.UseCookies);

        var google = Assert.IsInstanceOfType<SocketsHttpHandler>(ProviderHttpClients.CreateHandlerForGoogleSdk());
        Assert.AreEqual(DecompressionMethods.None, google.AutomaticDecompression);
        Assert.IsFalse(google.UseCookies);
    }

    [TestMethod]
    public async Task GoogleSdkClients_DisposedByTheSdk_LeaveTheSharedHandlerUsable()
    {
        var requests = 0;
        var handler = new StubHandler(_ =>
        {
            requests++;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var options = GoogleGenAIModelProviderRuntime.CreateClientOptions(new GoogleGenAIProviderOptions { ProviderKey = "google", HttpMessageHandler = handler });
        var factory = options!.HttpClientFactory!;

        var first = factory();
        Assert.AreEqual(Timeout.InfiniteTimeSpan, first.Timeout);
        using (first)
        {
            (await first.GetAsync(new Uri("https://generativelanguage.test/"))).Dispose();
        }

        using var second = factory();
        Assert.AreNotSame(first, second);
        (await second.GetAsync(new Uri("https://generativelanguage.test/"))).Dispose();
        Assert.AreEqual(2, requests);
        Assert.IsNull(GoogleGenAIModelProviderRuntime.CreateClientOptions(new GoogleGenAIProviderOptions { ProviderKey = "google" }));
    }

    [TestMethod]
    public async Task CopilotCredentialStore_IsNeverMissingWhileItIsReplaced()
    {
        using var temp = TestTempDirectory.Create();
        var store = new CopilotDirectCredentialStore(temp.Path, "copilot");
        await store.WriteAsync(new CopilotDirectCredentialCache { GitHubToken = "gh-0" }, CancellationToken.None);

        await RaceWritersAndReadersAsync(
            index => store.WriteAsync(new CopilotDirectCredentialCache { GitHubToken = "gh-" + index }, CancellationToken.None),
            async () => (await store.ReadAsync(CancellationToken.None))?.GitHubToken);

        AssertOwnerOnly(Path.Combine(temp.Path, "auth", "copilot", "copilot.json"));
        Assert.AreEqual(1, Directory.GetFiles(Path.Combine(temp.Path, "auth", "copilot")).Length);
    }

    [TestMethod]
    public async Task XaiCredentialStore_IsNeverMissingWhileItIsReplaced()
    {
        using var temp = TestTempDirectory.Create();
        var store = new XaiDirectCredentialStore(temp.Path, "xai");
        await store.WriteAsync(new XaiDirectCredentialCache { AccessToken = "at-0" }, CancellationToken.None);

        await RaceWritersAndReadersAsync(
            index => store.WriteAsync(new XaiDirectCredentialCache { AccessToken = "at-" + index }, CancellationToken.None),
            async () => (await store.ReadAsync(CancellationToken.None))?.AccessToken);

        AssertOwnerOnly(Path.Combine(temp.Path, "auth", "xai", "xai.json"));
    }

    [TestMethod]
    public async Task CopilotCredentialStore_RefusedWrite_KeepsTheCacheHiddenAndReadOnly()
    {
        using var temp = TestTempDirectory.Create();
        var store = new CopilotDirectCredentialStore(temp.Path, "copilot");
        await AssertRefusedWriteKeepsRestrictionAsync(
            Path.Combine(temp.Path, "auth", "copilot", "copilot.json"),
            () => store.WriteAsync(new CopilotDirectCredentialCache { GitHubToken = "gh-0" }, CancellationToken.None),
            () => store.WriteAsync(new CopilotDirectCredentialCache { GitHubToken = "gh-1" }, CancellationToken.None));
    }

    [TestMethod]
    public async Task XaiCredentialStore_RefusedWrite_KeepsTheCacheHiddenAndReadOnly()
    {
        using var temp = TestTempDirectory.Create();
        var store = new XaiDirectCredentialStore(temp.Path, "xai");
        await AssertRefusedWriteKeepsRestrictionAsync(
            Path.Combine(temp.Path, "auth", "xai", "xai.json"),
            () => store.WriteAsync(new XaiDirectCredentialCache { AccessToken = "at-0" }, CancellationToken.None),
            () => store.WriteAsync(new XaiDirectCredentialCache { AccessToken = "at-1" }, CancellationToken.None));
    }

    [TestMethod]
    public async Task XaiCredential_ExpiredForTwoManagersAtOnce_IsRefreshedOnce()
    {
        using var temp = TestTempDirectory.Create();
        var store = new XaiDirectCredentialStore(temp.Path, "xai");
        await store.WriteAsync(new XaiDirectCredentialCache
        {
            AccessToken = "expired",
            RefreshToken = "refresh-1",
            ExpiresAtUnixSeconds = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds(),
        }, CancellationToken.None);

        // A refresh token is used once: a second refresh with it would be refused, and the credential deleted.
        var refreshes = 0;
        var usedRefreshTokens = new HashSet<string>(StringComparer.Ordinal);
        using var gate = new SemaphoreSlim(1, 1);
        var handler = new AsyncStubHandler(async request =>
        {
            var body = await request.Content!.ReadAsStringAsync();
            var refreshToken = body.Split('&').Single(static pair => pair.StartsWith("refresh_token=", StringComparison.Ordinal))["refresh_token=".Length..];
            await gate.WaitAsync();
            try
            {
                refreshes++;
                if (!usedRefreshTokens.Add(refreshToken))
                {
                    return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("""{"error":"invalid_grant"}""") };
                }
            }
            finally
            {
                gate.Release();
            }

            // Long enough for the other manager to find the credential expired too.
            await Task.Delay(100);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""{"access_token":"refreshed-{{refreshes}}","refresh_token":"refresh-{{refreshes + 1}}","expires_in":3600}""",
                    Encoding.UTF8,
                    "application/json"),
            };
        });

        // Two registrations (or two processes) of one provider.
        var first = new XaiDirectAuthManager(new XaiProviderOptions { ProviderKey = "xai", StateRootPath = temp.Path }, new HttpClient(handler));
        var second = new XaiDirectAuthManager(new XaiProviderOptions { ProviderKey = "xai", StateRootPath = temp.Path }, new HttpClient(handler));
        var credentials = await Task.WhenAll(
            first.GetCredentialAsync(CancellationToken.None).AsTask(),
            second.GetCredentialAsync(CancellationToken.None).AsTask()).WaitAsync(Hang);

        Assert.AreEqual(1, refreshes);
        Assert.AreEqual(credentials[0].Token, credentials[1].Token);
        Assert.AreEqual("refreshed-1", (await store.ReadAsync(CancellationToken.None))?.AccessToken);
    }

    [TestMethod]
    public async Task CopilotSharedCredentialManager_ExchangesTheTokenOnceForAllItsSessions()
    {
        using var temp = TestTempDirectory.Create();
        var exchanges = 0;
        var handler = new AsyncStubHandler(async _ =>
        {
            Interlocked.Increment(ref exchanges);
            await Task.Delay(50);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$$"""{"token":"copilot-token","expires_at":{{{DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()}}},"endpoints":{"api":"https://api.copilot.test"}}""",
                    Encoding.UTF8,
                    "application/json"),
            };
        });
        var variable = "CODEALTA_TEST_GITHUB_TOKEN_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, "gh-token");
        try
        {
            var provider = new CopilotDirectProviderOptions
            {
                ProviderKey = "copilot",
                StateRootPath = temp.Path,
                Auth = new CopilotDirectAuthOptions { AuthSource = CopilotDirectAuthSources.GitHubTokenEnvironment, GitHubTokenEnvironmentVariable = variable },
            };
            var services = new CopilotDirectProviderServices(provider, new HttpClient(handler));
            var credentials = await Task.WhenAll(Enumerable.Range(0, 8)
                .Select(_ => services.AuthManager.GetCredentialAsync(CancellationToken.None).AsTask())).WaitAsync(Hang);

            Assert.AreEqual(1, exchanges);
            Assert.IsTrue(credentials.All(static credential => credential.Token == "copilot-token"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    private static async Task RaceWritersAndReadersAsync(Func<int, ValueTask> write, Func<Task<string?>> read)
    {
        using var stop = new CancellationTokenSource();
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            var reads = 0;
            while (!stop.IsCancellationRequested || reads == 0)
            {
                Assert.IsNotNull(await read(), "A reader found no credential while it was replaced.");
                reads++;
            }
        })).ToArray();
        var writers = Enumerable.Range(0, 4).Select(writer => Task.Run(async () =>
        {
            for (var index = 0; index < 50; index++)
            {
                await write(writer * 1000 + index);
            }
        })).ToArray();

        try
        {
            await Task.WhenAll(writers).WaitAsync(Hang);
        }
        finally
        {
            await stop.CancelAsync();
        }

        await Task.WhenAll(readers).WaitAsync(Hang);
    }

    // On Windows a reader that does not share the deletion of the cache (an antivirus, a backup tool, an editor) makes
    // its replacement fail: the cache it keeps stays hidden, and read-only when the user made it so.
    private static async Task AssertRefusedWriteKeepsRestrictionAsync(string path, Func<ValueTask> write, Func<ValueTask> writeAgain)
    {
        if (!OperatingSystem.IsWindows()) return;
        await write();
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
        var before = File.ReadAllText(path);

        await using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var failure = await Assert.ThrowsAsync<Exception>(() => writeAgain().AsTask().WaitAsync(Hang));
            Assert.IsTrue(failure is IOException or UnauthorizedAccessException, failure.ToString());
        }

        var attributes = File.GetAttributes(path);
        Assert.AreEqual(FileAttributes.Hidden | FileAttributes.ReadOnly, attributes & (FileAttributes.Hidden | FileAttributes.ReadOnly), $"Attributes: {attributes}");
        Assert.AreEqual(before, File.ReadAllText(path));
        Assert.AreEqual(1, Directory.GetFiles(Path.GetDirectoryName(path)!).Length, "No temporary file is left behind.");
        File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
    }

    private static void AssertOwnerOnly(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
    }

    private static (IModelProviderRuntime First, IModelProviderRuntime Second) CreateTwoRuntimes(string providerType, string stateRoot)
    {
        Assert.IsTrue(ConfiguredModelProviderRegistryBuilder.TryCreateProviderRegistration(
            new CodeAltaProviderDocument
            {
                ProviderKey = providerType,
                ProviderType = providerType,
                ApiKey = "test-key",
                AuthSource = providerType == "copilot" ? CopilotDirectAuthSources.GitHubDeviceFlow : null,
            },
            stateRoot,
            null,
            out _,
            out var createRuntime));
        // Two sessions of the provider, each with the runtime the registry creates for it.
        return (createRuntime(), createRuntime());
    }

    private static object? GetSharedTransport(string providerType, IModelProviderRuntime runtime)
    {
        var executor = ((IAgentModelProviderRuntime)runtime).CreateTurnExecutor();
        return providerType switch
        {
            "mistral" => FindReachable<MistralProviderOptions>(executor)?.HttpClient,
            "anthropic" => FindReachable<AnthropicProviderOptions>(executor)?.HttpClient,
            "google-genai" => FindReachable<GoogleGenAIProviderOptions>(executor)?.HttpMessageHandler,
            "codex" => FindReachable<OpenAIProviderOptions>(executor)?.CodexSubscriptionHttpClient,
            _ => throw new ArgumentOutOfRangeException(nameof(providerType)),
        };
    }

    private static SocketsHttpHandler HandlerOf(HttpClient client)
    {
        var field = typeof(HttpMessageInvoker).GetField("_handler", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return Assert.IsInstanceOfType<SocketsHttpHandler>(field.GetValue(client));
    }

    private static T GetField<T>(object owner, string name)
        => (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;

    private static T? FindReachable<T>(object root)
        where T : class
        => (T?)FindReachable(root, static value => value is T);

    // The options a runtime captured, found through its fields and the targets of its delegates, without starting it.
    private static object? FindReachable(object root, Func<object, bool> match)
    {
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var pending = new Queue<(object Value, int Depth)>();
        pending.Enqueue((root, 0));
        while (pending.TryDequeue(out var entry))
        {
            if (!visited.Add(entry.Value))
            {
                continue;
            }

            if (match(entry.Value))
            {
                return entry.Value;
            }

            if (entry.Depth >= 6)
            {
                continue;
            }

            for (var type = entry.Value.GetType(); type is not null && type != typeof(object); type = type.BaseType)
            {
                foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly))
                {
                    if (field.FieldType.IsPrimitive || field.FieldType == typeof(string) || field.FieldType.IsPointer)
                    {
                        continue;
                    }

                    if (field.GetValue(entry.Value) is { } value && !value.GetType().IsPrimitive && value is not string and not JsonElement)
                    {
                        pending.Enqueue((value, entry.Depth + 1));
                    }
                }
            }
        }

        return null;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(handler(request));
    }

    private sealed class AsyncStubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => handler(request);
    }
}
