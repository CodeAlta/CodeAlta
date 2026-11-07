using CodeAlta.Fleet.Client;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Fleet.Protocol;

namespace CodeAlta.Node.Tests;

/// <summary>
/// The devices that manage a node (<see cref="DevicePermissions.Manage"/>): the first one paired, or one a pairing link
/// for a manager adds, calls the owner's <c>control.*</c> methods from elsewhere, and only it.
/// </summary>
[TestClass]
public sealed class FleetNodeManagementTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task OnlyALinkMadeToManageGivesManage_TheFirstDeviceIncluded_AndADeviceThatManagedKeepsIt()
    {
        await using var fixture = await ManagedNodeFixture.StartAsync(TestContext.CancellationToken);
        var token = TestContext.CancellationToken;

        // The first device that pairs is not trusted with the node's devices, secrets and updates unless its owner asked.
        await fixture.PairAsync(fixture.Owner, "laptop", manages: false, token);
        await fixture.PairAsync(fixture.Other, "phone", manages: false, token);
        await fixture.PairAsync(fixture.Third, "tablet", manages: true, token);

        var trusted = fixture.Fleet.Service.TrustedDevices;
        Assert.IsFalse(trusted.FindTrusted(fixture.Owner.Fingerprint)!.Permissions.HasFlag(DevicePermissions.Manage), "The first device does not manage the node unasked.");
        Assert.IsFalse(trusted.FindTrusted(fixture.Other.Fingerprint)!.Permissions.HasFlag(DevicePermissions.Manage), "A later device does not manage it unasked.");
        Assert.IsTrue(trusted.FindTrusted(fixture.Third.Fingerprint)!.Permissions.HasFlag(DevicePermissions.Manage), "A link made with --manage gives it.");

        // Paired again, a device that manages keeps managing it; one that did not, still does not.
        await fixture.PairAsync(fixture.Third, "tablet", manages: false, token);
        Assert.IsTrue(trusted.FindTrusted(fixture.Third.Fingerprint)!.Permissions.HasFlag(DevicePermissions.Manage));
        await fixture.PairAsync(fixture.Owner, "laptop", manages: false, token);
        Assert.IsFalse(trusted.FindTrusted(fixture.Owner.Fingerprint)!.Permissions.HasFlag(DevicePermissions.Manage));
    }

    [TestMethod]
    public async Task Manager_ChangesDevicesAndProjects_ReadsLogAndUpdates_WhileOthersAreForbidden()
    {
        await using var fixture = await ManagedNodeFixture.StartAsync(TestContext.CancellationToken);
        var token = TestContext.CancellationToken;
        var ownerMachine = await fixture.PairAsync(fixture.Owner, "laptop", manages: true, token);
        var otherMachine = await fixture.PairAsync(fixture.Other, "phone", manages: false, token);

        await using var manager = await FleetNodeClient.ConnectAsync(ownerMachine, fixture.Owner, token);
        await using var other = await FleetNodeClient.ConnectAsync(otherMachine, fixture.Other, token);

        var devices = await manager.ListDevicesAsync(token);
        CollectionAssert.AreEquivalent(new[] { "laptop", "phone" }, devices.Devices.Select(static device => device.Name).ToArray());
        var status = await manager.GetStatusAsync(token);
        Assert.AreEqual("managed-node", status.Name);

        // What each device may do changes from the manager's desktop, as altanode permissions does on the node.
        var changed = await manager.SetDevicePermissionsAsync(new FleetDevicePermissionsParams(fixture.Other.DeviceId, ["move", "automate"], []), token);
        Assert.IsTrue(changed.Changed);
        CollectionAssert.IsSubsetOf(new[] { "move", "automate" }, changed.Device.Permissions.ToArray());

        // A device that does not manage the node may not, however its channel was opened.
        var forbidden = await Assert.ThrowsExactlyAsync<FleetException>(() => other.ListDevicesAsync(token));
        Assert.AreEqual(FleetErrorCodes.Forbidden, forbidden.Error.Code);
        var grant = await Assert.ThrowsExactlyAsync<FleetException>(() =>
            other.SetDevicePermissionsAsync(new FleetDevicePermissionsParams(fixture.Other.DeviceId, ["manage"], []), token));
        Assert.AreEqual(FleetErrorCodes.Forbidden, grant.Error.Code, "A device never gives itself the manage permission.");
        Assert.AreEqual(FleetErrorCodes.Forbidden, (await Assert.ThrowsExactlyAsync<FleetException>(() => other.ReadLogAsync(10, token))).Error.Code);

        // Pairing and stopping stay the owner's on the machine.
        Assert.AreEqual(FleetErrorCodes.MethodNotFound, (await Assert.ThrowsExactlyAsync<FleetException>(() => manager.CreatePairingOfferAsync(null, token))).Error.Code);
        Assert.AreEqual(FleetErrorCodes.MethodNotFound, (await Assert.ThrowsExactlyAsync<FleetException>(() => manager.StopNodeAsync(token))).Error.Code);

        // Projects.
        var folder = Directory.CreateDirectory(Path.Combine(fixture.Root.Root, "work", "app")).FullName;
        var added = await manager.AddProjectAsync(new FleetProjectAddParams(folder), token);
        Assert.IsTrue(added.Changed);
        Assert.IsTrue((await manager.ListProjectsAsync(token)).Projects.Any(project => project.Id == added.Project.Id));
        var removed = await manager.RemoveProjectAsync(added.Project.Id, token);
        Assert.AreEqual(added.Project.Id, removed.Project.Id);
        Assert.IsFalse((await manager.ListProjectsAsync(token)).Projects.Any(project => project.Id == added.Project.Id && !project.Archived));

        // The log and the updates go to the management of the node, with who asked.
        var log = await manager.ReadLogAsync(12, token);
        Assert.AreEqual("12 lines", log.Text);
        var invalid = await Assert.ThrowsExactlyAsync<FleetException>(() => manager.ReadLogAsync(FleetNodeLogParams.MaxLines + 1, token));
        Assert.AreEqual(FleetErrorCodes.InvalidParams, invalid.Error.Code);
        var update = await manager.UpdateNodeAsync(new FleetNodeUpdateParams(Check: false, Now: true), token);
        Assert.AreEqual(FleetNodeUpdateResult.Staged, update.Status);
        Assert.AreEqual("manage:" + fixture.Owner.DeviceId, fixture.Management.LastRequestedBy, "A switch records the device that asked for it.");

        // Revoking a device closes its channel at once.
        var revoked = await manager.RevokeDeviceAsync(fixture.Other.DeviceId, token);
        Assert.IsTrue(revoked.Changed);
        Assert.IsNotNull(revoked.Device.RevokedAt);
        var closed = await other.Completion.WaitAsync(TimeSpan.FromSeconds(30), token);
        Assert.IsFalse(closed.ClosedLocally);
    }

    [TestMethod]
    public async Task Manager_SetsRotatesAndDeletesTheSecretsOfTheMachine_WhileOthersAreForbidden()
    {
        await using var fixture = await ManagedNodeFixture.StartAsync(TestContext.CancellationToken);
        var token = TestContext.CancellationToken;
        var store = new CodeAlta.Catalog.Secrets.LocalSecretStore(fixture.Node.Host.CatalogOptions.GlobalRoot);
        try
        {
            var ownerMachine = await fixture.PairAsync(fixture.Owner, "laptop", manages: true, token);
            var otherMachine = await fixture.PairAsync(fixture.Other, "phone", manages: false, token);
            await using var manager = await FleetNodeClient.ConnectAsync(ownerMachine, fixture.Owner, token);
            await using var other = await FleetNodeClient.ConnectAsync(otherMachine, fixture.Other, token);

            // The device that manages the machine sets a secret, rotates it and sets a variable; the value never comes back.
            var set = await manager.SetSecretAsync(new FleetSecretSetParams("GRAFANA_TOKEN", "glsa_from_laptop_1"), cancellationToken: token);
            Assert.AreEqual((1, (string?)null, "laptop (manage)"), (set.Secret!.Version, set.Secret.Value, set.Secret.SetBy), "who set it is recorded by the name of its device");
            Assert.AreEqual(2, (await manager.SetSecretAsync(new FleetSecretSetParams("GRAFANA_TOKEN", "glsa_from_laptop_2"), rotate: true, token)).Secret!.Version);
            await manager.SetSecretAsync(new FleetSecretSetParams("GRAFANA_URL", "https://grafana.example", Variable: true), cancellationToken: token);
            var list = await manager.ListSecretsAsync(token);
            Assert.AreEqual(("GRAFANA_TOKEN", (string?)null), (list.Secrets.Single().Name, list.Secrets.Single().Value));
            Assert.AreEqual("https://grafana.example", list.Variables.Single().Value);
            Assert.IsTrue(store.TryGetSecret("GRAFANA_TOKEN", out var value));
            Assert.AreEqual("glsa_from_laptop_2", value, "the machine holds the new value");

            // A device that does not manage the machine reads, sets and deletes nothing there.
            foreach (var call in new Func<Task>[]
            {
                () => other.ListSecretsAsync(token),
                () => other.SetSecretAsync(new FleetSecretSetParams("GRAFANA_TOKEN", "stolen"), cancellationToken: token),
                () => other.SetSecretAsync(new FleetSecretSetParams("GRAFANA_TOKEN", "stolen"), rotate: true, token),
                () => other.DeleteSecretAsync(new FleetSecretRef("GRAFANA_TOKEN"), token),
            })
            {
                Assert.AreEqual(FleetErrorCodes.Forbidden, (await Assert.ThrowsExactlyAsync<FleetException>(call)).Error.Code);
            }

            Assert.IsTrue(store.TryGetSecret("GRAFANA_TOKEN", out value));
            Assert.AreEqual("glsa_from_laptop_2", value, "the other device changed nothing");

            await manager.DeleteSecretAsync(new FleetSecretRef("GRAFANA_TOKEN"), token);
            await manager.DeleteSecretAsync(new FleetSecretRef("GRAFANA_URL", Variable: true), token);
            Assert.AreEqual(FleetNodeErrorCodes.SecretNotFound,
                (await Assert.ThrowsExactlyAsync<FleetException>(() => manager.DeleteSecretAsync(new FleetSecretRef("GRAFANA_TOKEN"), token))).Error.Code);
            var empty = await manager.ListSecretsAsync(token);
            Assert.AreEqual((0, 0), (empty.Secrets.Count, empty.Variables.Count));
        }
        finally
        {
            store.Delete();
        }
    }

    [TestMethod]
    public async Task Manager_GivesAProviderItsKey_WhichTheMachineKeepsInItsSecrets_AndSeesWhyAProviderIsNotReady()
    {
        await using var fixture = await ManagedNodeFixture.StartAsync(TestContext.CancellationToken);
        var token = TestContext.CancellationToken;
        var store = new CodeAlta.Catalog.Secrets.LocalSecretStore(fixture.Node.Host.CatalogOptions.GlobalRoot);
        try
        {
            var ownerMachine = await fixture.PairAsync(fixture.Owner, "laptop", manages: true, token);
            var otherMachine = await fixture.PairAsync(fixture.Other, "phone", manages: false, token);
            await using var manager = await FleetNodeClient.ConnectAsync(ownerMachine, fixture.Owner, token);
            await using var other = await FleetNodeClient.ConnectAsync(otherMachine, fixture.Other, token);

            var listed = await manager.ListProvidersAsync(token);
            CollectionAssert.IsSubsetOf(new[] { "openai-responses", "anthropic", "copilot" }, listed.Types.Select(static type => type.Type).ToArray());
            Assert.IsTrue(listed.Types.Single(static type => type.Type == "copilot").SignIn, "Copilot signs in with a device code the desktop shows");

            // A provider without its key: the machine says why it is not ready (the secret its key will be), instead of only in its log.
            var bare = await manager.SetProviderAsync(new FleetProviderSetParams("anthropic", "anthropic", null, null, null, null), token);
            Assert.AreEqual((FleetProviderStates.NotReady, FleetProviderReasons.SecretMissing, "PROVIDER_ANTHROPIC_API_KEY", "key"), (bare.State, bare.Reason, bare.Detail, bare.Credential));

            // Its key, typed on the desktop, goes to the secrets of the machine; the configuration names the secret, never the key.
            // (Its endpoint is a closed port of this computer: its preparation fails at once, without the network.)
            var keyed = await manager.SetProviderAsync(new FleetProviderSetParams("anthropic", null, "Claude API", null, "http://127.0.0.1:9/v1", "sk-ant-from-laptop-123",
                MakeDefault: true), token);
            Assert.AreEqual(("PROVIDER_ANTHROPIC_API_KEY", "Claude API", true), (keyed.KeySecret, keyed.Name, keyed.IsDefault));
            Assert.AreNotEqual(FleetProviderReasons.KeyMissing, keyed.Reason, "it has its key now");
            Assert.IsTrue(store.TryGetSecret("PROVIDER_ANTHROPIC_API_KEY", out var value));
            Assert.AreEqual("sk-ant-from-laptop-123", value);
            Assert.AreEqual("laptop (manage)", store.ListSecrets().Single(static secret => secret.Name == "PROVIDER_ANTHROPIC_API_KEY").SetBy);
            var config = await File.ReadAllTextAsync(Path.Combine(fixture.Node.Host.CatalogOptions.GlobalRoot, "config.toml"), token);
            StringAssert.Contains(config, "PROVIDER_ANTHROPIC_API_KEY");
            Assert.IsFalse(config.Contains("sk-ant-from-laptop-123", StringComparison.Ordinal), "never the key in clear in the configuration");
            Assert.IsTrue(fixture.Node.Host.ModelProviderRegistry.ListProviders(includeDisabled: true).Any(static provider => provider.ProviderId.Value == "anthropic"),
                "the providers start again: no restart of the node");

            // The secret is gone: the machine names the one it waits for.
            store.DeleteSecret("PROVIDER_ANTHROPIC_API_KEY");
            await manager.SetProviderAsync(new FleetProviderSetParams("anthropic", null, null, null, null, null), token);
            var missing = (await manager.ListProvidersAsync(token)).Providers.Single(static provider => provider.Key == "anthropic");
            Assert.AreEqual((FleetProviderReasons.SecretMissing, "PROVIDER_ANTHROPIC_API_KEY"), (missing.Reason, missing.Detail));

            // A provider that takes a key has no sign-in; an unknown type and a bad key are refused.
            Assert.AreEqual(FleetNodeErrorCodes.ProviderSignInUnsupported,
                (await Assert.ThrowsExactlyAsync<FleetException>(() => manager.StartProviderSignInAsync("anthropic", token))).Error.Code);
            Assert.AreEqual(FleetNodeErrorCodes.ProviderNotFound, (await Assert.ThrowsExactlyAsync<FleetException>(() => manager.StartProviderSignInAsync("nothing", token))).Error.Code);
            Assert.AreEqual(FleetErrorCodes.InvalidParams,
                (await Assert.ThrowsExactlyAsync<FleetException>(() => manager.SetProviderAsync(new FleetProviderSetParams("odd", "made-up", null, null, null, null), token))).Error.Code);
            Assert.AreEqual(FleetErrorCodes.InvalidParams,
                (await Assert.ThrowsExactlyAsync<FleetException>(() => manager.SetProviderAsync(new FleetProviderSetParams("no spaces", "anthropic", null, null, null, null), token))).Error.Code);
            Assert.AreEqual("none", (await manager.WaitProviderSignInAsync("anthropic", 0, token)).Status);

            // A device that does not manage the machine reads and changes none of its providers.
            foreach (var call in new Func<Task>[]
            {
                () => other.ListProvidersAsync(token),
                () => other.SetProviderAsync(new FleetProviderSetParams("anthropic", null, null, null, null, "stolen"), token),
                () => other.StartProviderSignInAsync("anthropic", token),
            })
            {
                Assert.AreEqual(FleetErrorCodes.Forbidden, (await Assert.ThrowsExactlyAsync<FleetException>(call)).Error.Code);
            }
        }
        finally
        {
            store.Delete();
        }
    }

    // The sign-ins a node keeps for itself, made from the desktop (doc/fleet/node.md, credentials on a node): the node starts a
    // browser sign-in and names the loopback address of the desktop its browser comes back to; the code verifier stays here.
    [TestMethod]
    public async Task Manager_StartsABrowserSignInOnTheMachine_WhoseRedirectIsALoopbackOfTheDesktop()
    {
        await using var fixture = await ManagedNodeFixture.StartAsync(TestContext.CancellationToken);
        var token = TestContext.CancellationToken;
        var store = new CodeAlta.Catalog.CodeAltaConfigStore(fixture.Node.Host.CatalogOptions);
        store.SaveGlobalProviderDefinitions([new CodeAlta.Catalog.CodeAltaProviderDocument { ProviderKey = "codex", ProviderType = "codex", Enabled = false }]);
        var ownerMachine = await fixture.PairAsync(fixture.Owner, "laptop", manages: true, token);
        var otherMachine = await fixture.PairAsync(fixture.Other, "phone", manages: false, token);
        await using var manager = await FleetNodeClient.ConnectAsync(ownerMachine, fixture.Owner, token);
        await using var other = await FleetNodeClient.ConnectAsync(otherMachine, fixture.Other, token);

        var codex = (await manager.ListSignInsAsync(token)).Items.Single(static item => item.Kind == FleetMachineSignInKinds.Provider && item.Key == "codex");
        Assert.AreEqual(FleetMachineSignInStates.SignedOut, codex.State);
        CollectionAssert.AreEqual(new[] { FleetMachineSignInWays.Browser }, codex.Ways.ToArray(), "Codex signs in with a browser, which can be the desktop's");

        var prompt = await manager.StartSignInAsync(new FleetMachineSignInStartParams(FleetMachineSignInKinds.Provider, "codex"), token);
        Assert.AreEqual(FleetMachineSignInWays.Browser, prompt.Way);
        var redirect = new Uri(prompt.Redirect!);
        Assert.AreEqual(("127.0.0.1", "/auth/callback"), (redirect.Host, redirect.AbsolutePath), "the desktop listens there");
        var page = new Uri(prompt.Page!);
        Assert.AreEqual(Uri.UriSchemeHttps, page.Scheme);
        StringAssert.Contains(Uri.UnescapeDataString(page.Query), "redirect_uri=" + redirect.AbsoluteUri);
        StringAssert.Contains(page.Query, "code_challenge=", "the verifier stays on the machine: only its challenge goes");
        Assert.AreEqual(prompt.AttemptId, (await manager.StartSignInAsync(new FleetMachineSignInStartParams(FleetMachineSignInKinds.Provider, "codex"), token)).AttemptId,
            "a start while one waits gives it again");

        // A callback that is not the one it waits for is refused; a cancelled sign-in ends.
        Assert.AreEqual(FleetNodeErrorCodes.SignInFailed, (await Assert.ThrowsExactlyAsync<FleetException>(() => manager.CompleteSignInAsync(
            new FleetMachineSignInCompleteParams(prompt.AttemptId, $"http://127.0.0.1:{redirect.Port}/elsewhere?code=x&state=y"), token))).Error.Code);
        Assert.AreEqual(FleetMachineSignInStates.Waiting, (await manager.WaitSignInAsync(prompt.AttemptId, 0, token)).Status);
        var cancelled = await manager.CancelSignInAsync(prompt.AttemptId, token);
        Assert.AreEqual((FleetMachineSignInStates.Failed, "canceled"), (cancelled.Status, cancelled.Code));

        // What has no sign-in from here, or is not there, says so; a device that does not manage the machine signs nothing in.
        Assert.AreEqual(FleetNodeErrorCodes.SignInTargetNotFound, (await Assert.ThrowsExactlyAsync<FleetException>(() => manager.StartSignInAsync(
            new FleetMachineSignInStartParams(FleetMachineSignInKinds.Provider, "nothing"), token))).Error.Code);
        Assert.AreEqual(FleetNodeErrorCodes.SignInUnsupported, (await Assert.ThrowsExactlyAsync<FleetException>(() => manager.StartSignInAsync(
            new FleetMachineSignInStartParams(FleetMachineSignInKinds.Provider, "codex", Way: FleetMachineSignInWays.Device), token))).Error.Code);
        Assert.AreEqual(FleetErrorCodes.Forbidden, (await Assert.ThrowsExactlyAsync<FleetException>(() => other.StartSignInAsync(
            new FleetMachineSignInStartParams(FleetMachineSignInKinds.Provider, "codex"), token))).Error.Code);
    }

    [TestMethod]
    public async Task AProviderThatFails_NeverGivesItsKeyBackInWhyItFailed()
    {
        await using var fixture = await ManagedNodeFixture.StartAsync(TestContext.CancellationToken);
        var token = TestContext.CancellationToken;
        var store = new CodeAlta.Catalog.Secrets.LocalSecretStore(fixture.Node.Host.CatalogOptions.GlobalRoot);
        // A gateway that refuses the key and says it back, as some do.
        using var gateway = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        gateway.Start();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var answering = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                using var client = await gateway.AcceptTcpClientAsync(stop.Token);
                using var stream = client.GetStream();
                var buffer = new byte[16 * 1024];
                var read = await stream.ReadAsync(buffer, stop.Token);
                var request = System.Text.Encoding.UTF8.GetString(buffer, 0, read);
                var key = request.Split('\n').Select(static line => line.Trim()).FirstOrDefault(static line => line.StartsWith("x-api-key:", StringComparison.OrdinalIgnoreCase)
                    || line.StartsWith("authorization:", StringComparison.OrdinalIgnoreCase))?.Split(':', 2)[1].Trim() ?? "";
                var body = $"{{\"type\":\"error\",\"error\":{{\"type\":\"authentication_error\",\"message\":\"invalid x-api-key: {key}\"}}}}";
                await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes($"HTTP/1.1 401 Unauthorized\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}"),
                    stop.Token);
            }
        }, stop.Token);
        try
        {
            var ownerMachine = await fixture.PairAsync(fixture.Owner, "laptop", manages: true, token);
            await using var manager = await FleetNodeClient.ConnectAsync(ownerMachine, fixture.Owner, token);
            await manager.SetProviderAsync(new FleetProviderSetParams("anthropic", "anthropic", "Claude API", null,
                $"http://127.0.0.1:{((System.Net.IPEndPoint)gateway.LocalEndpoint).Port}", "sk-ant-leaked-key-123456"), token);
            FleetProviderStatus? failed = null;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
            while (DateTime.UtcNow < deadline)
            {
                failed = (await manager.ListProvidersAsync(token)).Providers.Single(static provider => provider.Key == "anthropic");
                if (failed.State == FleetProviderStates.Failed) break;
                await Task.Delay(200, token);
            }

            Assert.AreEqual(FleetProviderStates.Failed, failed?.State, failed?.Detail);
            Assert.IsFalse(failed!.Detail?.Contains("sk-ant-leaked-key-123456", StringComparison.Ordinal) == true, $"the key never comes back: {failed.Detail}");
        }
        finally
        {
            await stop.CancelAsync();
            try { await answering; }
            catch (Exception exception) when (exception is OperationCanceledException or System.Net.Sockets.SocketException or IOException) { }
            store.Delete();
        }
    }

    [TestMethod]
    public async Task TwoProvidersWhoseIdsReadAlike_KeepTheirOwnKeys()
    {
        await using var fixture = await ManagedNodeFixture.StartAsync(TestContext.CancellationToken);
        var token = TestContext.CancellationToken;
        var store = new CodeAlta.Catalog.Secrets.LocalSecretStore(fixture.Node.Host.CatalogOptions.GlobalRoot);
        try
        {
            var ownerMachine = await fixture.PairAsync(fixture.Owner, "laptop", manages: true, token);
            await using var manager = await FleetNodeClient.ConnectAsync(ownerMachine, fixture.Owner, token);
            var first = await manager.SetProviderAsync(new FleetProviderSetParams("my-gateway", "anthropic", null, null, "http://127.0.0.1:9/v1", "sk-first-account-1111"), token);
            var second = await manager.SetProviderAsync(new FleetProviderSetParams("my_gateway", "anthropic", null, null, "http://127.0.0.1:9/v1", "sk-second-account-2222"), token);
            Assert.AreNotEqual(first.KeySecret, second.KeySecret, "each provider has a secret of its own");
            Assert.IsTrue(store.TryGetSecret(first.KeySecret!, out var firstKey));
            Assert.AreEqual("sk-first-account-1111", firstKey, "setting the key of one never changes the key of the other");
            Assert.IsTrue(store.TryGetSecret(second.KeySecret!, out var secondKey));
            Assert.AreEqual("sk-second-account-2222", secondKey);

            // Its key given again, a provider keeps its secret.
            var again = await manager.SetProviderAsync(new FleetProviderSetParams("my_gateway", null, null, null, null, "sk-second-rotated-3333"), token);
            Assert.AreEqual(second.KeySecret, again.KeySecret);
            Assert.IsTrue(store.TryGetSecret(first.KeySecret!, out firstKey));
            Assert.AreEqual("sk-first-account-1111", firstKey);
        }
        finally
        {
            store.Delete();
        }
    }

    [TestMethod]
    public void NodeProviders_NameTheSecretOfAKey_AndTellHowEachTypeGetsItsCredential()
    {
        Assert.AreEqual("PROVIDER_OPENAI_API_KEY", NodeProviders.SecretNameOf("openai"));
        Assert.AreEqual("PROVIDER_MY_GATEWAY_2_API_KEY", NodeProviders.SecretNameOf("my-gateway-2"));
        Assert.AreEqual(("key", "sign_in", "sign_in", "cli"), (NodeProviders.Credential("anthropic"), NodeProviders.Credential("copilot"), NodeProviders.Credential("codex"),
            NodeProviders.Credential("claude-code")));
        Assert.IsTrue(NodeProviders.SignsInWithCode("copilot"));
        Assert.IsFalse(NodeProviders.SignsInWithCode("codex"), "Codex signs in with a browser on the machine");
    }

    [TestMethod]
    public async Task ManagerWhoLosesManage_IsForbiddenAtTheNextCall()
    {
        await using var fixture = await ManagedNodeFixture.StartAsync(TestContext.CancellationToken);
        var token = TestContext.CancellationToken;
        var machine = await fixture.PairAsync(fixture.Owner, "laptop", manages: true, token);
        await using (var manager = await FleetNodeClient.ConnectAsync(machine, fixture.Owner, token))
        {
            await manager.SetDevicePermissionsAsync(new FleetDevicePermissionsParams(fixture.Owner.DeviceId, [], ["manage"]), token);
        }

        // The channel of a device that lost a permission closes; the next one is checked against what it may do now.
        await using var again = await FleetNodeClient.ConnectAsync(machine, fixture.Owner, token);
        var forbidden = await Assert.ThrowsExactlyAsync<FleetException>(() => again.ListDevicesAsync(token));
        Assert.AreEqual(FleetErrorCodes.Forbidden, forbidden.Error.Code);
        Assert.IsTrue((await again.GetInfoAsync(token)).Permissions.Contains("view"), "It still views the sessions.");
    }

    [TestMethod]
    public async Task Manager_HasTheNodeJoinAServer_WhichItThenApprovesByTheDeviceTheNodeNamed()
    {
        await using var fixture = await ManagedNodeFixture.StartAsync(TestContext.CancellationToken);
        var token = TestContext.CancellationToken;
        await using var server = await CodeAlta.Server.Hosting.CoordinatorServer.StartAsync(new CodeAlta.Server.Hosting.CoordinatorServerOptions
        {
            DataRoot = Path.Combine(fixture.Root.Root, "server"), Urls = ["http://127.0.0.1:0"], ConfigureLogging = static _ => { },
        }, token);
        var address = server.Addresses.First(static uri => uri.Scheme == "http");
        using var desktop = new CodeAlta.Fleet.Server.FleetServerClient(address, fixture.Owner);
        await desktop.SetUpAsync(server.SetupCode!.Code, "Alex", "Desktop", "Home", token);
        var enrollment = await desktop.CreateEnrollmentTokenAsync(null, "managed-node", token);
        var ownerMachine = await fixture.PairAsync(fixture.Owner, "laptop", manages: true, token);
        var otherMachine = await fixture.PairAsync(fixture.Other, "phone", manages: false, token);
        await using var manager = await FleetNodeClient.ConnectAsync(ownerMachine, fixture.Owner, token);
        await using var other = await FleetNodeClient.ConnectAsync(otherMachine, fixture.Other, token);

        var refused = await Assert.ThrowsExactlyAsync<FleetException>(() => other.JoinServerAsync(new FleetServerJoinParams(address.ToString(), enrollment.Token), token));
        Assert.AreEqual(FleetErrorCodes.Forbidden, refused.Error.Code, "only a device that manages the node has it join a server");

        var joined = await manager.JoinServerAsync(new FleetServerJoinParams(address.ToString(), enrollment.Token, "build-box"), token);
        Assert.AreEqual(fixture.Fleet.DeviceId, joined.DeviceId, "the node enrolls with its own key: the device to approve is the node");
        Assert.AreEqual("build-box", joined.Name);
        Assert.IsFalse(joined.AlreadyLinked);
        var link = new CodeAlta.Fleet.Server.FleetServerLinkStore(fixture.Node.Host.CatalogOptions.FleetRoot).List().Single();
        Assert.AreEqual(CodeAlta.Fleet.Server.FleetServerLinkStates.Pending, link.State);
        var pending = (await desktop.ListDevicesAsync(token)).Single(static device => device.Status == "pending");
        Assert.AreEqual(joined.DeviceId, pending.Id);
        await desktop.ApproveAsync(pending.Id, FleetDeviceKinds.Node, token);
        Assert.AreEqual("approved", (await desktop.ListDevicesAsync(token)).Single(device => device.Id == joined.DeviceId).Status);

        Assert.IsTrue((await manager.JoinServerAsync(new FleetServerJoinParams(address.ToString(), "used-token"), token)).AlreadyLinked,
            "asked again, the node is linked already and enrolls nothing");
        var bad = await Assert.ThrowsExactlyAsync<FleetException>(() => manager.JoinServerAsync(new FleetServerJoinParams("ftp://nowhere", "x"), token));
        Assert.AreEqual(FleetErrorCodes.InvalidParams, bad.Error.Code);
    }

    [TestMethod]
    public async Task NodeManagement_ReadsTheEndOfTheLog_AndAnEmptyOneBeforeTheFirstRun()
    {
        using var root = new NodeTestRoot();
        var profile = root.Profile();
        var management = new NodeManagement(profile, "1.0.0", static () => null);
        var none = await management.ReadLogAsync(5, TestContext.CancellationToken);
        Assert.AreEqual("", none.Text);
        Assert.AreEqual(NodeLogging.GetLogFilePath(profile.StateRoot), none.Path);

        Directory.CreateDirectory(Path.GetDirectoryName(none.Path)!);
        await File.WriteAllTextAsync(none.Path, "one\ntwo\nthree\n", TestContext.CancellationToken);
        var tail = await management.ReadLogAsync(2, TestContext.CancellationToken);
        Assert.AreEqual("two\nthree\n", tail.Text);
        Assert.IsFalse(tail.Truncated);

        var update = await management.UpdateAsync(new FleetNodeUpdateParams(), "local", TestContext.CancellationToken);
        Assert.AreEqual(FleetNodeUpdateResult.Unavailable, update.Status, "A node whose self-update does not run says so.");
    }
}

/// <summary>What a managed node answers for its log and its updates, in a test.</summary>
internal sealed class RecordingManagement : IFleetNodeManagement
{
    public string? LastRequestedBy { get; private set; }

    public Task<FleetNodeLog> ReadLogAsync(int lines, CancellationToken cancellationToken)
        => Task.FromResult(new FleetNodeLog($"{lines} lines", "node.log", false));

    public Task<FleetNodeUpdateResult> UpdateAsync(FleetNodeUpdateParams parameters, string requestedBy, CancellationToken cancellationToken)
    {
        LastRequestedBy = requestedBy;
        return Task.FromResult(parameters.Check
            ? new FleetNodeUpdateResult(FleetNodeUpdateResult.Available, "1.0.0", "1.1.0", "1.1.0")
            : new FleetNodeUpdateResult(FleetNodeUpdateResult.Staged, "1.0.0", "1.1.0", "1.1.0"));
    }
}

/// <summary>A node whose management records what it is asked, and three devices of other profiles.</summary>
internal sealed class ManagedNodeFixture : IAsyncDisposable
{
    private ManagedNodeFixture(NodeTestRoot root, NodeHost node, NodeFleet fleet, RecordingManagement management)
    {
        Root = root;
        Node = node;
        Fleet = fleet;
        Management = management;
        Owner = DeviceIdentity.LoadOrCreate(Path.Combine(root.Root, "laptop", "fleet"));
        Other = DeviceIdentity.LoadOrCreate(Path.Combine(root.Root, "phone", "fleet"));
        Third = DeviceIdentity.LoadOrCreate(Path.Combine(root.Root, "tablet", "fleet"));
    }

    public NodeTestRoot Root { get; }

    public NodeHost Node { get; }

    public NodeFleet Fleet { get; }

    public RecordingManagement Management { get; }

    public DeviceIdentity Owner { get; }

    public DeviceIdentity Other { get; }

    public DeviceIdentity Third { get; }

    public static async Task<ManagedNodeFixture> StartAsync(CancellationToken cancellationToken)
    {
        var root = new NodeTestRoot();
        try
        {
            var node = await NodeHost.StartAsync(root.Options(), cancellationToken);
            var management = new RecordingManagement();
            var fleet = NodeFleet.Start(node, new NodeListenOptions { Port = 0 }, "managed-node", "1.0.0", null, null, null, management);
            return new ManagedNodeFixture(root, node, fleet, management);
        }
        catch
        {
            root.Dispose();
            throw;
        }
    }

    public async Task<PairedMachine> PairAsync(DeviceIdentity device, string name, bool manages, CancellationToken cancellationToken)
    {
        var (offer, _) = Fleet.Service.Pairing.CreateOffer("managed-node", Fleet.Addresses, TimeSpan.FromMinutes(1), manages);
        return await FleetNodeClient.PairAsync(offer, device, name, new PairedMachineStore(Path.Combine(Root.Root, name, "fleet")), cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await Fleet.DisposeAsync();
        await Node.DisposeAsync();
        Owner.Dispose();
        Other.Dispose();
        Third.Dispose();
        Root.Dispose();
    }
}
