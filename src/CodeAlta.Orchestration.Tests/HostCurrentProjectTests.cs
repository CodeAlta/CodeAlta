using CodeAlta.Catalog;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Orchestration.Tests;

[TestClass]
public sealed class HostCurrentProjectTests
{
    // A host starts on any folder that is not a project yet, also one of a single letter (the home folder of the
    // user "A" on Windows, #429), which made a slug too short to be valid; and the name of the folder is kept whole.
    [TestMethod]
    [DataRow("A", "project")]
    [DataRow("é", "project")]
    [DataRow("AA", "aa")]
    [DataRow("X.Y", "x-y")]
    public async Task AHostStartsOnAFolderThatIsNotAProject(string folder, string slug)
    {
        var root = Path.Combine(Path.GetTempPath(), "CodeAlta-host-current-project-" + Guid.NewGuid().ToString("N"));
        try
        {
            var home = Directory.CreateDirectory(Path.Combine(root, "home")).FullName;
            var project = Directory.CreateDirectory(Path.Combine(root, folder)).FullName;
            var builtins = Directory.CreateDirectory(Path.Combine(root, "builtin-skills")).FullName;
            var alta = Path.Combine(home, ".alta");
            await using var host = await CodeAltaHost.CreateAsync(new CodeAltaHostOptions
            {
                GlobalRoot = alta,
                StateRoot = alta,
                CurrentProjectPath = project,
                DiscoveryScope = new SessionDiscoveryScope(home, project),
                BuiltInSkillRoot = builtins,
                StartPlugins = false,
                OwnsLogging = false,
                IsHeadless = true,
            });

            Assert.AreEqual(slug, host.CurrentProject.Slug);
            Assert.IsTrue(CatalogSlugValidator.IsValid(host.CurrentProject.Slug));
            Assert.AreEqual(folder, host.CurrentProject.DisplayName);
            Assert.AreEqual(project, host.CurrentProject.ProjectPath);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
