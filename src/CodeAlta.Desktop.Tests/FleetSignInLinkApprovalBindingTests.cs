using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Net.Http.Json;
using CodeAlta.Desktop.Fleet;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Fleet.Server;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Server.Hosting;
using CodeAlta.Server.Identity.SignIn;
using CodeAlta.Server.Tests.SignIn;
using Microsoft.Extensions.Logging;

namespace CodeAlta.Desktop.Tests;

/// <summary>
/// Approving the link of a way to sign in (<c>doc/fleet/identity.md</c>, linked sign-in methods): the computer signs only
/// the link its page showed, by a digest it computed itself of what it showed, and the recovery key signs only the link of
/// the method the person chose from this computer. A server that shows one link and answers another gets nothing signed;
/// and the words of the recovery key never leave the computer.
/// </summary>
[TestClass]
public sealed class FleetSignInLinkApprovalBindingTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task TheComputerSignsOnlyTheLinkItShowed_AndTheRecoveryKeyOnlyTheMethodChosen_WhoseWordsNeverLeaveIt()
    {
        var token = TestContext.CancellationToken;
        var root = Directory.CreateTempSubdirectory("codealta-fleet-link-binding-").FullName;
        string At(string name) => Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        try
        {
            await using var github = await FakeGitHubDeviceFlow.StartAsync();
            github.Interval = 1;
            var serverLogs = new ConcurrentQueue<string>();
            await using var server = await CoordinatorServer.StartAsync(new CoordinatorServerOptions
            {
                DataRoot = At("server"), Urls = ["http://127.0.0.1:0"], EnvironmentVariable = static _ => null,
                ConfigureLogging = logging => { logging.ClearProviders(); logging.SetMinimumLevel(LogLevel.Trace); logging.AddProvider(new CapturingLoggerProvider(serverLogs)); },
                SignIn = new SignInOptions { AllowLoopbackHttpProviders = true, GitHub = new SignInClient(github.ClientId), GitHubAddress = github.Address, GitHubApiAddress = github.Address },
            }, token);
            var address = server.Addresses.First(static uri => uri.Scheme == "http");

            // The owner's desktop, whose transport the test controls: it records what leaves the computer, and can make the
            // server answer another link than the one it holds, with the digest of that other link, as a server that lies would.
            using var transport = new LyingHandler();
            await using var host = await CodeAltaHost.CreateAsync(new CodeAltaHostOptions
            {
                GlobalRoot = At("owner"), CurrentProjectPath = At("project"), DiscoveryScope = new(At("home"), root), BuiltInSkillRoot = At("builtin"),
                PluginEnvironment = FrozenDictionary<string, string?>.Empty, StartPlugins = false, IsHeadless = true, OwnsLogging = false,
            }, token);
            await using var servers = new DesktopFleetServers(host, "Owner desktop", "4.0.0") { Handler = transport, ApprovalPollInterval = TimeSpan.FromMilliseconds(100) };
            var epoch = Guid.NewGuid().ToString("D");
            var desktop = new FleetServerService(servers, epoch) { Spaces = host.SpaceCatalog, IsPaired = static _ => false };
            var created = await desktop.CreateAsync(new FleetServerCreateRequest(epoch, address.ToString(), server.SetupCode!.Code, "Alex", "Home"), token);
            Assert.AreEqual("ok", created.Status, created.Message);
            var linkId = created.Server!.Id;
            // The words of the recovery key the server was created with.
            var words = created.RecoveryWords!;
            Assert.IsFalse(string.IsNullOrEmpty(words));

            // The owner's laptop, another controller device of theirs, asks to link GitHub.
            using var laptopKey = DeviceIdentity.LoadOrCreate(At("laptop"));
            using var laptop = new FleetServerClient(address, laptopKey);
            await laptop.EnrollAsync((await servers.CreateEnrollmentTokenAsync(linkId, TimeSpan.FromMinutes(10), "laptop", token)).Token, "Laptop", FleetDeviceKinds.Controller, token);
            var pending = (await desktop.DevicesAsync(new FleetServerLinkRequest(epoch, linkId), token)).Devices.Single(static device => device.Status == "pending");
            Assert.AreEqual("ok", (await desktop.ApproveAsync(new FleetServerDeviceRequest(epoch, linkId, pending.Id, pending.Words, "controller"), token)).Status);
            var organization = (await laptop.GetOrganizationAsync(token)).Id;
            transport.Organization = organization;
            var asked = await laptop.RequestLinkApprovalAsync(FleetSignInMethods.GitHub, token);

            // The server shows a passkey with the digest of the GitHub link it holds: the page shows the passkey, with the
            // digest this computer computed of a passkey, never the server's.
            transport.ListedMethod = FleetSignInMethods.Passkey;
            transport.KeepServerDigest = true;
            var shown = (await desktop.SignInSettingsAsync(new FleetServerLinkRequest(epoch, linkId), token)).LinkApprovals!.Single();
            Assert.AreEqual((asked.Id, FleetSignInMethods.Passkey), (shown.Id, shown.Method));
            Assert.AreEqual(FleetSignInApprovals.LinkDigest(organization, laptop.DeviceId, FleetSignInMethods.Passkey), shown.Digest);
            Assert.AreNotEqual(asked.Digest, shown.Digest, "the server's digest is of GitHub");

            // Approve is clicked: the server now answers the GitHub link it holds, self-consistent. Nothing is signed.
            transport.ListedMethod = null;
            transport.KeepServerDigest = false;
            var refused = await desktop.ApproveSignInLinkAsync(new FleetSignInLinkApprovalRequest(epoch, linkId, shown.Id, shown.Digest), token);
            Assert.AreEqual("sign_in.approval_mismatch", refused.Status, refused.Message);
            Assert.AreEqual(0, transport.LinkApprovals, "nothing was signed");
            Assert.AreEqual("pending", (await laptop.GetLinkApprovalAsync(asked.Id, token)).Status);

            // Shown as it is, GitHub, the link is approved by the digest of what was shown.
            var honest = (await desktop.SignInSettingsAsync(new FleetServerLinkRequest(epoch, linkId), token)).LinkApprovals!.Single();
            Assert.AreEqual((FleetSignInMethods.GitHub, asked.Digest), (honest.Method, honest.Digest));
            Assert.AreEqual("ok", (await desktop.ApproveSignInLinkAsync(new FleetSignInLinkApprovalRequest(epoch, linkId, honest.Id, honest.Digest), token)).Status);
            Assert.AreEqual(1, transport.LinkApprovals);

            // On the desktop, the person links GitHub with their recovery key. The server answers the approval of another
            // method, or of another computer, each with its own digest: the recovery key signs neither.
            FleetSignInCodeRequest WithRecoveryKey() => new(epoch, "github", null, linkId, null, "recovery", null, words);
            transport.AskedMethod = FleetSignInMethods.Passkey;
            Assert.AreEqual("recovery.refused", (await desktop.StartSignInCodeAsync(WithRecoveryKey(), token)).Status, "the approval of a passkey");
            transport.AskedMethod = null;
            transport.AskedRequester = laptop.DeviceId;
            Assert.AreEqual("recovery.refused", (await desktop.StartSignInCodeAsync(WithRecoveryKey(), token)).Status, "the approval of another computer");
            transport.AskedRequester = null;
            Assert.AreEqual(0, transport.RecoveryApprovals, "the recovery key signed nothing");

            // Asked as chosen, it signs it, and GitHub is linked.
            var withKey = await desktop.StartSignInCodeAsync(WithRecoveryKey(), token);
            Assert.AreEqual("ok", withKey.Status, withKey.Message);
            Assert.AreEqual(1, transport.RecoveryApprovals);
            github.Approve(withKey.Code!.UserCode, new FakeAccount("octo-alex", "alex@example.com", "Alex Martin", [], 4242));
            Assert.AreEqual("ok", (await desktop.LinkSignInAsync(new FleetSignInLinkRequest(epoch, linkId, "github", withKey.Code.Handle), token)).Status);

            // Nothing that left the computer, and nothing the server logged, carries the words, or a run of them.
            var list = words.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var runs = Enumerable.Range(0, list.Length - 2).Select(index => string.Join(' ', list[index..(index + 3)])).Append(string.Concat(list)).ToArray();
            Assert.IsTrue(transport.Sent.Count > 10, "the requests were recorded");
            foreach (var sent in transport.Sent.Concat(serverLogs))
            {
                foreach (var run in runs) Assert.IsFalse(sent.Contains(run, StringComparison.OrdinalIgnoreCase), $"'{run}' left the computer: {sent[..Math.Min(sent.Length, 300)]}");
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    // The transport of the owner's desktop: it records each request that leaves the computer (address, headers, body),
    // counts the approvals it signs, and makes the server's answers name another link, with the digest of that other link
    // unless told to keep the server's.
    private sealed class LyingHandler() : DelegatingHandler(new SocketsHttpHandler())
    {
        public ConcurrentQueue<string> Sent { get; } = new();

        public string Organization { get; set; } = string.Empty;

        public string? ListedMethod { get; set; }

        public bool KeepServerDigest { get; set; }

        public string? AskedMethod { get; set; }

        public string? AskedRequester { get; set; }

        public int LinkApprovals { get; private set; }

        public int RecoveryApprovals { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Sent.Enqueue($"{request.Method} {request.RequestUri}\n{string.Join('\n', request.Headers.Select(static header => header.Key + ": " + string.Join(',', header.Value)))}\n{body}");
            if (request.Method == HttpMethod.Post && path.StartsWith("/api/v1/sign-in/link-approvals/", StringComparison.Ordinal))
            {
                if (path.EndsWith("/approve", StringComparison.Ordinal)) LinkApprovals++;
                if (path.EndsWith("/recovery-key", StringComparison.Ordinal)) RecoveryApprovals++;
            }

            var response = await base.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode || path != "/api/v1/sign-in/link-approvals") return response;
            var organization = Organization;
            SignInLinkApprovalDto Lie(SignInLinkApprovalDto approval, string? method, string? requester)
            {
                var lied = approval with { Method = method ?? approval.Method, RequesterDeviceId = requester ?? approval.RequesterDeviceId };
                return KeepServerDigest ? lied : lied with { Digest = FleetSignInApprovals.LinkDigest(organization, lied.RequesterDeviceId, lied.Method) };
            }

            if (request.Method == HttpMethod.Get && ListedMethod is { } listed)
            {
                var list = await response.Content.ReadFromJsonAsync(FleetSignInJsonContext.Default.ListResponseSignInLinkApprovalDto, cancellationToken);
                response.Content = JsonContent.Create(new ListResponse<SignInLinkApprovalDto>([.. list!.Items.Select(approval => Lie(approval, listed, null))]),
                    FleetSignInJsonContext.Default.ListResponseSignInLinkApprovalDto);
            }
            else if (request.Method == HttpMethod.Post && (AskedMethod is not null || AskedRequester is not null))
            {
                var approval = await response.Content.ReadFromJsonAsync(FleetSignInJsonContext.Default.SignInLinkApprovalDto, cancellationToken);
                response.Content = JsonContent.Create(Lie(approval!, AskedMethod, AskedRequester), FleetSignInJsonContext.Default.SignInLinkApprovalDto);
            }

            return response;
        }
    }

    // What the server logs, kept to be searched.
    private sealed class CapturingLoggerProvider(ConcurrentQueue<string> lines) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, lines);

        public void Dispose()
        {
        }

        private sealed class Logger(string category, ConcurrentQueue<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => lines.Enqueue($"{category}: {formatter(state, exception)} {exception}");
        }
    }
}
