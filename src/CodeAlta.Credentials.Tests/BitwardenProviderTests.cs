using System.Text.Json.Nodes;
using CodeAlta.Catalog.Secrets;
using CodeAlta.Credentials.Providers;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Credentials.Tests;

[TestClass]
public sealed class BitwardenProviderTests
{
    private const string MasterPassword = "bw-master-password-0001";
    private const string SessionKey = "bw-session-key-0002";
    private const string ItemId = "1f2e3d4c-0000-4000-8000-000000000001";
    private static readonly string[] Values = ["bw-password-value-111", "bw-note-value-222", "bw-field-value-333", "JBSWY3DPEHPK3PXP", "https://login.example.com", "bob-user", MasterPassword, SessionKey];

    [TestMethod]
    public async Task The_status_follows_bw_and_the_unlock_keeps_the_session_key_in_memory_only()
    {
        using var vault = NewVault();
        vault.State["bw"]!["status"] = "unauthenticated";
        vault.Save();
        var masked = new List<string>();
        var provider = Create(vault, masked);

        var signIn = await provider.GetStatusAsync(CancellationToken.None);
        Assert.AreEqual(CredentialProviderState.SignInNeeded, signIn.State);
        CollectionAssert.AreEqual(new[] { vault.ProgramPath, "login" }, signIn.SignInCommand!.ToArray());

        vault.State["bw"]!["status"] = "locked";
        vault.Save();
        var locked = await provider.GetStatusAsync(CancellationToken.None);
        Assert.AreEqual(CredentialProviderState.Locked, locked.State);
        Assert.AreEqual("bob@example.com", locked.Account);

        var noPassword = await provider.UnlockAsync(new CredentialUnlockContext(), CancellationToken.None);
        Assert.AreEqual(CredentialProviderState.Locked, noPassword.State);
        StringAssert.Contains(noPassword.Message, "master password");

        var wrong = await provider.UnlockAsync(new CredentialUnlockContext { Password = "not-the-password" }, CancellationToken.None);
        Assert.AreEqual(CredentialProviderState.Locked, wrong.State);
        StringAssert.Contains(wrong.Message, "Invalid master password");

        var ready = await provider.UnlockAsync(new CredentialUnlockContext { Password = MasterPassword }, CancellationToken.None);
        Assert.AreEqual(CredentialProviderState.Ready, ready.State);
        Assert.AreEqual("bob@example.com", ready.Account);
        Assert.Contains(SessionKey, masked, "the session key is masked once it is known");
        Assert.DoesNotContain(MasterPassword, masked, "the master password is the host's: it masks it itself");
        ProviderHarness.AssertNoArgumentHolds(vault, MasterPassword, SessionKey);
        Assert.IsFalse(vault.Calls().Any(static call => call.StartsWith("sync", StringComparison.Ordinal)), "bw sync is never run by itself");
    }

    [TestMethod]
    public async Task Each_kind_of_field_is_read_and_a_missing_one_is_not_found()
    {
        using var vault = NewVault();
        var provider = await UnlockedAsync(vault);

        Assert.AreEqual("bw-password-value-111", (await provider.ReadAsync($"{ItemId}/password", false, CancellationToken.None)).Value);
        Assert.AreEqual("bob-user", (await provider.ReadAsync($"{ItemId}/username", false, CancellationToken.None)).Value);
        Assert.AreEqual("bw-note-value-222", (await provider.ReadAsync($"{ItemId}/notes", false, CancellationToken.None)).Value);
        Assert.AreEqual("https://login.example.com", (await provider.ReadAsync($"{ItemId}/uri", false, CancellationToken.None)).Value);
        Assert.AreEqual("bw-field-value-333", (await provider.ReadAsync($"{ItemId}/field:API key", false, CancellationToken.None)).Value);
        var totp = await provider.ReadAsync($"{ItemId}/totp", false, CancellationToken.None);
        Assert.AreEqual("123456", totp.Value);
        Assert.IsNotNull(totp.ExpiresAt);
        Assert.IsTrue(totp.ExpiresAt > DateTimeOffset.UtcNow && totp.ExpiresAt <= DateTimeOffset.UtcNow.AddSeconds(31));
        Assert.IsNull((await provider.ReadAsync($"{ItemId}/password", false, CancellationToken.None)).ExpiresAt);

        Assert.AreEqual(CredentialFailure.NotFound, (await ProviderHarness.ReadFailsAsync(provider, "00000000-0000-0000-0000-000000000000/password")).Failure);
        Assert.AreEqual(CredentialFailure.NotFound, (await ProviderHarness.ReadFailsAsync(provider, "note-1/totp")).Failure);
        Assert.AreEqual(CredentialFailure.NotFound, (await ProviderHarness.ReadFailsAsync(provider, $"{ItemId}/field:Nothing")).Failure);
        var malformed = await ProviderHarness.ReadFailsAsync(provider, $"{ItemId}/secret");
        Assert.AreEqual(CredentialFailure.NotFound, malformed.Failure);
        StringAssert.Contains(malformed.Message, "field:<name>");
        ProviderHarness.AssertNoArgumentHolds(vault, SessionKey, MasterPassword);
    }

    [TestMethod]
    public async Task A_locked_vault_is_not_run_and_a_lock_forgets_the_session_key()
    {
        using var vault = NewVault();
        var provider = Create(vault);
        var failure = await ProviderHarness.ReadFailsAsync(provider, $"{ItemId}/password", interactive: false);
        Assert.AreEqual(CredentialFailure.Locked, failure.Failure);
        Assert.IsEmpty(vault.Calls(), "without a session key bw is not run");

        provider = await UnlockedAsync(vault);
        Assert.AreEqual("bw-password-value-111", (await provider.ReadAsync($"{ItemId}/password", false, CancellationToken.None)).Value);
        var calls = vault.Calls().Count;
        provider.Lock();
        Assert.AreEqual(calls, vault.Calls().Count, "a lock runs nothing");
        Assert.AreEqual(CredentialFailure.Locked, (await ProviderHarness.ReadFailsAsync(provider, $"{ItemId}/password", interactive: false)).Failure);
        Assert.AreEqual(calls, vault.Calls().Count);
        Assert.AreEqual(CredentialProviderState.Locked, (await provider.GetStatusAsync(CancellationToken.None)).State);

        // bw was locked in a terminal: the session key it no longer takes is forgotten.
        provider = await UnlockedAsync(vault);
        vault.State["bw"]!["sessionKey"] = "another-session";
        vault.Save();
        Assert.AreEqual(CredentialProviderState.Locked, (await provider.GetStatusAsync(CancellationToken.None)).State);
        Assert.AreEqual(CredentialFailure.Locked, (await ProviderHarness.ReadFailsAsync(provider, $"{ItemId}/password")).Failure);
    }

    [TestMethod]
    public async Task The_picker_lists_items_and_their_fields_never_their_values()
    {
        using var vault = NewVault();
        var provider = await UnlockedAsync(vault);
        var items = await provider.BrowseAsync(new CredentialBrowseRequest(null), CancellationToken.None);
        CollectionAssert.AreEqual(new[] { "GitHub", "Server notes" }, items.Entries.Select(static entry => entry.Label).ToArray());
        Assert.AreEqual(("login", "note"), (items.Entries[0].Detail, items.Entries[1].Detail));
        Assert.IsTrue(items.Entries.All(static entry => entry.Kind == CredentialBrowseEntryKind.Folder));
        ProviderHarness.AssertHoldsNone(items, Values);

        var fields = await provider.BrowseAsync(new CredentialBrowseRequest(items.Entries[0].Id), CancellationToken.None);
        Assert.AreEqual("GitHub", fields.Path);
        CollectionAssert.AreEqual(new[] { $"{ItemId}/password", $"{ItemId}/username", $"{ItemId}/totp", $"{ItemId}/uri", $"{ItemId}/notes", $"{ItemId}/field:API key" },
            fields.Entries.Select(static entry => entry.Reference).ToArray());
        Assert.AreEqual("hidden", fields.Entries[^1].Detail);
        ProviderHarness.AssertHoldsNone(fields, Values);
        foreach (var field in fields.Entries) Assert.IsNotEmpty((await provider.ReadAsync(field.Reference!, false, CancellationToken.None)).Value, "every reference the picker gives is read");

        var note = await provider.BrowseAsync(new CredentialBrowseRequest(items.Entries[1].Id), CancellationToken.None);
        CollectionAssert.AreEqual(new[] { "note-1/notes" }, note.Entries.Select(static entry => entry.Reference).ToArray(), "a note has no login fields, and no totp");
        ProviderHarness.AssertNoArgumentHolds(vault, SessionKey);
    }

    [TestMethod]
    public async Task The_broker_unlocks_Bitwarden_with_the_master_password_typed_and_reads_its_bound_secrets()
    {
        using var vault = NewVault();
        vault.WriteConfig($"[credentials.bw]\ntype = \"bitwarden\"\npath = \"{vault.TomlPath}\"\n");
        new LocalSecretStore(vault.Profile).Bind("GITHUB_PASSWORD", "bw", $"{ItemId}/password", "GitHub › password", "alice");
        var masked = new List<string>();
        using var broker = new CredentialBroker(new CredentialBrokerOptions { GlobalRoot = vault.Profile, Mask = masked.Add, CanPrompt = true });
        await broker.RefreshAsync();
        Assert.AreEqual(CredentialProviderState.Locked, broker.GetProvider("bw")!.Status.State);
        Assert.IsTrue(broker.GetProvider("bw")!.UnlockAsksPassword);
        Assert.IsNull(broker.ReadBound("GITHUB_PASSWORD", null).Value);

        var view = await broker.UnlockAsync("bw", MasterPassword);
        Assert.AreEqual(CredentialProviderState.Ready, view.Status.State);
        Assert.AreEqual("bw-password-value-111", broker.ReadBound("GITHUB_PASSWORD", null).Value);
        Assert.Contains(SessionKey, masked);
        ProviderHarness.AssertNoArgumentHolds(vault, MasterPassword, SessionKey);
    }

    [TestMethod]
    public void The_vaults_are_offered_in_their_order()
    {
        var types = new CredentialProviderTypes(new VaultCommandRunner());
        var builtIn = types.BuiltIn().Select(static type => type.Type).ToList();
        var vaults = new[] { "1password", "bitwarden", "bitwarden-secrets", "keepassxc", "pass", "keychain", "command" };
        CollectionAssert.AreEqual(vaults, builtIn.Take(vaults.Length).ToArray());
        Assert.IsTrue(types.Find("keepassxc")!.UnlockAsksPassword);
        Assert.IsTrue(types.Find("bitwarden-secrets")!.Settings.Single(static setting => setting.Key == "access_token") is { Required: true, Kind: PluginCredentialProviderSettingKind.Secret });
    }

    private static FakeVault NewVault()
    {
        var vault = new FakeVault("bw");
        vault.State["bw"] = new JsonObject
        {
            ["status"] = "locked",
            ["email"] = "bob@example.com",
            ["masterPassword"] = MasterPassword,
            ["sessionKey"] = SessionKey,
            ["items"] = new JsonArray(
                new JsonObject
                {
                    ["object"] = "item", ["id"] = ItemId, ["type"] = 1, ["name"] = "GitHub", ["notes"] = "bw-note-value-222",
                    ["login"] = new JsonObject
                    {
                        ["username"] = "bob-user", ["password"] = "bw-password-value-111", ["totp"] = "JBSWY3DPEHPK3PXP",
                        ["uris"] = new JsonArray(new JsonObject { ["match"] = null, ["uri"] = "https://login.example.com" }),
                    },
                    ["fields"] = new JsonArray(
                        new JsonObject { ["name"] = "API key", ["value"] = "bw-field-value-333", ["type"] = 1 },
                        new JsonObject { ["name"] = "Linked", ["value"] = null, ["type"] = 3, ["linkedId"] = 100 }),
                },
                new JsonObject { ["object"] = "item", ["id"] = "note-1", ["type"] = 2, ["name"] = "Server notes", ["notes"] = "bw-note-value-222", ["login"] = null, ["fields"] = new JsonArray() }),
        };
        vault.Save();
        return vault;
    }

    private static ICredentialProvider Create(FakeVault vault, List<string>? masked = null)
        => ProviderHarness.Create(BitwardenProvider.Contribution(new VaultCommandRunner()), new Dictionary<string, object?> { ["path"] = vault.ProgramPath }, masked);

    private static async Task<ICredentialProvider> UnlockedAsync(FakeVault vault)
    {
        var provider = Create(vault);
        Assert.AreEqual(CredentialProviderState.Ready, (await provider.UnlockAsync(new CredentialUnlockContext { Password = MasterPassword }, CancellationToken.None)).State);
        return provider;
    }
}
