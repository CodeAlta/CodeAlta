using CodeAlta.Catalog.Skills;

namespace CodeAlta.Catalog.Tests;

[TestClass]
public sealed class SkillManagementServiceTests
{
    [TestMethod]
    public async Task AListingForDisplay_NamesTheConfigurationFilesItCouldNotRead_AndListsTheSkillsAsIfTheyDisabledNone()
    {
        var root = Directory.CreateTempSubdirectory("codealta-skill-listing-").FullName;
        try
        {
            var global = Directory.CreateDirectory(Path.Combine(root, "global")).FullName;
            var project = Directory.CreateDirectory(Path.Combine(root, "project")).FullName;
            foreach (var (folder, name) in new[] { (Path.Combine(global, "skills"), "alpha"), (Path.Combine(project, ".alta", "skills"), "beta") })
            {
                File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(folder, name)).FullName, "SKILL.md"), $"---\nname: {name}\ndescription: The {name} workflow.\n---\n\n# {name}\n");
            }

            var service = new SkillManagementService(new SkillCatalog([new ProjectCodeAltaSkillRootProvider(), new UserCodeAltaSkillRootProvider()]), global, null);
            var globalConfig = Path.Combine(global, "config.toml");
            var projectConfig = Path.Combine(project, ".alta", "config.toml");
            new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = global }).SaveGlobalDisabledSkillNames(["alpha"]);

            // Every file is read: what it disables is disabled, and nothing is named.
            var listing = await service.LoadListingAsync(SkillListingScope.Combined, project);
            Assert.IsEmpty(listing.Problems);
            Assert.IsFalse(listing.Skills.Single(static skill => skill.Name == "alpha").IsEnabled);
            Assert.IsTrue(listing.Skills.Single(static skill => skill.Name == "beta").IsEnabled);

            // The file of the project does not parse: it is named, and the file of the user still applies.
            File.WriteAllText(projectConfig, "[skills\nbroken");
            listing = await service.LoadListingAsync(SkillListingScope.Combined, project);
            var problem = listing.Problems.Single();
            Assert.AreEqual((true, projectConfig), (problem.IsProject, problem.Path));
            Assert.IsFalse(string.IsNullOrWhiteSpace(problem.Message));
            Assert.HasCount(2, listing.Skills);
            Assert.IsFalse(listing.Skills.Single(static skill => skill.Name == "alpha").IsEnabled);

            // The file of the user does not parse: what it disabled is not known, and is listed as enabled.
            File.WriteAllText(globalConfig, "[skills\nbroken");
            listing = await service.LoadListingAsync(SkillListingScope.Combined, project);
            CollectionAssert.AreEqual(new[] { (false, globalConfig), (true, projectConfig) }, listing.Problems.Select(static item => (item.IsProject, item.Path)).ToArray());
            Assert.IsTrue(listing.Skills.All(static skill => skill.IsEnabled));
            Assert.IsFalse((await service.LoadListingAsync(SkillListingScope.User, null)).Problems.Single().IsProject);

            // What decides the skills a model is offered does not guess: it still refuses the file.
            Assert.ThrowsExactly<InvalidDataException>(() => { _ = service.LoadAsync(SkillListingScope.Combined, project); });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task CreationPreservesUnicodeTemplateAndConventions()
    {
        using var fixture = new Fixture();
        var result = await fixture.Service.CreateSkillAsync(SkillCreationTargetKind.UserCodeAlta, null, "日本語-école", "  User's workflow\nsecond line  ");
        Assert.AreEqual(Path.Combine(fixture.Root, "skills", "日本語-école"), result.SkillRootPath);
        var contents = File.ReadAllText(result.SkillFilePath);
        StringAssert.Contains(contents, "name: 日本語-école");
        StringAssert.Contains(contents, "description: 'User''s workflow second line'");
        StringAssert.Contains(contents, "do not execute scripts automatically");
        CollectionAssert.AreEquivalent(new[] { "scripts", "references", "assets" }, Directory.GetDirectories(result.SkillRootPath).Select(Path.GetFileName).ToArray());
        Assert.AreEqual(0, Directory.GetDirectories(Path.GetDirectoryName(result.SkillRootPath)!, ".skill-staging-*").Length);
    }

    [TestMethod]
    [DataRow("Valid literal \uFFFD description")]
    [DataRow("Valid supplementary \U0001F680 description")]
    public async Task ValidUnicodeScalarDescriptionsArePreserved(string description)
    {
        using var fixture = new Fixture();
        var result = await fixture.Service.CreateSkillAsync(SkillCreationTargetKind.UserCodeAlta, null, "sample", description);

        StringAssert.Contains(File.ReadAllText(result.SkillFilePath), $"description: '{description}'");
        Assert.AreEqual(3, Directory.GetDirectories(result.SkillRootPath).Length);
        Assert.AreEqual(0, Directory.GetDirectories(Path.GetDirectoryName(result.SkillRootPath)!, ".skill-staging-*").Length);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task LoneSurrogateDescriptionsAreRejectedBeforeMutation(bool highSurrogate)
    {
        using var fixture = new Fixture();
        // Construct at runtime: attribute string encoding must not replace the malformed UTF-16 first.
        var description = "Invalid " + (highSurrogate ? '\uD800' : '\uDC00') + " description";
        var entriesBefore = Directory.GetFileSystemEntries(fixture.Root);

        await Assert.ThrowsExactlyAsync<ArgumentException>(() => fixture.Service.CreateSkillAsync(
            SkillCreationTargetKind.UserCodeAlta, null, "sample", description));

        CollectionAssert.AreEquivalent(entriesBefore, Directory.GetFileSystemEntries(fixture.Root));
        Assert.IsFalse(Directory.Exists(Path.Combine(fixture.Root, "skills")));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("../escape")]
    [DataRow("a/b")]
    [DataRow("a\\b")]
    [DataRow("a:b")]
    [DataRow("con")]
    [DataRow("nul")]
    [DataRow("com1")]
    [DataRow("lpt²")]
    [DataRow("BadName")]
    [DataRow("bad--name")]
    public async Task InvalidNamesDoNotMutate(string name)
    {
        using var fixture = new Fixture();
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.CreateSkillAsync(SkillCreationTargetKind.UserCodeAlta, null, name, "Description"));
        Assert.IsFalse(Directory.Exists(Path.Combine(fixture.Root, "skills")));
    }

    [TestMethod]
    public async Task InvalidDescriptionAndTargetDoNotMutate()
    {
        using var fixture = new Fixture();
        foreach (var description in new[] { "", "\r\n", "bad\0text", "bad\u2028text", new string('a', 1025) })
        {
            await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.CreateSkillAsync(SkillCreationTargetKind.UserCodeAlta, null, "sample", description));
        }

        // The API intentionally has no built-in creation target.
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => fixture.Service.CreateSkillAsync((SkillCreationTargetKind)99, null, "sample", "Description"));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fixture.Service.CreateSkillAsync(SkillCreationTargetKind.ProjectCodeAlta, null, "sample", "Description"));
        Assert.IsFalse(Directory.Exists(Path.Combine(fixture.Root, "skills")));
    }

    [TestMethod]
    public async Task RelativeTraversalAndUnavailableRootsAreRejected()
    {
        using var fixture = new Fixture();
        foreach (var root in new[] { "relative", Path.Combine(fixture.Root, "..", "escape"), Path.Combine(fixture.Root, "bad*root") })
        {
            await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.CreateSkillAsync(SkillCreationTargetKind.ProjectCodeAlta, root, "sample", "Description"));
        }

        await Assert.ThrowsExactlyAsync<DirectoryNotFoundException>(() => fixture.Service.CreateSkillAsync(SkillCreationTargetKind.ProjectCodeAlta,
            Path.Combine(fixture.Root, "missing"), "sample", "Description"));
        Assert.AreEqual(1, Directory.GetDirectories(fixture.Root).Length); // Owned .git boundary only.
    }

    [TestMethod]
    public async Task ExistingFilesAndDirectoriesAreNeverReplaced()
    {
        using var fixture = new Fixture();
        var skills = Directory.CreateDirectory(Path.Combine(fixture.Root, "skills")).FullName;
        File.WriteAllText(Path.Combine(skills, "sample"), "sentinel");
        await Assert.ThrowsAsync<IOException>(() => fixture.Service.CreateSkillAsync(SkillCreationTargetKind.UserCodeAlta, null, "sample", "Description"));
        Assert.AreEqual("sentinel", File.ReadAllText(Path.Combine(skills, "sample")));
        Directory.CreateDirectory(Path.Combine(skills, "empty"));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fixture.Service.CreateSkillAsync(SkillCreationTargetKind.UserCodeAlta, null, "empty", "Description"));
        Assert.AreEqual(0, Directory.GetFileSystemEntries(Path.Combine(skills, "empty")).Length);
        Assert.AreEqual(0, Directory.GetDirectories(skills, ".skill-staging-*").Length);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LinkedCreationComponentsAndDanglingDestinationsAreRejected(bool dangling)
    {
        using var fixture = new Fixture();
        var target = Path.Combine(fixture.Root, "target");
        if (!dangling) Directory.CreateDirectory(target);
        var skills = Directory.CreateDirectory(Path.Combine(fixture.Root, "skills")).FullName;
        var link = Path.Combine(skills, "sample");
        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
        {
            Assert.Inconclusive($"Symbolic-link creation unavailable: {ex.GetType().Name}");
        }

        await Assert.ThrowsAsync<IOException>(() => fixture.Service.CreateSkillAsync(SkillCreationTargetKind.UserCodeAlta, null, "sample", "Description"));
        Assert.IsNotNull(new DirectoryInfo(link).LinkTarget);
        Assert.AreEqual(!dangling, Directory.Exists(target));
        if (!dangling)
        {
            var linkedService = new SkillManagementService(Fixture.CreateCatalog(), link, null);
            await Assert.ThrowsAsync<IOException>(() => linkedService.CreateSkillAsync(SkillCreationTargetKind.UserCodeAlta, null, "child", "Description"));
            Assert.AreEqual(0, Directory.GetFileSystemEntries(target).Length);
        }

        // On Unix a link whose target is missing is not a directory: Directory.Delete does not find it.
        if (dangling && !OperatingSystem.IsWindows()) File.Delete(link);
        else Directory.Delete(link);
    }

    [TestMethod]
    public async Task CanceledCreationLeavesNoFinalOrStagingDirectory()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.Service.CreateSkillAsync(SkillCreationTargetKind.UserCodeAlta, null, "sample", "Description", cancellation.Token));
        Assert.IsFalse(Directory.Exists(Path.Combine(fixture.Root, "skills")));
    }

    [TestMethod]
    public async Task ConcurrentCreationHasOneWinnerAndKeepsItsCompleteContents()
    {
        using var fixture = new Fixture();
        async Task<bool> Create(string description)
        {
            try
            {
                await fixture.Service.CreateSkillAsync(SkillCreationTargetKind.UserCodeAlta, null, "sample", description);
                return true;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                return false;
            }
        }

        var winners = await Task.WhenAll(Task.Run(() => Create("First")), Task.Run(() => Create("Second")));
        Assert.AreEqual(1, winners.Count(static winner => winner));
        var root = Path.Combine(fixture.Root, "skills", "sample");
        Assert.IsTrue(File.Exists(Path.Combine(root, "SKILL.md")));
        Assert.AreEqual(3, Directory.GetDirectories(root).Length);
        Assert.AreEqual(0, Directory.GetDirectories(Path.GetDirectoryName(root)!, ".skill-staging-*").Length);
    }

    [TestMethod]
    public async Task AllEnablementInputsAreValidatedBeforeAnyWrite()
    {
        using var fixture = new Fixture();
        var project = Directory.CreateDirectory(Path.Combine(fixture.Root, "project")).FullName;
        var config = Path.Combine(fixture.Root, "config.toml");
        File.WriteAllText(config, "# sentinel\nunknown = 42\n");
        foreach (var names in new IReadOnlyList<string>[] { ["sample"], [] })
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => fixture.Service.SetSkillsEnabled(SkillEnablementScope.Both, null, names, false));
            Assert.ThrowsExactly<InvalidOperationException>(() => fixture.Service.InvertSkillsEnabled(SkillEnablementScope.Both, null, names));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => fixture.Service.SetSkillsEnabled((SkillEnablementScope)99, project, names, false));
        }

        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.LoadAsync((SkillListingScope)99, project));
        Assert.Throws<ArgumentException>(() => fixture.Service.SetSkillsEnabled(SkillEnablementScope.Both, project, ["sample", "../bad"], false));
        Assert.Throws<ArgumentException>(() => fixture.Service.SetSkillsEnabled(SkillEnablementScope.Both, "relative", ["sample"], false));
        Assert.Throws<DirectoryNotFoundException>(() => fixture.Service.SetSkillsEnabled(SkillEnablementScope.Both, Path.Combine(project, "missing"), ["sample"], false));
        Assert.AreEqual("# sentinel\nunknown = 42\n", File.ReadAllText(config));
        Assert.IsFalse(Directory.Exists(Path.Combine(project, ".alta")));
    }

    [TestMethod]
    public void EnablementPreservesCommentsUnknownFieldsAndPerScopeInvert()
    {
        using var fixture = new Fixture();
        var project = Directory.CreateDirectory(Path.Combine(fixture.Root, "project")).FullName;
        var globalConfig = Path.Combine(fixture.Root, "config.toml");
        var projectConfig = Path.Combine(Directory.CreateDirectory(Path.Combine(project, ".alta")).FullName, "config.toml");
        const string original = "# keep comment\ncustom = 42\n[skills]\n# keep skills\nfuture = 'value'\ndisabled = ['other'] # keep trailing\n";
        File.WriteAllText(globalConfig, original);
        File.WriteAllText(projectConfig, original);
        var disabled = fixture.Service.SetSkillsEnabled(SkillEnablementScope.Both, project, ["Sample", "sample", "second"], false);
        Assert.AreEqual(new SkillEnablementUpdateResult(2, 2), disabled);
        var inverted = fixture.Service.InvertSkillsEnabled(SkillEnablementScope.Project, project, ["sample", "third"]);
        Assert.AreEqual(new SkillEnablementUpdateResult(0, 2), inverted);
        Assert.AreEqual(new SkillEnablementUpdateResult(1, 0), fixture.Service.SetSkillEnabled(SkillEnablementScope.Global, null, "second", true));
        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = fixture.Root });
        CollectionAssert.AreEquivalent(new[] { "other", "sample" }, store.LoadGlobalDisabledSkillNames().ToArray());
        CollectionAssert.AreEquivalent(new[] { "other", "second", "third" }, store.LoadProjectDisabledSkillNames(project).ToArray());
        foreach (var file in new[] { globalConfig, projectConfig })
        {
            var content = File.ReadAllText(file);
            foreach (var preserved in new[] { "# keep comment", "custom = 42", "# keep skills", "future = 'value'", "# keep trailing" })
            {
                StringAssert.Contains(content, preserved);
            }
        }
    }

    [TestMethod]
    public async Task ExplicitHomeQueryDoesNotInventDefaultDiscovery()
    {
        using var fixture = new Fixture();
        var provider = new CapturingProvider();
        var service = new SkillManagementService(new SkillCatalog([provider]), fixture.Root, fixture.Root);
        await service.LoadAsync(SkillListingScope.Combined, fixture.Root);
        Assert.IsNotNull(provider.Context);
        Assert.AreEqual(fixture.Root, provider.Context.UserProfileRoot);
        Assert.AreEqual(fixture.Root, provider.Context.UserCodeAltaRoot);
        CollectionAssert.AreEqual(new[] { fixture.Root }, provider.Context.ProjectRoots.ToArray());
        var withoutHome = new SkillManagementService(new SkillCatalog([provider]), fixture.Root, null);
        await withoutHome.LoadAsync(SkillListingScope.User, null);
        Assert.IsNull(provider.Context.UserProfileRoot);
    }

    [TestMethod]
    [DataRow("# keep\n[skills]\nfuture = 42\n")]
    [DataRow("# keep\n[skills]")]
    [DataRow("# keep\nskills.disabled = ['old'] # trailing\nskills.future = 42\n")]
    [DataRow("# keep\nskills = { disabled = ['old'], future = 42 }\n")]
    [DataRow("# keep\nskills = { future = 42 }\n")]
    [DataRow("# keep\nskills = {}\n")]
    [DataRow("# keep\nskills.future = 42\n")]
    [DataRow("# keep\n[skills.future]\nvalue = 42\n")]
    [DataRow("# keep\n[skills]\ndisabled = [\n # before\n 'old', # after\n 'other',\n] # trailing\n")]
    [DataRow("# keep\n'skills.disabled' = 42\n[skills]\n'disabled' = []\n")]
    public void ConfigOwnerEditsOnlySkillDisabledTokens(string original)
    {
        using var fixture = new Fixture();
        var config = Path.Combine(fixture.Root, "config.toml");
        File.WriteAllText(config, original);
        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = fixture.Root });
        store.SaveGlobalDisabledSkillNames(["sample", "second"]);
        CollectionAssert.AreEquivalent(new[] { "sample", "second" }, store.LoadGlobalDisabledSkillNames().ToArray());
        store.SaveGlobalDisabledSkillNames([]);
        Assert.AreEqual(0, store.LoadGlobalDisabledSkillNames().Count);
        var content = File.ReadAllText(config);
        foreach (var comment in new[] { "# keep", "# before", "# after", "# trailing" })
        {
            if (original.Contains(comment, StringComparison.Ordinal)) StringAssert.Contains(content, comment);
        }

        if (original.Contains("future = 42", StringComparison.Ordinal)) StringAssert.Contains(content, "future = 42");
    }

    [TestMethod]
    public void InvalidConfigIsNotRepairedOrOverwritten()
    {
        using var fixture = new Fixture();
        var config = Path.Combine(fixture.Root, "config.toml");
        const string content = "[skills]\ndisabled = [broken";
        File.WriteAllText(config, content);
        Assert.Throws<InvalidDataException>(() => fixture.Service.SetSkillEnabled(SkillEnablementScope.Global, null, "sample", false));
        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = fixture.Root });
        Assert.Throws<InvalidDataException>(() => store.SaveGlobalDisabledSkillNames(["sample"]));
        Assert.AreEqual(content, File.ReadAllText(config));
    }

    [TestMethod]
    public void ConfigOwnerPreservesUnicodeCrLfAndUnrelatedProviderTable()
    {
        using var fixture = new Fixture();
        var config = Path.Combine(fixture.Root, "config.toml");
        const string prefix = "# caf\u00E9\r\ncustom = '\u65E5\u672C\u8A9E'\r\n[skills]\r\n# skill comment\r\ndisabled = ";
        const string suffix = " # trailing comment\r\nfuture = '\u00E9clair'\r\n\r\n" +
            "# unrelated provider \U0001F680\r\n[providers.fixture]\r\nenabled = false # keep disabled\r\n" +
            "type = 'openai-responses'\r\ndisplay_name = '\u30D7\u30ED\u30D0\u30A4\u30C0\u30FC \U0001F680'\r\n" +
            "model = 'fixture-model'\r\nfuture_option = '\u00C9clair' # unknown provider field\r\n";
        File.WriteAllText(config, prefix + "['ancien']" + suffix);
        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = fixture.Root });

        foreach (var names in new string[][] { ["\u00E9cole", "\u65E5\u672C\u8A9E"], [] })
        {
            store.SaveGlobalDisabledSkillNames(names);

            CollectionAssert.AreEquivalent(names, store.LoadGlobalDisabledSkillNames().ToArray());
            var content = File.ReadAllText(config);
            Assert.IsTrue(content.StartsWith(prefix, StringComparison.Ordinal));
            Assert.IsTrue(content.EndsWith(suffix, StringComparison.Ordinal));
            var withoutCrLf = content.Replace("\r\n", string.Empty, StringComparison.Ordinal);
            Assert.IsFalse(withoutCrLf.Contains('\r') || withoutCrLf.Contains('\n'));
        }
    }

    [TestMethod]
    public void ConfigOwnerRejectsValidTomlWithScalarDisabledWithoutMutation()
    {
        using var fixture = new Fixture();
        var config = Path.Combine(fixture.Root, "config.toml");
        const string content = "# valid TOML, incompatible skill settings\r\n[skills]\r\ndisabled = 'sample' # not an array\r\n";
        File.WriteAllText(config, content);
        var before = File.ReadAllBytes(config);
        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = fixture.Root });

        Assert.Throws<InvalidDataException>(() => store.SaveGlobalDisabledSkillNames(["other"]));

        CollectionAssert.AreEqual(before, File.ReadAllBytes(config));
    }

    [TestMethod]
    public void ConfigOwnerReportsReplacementCharacterCommentParserLimitationWithoutMutation()
    {
        using var fixture = new Fixture();
        var config = Path.Combine(fixture.Root, "config.toml");
        // U+FFFD is a valid Unicode scalar; rejection is a current parser compatibility limitation.
        const string content = "# valid literal \uFFFD comment\r\n[skills]\r\ndisabled = ['sample']\r\n";
        File.WriteAllText(config, content);
        var before = File.ReadAllBytes(config);
        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = fixture.Root });

        var exception = Assert.ThrowsExactly<InvalidDataException>(() => store.SaveGlobalDisabledSkillNames(["other"]));

        StringAssert.Contains(exception.GetBaseException().Message, "Invalid UTF-16 surrogate sequence in TOML input.");
        CollectionAssert.AreEqual(before, File.ReadAllBytes(config));
    }

    [TestMethod]
    public async Task RegisteredBuiltInAndPluginDescriptorsRetainManagementProvenance()
    {
        using var fixture = new Fixture();
        var registrations = new List<SkillRootRegistration>();
        foreach (var kind in new[] { SkillSourceKind.Builtin, SkillSourceKind.Plugin })
        {
            var root = Directory.CreateDirectory(Path.Combine(fixture.Root, kind.ToString(), "sample")).FullName;
            File.WriteAllText(Path.Combine(root, "SKILL.md"), "---\nname: sample\ndescription: Description\n---\n# Sample");
            registrations.Add(new SkillRootRegistration
            {
                RootPath = Path.GetDirectoryName(root)!, SourceKind = kind, SourceId = $"fixture:{kind}",
                Scope = kind == SkillSourceKind.Builtin ? SkillScopeKind.Builtin : SkillScopeKind.Plugin,
                Precedence = kind == SkillSourceKind.Builtin ? 4 : 5, IsTrusted = kind == SkillSourceKind.Builtin,
            });
        }

        var service = new SkillManagementService(new SkillCatalog([new RegisteredProvider(registrations)]), fixture.Root, null);
        service.SetSkillEnabled(SkillEnablementScope.Global, null, "sample", false);
        var descriptors = await service.LoadAsync(SkillListingScope.User, null);
        Assert.AreEqual(2, descriptors.Count);
        Assert.AreEqual(SkillSourceKind.Builtin, descriptors[0].SourceKind);
        Assert.AreEqual("fixture:Builtin", descriptors[0].SourceId);
        Assert.AreEqual(SkillSourceKind.Plugin, descriptors[1].SourceKind);
        Assert.IsFalse(descriptors[1].IsTrusted);
        Assert.IsTrue(descriptors[1].IsShadowed);
        Assert.IsTrue(descriptors.All(static descriptor => !descriptor.IsEnabled));
    }

    [TestMethod]
    [DataRow(SkillSourceKind.Builtin)]
    [DataRow(SkillSourceKind.Plugin)]
    [DataRow(SkillSourceKind.UserAlta)]
    [DataRow(SkillSourceKind.ProjectAlta)]
    public async Task SkillDocuments_EnforceCatalogPolicyAndRetainConditionalCodecSaves(SkillSourceKind kind)
    {
        using var fixture = new Fixture();
        var root = Directory.CreateDirectory(Path.Combine(fixture.Root, "registered", "sample")).FullName;
        var path = Path.Combine(root, "SKILL.md");
        File.WriteAllText(path, "---\r\nname: sample\r\ndescription: Description\r\n---\r\n# Sample", new System.Text.UnicodeEncoding(false, true));
        Directory.CreateDirectory(Path.Combine(root, "references"));
        File.WriteAllText(Path.Combine(root, "references", "guide.md"), "guide");
        var service = new SkillManagementService(new SkillCatalog([new RegisteredProvider([new()
        {
            RootPath = Path.GetDirectoryName(root)!, SourceKind = kind, SourceId = "fixture:registered",
            Scope = SkillScopeKind.User, Precedence = 0, IsTrusted = true,
        }])]), fixture.Root, null);
        var codec = new TextFileCodec();
        var documents = new[] { await service.GetFileDocumentAsync(path, null, null), await service.GetFileDocumentAsync(path, "references/guide.md", null) };
        foreach (var document in documents)
        {
            Assert.AreEqual(kind, document.SkillSourceKind);
            Assert.AreEqual("fixture:registered", document.SkillSourceId);
            Assert.AreEqual(kind == SkillSourceKind.Builtin, document.IsReadOnly);
            var snapshot = await codec.LoadAsync(document);
            var before = File.ReadAllBytes(document.FullPath);
            if (document.IsReadOnly)
            {
                await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => codec.SaveAsync(document, "programmatic edits", snapshot, snapshot.Revision));
                await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => codec.SaveAsync(document, "overwrite", snapshot, TextFileRevision.Missing));
                CollectionAssert.AreEqual(before, File.ReadAllBytes(document.FullPath));
            }
            else
            {
                File.WriteAllText(document.FullPath, "external");
                var conflict = await codec.SaveAsync(document, "changed\r\n", snapshot, snapshot.Revision);
                Assert.IsTrue(conflict.IsConflict);
                var saved = await codec.SaveAsync(document, "changed\r\n", snapshot, conflict.CurrentRevision);
                Assert.IsFalse(saved.IsConflict);
                Assert.AreEqual("changed\r\n", (await codec.LoadAsync(document)).Text);
                Assert.AreEqual(snapshot.HasByteOrderMark, saved.Snapshot.HasByteOrderMark);
                Assert.AreEqual(snapshot.Encoding.CodePage, saved.Snapshot.Encoding.CodePage);
            }
        }

        foreach (var malformed in new[] { "", "../SKILL.md", "references/../SKILL.md", "references\\guide.md", "/references/guide.md", "references//guide.md", "assets/missing", "references/guide.md:stream" })
        {
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.GetFileDocumentAsync(path, malformed, null));
        }
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.GetFileDocumentAsync(Path.Combine(fixture.Root, "arbitrary.md"), null, null));
    }

    private sealed class RegisteredProvider(IReadOnlyList<SkillRootRegistration> roots) : ISkillRootProvider
    {
        public ValueTask<IReadOnlyList<SkillRootRegistration>> GetRootsAsync(SkillDiscoveryContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(roots);
    }

    private sealed class CapturingProvider : ISkillRootProvider
    {
        public SkillDiscoveryContext? Context { get; private set; }
        public ValueTask<IReadOnlyList<SkillRootRegistration>> GetRootsAsync(SkillDiscoveryContext context, CancellationToken cancellationToken = default)
        {
            Context = context;
            return ValueTask.FromResult<IReadOnlyList<SkillRootRegistration>>([]);
        }
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"codealta-skill-management-{Guid.NewGuid():N}");
        public SkillManagementService Service { get; }
        public Fixture()
        {
            var git = Directory.CreateDirectory(Path.Combine(Root, ".git")).FullName;
            File.WriteAllText(Path.Combine(git, "config"), "[core]\nignorecase = false\nexcludesFile = excludes\n");
            File.WriteAllText(Path.Combine(git, "excludes"), string.Empty);
            Service = new SkillManagementService(CreateCatalog(), Root, Root);
        }

        public static SkillCatalog CreateCatalog() => new([new ProjectCodeAltaSkillRootProvider(), new ProjectCommonSkillRootProvider(),
            new UserCodeAltaSkillRootProvider(), new UserCommonSkillRootProvider()]);

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
