using System.Reflection;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Runtime.SystemPrompts;
using CodeAlta.Tui.Views;
using XenoAtom.Terminal;
using XenoAtom.Terminal.Backends;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Extensions.CodeEditor.TextMateSharp;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Hosting;

namespace CodeAlta.Tests;

[TestClass]
public sealed class PromptManagementDialogTests
{
    [TestMethod]
    public void BodyEditorUsesMarkdownSyntaxHighlighter()
    {
        using var tempDirectory = TempDirectory.Create();
        var promptDialog = CreatePromptDialog(tempDirectory.Path);

        var editor = GetPrivateField<CodeEditor>(promptDialog, "_bodyEditor");
        var highlighter = Assert.IsInstanceOfType<TextMateCodeEditorSyntaxHighlighter>(editor.SyntaxHighlighter);

        Assert.AreEqual("markdown", highlighter.Options.LanguageId);
        Assert.AreEqual("prompt.md", highlighter.Options.FileName);
    }

    [TestMethod]
    public void BodyEditorRefreshesMarkdownSyntaxHighlighterWhenPromptBodyLoads()
    {
        using var tempDirectory = TempDirectory.Create();
        WritePrompt(tempDirectory.Path, body: "# Heading\n\n```csharp\nvar value = 1;\n```");
        var promptDialog = CreatePromptDialog(tempDirectory.Path);
        var editor = GetPrivateField<CodeEditor>(promptDialog, "_bodyEditor");
        var initialHighlighter = Assert.IsInstanceOfType<TextMateCodeEditorSyntaxHighlighter>(editor.SyntaxHighlighter);

        using var terminalSession = Terminal.Open(new InMemoryTerminalBackend(new TerminalSize(120, 40)), new TerminalOptions { ImplicitStartInput = true }, force: true);
        var app = new TerminalApp(
            new TextBlock("Host"),
            terminalSession.Instance,
            new TerminalAppOptions
            {
                HostKind = TerminalHostKind.Fullscreen,
            });

        InvokeTerminalApp(app, "BeginRun");
        try
        {
            promptDialog.Show();
            TickTerminalApp(app);

            var loadedHighlighter = Assert.IsInstanceOfType<TextMateCodeEditorSyntaxHighlighter>(editor.SyntaxHighlighter);
            Assert.AreNotSame(initialHighlighter, loadedHighlighter);
            Assert.AreEqual("markdown", loadedHighlighter.Options.LanguageId);
            StringAssert.Contains(GetEditorText(editor), "```csharp");
        }
        finally
        {
            InvokeTerminalApp(app, "EndRun");
        }
    }

    [TestMethod]
    public void DeleteKeyInsideBodyEditorDeletesTextInsteadOfPrompt()
    {
        using var tempDirectory = TempDirectory.Create();
        var promptPath = WritePrompt(tempDirectory.Path, body: "abc");
        var promptDialog = CreatePromptDialog(tempDirectory.Path);

        using var terminalSession = Terminal.Open(new InMemoryTerminalBackend(new TerminalSize(120, 40)), new TerminalOptions { ImplicitStartInput = true }, force: true);
        var app = new TerminalApp(
            new TextBlock("Host"),
            terminalSession.Instance,
            new TerminalAppOptions
            {
                HostKind = TerminalHostKind.Fullscreen,
            });

        InvokeTerminalApp(app, "BeginRun");
        try
        {
            promptDialog.Show();
            TickTerminalApp(app);
            var editor = GetPrivateField<CodeEditor>(promptDialog, "_bodyEditor");
            editor.CaretIndex = 0;
            app.Focus(editor);

            DispatchKeyEvent(app, TerminalKey.Delete);

            Assert.AreEqual("bc", GetEditorText(editor));
            Assert.AreEqual(1, CountDialogs(app));
            Assert.IsTrue(File.Exists(promptPath));
        }
        finally
        {
            InvokeTerminalApp(app, "EndRun");
        }
    }

    [TestMethod]
    public void DeleteKeyOnPromptListOpensDeleteConfirmation()
    {
        using var tempDirectory = TempDirectory.Create();
        var promptPath = WritePrompt(tempDirectory.Path, body: "abc");
        var promptDialog = CreatePromptDialog(tempDirectory.Path);

        using var terminalSession = Terminal.Open(new InMemoryTerminalBackend(new TerminalSize(120, 40)), new TerminalOptions { ImplicitStartInput = true }, force: true);
        var app = new TerminalApp(
            new TextBlock("Host"),
            terminalSession.Instance,
            new TerminalAppOptions
            {
                HostKind = TerminalHostKind.Fullscreen,
            });

        InvokeTerminalApp(app, "BeginRun");
        try
        {
            promptDialog.Show();
            TickTerminalApp(app);
            var promptList = GetPrivateField<Visual>(promptDialog, "_promptList");
            app.Focus(promptList);

            DispatchKeyEvent(app, TerminalKey.Delete);

            Assert.AreEqual(2, CountDialogs(app));
            Assert.IsTrue(HasDialogWithTitle(app, "Delete Prompt?"));
            Assert.IsTrue(File.Exists(promptPath));
        }
        finally
        {
            InvokeTerminalApp(app, "EndRun");
        }
    }

    [TestMethod]
    public void SystemPromptTabDisplaysGlobalSystemPromptForEditing()
    {
        using var tempDirectory = TempDirectory.Create();
        var systemPromptPath = WriteSystemPrompt(tempDirectory.Path, body: "abc");
        var promptDialog = CreatePromptDialog(tempDirectory.Path);

        using var terminalSession = Terminal.Open(new InMemoryTerminalBackend(new TerminalSize(120, 40)), new TerminalOptions { ImplicitStartInput = true }, force: true);
        var app = new TerminalApp(
            new TextBlock("Host"),
            terminalSession.Instance,
            new TerminalAppOptions
            {
                HostKind = TerminalHostKind.Fullscreen,
            });

        InvokeTerminalApp(app, "BeginRun");
        try
        {
            promptDialog.Show();
            TickTerminalApp(app);
            var tabs = app.Root.EnumerateVisualsDepthFirst().OfType<TabControl>().First();
            tabs.SelectedIndex = 1;
            TickTerminalApp(app);
            var editor = GetPrivateField<CodeEditor>(promptDialog, "_bodyEditor");
            editor.CaretIndex = 0;
            app.Focus(editor);

            DispatchKeyEvent(app, TerminalKey.Delete);

            Assert.AreEqual("bc", GetEditorText(editor));
            Assert.AreEqual(1, CountDialogs(app));
            Assert.IsTrue(File.Exists(systemPromptPath));
        }
        finally
        {
            InvokeTerminalApp(app, "EndRun");
        }
    }

    [TestMethod]
    public void ActualDialogSaveRetainsDirtyTextOnConflictAndReloadsOnceAfterConfirmedRetry()
    {
        using var tempDirectory = TempDirectory.Create();
        var path = WritePrompt(tempDirectory.Path, "original");
        var changes = 0;
        var promptDialog = CreatePromptDialog(tempDirectory.Path, () => changes++);
        using var terminalSession = Terminal.Open(new InMemoryTerminalBackend(new TerminalSize(120, 40)), new TerminalOptions { ImplicitStartInput = true }, force: true);
        var app = new TerminalApp(new TextBlock("Host"), terminalSession.Instance, new TerminalAppOptions { HostKind = TerminalHostKind.Fullscreen });
        InvokeTerminalApp(app, "BeginRun");
        try
        {
            promptDialog.Show();
            TickTerminalApp(app);
            var editing = GetPrivateField<PromptEditingSession>(promptDialog, "_editingSession");
            InvokePromptDialog(promptDialog, "SetBodyText", "dirty");
            File.WriteAllText(path, "---\nname: External\n---\nnewer");
            File.SetLastWriteTimeUtc(path, editing.Snapshot.File.LastWriteTimeUtc.UtcDateTime);
            InvokePromptDialog(promptDialog, "SaveSelectedPrompt");
            Assert.AreEqual(0, changes);
            Assert.AreSame(editing, GetPrivateField<PromptEditingSession>(promptDialog, "_editingSession"));
            Assert.AreEqual("original", GetPrivateField<string>(promptDialog, "_loadedBody"));
            Assert.AreEqual("dirty", GetEditorText(GetPrivateField<CodeEditor>(promptDialog, "_bodyEditor")));
            Assert.IsTrue(HasDialogWithTitle(app, "Prompt File Changed"));
            var observed = editing.ConflictRevision;
            Assert.IsNotNull(observed);
            DispatchKeyEvent(app, TerminalKey.Escape); // Cancel never acknowledges the conflict.
            Assert.AreSame(editing, GetPrivateField<PromptEditingSession>(promptDialog, "_editingSession"));
            InvokePromptDialog(promptDialog, "SaveEditingPrompt", editing, editing.Snapshot.Content with { Body = "dirty" }, observed);
            Assert.AreEqual(1, changes);
            Assert.AreNotSame(editing, GetPrivateField<PromptEditingSession>(promptDialog, "_editingSession"));
            Assert.AreEqual("dirty", GetPrivateField<string>(promptDialog, "_loadedBody"));
        }
        finally { InvokeTerminalApp(app, "EndRun"); }
    }

    [TestMethod]
    public void ActualCreateCollisionRetainsFieldsAndDoesNotNotifyOrReload()
    {
        using var tempDirectory = TempDirectory.Create();
        var path = WritePrompt(tempDirectory.Path, "original");
        var before = File.ReadAllBytes(path);
        var changes = 0;
        var promptDialog = CreatePromptDialog(tempDirectory.Path, () => changes++);
        InvokePromptDialog(promptDialog, "ReloadAgentPrompts", path);
        InvokePromptDialog(promptDialog, "SetBodyText", "dirty selected text");
        var editing = GetPrivateField<PromptEditingSession>(promptDialog, "_editingSession");
        var id = new TextBox("custom");
        var name = new TextBox("New name");
        var validation = new TextBlock(string.Empty);
        var tabType = typeof(PromptManagementDialog).GetNestedType("PromptResourceTab", BindingFlags.NonPublic)!;
        InvokePromptDialog(promptDialog, "CreatePromptFromDialog", new Dialog(), PromptResourceScope.Global,
            Enum.ToObject(tabType, 0), id, name, new TextBox("description"), new TextBox("default"), validation);
        Assert.AreEqual(0, changes);
        StringAssert.Contains(validation.Text, "already exists");
        Assert.AreEqual("New name", name.Text);
        Assert.AreEqual("custom", id.Text);
        Assert.AreSame(editing, GetPrivateField<PromptEditingSession>(promptDialog, "_editingSession"));
        Assert.AreEqual("dirty selected text", GetEditorText(GetPrivateField<CodeEditor>(promptDialog, "_bodyEditor")));
        CollectionAssert.AreEqual(before, File.ReadAllBytes(path));
    }

    [TestMethod]
    public void SelectionLoadsMetadataAndRevisionTogetherRatherThanStaleDiscoveryText()
    {
        using var tempDirectory = TempDirectory.Create();
        var path = WritePrompt(tempDirectory.Path, "old body");
        var promptDialog = CreatePromptDialog(tempDirectory.Path);
        InvokePromptDialog(promptDialog, "ReloadAgentPrompts", path);
        File.WriteAllText(path, "---\nname: New name\ndescription: New description\nsystem: other\nmode: append\n---\nnew body");
        // The row still has old discovery text, but selecting it must use one fresh file snapshot.
        InvokePromptDialog(promptDialog, "LoadSelectedAgentPrompt", 0);
        var editing = GetPrivateField<PromptEditingSession>(promptDialog, "_editingSession");
        Assert.AreEqual("New name", GetPrivateField<TextBox>(promptDialog, "_nameBox").Text);
        Assert.AreEqual("New description", GetPrivateField<TextBox>(promptDialog, "_descriptionBox").Text);
        Assert.AreEqual("other", GetPrivateField<TextBox>(promptDialog, "_systemBox").Text);
        Assert.AreEqual("new body", GetEditorText(GetPrivateField<CodeEditor>(promptDialog, "_bodyEditor")));
        Assert.IsTrue(editing.Snapshot.Content.Append);
        Assert.AreEqual(new TextFileCodec().Load(path).Revision, editing.Snapshot.File.Revision);
    }

    [TestMethod]
    public void SystemPromptCatalogListsBuiltInAndOverrideWithoutOverwritingBuiltIn()
    {
        using var tempDirectory = TempDirectory.Create();
        var appBase = Path.Combine(tempDirectory.Path, "app");
        var builtInPath = WriteSystemPromptRoot(Path.Combine(appBase, "content", "prompts"), "default", "built-in body");
        var overridePath = WriteSystemPromptRoot(Path.Combine(tempDirectory.Path, "global", "prompts"), "default", "override body");
        var catalog = new AgentPromptCatalog(new FileSystemPromptContentLocator(appBase));

        var prompts = catalog.ListSystemPrompts(new AgentPromptCatalogQuery
        {
            AppBaseDirectory = appBase,
            UserProfileRoot = Path.Combine(tempDirectory.Path, "profile"),
            UserCodeAltaRoot = Path.Combine(tempDirectory.Path, "global"),
            ProjectRoot = Path.Combine(tempDirectory.Path, "project"),
        });

        Assert.AreEqual(2, prompts.Count);
        var builtIn = prompts.Single(prompt => prompt.IsBuiltIn);
        var userGlobal = prompts.Single(prompt => prompt.SourceKind == AgentPromptSourceKind.UserGlobal);
        Assert.AreEqual(Path.GetFullPath(builtInPath), builtIn.SourcePath);
        Assert.AreEqual(Path.GetFullPath(overridePath), userGlobal.SourcePath);
        Assert.IsTrue(builtIn.IsShadowed);
        Assert.IsFalse(userGlobal.IsShadowed);
        Assert.AreEqual("built-in body", builtIn.Body);
        Assert.AreEqual("override body", userGlobal.Body);
    }

    private static PromptManagementDialog CreatePromptDialog(string root, Action? onPromptsChanged = null)
    {
        var globalRoot = Path.Combine(root, "global");
        var appBase = Path.Combine(root, "app");
        var catalog = new AgentPromptCatalog(new FileSystemPromptContentLocator(appBase));
        return new PromptManagementDialog(
            new CatalogOptions { GlobalRoot = globalRoot },
            new TextFileCodec(),
            static () => null,
            static () => new Rectangle(0, 0, 120, 40),
            static () => null,
            onPromptsChanged ?? (static () => { }),
            static (_, _) => { },
            catalog,
            new AgentPromptCatalogQuery
            {
                AppBaseDirectory = appBase,
                UserProfileRoot = Path.Combine(root, "profile"),
                UserCodeAltaRoot = globalRoot,
                ProjectRoot = Path.Combine(root, "project"),
            });
    }

    private static string WritePrompt(string root, string body)
    {
        var promptDirectory = Path.Combine(root, "global", "prompts", "agents");
        Directory.CreateDirectory(promptDirectory);
        var promptPath = Path.Combine(promptDirectory, "custom.prompt.md");
        File.WriteAllText(promptPath, $"---\nname: Custom Prompt\n---\n{body}\n");
        return promptPath;
    }

    private static string WriteSystemPrompt(string root, string body)
        => WriteSystemPromptRoot(Path.Combine(root, "global", "prompts"), "custom", body);

    private static string WriteSystemPromptRoot(string instructionsRoot, string id, string body)
    {
        var promptDirectory = Path.Combine(instructionsRoot, "system");
        Directory.CreateDirectory(promptDirectory);
        var promptPath = Path.Combine(promptDirectory, id + ".system-prompt.md");
        File.WriteAllText(promptPath, body + "\n");
        return promptPath;
    }

    private static void DispatchKeyEvent(TerminalApp app, TerminalKey key)
        => InvokeTerminalApp(app, "DispatchKeyEvent", new TerminalKeyEvent { Key = key }, true);

    private static void InvokePromptDialog(PromptManagementDialog dialog, string method, params object?[] arguments)
        => typeof(PromptManagementDialog).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(dialog, arguments);

    private static int CountDialogs(TerminalApp app)
        => app.Root.EnumerateVisualsDepthFirst().OfType<Dialog>().Count();

    private static bool HasDialogWithTitle(TerminalApp app, string title)
        => app.Root.EnumerateVisualsDepthFirst().OfType<Dialog>().Any(dialog => string.Equals(GetDialogTitle(dialog), title, StringComparison.Ordinal));

    private static string? GetDialogTitle(Dialog dialog)
        => dialog.Title switch
        {
            TextBlock textBlock => textBlock.Text,
            Markup markup => markup.Text,
            _ => null,
        };

    private static string GetEditorText(CodeEditor editor)
    {
        var snapshot = editor.TextDocument.CurrentSnapshot;
        if (snapshot.Length == 0)
        {
            return string.Empty;
        }

        return string.Create(snapshot.Length, snapshot, static (span, currentSnapshot) => currentSnapshot.CopyTo(0, span));
    }

    private static T GetPrivateField<T>(object instance, string fieldName)
        where T : class
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(field);
        return Assert.IsInstanceOfType<T>(field.GetValue(instance));
    }

    private static void InvokeTerminalApp(TerminalApp app, string methodName, params object[] arguments)
    {
        var method = typeof(TerminalApp).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(method);
        method.Invoke(app, arguments);
    }

    private static void TickTerminalApp(TerminalApp app)
    {
        var method = typeof(TerminalApp).GetMethod("Tick", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(method);
        method.Invoke(app, [null]);
    }

    private sealed class TempDirectory(string path) : IDisposable
    {
        public string Path { get; } = path;

        public static TempDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"CodeAlta.Tests.{Guid.NewGuid():N}");
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
