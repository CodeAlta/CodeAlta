using System.Text.Json.Nodes;
using CodeAlta.Catalog.Secrets;
using CodeAlta.Credentials.Providers;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Credentials.Tests;

[TestClass]
public sealed class BitwardenSecretsProviderTests
{
    private const string Token = "0.bws-access-token-0001";
    private const string SecretId = "be8e0ad8-d545-4017-a55a-b02f014d4158";

    [TestMethod]
    public async Task The_access_token_goes_in_the_environment_and_a_refused_one_says_why()
    {
        using var vault = NewVault();
        var ready = await Create(vault, Token).GetStatusAsync(CancellationToken.None);
        Assert.AreEqual(CredentialProviderState.Ready, ready.State);
        Assert.AreEqual(vault.ProgramPath, ready.ProgramPath);

        var refused = await Create(vault, "0.another-token").GetStatusAsync(CancellationToken.None);
        Assert.AreEqual(CredentialProviderState.Unavailable, refused.State);
        StringAssert.Contains(refused.Message, "refused the access token");
        StringAssert.Contains(refused.Detail, "Access token is not in a valid format", "the first line that says something, not \"Error:\"");

        var missing = await ProviderHarness.Create(BitwardenSecretsProvider.Contribution(new VaultCommandRunner()), new Dictionary<string, object?> { ["path"] = Path.Combine(vault.Bin, "none.exe") })
            .GetStatusAsync(CancellationToken.None);
        Assert.AreEqual(CredentialProviderState.NotInstalled, missing.State);
        Assert.IsNotNull(missing.Install?.Page);
        ProviderHarness.AssertNoArgumentHolds(vault, Token, "0.another-token");
    }

    [TestMethod]
    public async Task A_secret_is_read_by_its_id_and_a_missing_one_is_not_found()
    {
        using var vault = NewVault();
        var provider = Create(vault, Token);
        Assert.AreEqual("bws-secret-value-111", (await provider.ReadAsync(SecretId, false, CancellationToken.None)).Value);
        var missing = await ProviderHarness.ReadFailsAsync(provider, "00000000-0000-0000-0000-000000000000");
        Assert.AreEqual(CredentialFailure.NotFound, missing.Failure);
        StringAssert.Contains(missing.Message, "no such secret");
        StringAssert.Contains(missing.Detail, "404");
        ProviderHarness.AssertNoArgumentHolds(vault, Token);
    }

    [TestMethod]
    public async Task The_picker_lists_the_keys_of_the_secrets_never_their_values()
    {
        using var vault = NewVault();
        var page = await Create(vault, Token).BrowseAsync(new CredentialBrowseRequest(null), CancellationToken.None);
        CollectionAssert.AreEqual(new[] { "DATABASE_URL", "OPENAI_API_KEY" }, page.Entries.Select(static entry => entry.Label).ToArray());
        Assert.AreEqual(SecretId, page.Entries[1].Reference);
        Assert.IsTrue(page.Entries.All(static entry => entry.Kind == CredentialBrowseEntryKind.Field));
        ProviderHarness.AssertHoldsNone(page, "bws-secret-value-111", "bws-secret-value-222", Token);
    }

    [TestMethod]
    public async Task The_broker_gives_the_token_from_a_secret_of_this_computer()
    {
        using var vault = NewVault();
        var store = new LocalSecretStore(vault.Profile);
        store.SetSecret("BWS_TOKEN", Token, "alice");
        vault.WriteConfig($"[credentials.sm]\ntype = \"bitwarden-secrets\"\naccess_token = \"${{secret:BWS_TOKEN}}\"\npath = \"{vault.TomlPath}\"\n");
        store.Bind("OPENAI_API_KEY", "sm", SecretId, "OPENAI_API_KEY", "alice");
        using var broker = new CredentialBroker(new CredentialBrokerOptions { GlobalRoot = vault.Profile });
        await broker.RefreshAsync();
        Assert.AreEqual(CredentialProviderState.Ready, broker.GetProvider("sm")!.Status.State);
        Assert.AreEqual("bws-secret-value-111", broker.ReadBound("OPENAI_API_KEY", null).Value);
        ProviderHarness.AssertNoArgumentHolds(vault, Token);

        vault.WriteConfig($"[credentials.sm]\ntype = \"bitwarden-secrets\"\npath = \"{vault.TomlPath}\"\n");
        await broker.RefreshAsync();
        StringAssert.Contains(broker.GetProvider("sm")!.Status.Message, "required");
    }

    private static FakeVault NewVault()
    {
        var vault = new FakeVault("bws");
        vault.State["bws"] = new JsonObject
        {
            ["token"] = Token,
            ["secrets"] = new JsonArray(
                new JsonObject { ["object"] = "secret", ["id"] = SecretId, ["organizationId"] = "o1", ["key"] = "OPENAI_API_KEY", ["value"] = "bws-secret-value-111", ["note"] = "" },
                new JsonObject { ["object"] = "secret", ["id"] = "c0ffee00-0000-4000-8000-000000000002", ["organizationId"] = "o1", ["key"] = "DATABASE_URL", ["value"] = "bws-secret-value-222", ["note"] = "" }),
        };
        vault.Save();
        return vault;
    }

    private static ICredentialProvider Create(FakeVault vault, string token)
        => ProviderHarness.Create(BitwardenSecretsProvider.Contribution(new VaultCommandRunner()), new Dictionary<string, object?> { ["path"] = vault.ProgramPath, ["access_token"] = token });
}
