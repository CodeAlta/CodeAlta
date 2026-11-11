using CodeAlta.Agent;
using CodeAlta.Agent.Copilot;
using CodeAlta.Catalog;

namespace CodeAlta.Hosting.Tests;

/// <summary>How the managed settings of the Copilot providers become the policy of every session.</summary>
[TestClass]
public sealed class CopilotManagedPolicySourceTests
{
    private const string Platform = "test-platform";

    [TestMethod]
    public async Task WithoutACopilotProvider_NothingIsDownloadedNorRead()
    {
        using var root = new TempRoot();
        var state = new ManagedPolicyState();
        var reads = 0;
        await using var source = Source(root, state, [new CodeAltaProviderDocument { ProviderKey = "openai", ProviderType = "openai-responses" }], installed: false,
            (_, _, _, _) => { reads++; return Task.FromResult<IReadOnlyList<CopilotManagedSettings>>([]); });

        var status = await source.RefreshAsync(CancellationToken.None);

        Assert.AreEqual("none", status.State);
        Assert.AreEqual(0, reads);
        Assert.IsFalse(Directory.Exists(Path.Combine(root.Path, "cache", "copilot-runtime")), "the runtime is not downloaded");
        Assert.AreSame(ManagedPolicy.None, state.Current);
    }

    [TestMethod]
    public async Task ASignedInProvider_GetsThePolicyOfItsAccount_KeptForTheNextStart()
    {
        using var root = new TempRoot();
        var variable = "CODEALTA_TEST_COPILOT_TOKEN_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, "gho_test");
        try
        {
            var state = new ManagedPolicyState();
            IReadOnlyList<string>? tokens = null;
            var providers = new[] { Copilot("copilot-work", variable) };
            await using (var source = Source(root, state, providers, installed: true, (_, _, given, _) =>
            {
                tokens = given;
                return Task.FromResult<IReadOnlyList<CopilotManagedSettings>>([new CopilotManagedSettings
                {
                    ServerManaged = true, BypassPermissionsDisabled = true, SandboxEnabled = true, SandboxBypassAllowed = false,
                    SupportUrl = "https://help.example", ManagedKeys = ["permissions", "sandbox"],
                }]);
            }))
            {
                var status = await source.RefreshAsync(CancellationToken.None);

                Assert.AreEqual("ready", status.State);
                CollectionAssert.AreEqual(new[] { "gho_test" }, tokens!.ToArray());
                Assert.IsTrue(state.Current.BypassPermissionsDisabled);
                Assert.IsTrue(state.Current.SandboxRequired);
                Assert.IsTrue(state.Current.RunOutsideSandboxDisallowed);
                Assert.AreEqual("https://help.example", state.Current.SupportUrl);
                Assert.AreEqual(CopilotManagedPolicySource.SourceName, state.Current.Source);
            }

            // The next start applies the kept policy before it reads anything, offline included.
            var next = new ManagedPolicyState();
            await using var again = Source(root, next, providers, installed: true, (_, _, _, _) => throw new InvalidOperationException("offline"));
            again.Start(TimeSpan.FromHours(1));
            Assert.IsTrue(next.Current.BypassPermissionsDisabled);
            Assert.IsTrue(next.Current.SandboxRequired);
            Assert.IsFalse(next.Current.FreshPolicyMissing);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [TestMethod]
    public async Task APolicyThatRequiresAFreshReading_BlocksSendsUntilOneSucceeds()
    {
        using var root = new TempRoot();
        var variable = "CODEALTA_TEST_COPILOT_TOKEN_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, "gho_test");
        try
        {
            var providers = new[] { Copilot("copilot", variable) };
            var settings = new CopilotManagedSettings { ServerManaged = true, BypassPermissionsDisabled = true, ForceRemoteSettingsRefresh = true, ManagedKeys = ["permissions"] };
            await using (var first = Source(root, new ManagedPolicyState(), providers, installed: true, (_, _, _, _) => Task.FromResult<IReadOnlyList<CopilotManagedSettings>>([settings])))
            {
                await first.RefreshAsync(CancellationToken.None);
            }

            var state = new ManagedPolicyState();
            var online = false;
            await using var source = Source(root, state, providers, installed: true, (_, _, _, _) => online
                ? Task.FromResult<IReadOnlyList<CopilotManagedSettings>>([settings])
                : throw new InvalidOperationException("GitHub cannot be reached"));
            source.Start(TimeSpan.FromHours(1));
            Assert.IsTrue(state.Current.FreshPolicyMissing, "kept from the last start, not read in this one");
            Assert.IsNotNull(state.Current.SendRefusal);

            var failed = await source.RefreshAsync(CancellationToken.None);
            Assert.AreEqual("failed", failed.State);
            StringAssert.Contains(failed.Message, "GitHub cannot be reached");
            Assert.IsTrue(state.Current.FreshPolicyMissing);

            online = true;
            await source.RefreshAsync(CancellationToken.None);
            Assert.IsFalse(state.Current.FreshPolicyMissing);
            Assert.IsTrue(state.Current.BypassPermissionsDisabled);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [TestMethod]
    public async Task WithoutASignIn_ThePolicyOfTheComputerApplies_AndARemovedProviderTakesItsPolicyAway()
    {
        using var root = new TempRoot();
        var state = new ManagedPolicyState();
        IReadOnlyList<string>? tokens = null;
        var providers = new List<CodeAltaProviderDocument> { Copilot("copilot", tokenVariable: null) };
        await using var source = Source(root, state, providers, installed: true, (_, _, given, _) =>
        {
            tokens = given;
            return Task.FromResult<IReadOnlyList<CopilotManagedSettings>>([new CopilotManagedSettings { DeviceManaged = true, SandboxEnabled = true, ManagedKeys = ["sandbox"] }]);
        });

        await source.RefreshAsync(CancellationToken.None);
        Assert.IsEmpty(tokens!, "the computer alone");
        Assert.IsTrue(state.Current.SandboxRequired);

        providers.Clear();
        var status = await source.RefreshAsync(CancellationToken.None);
        Assert.AreEqual("none", status.State);
        Assert.AreSame(ManagedPolicy.None, state.Current);
    }

    [TestMethod]
    public void Map_GivesCodeAltaModesForTheModesOfCopilot()
    {
        var at = DateTimeOffset.Parse("2026-11-11T10:00:00Z");
        Assert.AreEqual("default", CopilotManagedPolicySource.Map(new CopilotManagedSettings { DefaultMode = "manual" }, "copilot", at).DefaultPermissionMode);
        Assert.AreEqual("default", CopilotManagedPolicySource.Map(new CopilotManagedSettings { DefaultMode = "assisted" }, "copilot", at).DefaultPermissionMode);
        Assert.AreEqual("bypassPermissions", CopilotManagedPolicySource.Map(new CopilotManagedSettings { DefaultMode = "allow-all" }, "copilot", at).DefaultPermissionMode);
        Assert.IsNull(CopilotManagedPolicySource.Map(new CopilotManagedSettings { DefaultMode = "allow-all", BypassPermissionsDisabled = true }, "copilot", at).DefaultPermissionMode,
            "a default the policy itself refuses is not given");
        Assert.AreSame(ManagedPolicy.None, CopilotManagedPolicySource.Map(new CopilotManagedSettings { ServerManaged = true, ManagedKeys = ["model"] }, "copilot", at),
            "a policy of settings CodeAlta does not enforce narrows nothing");
    }

    private static CodeAltaProviderDocument Copilot(string key, string? tokenVariable)
        => new()
        {
            ProviderKey = key,
            ProviderType = "copilot",
            AuthSource = tokenVariable is null ? null : CopilotDirectAuthSources.GitHubTokenEnvironment,
            GitHubTokenEnv = tokenVariable,
        };

    private static CopilotManagedPolicySource Source(TempRoot root, ManagedPolicyState state, IReadOnlyList<CodeAltaProviderDocument> providers, bool installed,
        Func<string, string, IReadOnlyList<string>, CancellationToken, Task<IReadOnlyList<CopilotManagedSettings>>> read)
    {
        var runtimeRoot = Path.Combine(root.Path, "cache", "copilot-runtime");
        if (installed)
        {
            // A runtime already in the cache: nothing is downloaded.
            var folder = CopilotPinnedRuntime.GetInstallDirectory(runtimeRoot, Platform);
            Directory.CreateDirectory(folder);
            foreach (var name in new[] { CopilotPinnedRuntime.MarkerFileName, CopilotPinnedRuntime.ExecutableName, "runtime.node" }) File.WriteAllText(Path.Combine(folder, name), "x");
        }

        var installer = new CopilotRuntimeInstaller(runtimeRoot, new Refusing(), new CopilotRuntimePackage(Platform, 1, new string('0', 64)));
        return new CopilotManagedPolicySource(state, root.Path, () => providers, installer, read, TimeProvider.System);
    }

    private sealed class TempRoot : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("CodeAlta-copilot-policy-").FullName;

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // A file still held: the temporary folder is cleaned up later.
            }
        }
    }

    private sealed class Refusing : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("The runtime is not downloaded in these tests.");
    }
}
