using CodeAlta.Catalog.Secrets;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Fleet.Protocol;

namespace CodeAlta.Node.Tests;

public sealed partial class NodeClientCommandTests
{
    [TestMethod]
    public async Task Secrets_AreManagedByTheOwnersLocalControl_NeverShownBack()
    {
        using var root = new NodeTestRoot();
        var token = TestContext.CancellationToken;
        var started = new TaskCompletionSource<Action>(TaskCreationOptions.RunContinuationsAsynchronously);
        var node = new NodeApplication(new CapturingWriter(), new CapturingWriter())
        {
            InitializeLogging = false,
            ConfigureHost = root.Isolate,
            Started = (_, stop) => started.TrySetResult(stop),
        };
        var running = node.RunAsync(["run", "--data-root", root.DataRoot, "--port", "0", "--name", "test-node"]);
        var stop = await started.Task.WithinAsync(Patience, token);
        try
        {
            var options = new NodeProfileOptions { DataRoot = root.DataRoot };
            var control = NodeLocalControl.Read(options.Resolve(out _)!);
            var (client, identity) = await control.ConnectAsync(token);
            using (identity)
            await using (client)
            {
                var set = await client.SetSecretAsync(new FleetSecretSetParams("GRAFANA_TOKEN", "glsa_owner_value_42"), cancellationToken: token);
                Assert.AreEqual((1, (string?)null), (set.Secret!.Version, set.Secret.Value), "A secret is never sent back.");
                Assert.AreEqual(2, (await client.SetSecretAsync(new FleetSecretSetParams("GRAFANA_TOKEN", "glsa_rotated_43"), rotate: true, token)).Secret!.Version);
                await client.SetSecretAsync(new FleetSecretSetParams("GRAFANA_URL", "https://grafana.example", Variable: true), cancellationToken: token);

                var list = await client.ListSecretsAsync(token);
                Assert.AreEqual("GRAFANA_TOKEN", list.Secrets.Single().Name);
                Assert.IsNull(list.Secrets.Single().Value);
                Assert.AreEqual("https://grafana.example", list.Variables.Single().Value);
                Assert.IsNotNull(list.Protection);

                var missing = await Assert.ThrowsExactlyAsync<FleetException>(() => client.SetSecretAsync(new FleetSecretSetParams("NOPE", "value"), rotate: true, token));
                Assert.AreEqual(FleetNodeErrorCodes.SecretNotFound, missing.Error.Code);
                var invalid = await Assert.ThrowsExactlyAsync<FleetException>(() => client.SetSecretAsync(new FleetSecretSetParams("bad-name", "value"), cancellationToken: token));
                Assert.AreEqual(FleetErrorCodes.InvalidParams, invalid.Error.Code);

                // altanode secrets reads the same store, by name.
                var (code, output, error) = await RunAsync(["secrets", "list", "--data-root", root.DataRoot]);
                Assert.AreEqual(NodeExitCodes.Success, code, error);
                StringAssert.Contains(output, "GRAFANA_TOKEN");
                Assert.IsFalse(output.Contains("glsa_rotated_43", StringComparison.Ordinal));
                Assert.IsTrue(new LocalSecretStore(root.DataRoot).TryGetSecret("GRAFANA_TOKEN", out var value));
                Assert.AreEqual("glsa_rotated_43", value);

                await client.DeleteSecretAsync(new FleetSecretRef("GRAFANA_TOKEN"), token);
                var gone = await Assert.ThrowsExactlyAsync<FleetException>(() => client.DeleteSecretAsync(new FleetSecretRef("GRAFANA_TOKEN"), token));
                Assert.AreEqual(FleetNodeErrorCodes.SecretNotFound, gone.Error.Code);
                Assert.AreEqual(0, (await client.ListSecretsAsync(token)).Secrets.Count);
            }
        }
        finally
        {
            stop();
            await running.WithinAsync(Patience, token);
            new LocalSecretStore(root.DataRoot).Delete();
        }
    }
}
