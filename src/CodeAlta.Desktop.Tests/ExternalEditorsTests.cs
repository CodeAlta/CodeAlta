using System.Collections.Immutable;
using System.Diagnostics;
using CodeAlta.Catalog;
using CodeAlta.Desktop.Rpc;

namespace CodeAlta.Desktop.Tests;

/// <summary>The machines are described by lists of files; the programs that would start are recorded.</summary>
[TestClass]
public sealed class ExternalEditorsTests
{
    private const string Epoch = "8c1f2a4e-6b3d-4e5f-9a0b-1c2d3e4f5a6b";

    private static ExternalEditorEnvironment Windows(params string[] files)
    {
        var set = files.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new("windows", @"C:\Windows;relative\bin;C:\Users\me\AppData\Local\Programs\cursor\resources\app\bin;C:\Tools", @"C:\Users\me",
            @"C:\Users\me\AppData\Local", [@"C:\Program Files", @"C:\Program Files (x86)"], set.Contains, static _ => true);
    }

    // A machine of another system is described with its own paths, which only its own Path reads.
    private static void RequireWindows() { if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Windows paths."); }

    [TestMethod]
    public void Detect_FindsTheEditorsOfAWindowsMachine_InTheOrderOfTheList()
    {
        RequireWindows();
        var found = ExternalEditors.Detect(Windows(
            @"C:\Users\me\AppData\Local\Programs\cursor\resources\app\bin\cursor.cmd",
            @"C:\Users\me\AppData\Local\Programs\cursor\Cursor.exe",
            @"C:\Program Files\Microsoft VS Code\Code.exe",
            @"C:\Tools\subl.exe"));

        CollectionAssert.AreEqual(new[] { "vscode", "cursor", "sublimetext" }, found.Select(static editor => editor.Id).ToArray());
        Assert.AreEqual(@"C:\Program Files\Microsoft VS Code\Code.exe", found[0].Program);
        Assert.AreEqual("Visual Studio Code", found[0].Name);
        // The launcher script on PATH leads to the executable of the editor: a batch file is never what is started.
        Assert.AreEqual(@"C:\Users\me\AppData\Local\Programs\cursor\Cursor.exe", found[1].Program);
        Assert.AreEqual(@"C:\Tools\subl.exe", found[2].Program);
    }

    [TestMethod]
    public void Detect_LeavesOutALauncherScriptWithoutItsExecutable_AndRelativeFoldersOfPath()
    {
        RequireWindows();
        var found = ExternalEditors.Detect(Windows(
            @"C:\Users\me\AppData\Local\Programs\cursor\resources\app\bin\cursor.cmd",
            @"relative\bin\zed.exe"));

        Assert.AreEqual(0, found.Length);
        Assert.AreEqual(0, ExternalEditors.Detect(Windows()).Length);
    }

    [TestMethod]
    public void Detect_FindsTheApplicationsOfAMac_AndTheCommandsOfLinux()
    {
        if (OperatingSystem.IsWindows()) Assert.Inconclusive("Unix paths.");
        var mac = new ExternalEditorEnvironment("macos", "/usr/bin", "/Users/me", null, [],
            new HashSet<string>(StringComparer.Ordinal) { "/Users/me/Applications/Visual Studio Code.app/Contents/Resources/app/bin/code", "/Applications/Zed.app/Contents/MacOS/cli" }.Contains,
            static _ => true);
        var onMac = ExternalEditors.Detect(mac);
        CollectionAssert.AreEqual(new[] { "vscode", "zed" }, onMac.Select(static editor => editor.Id).ToArray());
        Assert.AreEqual("/Users/me/Applications/Visual Studio Code.app/Contents/Resources/app/bin/code", onMac[0].Program);

        var linux = new ExternalEditorEnvironment("linux", "/home/me/bin:/usr/bin", "/home/me", null, [],
            new HashSet<string>(StringComparer.Ordinal) { "/usr/bin/code", "/home/me/.local/bin/windsurf" }.Contains, static _ => true);
        var onLinux = ExternalEditors.Detect(linux);
        CollectionAssert.AreEqual(new[] { "vscode", "windsurf" }, onLinux.Select(static editor => editor.Id).ToArray());
        Assert.AreEqual("/usr/bin/code", onLinux[0].Program);
    }

    [TestMethod]
    public void Arguments_OpenTheFolder_AndAFileAtItsLine()
    {
        CollectionAssert.AreEqual(new[] { "/repo" }, ExternalEditors.Arguments(ExternalEditorStyle.VsCode, "/repo", null, 4, 2).ToArray());
        CollectionAssert.AreEqual(new[] { "/repo", "--goto", "/repo/a b.cs:12:3" }, ExternalEditors.Arguments(ExternalEditorStyle.VsCode, "/repo", "/repo/a b.cs", 12, 3).ToArray());
        CollectionAssert.AreEqual(new[] { "/repo", "--goto", "/repo/a.cs:12" }, ExternalEditors.Arguments(ExternalEditorStyle.VsCode, "/repo", "/repo/a.cs", 12, null).ToArray());
        CollectionAssert.AreEqual(new[] { "/repo", "--goto", "/repo/a.cs" }, ExternalEditors.Arguments(ExternalEditorStyle.VsCode, "/repo", "/repo/a.cs", null, 3).ToArray());
        CollectionAssert.AreEqual(new[] { "/repo", "/repo/a.cs:7" }, ExternalEditors.Arguments(ExternalEditorStyle.PathAndLine, "/repo", "/repo/a.cs", 7, null).ToArray());
    }

    [TestMethod]
    public void CustomArguments_CutTheTemplateFirst_SoThatAPathWithSpacesStaysOneArgument()
    {
        CollectionAssert.AreEqual(new[] { "--dir", @"C:\my repo", @"C:\my repo\a.cs:5" },
            ExternalEditors.CustomArguments("--dir {folder} {file}:{line}", @"C:\my repo", @"C:\my repo\a.cs", 5, null)!.ToArray());
        // An argument about a file is left out when only the folder is opened; a missing line or column is 1.
        CollectionAssert.AreEqual(new[] { "-n", "/r" }, ExternalEditors.CustomArguments("-n {folder} --line={line} {file}", "/r", null, null, null)!
            .Where(static argument => !argument.StartsWith("--line", StringComparison.Ordinal)).ToArray());
        CollectionAssert.AreEqual(new[] { "/r/f:1:1" }, ExternalEditors.CustomArguments("{file}:{line}:{column}", "/r", "/r/f", null, null)!.ToArray());
        // Quotes make one argument, and two quotes inside them are a quote; a quote that is not closed is refused.
        CollectionAssert.AreEqual(new[] { "a b", "say \"hi\"", "" }, ExternalEditors.CustomArguments("\"a b\" \"say \"\"hi\"\"\" \"\"", "/r", null, null, null)!.ToArray());
        Assert.IsNull(ExternalEditors.CustomArguments("\"{folder}", "/r", null, null, null));
        // Whatever a file is named, it stays inside its argument.
        CollectionAssert.AreEqual(new[] { "/r/a\" & calc.cs" }, ExternalEditors.CustomArguments("{file}", "/r", "/r/a\" & calc.cs", null, null)!.ToArray());
    }

    [TestMethod]
    public void Choice_IsAnEditorOfTheList_OrACustomProgram()
    {
        Assert.AreEqual(ExternalEditorChoice.None, ExternalEditorChoice.Valid(null, null, null));
        Assert.AreEqual(new ExternalEditorChoice("zed", null, null), ExternalEditorChoice.Valid("zed", "/ignored", "x"));
        Assert.IsNull(ExternalEditorChoice.Valid("notepad", null, null));
        Assert.AreEqual(new ExternalEditorChoice("custom", "/opt/ed", "{folder}"), ExternalEditorChoice.Valid("custom", " /opt/ed ", "  "));
        Assert.IsNull(ExternalEditorChoice.Valid("custom", " ", null));
        Assert.IsNull(ExternalEditorChoice.Valid("custom", "/opt/e\nd", null));
        Assert.IsNull(ExternalEditorChoice.Valid("custom", new string('a', ExternalEditorChoice.MaximumLength + 1), null));
    }

    [TestMethod]
    public void Preferences_KeepTheChosenEditor_OnlyWhenOneWasChosen()
    {
        var root = Directory.CreateTempSubdirectory("codealta-editor-").FullName;
        try
        {
            var file = Path.Combine(root, "preferences.json");
            Assert.AreEqual(ExternalEditorChoice.None, DesktopPreferences.Load(root).ExternalEditor);
            Assert.IsTrue((DesktopPreferences.Default with { ExternalEditor = new("cursor", null, null) }).Save(root));
            Assert.AreEqual("""{"onClose":"ask","externalEditor":{"id":"cursor"}}""", File.ReadAllText(file));
            Assert.AreEqual(new ExternalEditorChoice("cursor", null, null), DesktopPreferences.Load(root).ExternalEditor);

            var custom = new ExternalEditorChoice("custom", @"C:\Tools\My ""Editor""\ed.exe", "--goto \"{file}:{line}\" {folder}");
            Assert.IsTrue((DesktopPreferences.Default with { ExternalEditor = custom, Zoom = 125 }).Save(root));
            Assert.AreEqual(custom, DesktopPreferences.Load(root).ExternalEditor);
            Assert.AreEqual(125, DesktopPreferences.Load(root).Zoom);

            // An editor that is none is forgotten; the rest of the file is kept.
            File.WriteAllText(file, """{"onClose":"exit","externalEditor":{"id":"notepad"}}""");
            Assert.AreEqual((DesktopCloseBehavior.Exit, ExternalEditorChoice.None), (DesktopPreferences.Load(root).OnClose, DesktopPreferences.Load(root).ExternalEditor));
            File.WriteAllText(file, """{"onClose":"exit","externalEditor":"code"}""");
            Assert.AreEqual(ExternalEditorChoice.None, DesktopPreferences.Load(root).ExternalEditor);

            Assert.IsTrue(DesktopPreferences.Default.Save(root));
            Assert.AreEqual("""{"onClose":"ask"}""", File.ReadAllText(file));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task Open_StartsTheChosenEditor_ElseTheFirstFound()
    {
        RequireWindows();
        var started = new List<ProcessStartInfo>();
        var choice = ExternalEditorChoice.None;
        var editors = new ExternalEditors(() => choice, Windows(@"C:\Program Files\Microsoft VS Code\Code.exe", @"C:\Tools\subl.exe"), start => { started.Add(start); return true; });

        Assert.AreEqual("ok", await editors.OpenAsync(@"C:\repo", @"C:\repo\a.cs", 3, 4, CancellationToken.None));
        Assert.AreEqual(@"C:\Program Files\Microsoft VS Code\Code.exe", started[^1].FileName);
        CollectionAssert.AreEqual(new[] { @"C:\repo", "--goto", @"C:\repo\a.cs:3:4" }, started[^1].ArgumentList.ToArray());
        Assert.IsFalse(started[^1].UseShellExecute);
        Assert.AreEqual(@"C:\repo", started[^1].WorkingDirectory);

        choice = new("sublimetext", null, null);
        Assert.AreEqual("ok", await editors.OpenAsync(@"C:\repo", null, null, null, CancellationToken.None));
        Assert.AreEqual(@"C:\Tools\subl.exe", started[^1].FileName);

        // An editor that was chosen and is no longer installed gives way to the first one found.
        choice = new("zed", null, null);
        Assert.AreEqual("ok", await editors.OpenAsync(@"C:\repo", null, null, null, CancellationToken.None));
        Assert.AreEqual(@"C:\Program Files\Microsoft VS Code\Code.exe", started[^1].FileName);
        Assert.AreEqual(3, started.Count);
    }

    [TestMethod]
    public async Task Open_RefusesACustomBatchFile_AndSaysWhenThereIsNoEditor()
    {
        RequireWindows();
        var started = new List<ProcessStartInfo>();
        var choice = new ExternalEditorChoice("custom", @"C:\Tools\ed.cmd", "{folder}");
        var editors = new ExternalEditors(() => choice, Windows(@"C:\Tools\ed.cmd", @"C:\Tools\ed.exe"), start => { started.Add(start); return true; });

        Assert.AreEqual("refused", await editors.OpenAsync(@"C:\repo", null, null, null, CancellationToken.None));
        choice = new("custom", @"C:\Tools\missing.exe", "{folder}");
        Assert.AreEqual("refused", await editors.OpenAsync(@"C:\repo", null, null, null, CancellationToken.None));
        choice = new("custom", @"C:\Tools\ed.exe", "-w \"{folder}\" {file}");
        Assert.AreEqual("ok", await editors.OpenAsync(@"C:\my repo", null, null, null, CancellationToken.None));
        CollectionAssert.AreEqual(new[] { "-w", @"C:\my repo" }, started.Single().ArgumentList.ToArray());

        var none = new ExternalEditors(() => ExternalEditorChoice.None, Windows(), _ => throw new AssertFailedException("nothing starts"));
        Assert.AreEqual("no_editor", await none.OpenAsync(@"C:\repo", null, null, null, CancellationToken.None));
        Assert.IsNull(await none.CurrentAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task Found_IsLookedForOnce_UntilItIsAskedAgain()
    {
        RequireWindows();
        var looks = 0;
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var environment = new ExternalEditorEnvironment("windows", null, null, @"C:\Local", [], path => { Interlocked.Increment(ref looks); return files.Contains(path); }, static _ => true);
        var editors = new ExternalEditors(() => ExternalEditorChoice.None, environment, static _ => true);

        Assert.AreEqual(0, looks, "nothing is looked for before it is asked");
        Assert.AreEqual(0, (await editors.FoundAsync(false, CancellationToken.None)).Length);
        var first = looks;
        Assert.IsTrue(first > 0);
        files.Add(@"C:\Local\Programs\Zed\Zed.exe");
        Assert.AreEqual(0, (await editors.FoundAsync(false, CancellationToken.None)).Length);
        Assert.AreEqual(first, looks);
        Assert.AreEqual("zed", (await editors.FoundAsync(true, CancellationToken.None)).Single().Id);
    }

    [TestMethod]
    public async Task Service_OpensTheFolderOfASession_OrOfAProject()
    {
        RequireWindows();
        var root = Directory.CreateTempSubdirectory("codealta-editors-rpc-").FullName;
        try
        {
            var projects = new ProjectCatalog(new CatalogOptions { GlobalRoot = Directory.CreateDirectory(Path.Combine(root, "global")).FullName });
            var project = await projects.UpsertFromPathAsync(Directory.CreateDirectory(Path.Combine(root, "project")).FullName);
            var worktree = Directory.CreateDirectory(Path.Combine(root, "worktree")).FullName;
            var started = new List<ProcessStartInfo>();
            var shown = new List<string>();
            var chosen = new List<ExternalEditorChoice>();
            var choice = ExternalEditorChoice.None;
            var editors = new ExternalEditors(() => choice, Windows(@"C:\Program Files\Microsoft VS Code\Code.exe"), start => { started.Add(start); return true; });
            var service = new ExternalEditorsService(editors, value => { chosen.Add(value); choice = value; }, projects, Epoch,
                (sessionId, _) => Task.FromResult(sessionId == "s1" ? worktree : sessionId == "gone" ? Path.Combine(root, "removed") : null),
                folder => { shown.Add(folder); return true; });

            Assert.AreEqual("ok", (await service.OpenFolderAsync(new(Epoch, project.Id, "s1", "editor"), CancellationToken.None)).Status);
            Assert.AreEqual(worktree, started.Single().ArgumentList.Single());
            // A session whose worktree is gone opens the folder of its project.
            Assert.AreEqual("ok", (await service.OpenFolderAsync(new(Epoch, project.Id, "gone", "explorer"), CancellationToken.None)).Status);
            Assert.AreEqual(project.ProjectPath, shown.Single());
            Assert.AreEqual("missing_session", (await service.OpenFolderAsync(new(Epoch, project.Id, "other", "editor"), CancellationToken.None)).Status);
            Assert.AreEqual("stale_epoch", (await service.OpenFolderAsync(new("old", project.Id, null, "editor"), CancellationToken.None)).Status);
            Assert.AreEqual("invalid", (await service.OpenFolderAsync(new(Epoch, project.Id, null, "shell"), CancellationToken.None)).Status);
            Assert.AreEqual("invalid", (await service.OpenFolderAsync(new(Epoch, null, null, "editor"), CancellationToken.None)).Status);

            var listed = await service.ListAsync(new(), CancellationToken.None);
            Assert.AreEqual(("ok", "vscode", (string?)null), (listed.Status, listed.Current!.Id, listed.Chosen));
            Assert.AreEqual("invalid", (await service.SetAsync(new("notepad"), CancellationToken.None)).Status);
            Assert.AreEqual(0, chosen.Count);
            var set = await service.SetAsync(new("custom", @"C:\Tools\ed.exe", null), CancellationToken.None);
            Assert.AreEqual(("ok", "custom", @"C:\Tools\ed.exe", "{folder}"), (set.Status, set.Chosen, set.CustomProgram, set.CustomArguments));
            Assert.AreEqual("ed", set.Current!.Name);

            Assert.AreEqual("unavailable", (await new ExternalEditorsService().ListAsync(new(), CancellationToken.None)).Status);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
