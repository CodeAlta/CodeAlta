using System.Reflection;
using System.Text;
using CodeAlta.Catalog;
using CodeAlta.Tui.Views;
using XenoAtom.Terminal;
using XenoAtom.Terminal.Backends;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Hosting;
using XenoAtom.Terminal.UI.Styling;

namespace CodeAlta.Tests;

[TestClass]
public sealed class ConfigRecoveryDialogTests
{
    private string _root = null!;
    private string ConfigPath => Path.Combine(_root, "config.toml");

    [TestInitialize]
    public void Initialize() => _root = Directory.CreateTempSubdirectory("codealta-config-recovery-").FullName;

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_root, recursive: true);

    [TestMethod]
    public void EditingInvalidConfigToValidConfig_EnablesSave()
    {
        File.WriteAllText(ConfigPath, "@");
        var dialog = new ConfigRecoveryDialog(
            LoadRecovery(),
            saveAndContinue: static () => { },
            exit: static () => { });

        Assert.IsFalse(dialog.CanSave());

        var editor = GetEditor(dialog);
        const string validConfig = """
            [providers.openai]
            type = "openai-chat"
            api_key_env = "OPENAI_API_KEY"
            api_url = "https://api.openai.com/v1"
            """;
        editor.TextDocument.Replace(0, editor.TextDocument.CurrentSnapshot.Length, validConfig.AsSpan());

        Assert.IsTrue(dialog.CanSave());
    }

    [TestMethod]
    public void SaveCallback_ExternalEditDoesNotContinueOrDiscardEdits()
    {
        File.WriteAllText(ConfigPath, "@");
        var continued = false;
        var dialog = new ConfigRecoveryDialog(LoadRecovery(),
            () => continued = true, () => { });
        var editor = GetEditor(dialog);
        editor.TextDocument.Replace(0, 1, "# repaired".AsSpan());
        File.WriteAllText(ConfigPath, "# external");

        dialog.SaveAndContinue();

        Assert.IsFalse(continued);
        Assert.AreEqual("# external", File.ReadAllText(ConfigPath));
        Assert.AreEqual("# repaired", CodeAlta.Tui.Presentation.Editing.CodeEditorFactory.GetText(editor));
        StringAssert.Contains(dialog.BuildStatusMarkup(), "Config changed on disk");
        Assert.IsTrue(dialog.CanSave());
    }

    [TestMethod]
    [DataRow(65001, false)]
    [DataRow(65001, true)]
    [DataRow(1200, true)]
    [DataRow(1201, true)]
    [DataRow(12000, true)]
    [DataRow(12001, true)]
    public void SaveCallback_PreservesEditorTextCommentsUnknownFieldsAndEncoding(int codePage, bool bom)
    {
        var encoding = codePage == 65001 ? new UTF8Encoding(bom) : Encoding.GetEncoding(codePage);
        const string original = "# café\r\n[unknown]\nvalue = 'retained'\r\n# no final newline";
        var bytes = encoding.GetPreamble().Concat(encoding.GetBytes(original)).ToArray();
        File.WriteAllBytes(ConfigPath, bytes);
        var continued = false;
        var dialog = new ConfigRecoveryDialog(LoadRecovery(), () => continued = true, () => { });
        Assert.AreEqual(original, GetText(dialog));
        dialog.SaveAndContinue();
        Assert.IsTrue(continued);
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(ConfigPath));
    }

    [TestMethod]
    public void ReloadCallback_ConfirmsDiscardAndDoesNotContinueUntilSaved()
    {
        File.WriteAllText(ConfigPath, "@");
        var continued = 0;
        Action? discard = null;
        var dialog = new ConfigRecoveryDialog(LoadRecovery(), () => continued++, () => { }, action => discard = action);
        SetText(dialog, "# repaired");
        File.WriteAllText(ConfigPath, "# external");
        dialog.SaveAndContinue();
        dialog.RequestReload();
        Assert.IsNotNull(discard);
        Assert.AreEqual("# repaired", GetText(dialog));
        Assert.AreEqual(0, continued); // Cancel/ignore confirmation keeps edits.
        discard();
        Assert.AreEqual("# external", GetText(dialog));
        Assert.AreEqual(0, continued); // Reload is not startup admission.
        SetText(dialog, "# reapplied");
        dialog.SaveAndContinue();
        Assert.AreEqual(1, continued);
        Assert.AreEqual("# reapplied", File.ReadAllText(ConfigPath));
    }

    [TestMethod]
    public void SaveCallback_ReadOnlyFailureRetainsValidEditsAndAllowsRetry()
    {
        File.WriteAllText(ConfigPath, "@");
        var continued = 0;
        var dialog = new ConfigRecoveryDialog(LoadRecovery(), () => continued++, () => { });
        SetText(dialog, "# repaired");
        File.SetAttributes(ConfigPath, FileAttributes.ReadOnly);
        try
        {
            dialog.SaveAndContinue();
            Assert.AreEqual(0, continued);
            Assert.AreEqual("# repaired", GetText(dialog));
            Assert.AreEqual("@", File.ReadAllText(ConfigPath));
            Assert.IsTrue(dialog.CanSave());
            StringAssert.Contains(dialog.BuildStatusMarkup(), "Unable to save config file");
            SetText(dialog, "# edited again");
            StringAssert.Contains(dialog.BuildStatusMarkup(), "Unable to save config file");
        }
        finally { File.SetAttributes(ConfigPath, FileAttributes.Normal); }
        dialog.SaveAndContinue();
        Assert.AreEqual(1, continued);
        Assert.AreEqual("# edited again", File.ReadAllText(ConfigPath));
    }

    [TestMethod]
    public void ExitCallback_NeverSavesOrContinues()
    {
        File.WriteAllText(ConfigPath, "@");
        var continued = false;
        var exited = false;
        var dialog = DeferredCodeAltaApp.PrepareConfigRecovery(new ConfigRecoveryService(_root, new TextFileCodec()),
            () => continued = true, () => exited = true);
        Assert.IsNotNull(dialog);
        SetText(dialog, "# repaired");
        dialog.Exit();
        Assert.IsTrue(exited);
        Assert.IsFalse(continued);
        Assert.AreEqual("@", File.ReadAllText(ConfigPath));
    }

    [TestMethod]
    public void Preflight_DecodeFailureRequiresExplicitReloadBeforeSave()
    {
        File.WriteAllBytes(ConfigPath, [0xff]);
        var continued = 0;
        var dialog = DeferredCodeAltaApp.PrepareConfigRecovery(new ConfigRecoveryService(_root, new TextFileCodec()),
            () => continued++, () => { });
        Assert.IsNotNull(dialog);
        Assert.IsFalse(dialog.CanSave());
        dialog.SaveAndContinue();
        Assert.AreEqual(0, continued);
        File.WriteAllText(ConfigPath, "# repaired externally");
        Assert.IsFalse(dialog.CanSave());
        dialog.RequestReload(); // Clean empty editor; no confirmation is needed.
        Assert.IsTrue(dialog.CanSave());
        Assert.AreEqual(0, continued);
        dialog.SaveAndContinue();
        Assert.AreEqual(1, continued);
    }

    [TestMethod]
    public void ReloadCallback_FailedReadRetainsEditsButRevokesSaveBaseline()
    {
        File.WriteAllText(ConfigPath, "@");
        var continued = 0;
        var dialog = new ConfigRecoveryDialog(LoadRecovery(), () => continued++, () => { }, action => action());
        SetText(dialog, "# my edits");
        File.WriteAllBytes(ConfigPath, [0xff]);
        dialog.RequestReload();
        Assert.AreEqual("# my edits", GetText(dialog));
        Assert.IsFalse(dialog.CanSave());
        StringAssert.Contains(dialog.BuildStatusMarkup(), "Unable to load or create config file");
        File.WriteAllText(ConfigPath, "@");
        dialog.SaveAndContinue();
        Assert.AreEqual(0, continued);
        dialog.RequestReload();
        SetText(dialog, "# reapplied");
        dialog.SaveAndContinue();
        Assert.AreEqual(1, continued);
    }

    [TestMethod]
    public void Preflight_DefaultCreateFailureReturnsRecoveryInsteadOfAdmission()
    {
        var blockedRoot = Path.Combine(_root, "blocked");
        File.WriteAllText(blockedRoot, "block directory");
        var continued = false;
        var dialog = DeferredCodeAltaApp.PrepareConfigRecovery(new ConfigRecoveryService(blockedRoot, new TextFileCodec()),
            () => continued = true, () => { });
        Assert.IsNotNull(dialog);
        Assert.IsFalse(dialog.CanSave());
        Assert.IsFalse(continued);
        File.Delete(blockedRoot);
        dialog.RequestReload();
        Assert.IsTrue(dialog.CanSave());
        Assert.IsFalse(continued);
        dialog.SaveAndContinue();
        Assert.IsTrue(continued);
    }

    [TestMethod]
    public void Preflight_ValidAndFirstRunFilesNeedNoDialog()
    {
        var service = new ConfigRecoveryService(_root, new TextFileCodec());
        Assert.IsNull(DeferredCodeAltaApp.PrepareConfigRecovery(service, () => Assert.Fail(), () => Assert.Fail()));
        Assert.IsTrue(service.CreatedDefault);
        File.WriteAllText(ConfigPath, "# existing");
        Assert.IsNull(DeferredCodeAltaApp.PrepareConfigRecovery(new ConfigRecoveryService(_root, new TextFileCodec()),
            () => Assert.Fail(), () => Assert.Fail()));
        Assert.AreEqual("# existing", File.ReadAllText(ConfigPath));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void MountedDialog_CommandsAndReloadConfirmationKeepStartupPausedUntilSave(bool exit)
    {
        File.WriteAllText(ConfigPath, "@");
        var continued = 0;
        var exited = 0;
        var dialog = DeferredCodeAltaApp.PrepareConfigRecovery(new ConfigRecoveryService(_root, new TextFileCodec()),
            () => continued++, () => exited++);
        Assert.IsNotNull(dialog);
        using var terminalSession = Terminal.Open(new InMemoryTerminalBackend(new TerminalSize(120, 40)),
            new TerminalOptions { ImplicitStartInput = true }, force: true);
        var app = new TerminalApp(new TextBlock("Owned recovery fixture"), terminalSession.Instance,
            new TerminalAppOptions { HostKind = TerminalHostKind.Fullscreen });
        InvokeApp(app, "BeginRun");
        try
        {
            dialog.Show(app);
            InvokeApp(app, "Tick", [null]);
            var saveButton = (Button)typeof(ConfigRecoveryDialog).GetField("_saveButton", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dialog)!;
            Assert.IsFalse(saveButton.IsEnabled);
            SetText(dialog, "# repaired");
            InvokeApp(app, "Tick", [null]);
            Assert.IsTrue(saveButton.IsEnabled);
            File.WriteAllText(ConfigPath, "# external");
            InvokeApp(app, "DispatchKeyEvent", new TerminalKeyEvent { Key = TerminalKey.Unknown, Char = TerminalChar.CtrlS, Modifiers = TerminalModifiers.Ctrl }, true);
            InvokeApp(app, "Tick", [null]);
            Assert.AreEqual(0, continued);
            Assert.AreEqual(1, app.Root.EnumerateVisualsDepthFirst().OfType<Dialog>().Count());
            Assert.AreEqual("# repaired", GetText(dialog));

            dialog.RequestReload();
            InvokeApp(app, "Tick", [null]);
            Assert.AreEqual(2, app.Root.EnumerateVisualsDepthFirst().OfType<Dialog>().Count());
            InvokeApp(app, "DispatchKeyEvent", new TerminalKeyEvent { Key = TerminalKey.Escape }, true);
            Assert.AreEqual("# repaired", GetText(dialog));
            dialog.RequestReload();
            InvokeApp(app, "Tick", [null]);
            var confirmation = app.Root.EnumerateVisualsDepthFirst().OfType<Dialog>().Last();
            app.Focus(confirmation.EnumerateVisualsDepthFirst().OfType<Button>().Single(button => button.Tone == ControlTone.Error));
            InvokeApp(app, "DispatchKeyEvent", new TerminalKeyEvent { Key = TerminalKey.Enter }, true);
            InvokeApp(app, "Tick", [null]);
            Assert.AreEqual("# external", GetText(dialog));
            Assert.AreEqual(0, continued);
            SetText(dialog, "# reapplied");
            InvokeApp(app, "Tick", [null]);
            InvokeApp(app, "DispatchKeyEvent", new TerminalKeyEvent
            {
                Key = TerminalKey.Unknown,
                Char = exit ? TerminalChar.CtrlQ : TerminalChar.CtrlS,
                Modifiers = TerminalModifiers.Ctrl,
            }, true);
            InvokeApp(app, "Tick", [null]);
            Assert.AreEqual(exit ? 0 : 1, continued);
            Assert.AreEqual(exit ? 1 : 0, exited);
            Assert.AreEqual(0, app.Root.EnumerateVisualsDepthFirst().OfType<Dialog>().Count());
            Assert.AreEqual(exit ? "# external" : "# reapplied", File.ReadAllText(ConfigPath));
        }
        finally { InvokeApp(app, "EndRun"); }
    }

    private static void InvokeApp(TerminalApp app, string methodName, params object?[] arguments)
        => typeof(TerminalApp).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(app, arguments);

    private ConfigRecoveryService LoadRecovery()
    {
        var service = new ConfigRecoveryService(_root, new TextFileCodec());
        Assert.IsTrue(service.Reload());
        return service;
    }

    private static void SetText(ConfigRecoveryDialog dialog, string text)
    {
        var editor = GetEditor(dialog);
        editor.TextDocument.Replace(0, editor.TextDocument.CurrentSnapshot.Length, text.AsSpan());
    }

    private static string GetText(ConfigRecoveryDialog dialog)
        => CodeAlta.Tui.Presentation.Editing.CodeEditorFactory.GetText(GetEditor(dialog));

    private static CodeEditor GetEditor(ConfigRecoveryDialog dialog)
    {
        var field = typeof(ConfigRecoveryDialog).GetField("_editor", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(field);
        return (CodeEditor)field.GetValue(dialog)!;
    }
}
