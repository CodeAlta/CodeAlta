using CodeAlta.Agent;
using CodeAlta.Orchestration.Hosting;

namespace CodeAlta.Tests;

[TestClass]
public sealed class CodeAltaHostSessionJournalsTests
{
    [TestMethod]
    public async Task SessionJournals_ListsEveryJournalOfTheStateRoot_EvenOfAProjectThatIsNotInTheCatalog()
    {
        var temp = Path.Combine(Path.GetTempPath(), "codealta-host-journals-" + Guid.NewGuid().ToString("N"));
        var projectRoot = Path.Combine(temp, "project");
        Directory.CreateDirectory(projectRoot);
        try
        {
            var options = new CodeAltaHostOptions
            {
                GlobalRoot = Path.Combine(temp, "home"),
                CurrentProjectPath = projectRoot,
                IsHeadless = true,
                HasInteractiveUi = false,
                PluginSafeMode = true,
                StartPlugins = false,
            };
            await using var host = await CodeAltaHost.CreateAsync(options, CancellationToken.None);
            var folder = Path.Combine(host.CatalogOptions.StateRoot, "sessions", "2026", "10", "09");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "orphan-session.jsonl");
            await File.WriteAllTextAsync(path, "{\"$type\":\"raw\",\"backendEventType\":\"codealta.sessionHeader\",\"raw\":{\"project_ref\":\"a-project-that-was-removed\"}}\n");

            var files = new List<SessionJournalFile>();
            await foreach (var file in host.SessionJournals.ListAsync(CancellationToken.None))
            {
                files.Add(file);
            }

            var listed = files.Single(static file => file.SessionId == "orphan-session");
            Assert.AreEqual(path, listed.Path);
            await using var stream = await host.SessionJournals.OpenAsync("orphan-session", 0, CancellationToken.None);
            Assert.IsNotNull(stream);
            Assert.AreEqual(0, stream.Position);
            Assert.AreEqual(listed.Length, stream.Length);
        }
        finally
        {
            try
            {
                Directory.Delete(temp, recursive: true);
            }
            catch (IOException)
            {
                // The host may still hold a file of its state for a moment; the temporary folder is cleaned later.
            }
        }
    }
}
