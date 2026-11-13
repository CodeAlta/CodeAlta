using System.Text;
using System.Text.Json.Nodes;
using CodeAlta.Catalog.Secrets;
using CodeAlta.Credentials.Providers;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Credentials.Tests;

/// <summary>The secrets of a vault are read with as few calls of its program as it allows, and a secret that cannot be read never fails the others.</summary>
[TestClass]
public sealed class CredentialBatchReadTests
{
    private const string Type = "test.vault/batch";
    private const string Notes = "first line\nsecond line\n";

    [TestMethod]
    public async Task The_secrets_of_1Password_are_read_with_one_call_after_the_unlock()
    {
        using var vault = OnePassword();
        var store = new LocalSecretStore(vault.Profile);
        store.Bind("ANTHROPIC_API_KEY", "personal", "op://Private/Anthropic/credential", "label", "alice");
        store.Bind("OPENAI_API_KEY", "personal", "op://Private/OpenAI/credential", "label", "alice");
        store.Bind("OPENAI_NOTES", "personal", "op://Private/OpenAI/notes", "label", "alice");
        store.Bind("KIMI_API_KEY", "personal", "op://Private/Kimi for Coding/credential", "label", "alice");
        var masked = new List<string>();
        using var broker = new CredentialBroker(new CredentialBrokerOptions { GlobalRoot = vault.Profile, Mask = masked.Add, CanPrompt = true });
        await broker.RefreshAsync();
        var before = vault.Calls().Count;

        var view = await broker.UnlockAsync("personal", null);

        Assert.AreEqual((CredentialProviderState.Ready, "alice@example.com", 4), (view.Status.State, view.Status.Account, view.Held), view.Status.Message);
        Assert.AreEqual("sk-ant-fake-value-1234", broker.ReadBound("ANTHROPIC_API_KEY", null).Value);
        Assert.AreEqual("sk-openai-fake-value-5678", broker.ReadBound("OPENAI_API_KEY", null).Value);
        Assert.AreEqual(Notes, broker.ReadBound("OPENAI_NOTES", null).Value, "a value of several lines is the one a read of it alone gives");
        Assert.AreEqual("sk-kimi-fake-value-9012", broker.ReadBound("KIMI_API_KEY", null).Value);
        CollectionAssert.IsSubsetOf(new[] { "sk-ant-fake-value-1234", "sk-openai-fake-value-5678", Notes, "sk-kimi-fake-value-9012" }, masked);
        // The prompt, one call for the three references a template holds, and one read for the name with spaces: the state is not asked again.
        CollectionAssert.AreEqual(new[] { "vault list --format json", "inject", "read --no-newline op://Private/Kimi for Coding/credential" }, vault.Calls().Skip(before).ToArray());
        ProviderHarness.AssertNoArgumentHolds(vault, "sk-ant-fake-value-1234", "sk-openai-fake-value-5678");
    }

    [TestMethod]
    public async Task A_secret_that_is_gone_from_1Password_fails_alone()
    {
        using var vault = OnePassword();
        var store = new LocalSecretStore(vault.Profile);
        store.Bind("ANTHROPIC_API_KEY", "personal", "op://Private/Anthropic/credential", "label", "alice");
        store.Bind("OPENAI_API_KEY", "personal", "op://Private/OpenAI/credential", "label", "alice");
        store.Bind("GONE", "personal", "op://Private/Removed/credential", "label", "alice");
        using var broker = new CredentialBroker(new CredentialBrokerOptions { GlobalRoot = vault.Profile, CanPrompt = true });
        await broker.RefreshAsync();

        var view = await broker.UnlockAsync("personal", null);

        Assert.AreEqual((CredentialProviderState.Ready, 2), (view.Status.State, view.Held));
        Assert.AreEqual("sk-ant-fake-value-1234", broker.ReadBound("ANTHROPIC_API_KEY", null).Value);
        Assert.AreEqual("sk-openai-fake-value-5678", broker.ReadBound("OPENAI_API_KEY", null).Value);
        Assert.AreEqual(SecretAvailabilityState.NotFound, broker.GetAvailability("GONE")!.State);
        var calls = vault.Calls();
        Assert.AreEqual(1, calls.Count(static call => call == "inject"));
        Assert.AreEqual(3, calls.Count(static call => call.StartsWith("read", StringComparison.Ordinal)), "the template fails as a whole: a read of each says which is gone");
    }

    [TestMethod]
    public async Task An_op_that_cannot_fill_a_template_reads_each_secret_alone()
    {
        using var vault = OnePassword();
        vault.State["injectUnknown"] = true;
        vault.Save();
        var store = new LocalSecretStore(vault.Profile);
        store.Bind("ANTHROPIC_API_KEY", "personal", "op://Private/Anthropic/credential", "label", "alice");
        store.Bind("OPENAI_API_KEY", "personal", "op://Private/OpenAI/credential", "label", "alice");
        using var broker = new CredentialBroker(new CredentialBrokerOptions { GlobalRoot = vault.Profile, CanPrompt = true });
        await broker.RefreshAsync();

        Assert.AreEqual(2, (await broker.UnlockAsync("personal", null)).Held);
        Assert.AreEqual("sk-openai-fake-value-5678", broker.ReadBound("OPENAI_API_KEY", null).Value);
    }

    [TestMethod]
    [DataRow("op://Private/Anthropic/credential", "op://Private/OpenAI/credential", "inject", DisplayName = "references of a template")]
    [DataRow("op://Private/Kimi for Coding/credential", "op://Private/My Notes/text", "read", DisplayName = "references read one by one")]
    public async Task An_authorization_the_1Password_app_took_back_is_met_once_and_not_for_each_secret(string first, string second, string command)
    {
        using var vault = OnePassword();
        vault.State["whoamiSignedOut"] = true;
        vault.Save();
        using var broker = new CredentialBroker(new CredentialBrokerOptions { GlobalRoot = vault.Profile, CanPrompt = true });
        await broker.RefreshAsync();
        Assert.AreEqual(CredentialProviderState.Ready, (await broker.UnlockAsync("personal", null)).Status.State);

        // The app locks: it authorizes this process no longer, and a command would show its prompt, which nobody answers.
        vault.Reload();
        vault.State["signedIn"] = false;
        vault.State["approve"] = false;
        vault.Save();
        var store = new LocalSecretStore(vault.Profile);
        store.Bind("FIRST", "personal", first, "label", "alice");
        store.Bind("SECOND", "personal", second, "label", "alice");
        var before = vault.Calls().Count;

        var view = await broker.TestProviderAsync("personal");

        Assert.AreEqual(CredentialProviderState.Locked, view.Status.State, "the vault says it must be unlocked again");
        Assert.AreEqual(SecretAvailabilityState.Locked, broker.GetAvailability("FIRST")!.State);
        Assert.AreEqual(SecretAvailabilityState.Locked, broker.GetAvailability("SECOND")!.State);
        var asked = vault.Calls().Skip(before).Where(static call => call.StartsWith("read", StringComparison.Ordinal) || call == "inject").ToArray();
        Assert.HasCount(1, asked, "one command met the prompt, the other secrets were not asked: " + string.Join(" | ", asked));
        StringAssert.StartsWith(asked[0], command);
        Assert.AreEqual(CredentialProviderState.Locked, (await broker.TestProviderAsync("personal")).Status.State, "a check after it keeps it locked");
    }

    [TestMethod]
    public void A_template_of_1Password_holds_plain_references_only()
    {
        Assert.IsTrue(OnePasswordProvider.IsTemplateReference("op://Private/Anthropic/credential"));
        Assert.IsTrue(OnePasswordProvider.IsTemplateReference("op://dev-vault/stripe.keys/section_1/public-key"));
        Assert.IsFalse(OnePasswordProvider.IsTemplateReference("op://Private/Kimi for Coding/credential"), "a template ends a reference at a space");
        Assert.IsFalse(OnePasswordProvider.IsTemplateReference("op://Private/GitHub/one-time password?attribute=otp"), "a code is read when it is needed");
        Assert.IsFalse(OnePasswordProvider.IsTemplateReference("op://Private/$HOME/credential"), "a template expands a variable");
        Assert.IsFalse(OnePasswordProvider.IsTemplateReference("op://Private/Item}}/credential"));
        Assert.IsFalse(OnePasswordProvider.IsTemplateReference("op://Private/Anthropic"));
        Assert.IsFalse(OnePasswordProvider.IsTemplateReference("op://Private//credential"));
        Assert.IsFalse(OnePasswordProvider.IsTemplateReference("op://a/b/c/d/e"));
        Assert.IsFalse(OnePasswordProvider.IsTemplateReference("OP://Private/Anthropic/credential"));
    }

    [TestMethod]
    public void What_1Password_writes_for_a_template_is_split_into_its_values_or_refused_as_a_whole()
    {
        const string Marker = "codealta-0123456789abcdef";
        static string[]? Split(string output, int count) => OnePasswordTemplate.Split(Encoding.UTF8.GetBytes(output), Marker, count);

        CollectionAssert.AreEqual(new[] { "one", "two\nlines", "é ü" }, Split($"{Marker}\none\n{Marker}\ntwo\nlines\n{Marker}\né ü\n{Marker}\n", 3));
        CollectionAssert.AreEqual(new[] { "one", "\ntwo\n" }, Split($"{Marker}\r\none\r\n{Marker}\r\n\ntwo\n\r\n{Marker}\r\n", 2), "the ends of the lines of the template only are taken away");
        CollectionAssert.AreEqual(new[] { "" }, Split($"{Marker}\n\n{Marker}\n", 1), "an empty value is the host's to refuse");
        Assert.IsNull(Split($"{Marker}\none\n{Marker}\n", 2), "fewer values than references");
        Assert.IsNull(Split($"{Marker}\none\n{Marker}\ntwo\n{Marker}\n", 1), "more values than references");
        Assert.IsNull(Split($"{Marker}\n{{{{ op://Private/Anthropic/credential }}}}\n{Marker}\n", 1), "a reference left as it was is not a value");
        Assert.IsNull(Split($"{Marker} one\n{Marker}\n", 1), "not the lines of the template");
        Assert.IsNull(Split("something else", 1));
    }

    [TestMethod]
    public async Task The_fields_of_one_item_of_Bitwarden_are_read_with_one_call_and_never_from_the_list_of_the_items()
    {
        const string ItemId = "1F2E3D4C-0000-4000-8000-000000000001";
        const string OtherId = "1f2e3d4c-0000-4000-8000-000000000002";
        using var vault = Bitwarden(ItemId.ToLowerInvariant(), OtherId);
        vault.WriteConfig($"[credentials.bw]\ntype = \"bitwarden\"\npath = \"{vault.TomlPath}\"\n");
        var store = new LocalSecretStore(vault.Profile);
        store.Bind("GH_PASSWORD", "bw", $"{ItemId}/password", "label", "alice");
        store.Bind("GH_USER", "bw", $"{ItemId}/username", "label", "alice");
        store.Bind("GH_NOTES", "bw", $"{ItemId}/notes", "label", "alice");
        store.Bind("GH_URI", "bw", $"{ItemId}/uri", "label", "alice");
        store.Bind("GH_KEY", "bw", $"{ItemId}/field:API key", "label", "alice");
        store.Bind("GH_CODE", "bw", $"{ItemId}/totp", "label", "alice");
        store.Bind("GH_NO_FIELD", "bw", $"{ItemId}/field:Nope", "label", "alice");
        store.Bind("OTHER_PASSWORD", "bw", $"{OtherId}/password", "label", "alice");
        store.Bind("NOTE_PASSWORD", "bw", "note-1/password", "label", "alice");
        store.Bind("NOTE_USER", "bw", "note-1/username", "label", "alice");
        using var broker = new CredentialBroker(new CredentialBrokerOptions { GlobalRoot = vault.Profile, CanPrompt = true });
        await broker.RefreshAsync();

        var view = await broker.UnlockAsync("bw", "bw-master-password-0001");

        Assert.AreEqual(CredentialProviderState.Ready, view.Status.State, view.Status.Message);
        Assert.AreEqual("bw-password-value-111", broker.ReadBound("GH_PASSWORD", null).Value);
        Assert.AreEqual("bob-user", broker.ReadBound("GH_USER", null).Value);
        Assert.AreEqual("bw-note-value-222", broker.ReadBound("GH_NOTES", null).Value);
        Assert.AreEqual("https://login.example.com", broker.ReadBound("GH_URI", null).Value);
        Assert.AreEqual("bw-field-value-333", broker.ReadBound("GH_KEY", null).Value);
        Assert.AreEqual("123456", broker.ReadBound("GH_CODE", null).Value);
        Assert.AreEqual("bw-other-password-444", broker.ReadBound("OTHER_PASSWORD", null).Value);
        Assert.AreEqual(SecretAvailabilityState.NotFound, broker.GetAvailability("GH_NO_FIELD")!.State);
        StringAssert.Contains(broker.GetAvailability("GH_NO_FIELD")!.Message, "no field 'Nope'");
        Assert.AreEqual(SecretAvailabilityState.NotFound, broker.GetAvailability("NOTE_PASSWORD")!.State, "an item that is not a login has no password");
        var calls = vault.Calls();
        Assert.IsFalse(calls.Any(static call => call.StartsWith("list", StringComparison.Ordinal)), "bw records a view of every item it lists: " + string.Join(" | ", calls));
        // One call for the five fields of the item, the code bw computes, the item with one field, and the name bw searches for each field.
        CollectionAssert.AreEquivalent(
            new[] { $"get item {ItemId.ToLowerInvariant()}", $"get totp {ItemId}", $"get password {OtherId}", "get password note-1", "get username note-1" },
            calls.Where(static call => call.StartsWith("get", StringComparison.Ordinal)).ToArray());
    }

    [TestMethod]
    public void A_field_of_an_item_of_Bitwarden_is_what_bw_get_gives_for_it()
    {
        static CredentialReadResult Value(string json, string field)
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            return BitwardenJson.Value(document.RootElement, "id", field);
        }

        const string Login = """{"type":1,"notes":"  ","login":{"username":"bob","password":" ","uris":[{"match":null,"uri":""}]},"fields":[{"name":"On","value":"true","type":2},{"name":"Empty","value":"","type":0}]}""";
        Assert.AreEqual("bob", Value(Login, "username").Value!.Value);
        Assert.AreEqual(CredentialFailure.NotFound, Value(Login, "password").Failure!.Failure, "a blank password is none, as bw says");
        Assert.AreEqual(CredentialFailure.NotFound, Value(Login, "notes").Failure!.Failure);
        Assert.AreEqual(CredentialFailure.NotFound, Value(Login, "uri").Failure!.Failure, "an empty first uri is none");
        Assert.AreEqual("true", Value(Login, "field:On").Value!.Value);
        Assert.AreEqual(CredentialFailure.NotFound, Value(Login, "field:Empty").Failure!.Failure);
        // A card that holds a login member by mistake gives no password: bw gives the password of a login only.
        const string Card = """{"type":3,"notes":"card notes","login":{"username":"bob","password":"secret"}}""";
        Assert.AreEqual(CredentialFailure.NotFound, Value(Card, "password").Failure!.Failure);
        Assert.AreEqual("card notes", Value(Card, "notes").Value!.Value, "any item has notes");
    }

    [TestMethod]
    public async Task The_secrets_of_Bitwarden_Secrets_Manager_are_each_read_by_its_id_and_never_from_the_list()
    {
        const string Token = "0.bws-access-token-0001";
        using var vault = new FakeVault("bws");
        vault.State["bws"] = new JsonObject
        {
            ["token"] = Token,
            ["secrets"] = new JsonArray(
                new JsonObject { ["object"] = "secret", ["id"] = "id-1", ["key"] = "OPENAI_API_KEY", ["value"] = "bws-secret-value-111" },
                new JsonObject { ["object"] = "secret", ["id"] = "id-2", ["key"] = "DATABASE_URL", ["value"] = "bws-secret-value-222" },
                new JsonObject { ["object"] = "secret", ["id"] = "id-3", ["key"] = "UNUSED", ["value"] = "bws-secret-value-333" }),
        };
        vault.Save();
        var store = new LocalSecretStore(vault.Profile);
        store.SetSecret("BWS_TOKEN", Token, "alice");
        vault.WriteConfig($"[credentials.sm]\ntype = \"bitwarden-secrets\"\naccess_token = \"${{secret:BWS_TOKEN}}\"\npath = \"{vault.TomlPath}\"\n");
        store.Bind("OPENAI_API_KEY", "sm", "id-1", "label", "alice");
        store.Bind("DATABASE_URL", "sm", "id-2", "label", "alice");
        using var broker = new CredentialBroker(new CredentialBrokerOptions { GlobalRoot = vault.Profile });

        await broker.RefreshAsync();

        Assert.AreEqual("bws-secret-value-111", broker.ReadBound("OPENAI_API_KEY", null).Value);
        Assert.AreEqual("bws-secret-value-222", broker.ReadBound("DATABASE_URL", null).Value);
        // bws secret list fetches every secret the machine account can read: only the secrets that are bound are asked for.
        Assert.IsFalse(vault.Calls().Any(static call => call.StartsWith("secret list", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task A_provider_that_reads_several_is_asked_once_for_each_reference_and_answers_for_each_secret()
    {
        using var fixture = new Fixture();
        fixture.Store.Bind("ONE", "team", "item-1", "label", "alice");
        fixture.Store.Bind("ONE_AGAIN", "team", "item-1", "label", "alice");
        fixture.Store.Bind("GONE", "team", "missing", "label", "alice");
        fixture.Store.Bind("EMPTY", "team", "empty", "label", "alice");

        await fixture.Broker.RefreshAsync();

        Assert.AreEqual("value of item-1", fixture.Broker.ReadBound("ONE", null).Value);
        Assert.AreEqual("value of item-1", fixture.Broker.ReadBound("ONE_AGAIN", null).Value);
        Assert.AreEqual(SecretAvailabilityState.NotFound, fixture.Broker.GetAvailability("GONE")!.State);
        Assert.AreEqual(SecretAvailabilityState.NotFound, fixture.Broker.GetAvailability("EMPTY")!.State, "an empty value is not one");
        CollectionAssert.AreEquivalent(new[] { "item-1", "missing", "empty" }, fixture.Provider.Batches.Single().Split(','), "one call, without a reference twice");
        Assert.AreEqual(0, fixture.Provider.Reads);

        // One secret left to read is read alone.
        fixture.Store.Bind("TWO", "team", "item-2", "label", "alice");
        Assert.AreEqual(SecretAvailabilityState.Ready, (await fixture.Broker.ReadSecretAsync("TWO")).State);
        Assert.AreEqual(1, fixture.Provider.Reads);
    }

    [TestMethod]
    public async Task A_provider_that_fails_to_read_several_leaves_each_secret_with_the_reason()
    {
        using var fixture = new Fixture();
        fixture.Store.Bind("ONE", "team", "item-1", "label", "alice");
        fixture.Store.Bind("TWO", "team", "item-2", "label", "alice");
        fixture.Provider.BatchFailure = new IOException("the program of the vault cannot start");

        await fixture.Broker.RefreshAsync();

        Assert.AreEqual(SecretAvailabilityState.Unavailable, fixture.Broker.GetAvailability("ONE")!.State);
        StringAssert.Contains(fixture.Broker.GetAvailability("TWO")!.Message, "cannot start");

        fixture.Provider.BatchFailure = null;
        fixture.Provider.Short = true;
        await fixture.Broker.RefreshAsync();
        Assert.AreEqual(SecretAvailabilityState.Unavailable, fixture.Broker.GetAvailability("ONE")!.State, "an answer for fewer secrets is not taken for the first of them");

        fixture.Provider.Short = false;
        await fixture.Broker.RefreshAsync();
        Assert.AreEqual("value of item-2", fixture.Broker.ReadBound("TWO", null).Value);
    }

    [TestMethod]
    public async Task What_asks_a_vault_to_unlock_while_its_prompt_is_shown_waits_for_that_prompt()
    {
        using var fixture = new Fixture();
        fixture.Provider.Locked = true;
        fixture.Provider.Prompt = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await fixture.Broker.RefreshAsync();
        Assert.IsFalse(fixture.Broker.GetProvider("team")!.Unlocking);

        var first = fixture.Broker.UnlockAsync("team", null);
        await WaitAsync(() => fixture.Provider.Unlocks == 1);
        Assert.IsTrue(fixture.Broker.GetProvider("team")!.Unlocking, "the window shows that the vault is being asked");
        using var leaving = new CancellationTokenSource();
        var second = fixture.Broker.UnlockAsync("team", null, leaving.Token);
        var third = fixture.Broker.UnlockAsync("team", null);
        await leaving.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => second, "one that stops waiting leaves the prompt to the others");

        fixture.Provider.Prompt.SetResult(true);
        var views = await Task.WhenAll(first, third).WithinAsync(CodeAlta.Testing.TestTime.Seconds(30));

        Assert.IsTrue(views.All(static view => view.Status.State == CredentialProviderState.Ready));
        Assert.AreEqual(1, fixture.Provider.Unlocks, "one prompt for all of them");
        Assert.IsFalse(fixture.Broker.GetProvider("team")!.Unlocking);
        Assert.IsFalse(views[0].Unlocking, "the answer of an unlock is the vault after it");

        // The next one asks again.
        fixture.Provider.Locked = true;
        fixture.Provider.Prompt = null;
        await fixture.Broker.TestProviderAsync("team");
        Assert.AreEqual(CredentialProviderState.Ready, (await fixture.Broker.UnlockAsync("team", null)).Status.State);
        Assert.AreEqual(2, fixture.Provider.Unlocks);
    }

    [TestMethod]
    public async Task At_start_a_vault_whose_application_does_not_answer_yet_is_asked_again_once_it_does()
    {
        using var fixture = new Fixture();
        fixture.Provider.Locked = true;
        fixture.Provider.ApplicationReady = false;
        fixture.Store.Bind("ONE", "team", "item-1", "label", "alice");
        await fixture.Broker.RefreshAsync();

        var start = fixture.Broker.UnlockAtStartAsync();
        await WaitAsync(() => fixture.Provider.ReadyChecks >= 3);
        Assert.AreEqual(1, fixture.Provider.Unlocks, "asked once, too early: nothing is asked again while its application is not there");
        Assert.IsFalse(start.IsCompleted);

        fixture.Provider.ApplicationReady = true;
        await start.WithinAsync(CodeAlta.Testing.TestTime.Seconds(30));

        Assert.AreEqual(2, fixture.Provider.Unlocks);
        Assert.AreEqual(CredentialProviderState.Ready, fixture.Broker.GetProvider("team")!.Status.State);
        Assert.AreEqual("value of item-1", fixture.Broker.ReadBound("ONE", null).Value);
    }

    [TestMethod]
    public async Task At_start_a_prompt_the_user_dismissed_is_not_shown_again()
    {
        using var fixture = new Fixture();
        fixture.Provider.Locked = true;
        fixture.Provider.Dismissed = true;
        await fixture.Broker.RefreshAsync();

        await fixture.Broker.UnlockAtStartAsync().WithinAsync(CodeAlta.Testing.TestTime.Seconds(30));

        Assert.AreEqual(1, fixture.Provider.Unlocks, "its application answers: the vault stays locked because the user said so");
        Assert.AreEqual(CredentialProviderState.Locked, fixture.Broker.GetProvider("team")!.Status.State);
    }

    [TestMethod]
    public async Task At_start_a_vault_whose_application_never_answers_stops_being_waited_for_and_one_unlocked_meanwhile_is_left_alone()
    {
        using var never = new Fixture(TimeSpan.FromMilliseconds(300));
        never.Provider.Locked = true;
        never.Provider.ApplicationReady = false;
        await never.Broker.RefreshAsync();
        await never.Broker.UnlockAtStartAsync().WithinAsync(CodeAlta.Testing.TestTime.Seconds(30));
        Assert.AreEqual(1, never.Provider.Unlocks);

        using var fixture = new Fixture();
        fixture.Provider.Locked = true;
        fixture.Provider.ApplicationReady = false;
        await fixture.Broker.RefreshAsync();
        var start = fixture.Broker.UnlockAtStartAsync();
        await WaitAsync(() => fixture.Provider.ReadyChecks >= 2);
        // The user unlocks it in Settings meanwhile (its application answers a click it did not answer at start).
        fixture.Provider.Locked = false;
        await fixture.Broker.TestProviderAsync("team");
        await start.WithinAsync(CodeAlta.Testing.TestTime.Seconds(30));
        Assert.AreEqual(1, fixture.Provider.Unlocks, "a vault that is ready is not asked");
    }

    [TestMethod]
    public async Task At_start_1Password_is_asked_once_its_app_gives_an_account()
    {
        using var vault = OnePassword();
        vault.State["accounts"] = false;
        vault.Save();
        new LocalSecretStore(vault.Profile).Bind("ANTHROPIC_API_KEY", "personal", "op://Private/Anthropic/credential", "label", "alice");
        using var broker = new CredentialBroker(new CredentialBrokerOptions { GlobalRoot = vault.Profile, CanPrompt = true, StartWaitStep = TimeSpan.FromMilliseconds(50) });
        await broker.RefreshAsync();

        var start = broker.UnlockAtStartAsync();
        await WaitAsync(() => vault.Calls().Count(static call => call.StartsWith("account list", StringComparison.Ordinal)) >= 3);
        Assert.IsFalse(start.IsCompleted, "the app is not there yet: CodeAlta waits for it");
        // The 1Password app starts.
        vault.Reload();
        vault.State["accounts"] = true;
        vault.Save();
        await start.WithinAsync(CodeAlta.Testing.TestTime.Seconds(60));

        Assert.AreEqual(CredentialProviderState.Ready, broker.GetProvider("personal")!.Status.State);
        Assert.AreEqual("sk-ant-fake-value-1234", broker.ReadBound("ANTHROPIC_API_KEY", null).Value);
        Assert.AreEqual(2, vault.Calls().Count(static call => call.StartsWith("vault list", StringComparison.Ordinal)), "once too early, once when the app is there");
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow + CodeAlta.Testing.TestTime.Seconds(20);
        while (!condition())
        {
            if (DateTime.UtcNow > until) Assert.Fail("It did not happen in time.");
            await Task.Delay(20);
        }
    }

    // A 1Password with three items, one whose name holds spaces, and a provider named personal.
    private static FakeVault OnePassword()
    {
        var vault = new FakeVault();
        var items = vault.State["vaults"]![0]!["items"]!.AsArray();
        items.Add(new JsonObject
        {
            ["id"] = "i2", ["title"] = "OpenAI", ["category"] = "API_CREDENTIAL",
            ["fields"] = new JsonArray(
                new JsonObject { ["id"] = "credential", ["label"] = "credential", ["type"] = "CONCEALED", ["value"] = "sk-openai-fake-value-5678" },
                new JsonObject { ["id"] = "notes", ["label"] = "notes", ["type"] = "STRING", ["value"] = Notes }),
        });
        items.Add(new JsonObject
        {
            ["id"] = "i3", ["title"] = "Kimi for Coding", ["category"] = "API_CREDENTIAL",
            ["fields"] = new JsonArray(new JsonObject { ["id"] = "credential", ["label"] = "credential", ["type"] = "CONCEALED", ["value"] = "sk-kimi-fake-value-9012" }),
        });
        items.Add(new JsonObject
        {
            ["id"] = "i4", ["title"] = "My Notes", ["category"] = "SECURE_NOTE",
            ["fields"] = new JsonArray(new JsonObject { ["id"] = "text", ["label"] = "text", ["type"] = "STRING", ["value"] = "a-note-value-3456" }),
        });
        vault.Save();
        vault.WriteConfig($"[credentials.personal]\ntype = \"1password\"\npath = \"{vault.TomlPath}\"\n");
        return vault;
    }

    private static FakeVault Bitwarden(string itemId, string otherId)
    {
        var vault = new FakeVault("bw");
        vault.State["bw"] = new JsonObject
        {
            ["status"] = "locked",
            ["email"] = "bob@example.com",
            ["masterPassword"] = "bw-master-password-0001",
            ["sessionKey"] = "bw-session-key-0002",
            ["items"] = new JsonArray(
                new JsonObject { ["object"] = "item", ["id"] = "note-1", ["type"] = 2, ["name"] = "Server notes", ["notes"] = "another-note", ["login"] = null, ["fields"] = new JsonArray() },
                new JsonObject
                {
                    ["object"] = "item", ["id"] = itemId, ["type"] = 1, ["name"] = "GitHub", ["notes"] = "bw-note-value-222",
                    ["login"] = new JsonObject
                    {
                        ["username"] = "bob-user", ["password"] = "bw-password-value-111", ["totp"] = "JBSWY3DPEHPK3PXP",
                        ["uris"] = new JsonArray(new JsonObject { ["match"] = null, ["uri"] = "https://login.example.com" }),
                    },
                    ["fields"] = new JsonArray(
                        new JsonObject { ["name"] = "API key", ["value"] = "bw-field-value-333", ["type"] = 1 },
                        new JsonObject { ["name"] = "Linked", ["value"] = null, ["type"] = 3, ["linkedId"] = 100 }),
                },
                new JsonObject
                {
                    ["object"] = "item", ["id"] = otherId, ["type"] = 1, ["name"] = "GitLab", ["notes"] = null,
                    ["login"] = new JsonObject { ["username"] = "bob-user", ["password"] = "bw-other-password-444", ["totp"] = null, ["uris"] = new JsonArray() },
                    ["fields"] = new JsonArray(),
                }),
        };
        vault.Save();
        return vault;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root;

        public Fixture(TimeSpan? startWait = null)
        {
            _root = Path.Combine(Path.GetTempPath(), "codealta-batch-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            File.WriteAllText(Path.Combine(_root, "config.toml"), $"[credentials.team]\ntype = \"{Type}\"\n");
            Store = new LocalSecretStore(_root);
            var contribution = new PluginCredentialProviderContribution { Type = Type, DisplayName = "Team vault", Create = _ => Provider };
            Broker = new CredentialBroker(new CredentialBrokerOptions
            {
                GlobalRoot = _root,
                Types = new CredentialProviderTypes(new VaultCommandRunner(), () => [contribution]),
                CanPrompt = true,
                StartWaitStep = TimeSpan.FromMilliseconds(20),
                StartWait = startWait ?? TimeSpan.FromSeconds(30),
            });
        }

        public TeamVault Provider { get; } = new();

        public LocalSecretStore Store { get; }

        public CredentialBroker Broker { get; }

        public void Dispose()
        {
            Broker.Dispose();
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // Cleaned by the system later.
            }
        }
    }

    // A vault of a plugin that reads several values at once and is opened by an application of the user.
    private sealed class TeamVault : ICredentialProvider, ICredentialBatchReader, ICredentialVaultApplication
    {
        private int _reads;
        private int _unlocks;
        private int _readyChecks;

        public volatile bool Locked;
        public volatile bool ApplicationReady = true;
        public volatile bool Dismissed;
        public volatile bool Short;

        public TaskCompletionSource<bool>? Prompt { get; set; }

        public Exception? BatchFailure { get; set; }

        public System.Collections.Concurrent.ConcurrentQueue<string> Batches { get; } = new();

        public int Reads => Volatile.Read(ref _reads);

        public int Unlocks => Volatile.Read(ref _unlocks);

        public int ReadyChecks => Volatile.Read(ref _readyChecks);

        public ValueTask<CredentialProviderStatus> GetStatusAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(Locked ? CredentialProviderStatus.Locked("Unlock it.") : CredentialProviderStatus.Ready());

        public async ValueTask<CredentialProviderStatus> UnlockAsync(CredentialUnlockContext context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _unlocks);
            if (!ApplicationReady) return CredentialProviderStatus.Locked("Open its application.");
            if (Prompt is { } prompt) await prompt.Task.WaitAsync(cancellationToken);
            if (Dismissed) return CredentialProviderStatus.Locked("Unlock it.");
            Locked = false;
            return CredentialProviderStatus.Ready();
        }

        public ValueTask<CredentialValue> ReadAsync(string reference, bool interactive, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _reads);
            return ValueTask.FromResult(new CredentialValue("value of " + reference));
        }

        public ValueTask<IReadOnlyList<CredentialReadResult>> ReadManyAsync(IReadOnlyList<string> references, bool interactive, CancellationToken cancellationToken)
        {
            Batches.Enqueue(string.Join(',', references));
            if (BatchFailure is { } failure) throw failure;
            IReadOnlyList<CredentialReadResult> results = [.. references.Skip(Short ? 1 : 0).Select(static reference => reference switch
            {
                "missing" => new CredentialReadResult(null, new CredentialProviderException(CredentialFailure.NotFound, "The vault has no such item.")),
                "empty" => new CredentialReadResult(new CredentialValue(string.Empty), null),
                _ => new CredentialReadResult(new CredentialValue("value of " + reference), null),
            })];
            return ValueTask.FromResult(results);
        }

        public ValueTask<bool> IsApplicationReadyAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _readyChecks);
            return ValueTask.FromResult(ApplicationReady);
        }
    }
}
