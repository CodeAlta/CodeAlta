using System.Text.Json.Nodes;
using CodeAlta.Catalog.Secrets;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Credentials.Tests;

[TestClass]
public sealed class SignInsTests
{
    private static SignIn GitHub(params string[] sites)
        => new("github", "GitHub", "personal", sites.Length == 0 ? ["https://github.com"] : sites, "op://Private/GitHub/username", "op://Private/GitHub/password", "op://Private/GitHub/password?attribute=otp");

    [TestMethod]
    public void A_sign_in_is_kept_beside_the_bindings_with_its_sites_and_holds_no_value()
    {
        using var vault = new FakeVault();
        var store = new LocalSecretStore(vault.Profile);
        store.Bind("ANTHROPIC_API_KEY", "personal", "op://Private/Anthropic/credential", "label", "alice");
        var signIns = new SignIns(store.Bindings);

        var saved = signIns.Save(GitHub("github.com", "https://GitHub.com:443", "http://localhost:5173"), "alice");
        CollectionAssert.AreEqual(new[] { "https://github.com", "http://localhost:5173" }, saved.Sites.ToArray(), "a site is an origin, written once");
        Assert.AreEqual("alice", saved.SetBy);

        var document = JsonNode.Parse(File.ReadAllText(store.Bindings.FilePath))!.AsObject();
        var entry = document[SignIns.SignInsMember]!["github"]!.AsObject();
        Assert.AreEqual("personal", entry["provider"]!.GetValue<string>());
        Assert.AreEqual("op://Private/GitHub/password", entry["password"]!.GetValue<string>());
        Assert.IsNotNull(document["bindings"]!["ANTHROPIC_API_KEY"], "the bindings stay");
        Assert.AreEqual("personal", store.Bindings.List().Single().Provider);

        var listed = new SignIns(new SecretBindings(Path.GetDirectoryName(store.Bindings.FilePath)!)).List().Single();
        Assert.AreEqual(("github", "GitHub", "op://Private/GitHub/password?attribute=otp"), (listed.Id, listed.Label, listed.Totp));

        Assert.ThrowsExactly<ArgumentException>(() => signIns.Save(GitHub("http://github.com"), "alice"), "http is a site only on this computer");
        Assert.ThrowsExactly<ArgumentException>(() => signIns.Save(GitHub("https://github.com/login"), "alice"), "a site has no path");
        Assert.ThrowsExactly<ArgumentException>(() => signIns.Save(GitHub() with { Id = "Git Hub" }, "alice"));
        Assert.IsTrue(signIns.Remove("github"));
        Assert.IsEmpty(signIns.List());
        signIns.Save(GitHub(), "alice");
        signIns.Save(GitHub() with { Id = "other", Provider = "work" }, "alice");
        CollectionAssert.AreEqual(new[] { "github" }, signIns.RemoveProvider("personal").ToArray(), "a removed provider takes its sign-ins with it");
        Assert.AreEqual("other", signIns.List().Single().Id);
        Assert.IsNotNull(JsonNode.Parse(File.ReadAllText(store.Bindings.FilePath))!["bindings"]!["ANTHROPIC_API_KEY"]);
    }

    [TestMethod]
    public void A_page_finds_the_sign_ins_of_its_exact_origin_only()
    {
        using var vault = new FakeVault();
        var signIns = new SignIns(new LocalSecretStore(vault.Profile).Bindings);
        signIns.Save(GitHub("https://github.com", "http://127.0.0.1:8080"), "alice");

        Assert.AreEqual("github", signIns.FindByOrigin("https://github.com/login?return_to=%2F").Single().Id);
        Assert.AreEqual("github", signIns.FindByOrigin("HTTPS://GITHUB.COM:443/").Single().Id);
        Assert.AreEqual("github", signIns.FindByOrigin("http://127.0.0.1:8080/form").Single().Id);
        foreach (var other in new[] { "https://gist.github.com/", "https://github.com.evil.example/", "http://github.com/", "https://github.com:8443/",
                     "https://user@github.com/", "http://127.0.0.1:8081/", "http://localhost:8080/", "file:///C:/github.com", "about:blank", "" })
        {
            Assert.IsEmpty(signIns.FindByOrigin(other), other);
        }
    }

    [TestMethod]
    public void A_site_the_user_allowed_stays_allowed_for_30_days_and_can_be_revoked()
    {
        using var vault = new FakeVault();
        var time = new ManualTime(DateTimeOffset.Parse("2026-11-11T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var signIns = new SignIns(new LocalSecretStore(vault.Profile).Bindings, time);
        signIns.Save(GitHub("https://github.com", "https://gitlab.com"), "alice");
        Assert.IsFalse(signIns.IsGranted("github", "https://github.com"));

        var grant = signIns.Grant("github", "https://github.com/login");
        Assert.AreEqual(("github", "https://github.com", time.GetUtcNow() + TimeSpan.FromDays(30)), (grant.SignIn, grant.Origin, grant.Until));
        Assert.IsTrue(signIns.IsGranted("github", "https://github.com/session"));
        Assert.IsFalse(signIns.IsGranted("github", "https://gitlab.com"), "a grant is for one site");
        Assert.ThrowsExactly<ArgumentException>(() => signIns.Grant("github", "https://example.com"), "a grant is for a site the sign-in lists");
        StringAssert.Contains(File.ReadAllText(new LocalSecretStore(vault.Profile).Bindings.FilePath), "\"until\": \"2026-12-11T10:00:00");

        time.Now += TimeSpan.FromDays(30);
        Assert.IsFalse(signIns.IsGranted("github", "https://github.com"), "it ends after 30 days");
        Assert.IsEmpty(signIns.ListGrants());

        signIns.Grant("github", "https://github.com");
        signIns.Grant("github", "https://gitlab.com");
        Assert.IsTrue(signIns.Revoke("github", "https://gitlab.com"));
        Assert.AreEqual("https://github.com", signIns.ListGrants().Single().Origin);
        signIns.Save(GitHub("https://gitlab.com"), "alice");
        Assert.IsEmpty(signIns.ListGrants(), "a site the sign-in no longer lists loses its grant");
        signIns.Grant("github", "https://gitlab.com");
        Assert.IsTrue(signIns.Remove("github"));
        Assert.IsEmpty(signIns.ListGrants(), "a removed sign-in loses its grants");
    }

    [TestMethod]
    public async Task A_sign_in_is_read_through_its_vault_without_a_prompt_and_every_value_is_masked_first()
    {
        using var vault = new FakeVault();
        AddGitHubItem(vault);
        vault.WriteConfig($"[credentials.personal]\ntype = \"1password\"\npath = \"{vault.TomlPath}\"\n");
        var masked = new List<string>();
        using var broker = new CredentialBroker(new CredentialBrokerOptions { GlobalRoot = vault.Profile, Mask = masked.Add, CanPrompt = true });
        broker.SignIns.Save(GitHub(), "alice");

        var locked = await Assert.ThrowsExactlyAsync<CredentialProviderException>(() => broker.ReadSignInAsync("github"));
        Assert.AreEqual(CredentialFailure.Locked, locked.Failure, "a locked vault is never unlocked by a fill");
        vault.Reload();
        Assert.IsFalse(vault.State["signedIn"]!.GetValue<bool>());

        vault.State["signedIn"] = true;
        vault.Save();
        using (var values = await broker.ReadSignInAsync("github"))
        {
            Assert.AreEqual(("octocat", "hunter2-github", "123456"), (values.Username, values.Password, values.Totp));
            CollectionAssert.IsSubsetOf(new[] { "octocat", "hunter2-github", "123456" }, masked, "each value is masked before it is given");
            Assert.DoesNotContain("hunter2", values.ToString());
            values.Dispose();
            Assert.ThrowsExactly<ObjectDisposedException>(() => _ = values.Password, "the values are let go after the fill");
        }

        Assert.IsFalse(broker.ListProviders().Single().Held > 0, "a sign-in is not kept in the memory of the broker");
        ProviderHarness.AssertNoArgumentHolds(vault, "hunter2-github", "octocat");
        await Assert.ThrowsExactlyAsync<KeyNotFoundException>(() => broker.ReadSignInAsync("nothing"));

        broker.SignIns.Save(GitHub() with { Password = "op://Private/GitHub/missing" }, "alice");
        var missing = await Assert.ThrowsExactlyAsync<CredentialProviderException>(() => broker.ReadSignInAsync("github"));
        Assert.DoesNotContain("octocat", missing.Message);
    }

    private static void AddGitHubItem(FakeVault vault)
    {
        var items = vault.State["vaults"]![0]!["items"]!.AsArray();
        items.Add(new JsonObject
        {
            ["id"] = "i2",
            ["title"] = "GitHub",
            ["category"] = "LOGIN",
            ["fields"] = new JsonArray(
                new JsonObject { ["id"] = "username", ["label"] = "username", ["type"] = "STRING", ["value"] = "octocat" },
                new JsonObject { ["id"] = "password", ["label"] = "password", ["type"] = "CONCEALED", ["value"] = "hunter2-github" }),
        });
        vault.Save();
    }

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
