using System.Reflection;
using CodeAlta.Catalog;
using CodeAlta.Tui.Views;
using XenoAtom.Terminal.UI.Controls;

namespace CodeAlta.Tests;

[TestClass]
public sealed class ConfigRecoveryDialogTests
{
    [TestMethod]
    public void EditingInvalidConfigToValidConfig_EnablesSave()
    {
        var initialValidation = CodeAltaConfigStore.ValidateGlobalConfigContent("@", "config.toml");
        var dialog = new ConfigRecoveryDialog(
            "config.toml",
            "@",
            initialValidation,
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
    public void ConfigRecoveryDialog_TracksEditVersionForReactiveValidationUi()
    {
        var source = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "Views", "ConfigRecoveryDialog.cs"));

        StringAssert.Contains(source, "private readonly State<int> _editVersion = new(0);");
        StringAssert.Contains(source, "_editVersion.Value++;");
        StringAssert.Contains(source, "_saveButton.IsEnabled(CanSave);");
        StringAssert.Contains(source, "CanExecute = _ => CanSave(),");
    }

    private static CodeEditor GetEditor(ConfigRecoveryDialog dialog)
    {
        var field = typeof(ConfigRecoveryDialog).GetField("_editor", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(field);
        return (CodeEditor)field.GetValue(dialog)!;
    }

    private static string GetCodeAltaSourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidates = new[]
            {
                directory.FullName,
                Path.Combine(directory.FullName, "CodeAlta.Tui"),
                Path.Combine(directory.FullName, "src", "CodeAlta.Tui"),
            };

            foreach (var candidate in candidates)
            {
                if (File.Exists(Path.Combine(candidate, "Views", "ConfigRecoveryDialog.cs")))
                {
                    return candidate;
                }
            }

            directory = directory.Parent;
        }

        Assert.Fail("Could not locate the CodeAlta.Tui source directory from the test output path.");
        return null!;
    }
}
