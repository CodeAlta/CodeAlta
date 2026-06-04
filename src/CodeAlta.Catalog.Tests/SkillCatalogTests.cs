using System.Globalization;
using CodeAlta.Catalog.Skills;

namespace CodeAlta.Catalog.Tests;

[TestClass]
public sealed class SkillCatalogTests
{
    [TestMethod]
    public async Task SkillCatalog_ListAsync_DiscoversProjectAndUserRoots()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = Path.Combine(temp.Path, "repo-main");
        var userHome = Path.Combine(temp.Path, "user-home");
        Directory.CreateDirectory(projectRoot);
        Directory.CreateDirectory(userHome);

        await WriteSkillAsync(Path.Combine(projectRoot, ".alta", "skills", "project-alta-skill"), "project-alta-skill", "Project CodeAlta skill.").ConfigureAwait(false);
        await WriteSkillAsync(Path.Combine(projectRoot, ".agents", "skills", "project-common-skill"), "project-common-skill", "Project common skill.").ConfigureAwait(false);
        await WriteSkillAsync(Path.Combine(userHome, ".alta", "skills", "user-alta-skill"), "user-alta-skill", "User CodeAlta skill.").ConfigureAwait(false);
        await WriteSkillAsync(Path.Combine(userHome, ".agents", "skills", "user-common-skill"), "user-common-skill", "User common skill.").ConfigureAwait(false);

        var catalog = new SkillCatalog();
        var descriptors = await catalog.ListAsync(
                new SkillCatalogQuery
                {
                    Discovery = new SkillDiscoveryContext
                    {
                        ProjectRoots = [projectRoot],
                        UserCodeAltaRoot = Path.Combine(userHome, ".alta"),
                        UserProfileRoot = userHome,
                    },
                })
            .ConfigureAwait(false);

        Assert.AreEqual(5, descriptors.Count);
        Assert.AreEqual(SkillSourceKind.Builtin, descriptors.Single(x => x.Name == "codealta-plugin-runtime").SourceKind);
        Assert.AreEqual(SkillSourceKind.ProjectAlta, descriptors.Single(x => x.Name == "project-alta-skill").SourceKind);
        Assert.AreEqual(SkillSourceKind.ProjectCommon, descriptors.Single(x => x.Name == "project-common-skill").SourceKind);
        Assert.AreEqual(SkillSourceKind.UserAlta, descriptors.Single(x => x.Name == "user-alta-skill").SourceKind);
        Assert.AreEqual(SkillSourceKind.UserCommon, descriptors.Single(x => x.Name == "user-common-skill").SourceKind);
    }

    [TestMethod]
    public async Task SkillCatalog_ListAsync_RespectsGitIgnoreAndStopsAtSkillRoots()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = Path.Combine(temp.Path, "repo-main");
        Directory.CreateDirectory(projectRoot);
        Directory.CreateDirectory(Path.Combine(projectRoot, ".git"));

        await File.WriteAllTextAsync(Path.Combine(projectRoot, ".gitignore"), "ignored/" + Environment.NewLine).ConfigureAwait(false);
        await WriteSkillAsync(Path.Combine(projectRoot, ".agents", "skills", "visible-skill"), "visible-skill", "Visible skill.").ConfigureAwait(false);
        await WriteSkillAsync(Path.Combine(projectRoot, ".agents", "skills", "ignored", "ignored-skill"), "ignored-skill", "Ignored skill.").ConfigureAwait(false);
        await WriteSkillAsync(Path.Combine(projectRoot, ".agents", "skills", "parent-skill"), "parent-skill", "Parent skill.").ConfigureAwait(false);
        await WriteSkillAsync(Path.Combine(projectRoot, ".agents", "skills", "parent-skill", "nested-child"), "nested-child", "Nested child skill.").ConfigureAwait(false);

        var catalog = new SkillCatalog();
        var descriptors = await catalog.ListAsync(
                new SkillCatalogQuery
                {
                    Discovery = new SkillDiscoveryContext
                    {
                        ProjectRoots = [projectRoot],
                    },
                    IncludeInvalid = true,
                    IncludeShadowed = true,
                })
            .ConfigureAwait(false);

        CollectionAssert.AreEquivalent(
            new[] { "codealta-plugin-runtime", "parent-skill", "visible-skill" },
            descriptors.Select(static descriptor => descriptor.Name).ToArray());
    }

    [TestMethod]
    public async Task SkillCatalog_ListAsync_AppliesPrecedenceAndShadowing()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = Path.Combine(temp.Path, "repo-main");
        var userHome = Path.Combine(temp.Path, "user-home");
        Directory.CreateDirectory(projectRoot);
        Directory.CreateDirectory(userHome);

        await WriteSkillAsync(Path.Combine(projectRoot, ".agents", "skills", "shared-skill"), "shared-skill", "Project common version.").ConfigureAwait(false);
        await WriteSkillAsync(Path.Combine(projectRoot, ".alta", "skills", "shared-skill"), "shared-skill", "Project CodeAlta version.").ConfigureAwait(false);
        await WriteSkillAsync(Path.Combine(userHome, ".alta", "skills", "shared-skill"), "shared-skill", "User CodeAlta version.").ConfigureAwait(false);
        await WriteSkillAsync(Path.Combine(userHome, ".agents", "skills", "shared-skill"), "shared-skill", "User common version.").ConfigureAwait(false);

        var catalog = new SkillCatalog();
        var allDescriptors = await catalog.ListAsync(
                new SkillCatalogQuery
                {
                    Discovery = new SkillDiscoveryContext
                    {
                        ProjectRoots = [projectRoot],
                        UserCodeAltaRoot = Path.Combine(userHome, ".alta"),
                        UserProfileRoot = userHome,
                    },
                })
            .ConfigureAwait(false);

        Assert.AreEqual(5, allDescriptors.Count);
        var winner = allDescriptors.Single(x => x.SourceKind == SkillSourceKind.ProjectAlta);
        Assert.IsTrue(winner.IsModelVisible);
        Assert.IsFalse(winner.IsShadowed);
        Assert.AreEqual(3, allDescriptors.Count(static descriptor => descriptor.IsShadowed));
        Assert.IsTrue(allDescriptors.Where(static descriptor => descriptor.IsShadowed).All(descriptor => descriptor.ShadowedBySkillFilePath == winner.SkillFilePath));

        var visible = await catalog.ListAsync(
                new SkillCatalogQuery
                {
                    Discovery = new SkillDiscoveryContext
                    {
                        ProjectRoots = [projectRoot],
                        UserCodeAltaRoot = Path.Combine(userHome, ".alta"),
                        UserProfileRoot = userHome,
                    },
                    ModelVisibleOnly = true,
                })
            .ConfigureAwait(false);

        Assert.AreEqual(2, visible.Count);
        Assert.IsTrue(visible.Any(static descriptor => descriptor.SourceKind == SkillSourceKind.ProjectAlta));
        Assert.IsTrue(visible.Any(static descriptor => descriptor.SourceKind == SkillSourceKind.Builtin));
    }

    [TestMethod]
    public async Task SkillCatalog_ValidateAsync_ReturnsDiagnosticsForInvalidFrontmatter()
    {
        using var temp = TempDirectory.Create();
        var skillsRoot = Path.Combine(temp.Path, ".alta", "skills");
        var invalidNamePath = Path.Combine(skillsRoot, "InvalidName");
        Directory.CreateDirectory(invalidNamePath);
        await File.WriteAllTextAsync(
            Path.Combine(invalidNamePath, "SKILL.md"),
            """
            ---
            name: InvalidName
            description: Broken skill.
            unsupported: true
            ---
            # Invalid
            """).ConfigureAwait(false);

        var missingFrontmatterPath = Path.Combine(skillsRoot, "missing-frontmatter");
        Directory.CreateDirectory(missingFrontmatterPath);
        await File.WriteAllTextAsync(
            Path.Combine(missingFrontmatterPath, "SKILL.md"),
            "# Missing").ConfigureAwait(false);

        var catalog = new SkillCatalog();
        var descriptors = await catalog.ValidateAsync(
                new SkillCatalogQuery
                {
                    Discovery = new SkillDiscoveryContext
                    {
                        UseBuiltInRoots = false,
                        AdditionalRoots =
                        [
                            new SkillRootRegistration
                            {
                                RootPath = skillsRoot,
                                SourceKind = SkillSourceKind.Temporary,
                                SourceId = "temporary-tests",
                                Scope = SkillScopeKind.Temporary,
                                Precedence = 0,
                            },
                        ],
                    },
                })
            .ConfigureAwait(false);

        var invalidName = descriptors.Single(x => x.SkillRootPath == Path.GetFullPath(invalidNamePath));
        CollectionAssert.IsSubsetOf(
            new[] { "name-format", "unknown-frontmatter-field" },
            invalidName.Diagnostics.Select(static diagnostic => diagnostic.Code).ToArray());
        Assert.IsFalse(invalidName.IsValid);
        Assert.IsFalse(invalidName.IsModelVisible);

        var missingFrontmatter = descriptors.Single(x => x.SkillRootPath == Path.GetFullPath(missingFrontmatterPath));
        CollectionAssert.Contains(missingFrontmatter.Diagnostics.Select(static diagnostic => diagnostic.Code).ToArray(), "frontmatter-missing");
        Assert.IsFalse(missingFrontmatter.IsValid);
    }

    [TestMethod]
    public async Task SkillCatalog_ReadResourceAsync_RejectsUnsafePaths()
    {
        using var temp = TempDirectory.Create();
        var skillRoot = Path.Combine(temp.Path, ".alta", "skills", "sample-skill");
        await WriteSkillAsync(skillRoot, "sample-skill", "Reads resource files safely.").ConfigureAwait(false);
        var resourcesRoot = Path.Combine(skillRoot, "resources");
        Directory.CreateDirectory(resourcesRoot);
        await File.WriteAllTextAsync(Path.Combine(resourcesRoot, "data.txt"), "hello").ConfigureAwait(false);

        var catalog = new SkillCatalog();
        var query = new SkillCatalogQuery
        {
            Discovery = new SkillDiscoveryContext
            {
                UseBuiltInRoots = false,
                AdditionalRoots =
                [
                    new SkillRootRegistration
                    {
                        RootPath = Path.Combine(temp.Path, ".alta", "skills"),
                        SourceKind = SkillSourceKind.Temporary,
                        SourceId = "temporary-tests",
                        Scope = SkillScopeKind.Temporary,
                        Precedence = 0,
                    },
                ],
            },
        };

        var resource = await catalog.ReadResourceAsync(query, "sample-skill", "resources/data.txt").ConfigureAwait(false);
        Assert.AreEqual("hello", System.Text.Encoding.UTF8.GetString(resource.Content));

        await Assert.ThrowsExactlyAsync<ArgumentException>(() => catalog.ReadResourceAsync(query, "sample-skill", "../escape.txt"));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => catalog.ReadResourceAsync(query, "sample-skill", Path.GetFullPath("C:\\escape.txt")));
    }

    [TestMethod]
    public async Task SkillCatalog_ActivateAsync_ReturnsCanonicalPayload()
    {
        using var temp = TempDirectory.Create();
        Directory.CreateDirectory(Path.Combine(temp.Path, ".git"));
        await File.WriteAllTextAsync(
            Path.Combine(temp.Path, ".gitignore"),
            ".alta/skills/sample-skill/assets/ignored.svg" + Environment.NewLine)
            .ConfigureAwait(false);
        var skillRoot = Path.Combine(temp.Path, ".alta", "skills", "sample-skill");
        await WriteSkillAsync(skillRoot, "sample-skill", "Activates a skill.").ConfigureAwait(false);
        Directory.CreateDirectory(Path.Combine(skillRoot, "references"));
        Directory.CreateDirectory(Path.Combine(skillRoot, "assets"));
        await File.WriteAllTextAsync(Path.Combine(skillRoot, "references", "guide.md"), "guide").ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(skillRoot, "assets", "visible.svg"), "<svg />").ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(skillRoot, "assets", "ignored.svg"), "<svg />").ConfigureAwait(false);

        var catalog = new SkillCatalog();
        var activation = await catalog.ActivateAsync(
                new SkillCatalogQuery
                {
                    Discovery = new SkillDiscoveryContext
                    {
                        UseBuiltInRoots = false,
                        AdditionalRoots =
                        [
                            new SkillRootRegistration
                            {
                                RootPath = Path.Combine(temp.Path, ".alta", "skills"),
                                SourceKind = SkillSourceKind.Temporary,
                                SourceId = "temporary-tests",
                                Scope = SkillScopeKind.Temporary,
                                Precedence = 0,
                            },
                        ],
                    },
                },
                "sample-skill")
            .ConfigureAwait(false);

        Assert.IsNotNull(activation);
        StringAssert.Contains(activation.Payload, "<skill_content name=\"sample-skill\"");
        StringAssert.Contains(activation.Payload, "Base directory: file:///");
        StringAssert.Contains(activation.Payload, "<file>assets/visible.svg</file>");
        StringAssert.Contains(activation.Payload, "<file>references/guide.md</file>");
        Assert.IsFalse(activation.Payload.Contains("ignored.svg", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task SkillCatalog_DisabledSkillsRemainManageableButNotVisibleOrActivatable()
    {
        using var temp = TempDirectory.Create();
        var skillsRoot = Path.Combine(temp.Path, ".alta", "skills");
        var lowerPrecedenceRoot = Path.Combine(temp.Path, "user", "skills");
        await WriteSkillAsync(Path.Combine(skillsRoot, "sample-skill"), "sample-skill", "Disabled skill.").ConfigureAwait(false);
        await WriteSkillAsync(Path.Combine(lowerPrecedenceRoot, "sample-skill"), "sample-skill", "Shadowed disabled skill.").ConfigureAwait(false);
        var catalog = new SkillCatalog();
        var baseQuery = new SkillCatalogQuery
        {
            Discovery = new SkillDiscoveryContext
            {
                UseBuiltInRoots = false,
                AdditionalRoots =
                [
                    new SkillRootRegistration
                    {
                        RootPath = skillsRoot,
                        SourceKind = SkillSourceKind.Temporary,
                        SourceId = "temporary-tests",
                        Scope = SkillScopeKind.Temporary,
                        Precedence = 0,
                    },
                    new SkillRootRegistration
                    {
                        RootPath = lowerPrecedenceRoot,
                        SourceKind = SkillSourceKind.Temporary,
                        SourceId = "temporary-tests-low",
                        Scope = SkillScopeKind.Temporary,
                        Precedence = 10,
                    },
                ],
            },
            GlobalDisabledSkillNames = ["sample-skill"],
        };

        var managementDescriptors = await catalog.ListAsync(baseQuery with { IncludeDisabled = true }).ConfigureAwait(false);

        Assert.AreEqual(2, managementDescriptors.Count);
        Assert.IsTrue(managementDescriptors.All(static descriptor => !descriptor.IsEnabled));
        Assert.IsTrue(managementDescriptors.All(static descriptor => descriptor.IsDisabledGlobally));
        Assert.IsTrue(managementDescriptors.All(static descriptor => !descriptor.IsModelVisible));
        Assert.AreEqual(1, managementDescriptors.Count(static descriptor => descriptor.IsShadowed));

        var defaultDescriptors = await catalog.ListAsync(baseQuery).ConfigureAwait(false);
        Assert.AreEqual(0, defaultDescriptors.Count);

        var visibleDescriptors = await catalog.ListAsync(baseQuery with { ModelVisibleOnly = true }).ConfigureAwait(false);
        Assert.AreEqual(0, visibleDescriptors.Count);

        var activation = await catalog.ActivateAsync(baseQuery with { IncludeDisabled = true }, "sample-skill").ConfigureAwait(false);
        Assert.IsNull(activation);
    }

    private static async Task WriteSkillAsync(string skillRoot, string name, string description)
    {
        Directory.CreateDirectory(skillRoot);
        await File.WriteAllTextAsync(
            Path.Combine(skillRoot, "SKILL.md"),
            $$"""
            ---
            name: {{name}}
            description: {{description}}
            compatibility: Requires repository access.
            metadata:
              owner: tests
            allowed-tools: Read
            ---
            # {{ToTitle(name)}}

            {{description}}
            """).ConfigureAwait(false);
    }

    private static string ToTitle(string value)
        => string.Join(' ', value.Split('-').Select(static segment => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(segment)));

    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TempDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"CodeAlta.Catalog.Tests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new TempDirectory(path);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch
            {
            }
        }
    }
}
