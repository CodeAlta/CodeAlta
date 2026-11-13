using System.Text;
using System.Text.Json.Nodes;
using CodeAlta.Credentials.Providers;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Credentials.Tests;

/// <summary>Several secrets of a KeePass database are read with one opening of it, and only where the export gives what show prints.</summary>
[TestClass]
public sealed class KeePassXcBatchReadTests
{
    private const string DatabasePassword = "kdbx-password-0001";

    // What keepassxc-cli 2.8.0 wrote for a database made with it (its Meta and the times of its entries shortened): a root entry
    // changed once, a group with an entry whose texts need escaping, an entry of placeholders, and a group in the group.
    private const string Export = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <KeePassFile>
        	<Meta>
        		<Generator>KeePassXC</Generator>
        		<DatabaseName>Passwords</DatabaseName>
        		<MemoryProtection>
        			<ProtectPassword>True</ProtectPassword>
        		</MemoryProtection>
        		<CustomData/>
        	</Meta>
        	<Root>
        		<Group>
        			<UUID>56SiJWFMRxCzXoLSViT3mQ==</UUID>
        			<Name>Passwords</Name>
        			<Notes/>
        			<Times>
        				<Expires>False</Expires>
        			</Times>
        			<Entry>
        				<UUID>rTgP719fTnSGwAF0r0D1zg==</UUID>
        				<Tags/>
        				<Times>
        					<Expires>False</Expires>
        				</Times>
        				<String>
        					<Key>Notes</Key>
        					<Value/>
        				</String>
        				<String>
        					<Key>Password</Key>
        					<Value ProtectInMemory="True">fake-root-secret-2</Value>
        				</String>
        				<String>
        					<Key>Title</Key>
        					<Value>Top</Value>
        				</String>
        				<String>
        					<Key>URL</Key>
        					<Value/>
        				</String>
        				<String>
        					<Key>UserName</Key>
        					<Value>root-user</Value>
        				</String>
        				<AutoType>
        					<Enabled>True</Enabled>
        					<DefaultSequence/>
        				</AutoType>
        				<History>
        					<Entry>
        						<UUID>rTgP719fTnSGwAF0r0D1zg==</UUID>
        						<String>
        							<Key>Password</Key>
        							<Value ProtectInMemory="True">fake-root-secret</Value>
        						</String>
        						<String>
        							<Key>Title</Key>
        							<Value>Top</Value>
        						</String>
        					</Entry>
        				</History>
        			</Entry>
        			<Group>
        				<UUID>jmriErfsReOF995rRxHtlQ==</UUID>
        				<Name>Work</Name>
        				<Notes/>
        				<Entry>
        					<UUID>LKpjDyVjS/qdeoWJGTL12A==</UUID>
        					<String>
        						<Key>Notes</Key>
        						<Value>line one
        line two</Value>
        					</String>
        					<String>
        						<Key>Password</Key>
        						<Value ProtectInMemory="True">fake &lt;&amp;&gt; &quot;quoted&quot; 'value' é</Value>
        					</String>
        					<String>
        						<Key>Title</Key>
        						<Value>Special &amp; &lt;chars&gt;</Value>
        					</String>
        					<String>
        						<Key>URL</Key>
        						<Value>https://example.com/?a=1&amp;b=2</Value>
        					</String>
        					<String>
        						<Key>UserName</Key>
        						<Value>special</Value>
        					</String>
        					<History/>
        				</Entry>
        				<Entry>
        					<UUID>ZAh5saDOSGe3JrRxAtROUg==</UUID>
        					<String>
        						<Key>Password</Key>
        						<Value ProtectInMemory="True">{REF:P@T:Top}</Value>
        					</String>
        					<String>
        						<Key>Title</Key>
        						<Value>Reference</Value>
        					</String>
        					<String>
        						<Key>UserName</Key>
        						<Value>{USERNAME}</Value>
        					</String>
        					<History/>
        				</Entry>
        				<Group>
        					<UUID>KQjA9sz5SbOnFMduuOZ3+g==</UUID>
        					<Name>Deep</Name>
        					<Notes/>
        					<Entry>
        						<UUID>46iBsC1sRLek6rqMSaEf/A==</UUID>
        						<String>
        							<Key>Password</Key>
        							<Value ProtectInMemory="True">fake-deep-secret</Value>
        						</String>
        						<String>
        							<Key>Title</Key>
        							<Value>Leaf</Value>
        						</String>
        						<History/>
        					</Entry>
        					<Entry>
        						<UUID>46iBsC1sRLek6rqMSaEf/B==</UUID>
        						<String>
        							<Key>Password</Key>
        							<Value ProtectInMemory="True">fake-second-leaf</Value>
        						</String>
        						<String>
        							<Key>Title</Key>
        							<Value>Leaf</Value>
        						</String>
        						<History/>
        					</Entry>
        				</Group>
        			</Group>
        		</Group>
        		<DeletedObjects/>
        	</Root>
        </KeePassFile>
        """;

    [TestMethod]
    public void The_export_gives_an_attribute_only_where_show_would_print_the_same()
    {
        string? Value(string reference)
        {
            var (entry, attribute) = KeePassXcProvider.Split(reference);
            var values = KeePassXcExport.Values(Encoding.UTF8.GetBytes(Export.ReplaceLineEndings("\r\n")), [(entry, attribute)]);
            Assert.IsNotNull(values, "the export is read");
            return values[0];
        }

        Assert.AreEqual("fake-root-secret-2", Value("Top#Password"), "the entry as it is now, never an older version of its history");
        Assert.AreEqual("fake-root-secret-2", Value("/Top"));
        Assert.AreEqual("root-user", Value("Top#UserName"));
        Assert.AreEqual("Top", Value("Top#Title"));
        Assert.AreEqual("fake <&> \"quoted\" 'value' é", Value("Work/Special & <chars>#Password"), "the references of XML are decoded");
        Assert.AreEqual("https://example.com/?a=1&b=2", Value("Work/Special & <chars>#URL"));
        Assert.AreEqual("fake-deep-secret", Value("Work/Deep/Leaf#Password"), "the first entry of that path, as show reads it");
        Assert.AreEqual("fake-deep-secret", Value("Leaf#Password"), "a title alone is searched in every group");
        // What show prints differently, or says itself: left to show.
        Assert.IsNull(Value("Work/Reference#Password"), "show resolves a reference to another entry");
        Assert.IsNull(Value("Work/Reference#UserName"), "show resolves a placeholder");
        Assert.IsNull(Value("Work/Special & <chars>#Notes"), "a value of several lines");
        Assert.IsNull(Value("Top#password"), "show finds an attribute whatever its case");
        Assert.IsNull(Value("Top#URL"), "an empty value");
        Assert.IsNull(Value("Top#Nothing"));
        Assert.IsNull(Value("Top#Tags"), "a field of the entry for show, not an attribute");
        Assert.IsNull(Value("Work/Nothing#Password"));
        Assert.IsNull(Value("work/Deep/Leaf#Password"), "a path is the names of the groups as they are written");
        Assert.IsNull(Value("Deep/Leaf#Password"), "a path that names a group starts at the root");
        Assert.IsNull(Value("Passwords/Top#Password"), "the root group is not part of a path");
    }

    [TestMethod]
    public void What_is_not_the_whole_export_of_a_database_gives_nothing()
    {
        (string, string)?[] wanted = [("Top", "Password")];
        var export = Encoding.UTF8.GetBytes(Export);
        Assert.IsNull(KeePassXcExport.Values(export.AsSpan(0, export.Length / 2), wanted), "an export cut at the limit of what is read");
        Assert.IsNull(KeePassXcExport.Values(export.AsSpan(0, export.Length - 20), wanted));
        Assert.IsNull(KeePassXcExport.Values("Invalid command export."u8, wanted));
        Assert.IsNull(KeePassXcExport.Values(""u8, wanted));
        Assert.IsNull(KeePassXcExport.Values("<a><b></a></b>"u8, wanted));
        Assert.IsNull(KeePassXcExport.Values(Encoding.UTF8.GetBytes(Export.Replace("fake-root-secret-2", "<![CDATA[x]]>")), wanted), "a form keepassxc-cli does not write");
        Assert.IsNull(KeePassXcExport.Values(Encoding.UTF8.GetBytes(Export.Replace("Special &amp;", "Special &nope;")), wanted), "a reference XML does not have");
        Assert.IsNotNull(KeePassXcExport.Values(export, wanted));
    }

    [TestMethod]
    public async Task Several_secrets_of_a_database_are_read_with_one_opening_and_show_reads_what_the_export_cannot_give()
    {
        using var vault = NewVault();
        var provider = await UnlockedAsync(vault);
        var before = vault.Calls().Count;
        string[] references =
        [
            "Work/GitHub#Password", "Work/GitHub#UserName", "Root entry#Password", "Work/Deep/Mail", "Work/GitHub#totp", "Work/Reference#Password",
            "Work/GitHub#password", "Work/Nothing#Password", "Work/GitHub#Nothing", "#",
        ];

        var read = await ((ICredentialBatchReader)provider).ReadManyAsync(references, interactive: false, CancellationToken.None);

        CollectionAssert.AreEqual(
            new[] { "kp-password-value-111", "kp-user-value", "kp-root-value", "kp-deep-value-333", "123456", "{REF:P@T:Root entry}", "kp-password-value-111", null, null, null },
            read.Select(static result => result.Value?.Value).ToArray());
        Assert.IsNotNull(read[4].Value!.ExpiresAt, "a one-time code expires");
        CollectionAssert.AreEqual(new CredentialFailure?[] { CredentialFailure.NotFound, CredentialFailure.NotFound, CredentialFailure.NotFound },
            read.Skip(7).Select(static result => result.Failure?.Failure).ToArray());
        var calls = vault.Calls().Skip(before).ToArray();
        Assert.AreEqual(1, calls.Count(static call => call.StartsWith("export -q -f xml ", StringComparison.Ordinal)), string.Join(" | ", calls));
        // The code, the placeholder, the attribute in another case, and what the database does not have: show answers for them.
        CollectionAssert.AreEquivalent(new[] { "Work/GitHub", "Work/Reference", "Work/GitHub", "Work/Nothing", "Work/GitHub" },
            calls.Where(static call => call.StartsWith("show ", StringComparison.Ordinal)).Select(static call => call[(call.LastIndexOf(".kdbx ", StringComparison.Ordinal) + 6)..]).ToArray());
        ProviderHarness.AssertNoArgumentHolds(vault, DatabasePassword);
    }

    [TestMethod]
    public async Task One_secret_and_a_keepassxc_cli_without_export_are_read_with_show()
    {
        using var vault = NewVault();
        var provider = (ICredentialBatchReader)await UnlockedAsync(vault);
        var one = await provider.ReadManyAsync(["Work/GitHub#Password"], interactive: false, CancellationToken.None);
        Assert.AreEqual("kp-password-value-111", one[0].Value!.Value);
        Assert.IsFalse(vault.Calls().Any(static call => call.StartsWith("export", StringComparison.Ordinal)), "one opening either way: show answers");

        vault.State["keepassxc"]!["exportUnknown"] = true;
        vault.Save();
        var read = await provider.ReadManyAsync(["Work/GitHub#Password", "Root entry#Password"], interactive: false, CancellationToken.None);
        CollectionAssert.AreEqual(new[] { "kp-password-value-111", "kp-root-value" }, read.Select(static result => result.Value?.Value).ToArray());
    }

    [TestMethod]
    public async Task A_database_whose_password_changed_is_locked_by_the_export_and_nothing_else_is_run()
    {
        using var vault = NewVault();
        var provider = await UnlockedAsync(vault);
        vault.State["keepassxc"]!["password"] = "changed";
        vault.Save();
        var before = vault.Calls().Count;

        var read = await ((ICredentialBatchReader)provider).ReadManyAsync(["Work/GitHub#Password", "Root entry#Password", "Work/GitHub#totp"], interactive: false, CancellationToken.None);

        Assert.IsTrue(read.All(static result => result.Failure?.Failure == CredentialFailure.Locked));
        Assert.AreEqual(1, vault.Calls().Count - before, "the export met the refusal: the password held is forgotten, and no show is run");
        Assert.AreEqual(CredentialProviderState.Locked, (await provider.GetStatusAsync(CancellationToken.None)).State);
    }

    /// <summary>
    /// Runs when a real keepassxc-cli is found (on the PATH, beside KeePassXC, or named by CODEALTA_TEST_KEEPASSXC_CLI): a database
    /// is made with it, and what the export gives for each reference is what show prints for it.
    /// </summary>
    [TestMethod]
    public async Task With_a_real_keepassxc_cli_the_export_gives_what_show_prints()
    {
        var cli = Environment.GetEnvironmentVariable("CODEALTA_TEST_KEEPASSXC_CLI") is { Length: > 0 } named ? named : VaultCommandRunner.FindProgram("keepassxc-cli", null);
        if (cli is null || !File.Exists(cli)) Assert.Inconclusive("No keepassxc-cli on this computer.");
        var folder = Path.Combine(Path.GetTempPath(), "codealta-keepassxc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var database = Path.Combine(folder, "Real.kdbx");
            var runner = new VaultCommandRunner(folder);
            // Qt shows what it says of a command line in a dialog when the program has no console: never here.
            var quiet = new Dictionary<string, string> { ["QT_COMMAND_LINE_PARSER_NO_GUI_MESSAGE_BOXES"] = "1" };
            async Task RunAsync(string input, params string[] arguments)
            {
                var result = await runner.RunAsync(cli, arguments, new VaultCommandOptions { Input = input, Environment = quiet, Timeout = TimeSpan.FromSeconds(60) }, CancellationToken.None);
                Assert.AreEqual(0, result.ExitCode, $"{arguments[0]}: {result.Error}");
            }

            const string Master = "real-master-password-1";
            await RunAsync($"{Master}\n{Master}\n", "db-create", "-q", "--set-password", "-t", "100", database);
            await RunAsync($"{Master}\n", "mkdir", "-q", database, "Work");
            await RunAsync($"{Master}\n", "mkdir", "-q", database, "Work/Deep");
            await RunAsync($"{Master}\nfake-root-secret\n", "add", "-q", "-u", "root-user", "-p", database, "Top");
            await RunAsync($"{Master}\nfake <&> \"quoted\" 'value'\n", "add", "-q", "-u", "special", "--url", "https://example.com/?a=1&b=2", "--notes", "line one\nline two", "-p", database, "Work/Special & <chars>");
            await RunAsync($"{Master}\nfake-deep-secret\n", "add", "-q", "-u", "deep-user", "-p", database, "Work/Deep/Leaf");
            await RunAsync($"{Master}\n{{REF:P@T:Top}}\n", "add", "-q", "-u", "{USERNAME}", "-p", database, "Work/Reference");
            // A change keeps the older version in the history of the entry.
            await RunAsync($"{Master}\nfake-root-secret-2\n", "edit", "-q", "-p", database, "Top");

            var provider = ProviderHarness.Create(KeePassXcProvider.Contribution(runner), new Dictionary<string, object?> { ["path"] = cli, ["database"] = database });
            Assert.AreEqual(CredentialProviderState.Ready, (await provider.UnlockAsync(new CredentialUnlockContext { Password = Master }, CancellationToken.None)).State);
            string[] references =
            [
                "Top#Password", "/Top#Password", "Top#UserName", "Top#Title", "Leaf#Password", "Work/Deep/Leaf#Password", "Work/Deep/Leaf#UserName",
                "Work/Special & <chars>#Password", "Work/Special & <chars>#URL", "Work/Special & <chars>#Notes", "Work/Reference#Password", "Work/Reference#UserName",
                "Top#password", "Top#URL", "Top#Nothing", "Work/Nothing#Password", "work/Deep/Leaf#Password",
            ];

            var many = await ((ICredentialBatchReader)provider).ReadManyAsync(references, interactive: false, CancellationToken.None);

            for (var index = 0; index < references.Length; index++)
            {
                string? shown = null;
                CredentialFailure? failure = null;
                try
                {
                    shown = (await provider.ReadAsync(references[index], interactive: false, CancellationToken.None)).Value;
                }
                catch (CredentialProviderException exception)
                {
                    failure = exception.Failure;
                }

                Assert.AreEqual((shown, failure), (many[index].Value?.Value, many[index].Failure?.Failure), references[index]);
            }

            Assert.AreEqual("fake-root-secret-2", many[0].Value!.Value);
            Assert.AreEqual("fake-root-secret-2", many[10].Value!.Value, "show resolved the reference to the other entry");

            // The export itself answers for the plain attributes, so that they cost one opening of the database.
            var export = await runner.RunAsync(cli, ["export", "-q", "-f", "xml", database], new VaultCommandOptions { Input = Master + "\n", Environment = quiet, Timeout = TimeSpan.FromSeconds(60) }, CancellationToken.None);
            Assert.AreEqual(0, export.ExitCode, export.Error);
            var values = KeePassXcExport.Values(export.Output, [.. references.Select(static reference => ((string, string)?)KeePassXcProvider.Split(reference))]);
            Assert.IsNotNull(values, "the export of this keepassxc-cli is read");
            CollectionAssert.AreEqual(
                new[] { "fake-root-secret-2", "fake-root-secret-2", "root-user", "Top", "fake-deep-secret", "fake-deep-secret", "deep-user", "fake <&> \"quoted\" 'value'", "https://example.com/?a=1&b=2", null, null, null, null, null, null, null, null },
                values);
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
                // Cleaned by the system later.
            }
        }
    }

    private static FakeVault NewVault()
    {
        var vault = new FakeVault("keepassxc-cli");
        File.WriteAllText(Database(vault), "not a real database");
        static JsonObject Entry(string path, string password, bool totp = false) => new()
        {
            ["path"] = path,
            ["totp"] = totp,
            ["attributes"] = new JsonObject { ["Title"] = path.Split('/')[^1], ["Password"] = password, ["UserName"] = "kp-user-value", ["URL"] = "https://kp.example.com", ["Notes"] = "kp-note-value-222" },
        };
        var changed = Entry("Root entry", "kp-root-value");
        changed["history"] = new JsonArray(new JsonObject { ["Title"] = "Root entry", ["Password"] = "kp-older-root-value" });
        vault.State["keepassxc"] = new JsonObject
        {
            ["password"] = DatabasePassword,
            ["groups"] = new JsonArray("Work/", "Work/Deep/", "Old/[empty]"),
            ["entries"] = new JsonArray(Entry("Work/GitHub", "kp-password-value-111", totp: true), changed, Entry("Work/Deep/Mail", "kp-deep-value-333"),
                Entry("Work/Reference", "{REF:P@T:Root entry}")),
        };
        vault.Save();
        return vault;
    }

    private static string Database(FakeVault vault) => Path.Combine(vault.Root, "Passwords.kdbx");

    private static async Task<ICredentialProvider> UnlockedAsync(FakeVault vault)
    {
        var provider = ProviderHarness.Create(KeePassXcProvider.Contribution(new VaultCommandRunner()), new Dictionary<string, object?> { ["path"] = vault.ProgramPath, ["database"] = Database(vault) });
        Assert.AreEqual(CredentialProviderState.Ready, (await provider.UnlockAsync(new CredentialUnlockContext { Password = DatabasePassword }, CancellationToken.None)).State);
        return provider;
    }
}
