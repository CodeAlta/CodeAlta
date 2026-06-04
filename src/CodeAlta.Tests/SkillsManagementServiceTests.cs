using CodeAlta.App;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Skills;

namespace CodeAlta.Tests;

[TestClass]
public sealed class SkillsManagementServiceTests
{
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
        var descriptor = CreateDescriptor(Path.Combine(Path.GetTempPath(), $"missing-skill-{Guid.NewGuid():N}"));
        var service = CreateService(Path.GetTempPath());

        var files = service.ListRelatedFiles(descriptor);

        Assert.AreEqual(0, files.Count);
    }

    [TestMethod]
    public void ListRelatedFiles_RespectsGitIgnore()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = Path.Combine(temp.Path, "repo");
        var skillRoot = Path.Combine(projectRoot, ".alta", "skills", "sample-skill");
        Directory.CreateDirectory(Path.Combine(projectRoot, ".git"));
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
        var service = new SkillsManagementService(
            new SkillCatalog(),
            new CatalogOptions { GlobalRoot = globalRoot },
            () => new ProjectDescriptor { ProjectPath = projectRoot });

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
        var service = new SkillsManagementService(
            new SkillCatalog(),
            new CatalogOptions { GlobalRoot = temp.Path },
            () => null);

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
        var service = new SkillsManagementService(
            new SkillCatalog(),
            new CatalogOptions { GlobalRoot = temp.Path },
            () => null);

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
        var service = new SkillsManagementService(
            new SkillCatalog(),
            options,
            () => new ProjectDescriptor { ProjectPath = projectRoot },
            configStore);

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
        var configStore = new CodeAltaConfigStore(options);
        var service = new SkillsManagementService(
            new SkillCatalog(),
            options,
            () => new ProjectDescriptor { ProjectPath = projectRoot },
            configStore);

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
        var service = new SkillsManagementService(new SkillCatalog(), options, () => null, new CodeAltaConfigStore(options));

        Assert.ThrowsExactly<InvalidOperationException>(() => service.SetSkillsEnabled(SkillEnablementScope.Project, ["sample-skill"], enabled: false));
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
        => new(
            new SkillCatalog(),
            new CatalogOptions { GlobalRoot = globalRoot },
            () => null);

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
