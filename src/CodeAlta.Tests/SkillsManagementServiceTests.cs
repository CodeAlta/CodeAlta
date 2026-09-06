using System.Reflection;
using CodeAlta.Tui.App;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Skills;
using CodeAlta.Tui.ViewModels;
using CodeAlta.Tui.Views;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.DataGrid;

namespace CodeAlta.Tests;

[TestClass]
public sealed class SkillsManagementServiceTests
{
    [TestMethod]
    [DataRow(false, SkillSourceKind.Builtin)]
    [DataRow(true, SkillSourceKind.Builtin)]
    [DataRow(false, SkillSourceKind.Plugin)]
    [DataRow(true, SkillSourceKind.Plugin)]
    [DataRow(false, SkillSourceKind.UserAlta)]
    [DataRow(true, SkillSourceKind.ProjectAlta)]
    public async Task Dialog_SkillOpenCarriesCatalogDocumentPolicy(bool related, SkillSourceKind kind)
    {
        using var temp = TempDirectory.Create();
        var root = Path.Combine(temp.Path, "builtin", "sample-skill");
        await WriteSkillAsync(root, "sample-skill", "Bundled");
        Directory.CreateDirectory(Path.Combine(root, "references"));
        File.WriteAllText(Path.Combine(root, "references", "guide.md"), "guide");
        var catalog = new SkillCatalog([new RegisteredProvider(Path.GetDirectoryName(root)!, kind)]);
        var service = SkillsManagementCoordinatorFactory.CreateService(catalog,
            new CatalogOptions { GlobalRoot = temp.Path }, () => null, null);
        var descriptor = (await service.LoadAsync(SkillsManagementScope.User)).Single();
        object? opened = null;
        var dialog = new SkillsManagementDialog(service, (request, _) => { opened = request; return Task.CompletedTask; },
            (_, _) => Task.CompletedTask, () => null, () => null);
        // A stale/claimed source flag in a view model must not determine workflow policy.
        var row = new SkillManagementRowViewModel(descriptor with { SourceKind = SkillSourceKind.Builtin }, (_, _, _) => Task.CompletedTask);
        typeof(SkillsManagementDialog).GetField("_rows", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(dialog, new[] { row });
        var cell = (State<DataGridCell>)typeof(SkillsManagementDialog).GetField("_currentSkillCell", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(dialog)!;
        cell.Value = new DataGridCell(0, 0);
        await (Task)typeof(SkillsManagementDialog).GetMethod(related ? "OpenSelectedRelatedFileAsync" : "OpenSelectedSkillAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(dialog, null)!;
        Assert.IsNotNull(opened);
        Assert.IsFalse(opened is string, "Skill opens must carry backend document policy, not a raw writable path.");
        var document = (TextFileDocument)opened;
        Assert.AreEqual(kind == SkillSourceKind.Builtin, document.IsReadOnly);
        Assert.AreEqual(kind, document.SkillSourceKind);
        Assert.AreEqual("fixture:builtin", document.SkillSourceId);
        Assert.AreEqual(related ? Path.Combine(root, "references", "guide.md") : descriptor.SkillFilePath, document.FullPath);

        opened = null;
        var arbitraryPath = Path.Combine(temp.Path, "outside.md");
        File.WriteAllText(arbitraryPath, "not a skill");
        row = new SkillManagementRowViewModel(descriptor with { SkillFilePath = arbitraryPath, SkillRootPath = temp.Path, SourceKind = SkillSourceKind.Builtin }, (_, _, _) => Task.CompletedTask);
        typeof(SkillsManagementDialog).GetField("_rows", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(dialog, new[] { row });
        await (Task)typeof(SkillsManagementDialog).GetMethod("OpenSelectedSkillAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(dialog, null)!;
        Assert.IsNull(opened, "A claimed builtin descriptor cannot open an undiscovered arbitrary file.");
    }

    private sealed class RegisteredProvider(string root, SkillSourceKind kind) : ISkillRootProvider
    {
        public ValueTask<IReadOnlyList<SkillRootRegistration>> GetRootsAsync(SkillDiscoveryContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<SkillRootRegistration>>([new()
            {
                RootPath = root, SourceKind = kind, SourceId = "fixture:builtin",
                Scope = SkillScopeKind.Builtin, Precedence = 4, IsTrusted = true,
            }]);
    }

    [TestMethod]
    public void ListRelatedFiles_ReturnsAuthoringFilesUnderKnownFolders()
    {
        using var temp = TempDirectory.Create();
        var skillRoot = Path.Combine(temp.Path, "sample-skill");
        Directory.CreateDirectory(skillRoot);
        Directory.CreateDirectory(Path.Combine(skillRoot, "scripts"));
        Directory.CreateDirectory(Path.Combine(skillRoot, "references", "deep"));
        Directory.CreateDirectory(Path.Combine(skillRoot, "assets"));
        File.WriteAllText(Path.Combine(skillRoot, "SKILL.md"), "---\nname: sample-skill\ndescription: sample\n---\n");
        File.WriteAllText(Path.Combine(skillRoot, "README.md"), "ignored");
        File.WriteAllText(Path.Combine(skillRoot, "scripts", "run.ps1"), "Write-Host test");
        File.WriteAllText(Path.Combine(skillRoot, "references", "deep", "guide.md"), "# Guide");
        File.WriteAllText(Path.Combine(skillRoot, "assets", "icon.svg"), "<svg />");
        var service = CreateService(temp.Path);

        var files = service.ListRelatedFiles(CreateDescriptor(skillRoot));

        CollectionAssert.AreEqual(
            new[]
            {
                "scripts/run.ps1",
                "references/deep/guide.md",
                "assets/icon.svg",
            },
            files.Select(static file => file.RelativePath).ToArray());
        CollectionAssert.AreEqual(
            new[]
            {
                "scripts",
                "references",
                "assets",
            },
            files.Select(static file => file.Category).ToArray());
        Assert.IsTrue(files.All(static file => File.Exists(file.FullPath)));
    }

    [TestMethod]
    public void ListRelatedFiles_ReturnsEmptyForMissingSkillRoot()
    {
        using var temp = TempDirectory.Create();
        var descriptor = CreateDescriptor(Path.Combine(temp.Path, "missing-skill"));
        var service = CreateService(temp.Path);

        var files = service.ListRelatedFiles(descriptor);

        Assert.AreEqual(0, files.Count);
    }

    [TestMethod]
    public void ListRelatedFiles_DoesNotIncludeTopLevelFilesNamedLikeFolders()
    {
        using var temp = TempDirectory.Create();
        File.WriteAllText(Path.Combine(temp.Path, "scripts"), "not a scripts resource");
        File.WriteAllText(Path.Combine(temp.Path, "references"), "not a references resource");
        File.WriteAllText(Path.Combine(temp.Path, "assets"), "not an assets resource");
        Assert.AreEqual(0, CreateService(temp.Path).ListRelatedFiles(CreateDescriptor(temp.Path)).Count);
    }

    [TestMethod]
    public void ListRelatedFiles_RespectsGitIgnore()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = Path.Combine(temp.Path, "repo");
        var skillRoot = Path.Combine(projectRoot, ".alta", "skills", "sample-skill");
        CreateGitBoundary(projectRoot);
        Directory.CreateDirectory(Path.Combine(skillRoot, "assets"));
        File.WriteAllText(
            Path.Combine(projectRoot, ".gitignore"),
            ".alta/skills/sample-skill/assets/ignored.svg" + Environment.NewLine);
        File.WriteAllText(Path.Combine(skillRoot, "SKILL.md"), "---\nname: sample-skill\ndescription: sample\n---\n");
        File.WriteAllText(Path.Combine(skillRoot, "assets", "visible.svg"), "<svg />");
        File.WriteAllText(Path.Combine(skillRoot, "assets", "ignored.svg"), "<svg />");
        var service = CreateService(projectRoot);

        var files = service.ListRelatedFiles(CreateDescriptor(skillRoot));

        CollectionAssert.AreEqual(
            new[] { "assets/visible.svg" },
            files.Select(static file => file.RelativePath).ToArray());
    }

    [TestMethod]
    public async Task CreateSkillAsync_CreatesProjectAltaSkillForCombinedScope()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = Path.Combine(temp.Path, "project");
        var globalRoot = Path.Combine(temp.Path, ".alta");
        Directory.CreateDirectory(projectRoot);
        Directory.CreateDirectory(globalRoot);
        var service = SkillsManagementCoordinatorFactory.CreateService(
            CreateCatalog(),
            new CatalogOptions { GlobalRoot = globalRoot },
            () => new ProjectDescriptor { ProjectPath = projectRoot }, temp.Path);

        var result = await service.CreateSkillAsync(
            SkillsManagementScope.Combined,
            "sample-skill",
            "Use this skill for tests.");

        Assert.AreEqual(SkillCreationTargetKind.ProjectCodeAlta, result.TargetKind);
        Assert.AreEqual(Path.Combine(projectRoot, ".alta", "skills", "sample-skill"), result.SkillRootPath);
        Assert.IsTrue(File.Exists(result.SkillFilePath));
        Assert.IsTrue(Directory.Exists(Path.Combine(result.SkillRootPath, "scripts")));
        Assert.IsTrue(Directory.Exists(Path.Combine(result.SkillRootPath, "references")));
        Assert.IsTrue(Directory.Exists(Path.Combine(result.SkillRootPath, "assets")));
        var contents = await File.ReadAllTextAsync(result.SkillFilePath);
        StringAssert.Contains(contents, "name: sample-skill");
        StringAssert.Contains(contents, "description: 'Use this skill for tests.'");
    }

    [TestMethod]
    public async Task CreateSkillAsync_CreatesUserAltaSkillWhenNoProjectIsSelected()
    {
        using var temp = TempDirectory.Create();
        var service = SkillsManagementCoordinatorFactory.CreateService(
            CreateCatalog(),
            new CatalogOptions { GlobalRoot = temp.Path },
            () => null, temp.Path);

        var result = await service.CreateSkillAsync(
            SkillsManagementScope.Combined,
            "user-skill",
            "Use this skill globally.");

        Assert.AreEqual(SkillCreationTargetKind.UserCodeAlta, result.TargetKind);
        Assert.AreEqual(Path.Combine(temp.Path, "skills", "user-skill"), result.SkillRootPath);
        Assert.IsTrue(File.Exists(result.SkillFilePath));
    }

    [TestMethod]
    [DataRow("BadName")]
    [DataRow("-bad")]
    [DataRow("bad-")]
    [DataRow("bad--name")]
    [DataRow("bad_name")]
    public async Task CreateSkillAsync_RejectsInvalidSkillNames(string name)
    {
        using var temp = TempDirectory.Create();
        var service = SkillsManagementCoordinatorFactory.CreateService(
            CreateCatalog(),
            new CatalogOptions { GlobalRoot = temp.Path },
            () => null, temp.Path);

        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => service.CreateSkillAsync(SkillsManagementScope.User, name, "Description.")).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task LoadAsync_IncludesDisabledSkillStateForManagement()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = Path.Combine(temp.Path, "project");
        Directory.CreateDirectory(projectRoot);
        await WriteSkillAsync(Path.Combine(projectRoot, ".alta", "skills", "sample-skill"), "sample-skill", "Managed skill.").ConfigureAwait(false);
        var options = new CatalogOptions { GlobalRoot = Path.Combine(temp.Path, "home") };
        var configStore = new CodeAltaConfigStore(options);
        configStore.SaveGlobalSkillEnabled("sample-skill", enabled: false);
        var service = SkillsManagementCoordinatorFactory.CreateService(
            CreateCatalog(),
            options,
            () => new ProjectDescriptor { ProjectPath = projectRoot },
            temp.Path);

        var descriptors = await service.LoadAsync(SkillsManagementScope.CurrentProject).ConfigureAwait(false);

        var descriptor = descriptors.Single(static descriptor => descriptor.Name == "sample-skill");
        Assert.IsFalse(descriptor.IsEnabled);
        Assert.IsTrue(descriptor.IsDisabledGlobally);
    }

    [TestMethod]
    public void SetSkillsEnabled_UpdatesRequestedScopesAndInvertRestoresThem()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = Path.Combine(temp.Path, "project");
        Directory.CreateDirectory(projectRoot);
        var options = new CatalogOptions { GlobalRoot = Path.Combine(temp.Path, "home") };
        Directory.CreateDirectory(options.GlobalRoot);
        var configStore = new CodeAltaConfigStore(options);
        var service = SkillsManagementCoordinatorFactory.CreateService(
            CreateCatalog(),
            options,
            () => new ProjectDescriptor { ProjectPath = projectRoot },
            temp.Path);

        var disabled = service.SetSkillsEnabled(SkillEnablementScope.Both, ["sample-skill", "other-skill"], enabled: false);

        Assert.AreEqual(2, disabled.GlobalChanged);
        Assert.AreEqual(2, disabled.ProjectChanged);
        CollectionAssert.AreEqual(new[] { "other-skill", "sample-skill" }, configStore.LoadGlobalDisabledSkillNames().OrderBy(static name => name, StringComparer.OrdinalIgnoreCase).ToArray());
        CollectionAssert.AreEqual(new[] { "other-skill", "sample-skill" }, configStore.LoadProjectDisabledSkillNames(projectRoot).OrderBy(static name => name, StringComparer.OrdinalIgnoreCase).ToArray());

        var inverted = service.InvertSkillsEnabled(SkillEnablementScope.Project, ["sample-skill"]);

        Assert.AreEqual(0, inverted.GlobalChanged);
        Assert.AreEqual(1, inverted.ProjectChanged);
        CollectionAssert.AreEqual(new[] { "other-skill" }, configStore.LoadProjectDisabledSkillNames(projectRoot).ToArray());
    }

    [TestMethod]
    public void SetSkillsEnabled_ProjectScopeRequiresSelectedProject()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = Path.Combine(temp.Path, "home") };
        Directory.CreateDirectory(options.GlobalRoot);
        var service = SkillsManagementCoordinatorFactory.CreateService(CreateCatalog(), options, () => null, temp.Path);

        Assert.ThrowsExactly<InvalidOperationException>(() => service.SetSkillsEnabled(SkillEnablementScope.Project, ["sample-skill"], enabled: false));
    }

    [TestMethod]
    public async Task CreateSkillAsync_CanceledRequestLeavesNoFinalDirectory()
    {
        using var temp = TempDirectory.Create();
        var service = CreateService(temp.Path);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.CreateSkillAsync(
            SkillsManagementScope.User, "canceled", "Description", cancellation.Token));
        Assert.IsFalse(Directory.Exists(Path.Combine(temp.Path, "skills", "canceled")));
    }

    [TestMethod]
    public void SetSkillsEnabled_BothWithoutProjectDoesNotWriteGlobal()
    {
        using var temp = TempDirectory.Create();
        var service = CreateService(temp.Path);
        Assert.ThrowsExactly<InvalidOperationException>(() => service.SetSkillsEnabled(
            SkillEnablementScope.Both, ["sample"], false));
        Assert.IsFalse(File.Exists(Path.Combine(temp.Path, "config.toml")));
    }

    [TestMethod]
    public async Task CreateSkillAsync_InvalidScopeDoesNotCreateSkill()
    {
        using var temp = TempDirectory.Create();
        var service = CreateService(temp.Path);
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => service.CreateSkillAsync(
            (SkillsManagementScope)99, "sample", "Description"));
        Assert.IsFalse(Directory.Exists(Path.Combine(temp.Path, "skills")));
    }

    [TestMethod]
    public async Task CreateSkillAsync_RejectsControlDescriptionBeforeMutation()
    {
        using var temp = TempDirectory.Create();
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => CreateService(temp.Path).CreateSkillAsync(
            SkillsManagementScope.User, "sample", "bad\0description"));
        Assert.IsFalse(Directory.Exists(Path.Combine(temp.Path, "skills")));
    }

    [TestMethod]
    public async Task CreateSkillAsync_PreservesCollision()
    {
        using var temp = TempDirectory.Create();
        var root = Path.Combine(temp.Path, "skills", "sample");
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "SKILL.md");
        File.WriteAllText(file, "existing");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => CreateService(temp.Path).CreateSkillAsync(
            SkillsManagementScope.User, "sample", "Description"));
        Assert.AreEqual("existing", File.ReadAllText(file));
    }

    [TestMethod]
    public async Task FactoryService_UsesInjectedHomeAndPreservesProvenance()
    {
        using var temp = TempDirectory.Create();
        var global = Directory.CreateDirectory(Path.Combine(temp.Path, "global")).FullName;
        var home = Directory.CreateDirectory(Path.Combine(temp.Path, "home")).FullName;
        var project = Directory.CreateDirectory(Path.Combine(temp.Path, "project")).FullName;
        await WriteSkillAsync(Path.Combine(global, "skills", "sample"), "sample", "Global");
        await WriteSkillAsync(Path.Combine(home, ".agents", "skills", "sample"), "sample", "Common");
        await WriteSkillAsync(Path.Combine(project, ".alta", "skills", "sample"), "sample", "Project");
        var service = SkillsManagementCoordinatorFactory.CreateService(CreateCatalog(), new CatalogOptions { GlobalRoot = global },
            () => new ProjectDescriptor { ProjectPath = project }, home);

        var all = await service.LoadAsync(SkillsManagementScope.Combined);
        Assert.AreEqual(3, all.Count);
        Assert.AreEqual(SkillSourceKind.ProjectAlta, all[0].SourceKind);
        Assert.IsFalse(all[0].IsShadowed);
        Assert.IsTrue(all[1].IsShadowed);
        Assert.AreEqual(SkillSourceKind.UserCommon, all[2].SourceKind);
        Assert.IsTrue(all.All(d => d.SkillRootPath.StartsWith(temp.Path, StringComparison.Ordinal)));
        Assert.AreEqual(2, (await service.LoadAsync(SkillsManagementScope.User)).Count);
        Assert.AreEqual(1, (await service.LoadAsync(SkillsManagementScope.CurrentProject)).Count);

        var noHome = SkillsManagementCoordinatorFactory.CreateService(CreateCatalog(), new CatalogOptions { GlobalRoot = global }, () => null, null);
        Assert.AreEqual(1, (await noHome.LoadAsync(SkillsManagementScope.User)).Count);
    }

    [TestMethod]
    public async Task CaptureContext_DoesNotRedirectQueuedOperations()
    {
        using var temp = TempDirectory.Create();
        var first = Directory.CreateDirectory(Path.Combine(temp.Path, "first")).FullName;
        var second = Directory.CreateDirectory(Path.Combine(temp.Path, "second")).FullName;
        var selected = new ProjectDescriptor { ProjectPath = first };
        var service = SkillsManagementCoordinatorFactory.CreateService(CreateCatalog(), new CatalogOptions { GlobalRoot = temp.Path }, () => selected, temp.Path);
        var captured = service.CaptureContext();
        selected = new ProjectDescriptor { ProjectPath = second };

        var result = await Task.Run(() => captured.CreateSkillAsync(SkillsManagementScope.Combined, "sample", "Description"));
        Assert.AreEqual(Path.Combine(first, ".alta", "skills", "sample"), result.SkillRootPath);
        await Task.Run(() => captured.SetSkillEnabled(SkillEnablementScope.Project, "sample", false));
        Assert.IsFalse(File.Exists(Path.Combine(second, ".alta", "config.toml")));
        Assert.AreEqual(1, (await captured.LoadAsync(SkillsManagementScope.CurrentProject)).Count);
    }

    [TestMethod]
    public async Task Dialog_CreateFailureShowsValidationWithoutOpeningEditor()
    {
        using var temp = TempDirectory.Create();
        var opened = false;
        var dialog = new SkillsManagementDialog(CreateService(temp.Path), (_, _) => { opened = true; return Task.CompletedTask; },
            (_, _) => Task.CompletedTask, () => null, () => null);
        var validation = new TextBlock();
        var method = typeof(SkillsManagementDialog).GetMethod("CreateSkillFromDialogAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)method.Invoke(dialog, [null, new TextBox { Text = "../invalid" }, new TextBox { Text = "Description" }, validation])!;
        Assert.IsFalse(opened);
        StringAssert.Contains(validation.Text, "Skill name");
        Assert.IsFalse(Directory.Exists(Path.Combine(temp.Path, "skills")));
    }

    [TestMethod]
    public async Task Dialog_OpenFailureShowsFeedback()
    {
        using var temp = TempDirectory.Create();
        var service = CreateService(temp.Path);
        await service.CreateSkillAsync(SkillsManagementScope.User, "sample-skill", "Description");
        var descriptor = (await service.LoadAsync(SkillsManagementScope.User)).Single();
        var dialog = new SkillsManagementDialog(service, (_, _) => throw new IOException("editor unavailable"),
            (_, _) => Task.CompletedTask, () => null, () => null);
        var row = new SkillManagementRowViewModel(descriptor, (_, _, _) => Task.CompletedTask);
        typeof(SkillsManagementDialog).GetField("_rows", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(dialog, new[] { row });
        var cell = (State<DataGridCell>)typeof(SkillsManagementDialog).GetField("_currentSkillCell", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(dialog)!;
        cell.Value = new DataGridCell(0, 0);
        await (Task)typeof(SkillsManagementDialog).GetMethod("OpenSelectedSkillAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(dialog, null)!;
        var summary = (string)typeof(SkillsManagementDialog).GetField("_summaryText", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(dialog)!;
        StringAssert.Contains(summary, "editor unavailable");
        StringAssert.Contains(summary, "[error]");
    }

    [TestMethod]
    public async Task Dialog_BothWithoutProjectShowsFailureWithoutGlobalWrite()
    {
        using var temp = TempDirectory.Create();
        var dialog = new SkillsManagementDialog(CreateService(temp.Path), (_, _) => Task.CompletedTask,
            (_, _) => Task.CompletedTask, () => null, () => null);
        var row = new SkillManagementRowViewModel(CreateDescriptor(temp.Path), (_, _, _) => Task.CompletedTask);
        typeof(SkillsManagementDialog).GetField("_rows", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(dialog, new[] { row });
        var scopeSelect = typeof(SkillsManagementDialog).GetField("_bulkScopeSelect", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(dialog)!;
        scopeSelect.GetType().GetProperty("SelectedIndex")!.SetValue(scopeSelect, 2);
        await (Task)typeof(SkillsManagementDialog).GetMethod("ApplyBulkEnablementAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(dialog, [false])!;
        var summary = (string)typeof(SkillsManagementDialog).GetField("_summaryText", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(dialog)!;
        StringAssert.Contains(summary, "[error]");
        StringAssert.Contains(summary, "Failed to update skill enablement");
        Assert.IsFalse(File.Exists(Path.Combine(temp.Path, "config.toml")));
    }

    private static void CreateGitBoundary(string root)
    {
        var git = Path.Combine(root, ".git");
        Directory.CreateDirectory(git);
        File.WriteAllText(Path.Combine(git, "config"), "[core]\nignorecase = false\nexcludesFile = excludes\n");
        File.WriteAllText(Path.Combine(git, "excludes"), string.Empty);
    }

    private static SkillDescriptor CreateDescriptor(string skillRoot)
        => new()
        {
            Name = "sample-skill",
            NormalizedName = "sample-skill",
            Title = "sample-skill",
            Description = "Sample skill.",
            SkillRootPath = skillRoot,
            SkillFilePath = Path.Combine(skillRoot, "SKILL.md"),
            SourceKind = SkillSourceKind.ProjectAlta,
            SourceId = "test",
            Scope = SkillScopeKind.Project,
            Precedence = 0,
            Frontmatter = new SkillFrontmatter(),
            IsTrusted = true,
            IsValid = true,
            IsModelVisible = true,
        };

    private static SkillsManagementService CreateService(string globalRoot)
        => SkillsManagementCoordinatorFactory.CreateService(
            CreateCatalog(),
            new CatalogOptions { GlobalRoot = globalRoot },
            () => null, globalRoot);

    private static SkillCatalog CreateCatalog()
        => new([new ProjectCodeAltaSkillRootProvider(), new ProjectCommonSkillRootProvider(),
            new UserCodeAltaSkillRootProvider(), new UserCommonSkillRootProvider()]);

    private static async Task WriteSkillAsync(string skillRoot, string name, string description)
    {
        Directory.CreateDirectory(skillRoot);
        await File.WriteAllTextAsync(
            Path.Combine(skillRoot, "SKILL.md"),
            $$"""
            ---
            name: {{name}}
            description: {{description}}
            ---
            # {{name}}
            """).ConfigureAwait(false);
    }

    private sealed class TempDirectory(string path) : IDisposable
    {
        public string Path { get; } = path;

        public static TempDirectory Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"codealta-skills-ui-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            CreateGitBoundary(path);
            return new TempDirectory(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
