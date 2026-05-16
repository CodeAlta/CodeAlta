using System.Reflection;
using CodeAlta.App;
using CodeAlta.Catalog;
using CodeAlta.Views;
using XenoAtom.Terminal;
using XenoAtom.Terminal.Backends;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Hosting;
using XenoAtom.Terminal.UI.Input;

namespace CodeAlta.Tests;

[TestClass]
public sealed class ModelProvidersDialogInteractionTests
{
    [TestMethod]
    public void ModelProvidersDialog_LoadedProviderDefaultsDoNotStartDirty()
    {
        var definitions = new[]
        {
            new CodeAltaProviderDocument
            {
                ProviderKey = "provider-1",
                Enabled = null,
                ProviderType = "openai-chat",
                ApiKey = "key-1",
            },
        };
        var dialog = CreateDialog(() => definitions);

        InvokeLoadDefinitionsIntoDialog(
            dialog,
            definitions,
            "[warning]No providers are configured yet.[/]",
            "[dim]Provider configuration loaded from disk.[/]");

        Assert.IsFalse(HasUnsavedChanges(dialog), "Loaded defaults should be compared against the normalized editor state.");
        Assert.IsFalse(BuildStatusMarkup(dialog).Contains("Unsaved model provider changes", StringComparison.Ordinal));

        var provider = GetProviders(dialog)[0];
        provider.UseDefaultDisplayName = false;
        provider.DisplayName = "Updated provider";

        Assert.IsTrue(HasUnsavedChanges(dialog));
        Assert.IsTrue(BuildStatusMarkup(dialog).Contains("Unsaved model provider changes", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ModelProvidersDialog_SaveClearsDirtyStateWhenReloadReturnsPrunedDefaults()
    {
        using var session = Terminal.Open(new InMemoryTerminalBackend(new TerminalSize(120, 40)), new TerminalOptions { ImplicitStartInput = true }, force: true);
        var root = new TextBlock("Root");
        var app = new TerminalApp(
            root,
            session.Instance,
            new TerminalAppOptions
            {
                HostKind = TerminalHostKind.Fullscreen,
            });

        IReadOnlyList<CodeAltaProviderDocument> definitionsFromDisk =
        [
            new CodeAltaProviderDocument
            {
                ProviderKey = "provider-1",
                Enabled = null,
                ProviderType = "openai-chat",
                ApiKey = "key-1",
            },
        ];

        var dialog = CreateDialog(
            () => definitionsFromDisk,
            definitions =>
            {
                definitionsFromDisk = definitions.Select(static definition => new CodeAltaProviderDocument
                {
                    ProviderKey = definition.ProviderKey,
                    Enabled = definition.Enabled == CodeAltaProviderDocument.DefaultEnabled ? null : definition.Enabled,
                    DisplayName = definition.DisplayName,
                    ProviderType = definition.ProviderType,
                    ApiKey = definition.ApiKey,
                }).ToArray();
                return Task.CompletedTask;
            },
            getFocusTarget: () => root);

        InvokeTerminalApp(app, "BeginRun");
        try
        {
            dialog.Show();
            WaitUntil(() => GetProviderCount(dialog) == 1, app);
            Assert.IsFalse(HasUnsavedChanges(dialog));

            var provider = GetProviders(dialog)[0];
            provider.UseDefaultDisplayName = false;
            provider.DisplayName = "Updated provider";
            Assert.IsTrue(HasUnsavedChanges(dialog));

            InvokeStartSave(dialog);
            WaitUntil(() => !HasUnsavedChanges(dialog), app);

            Assert.IsTrue(BuildStatusMarkup(dialog).Contains("saved", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            InvokeTerminalApp(app, "EndRun");
        }
    }

    [TestMethod]
    public void ModelProvidersDialog_StatusAndChangeSummaryReflectLoadedAndDirtyState()
    {
        using var session = Terminal.Open(new InMemoryTerminalBackend(new TerminalSize(120, 40)), new TerminalOptions { ImplicitStartInput = true }, force: true);
        var root = new TextBlock("Root");
        var app = new TerminalApp(
            root,
            session.Instance,
            new TerminalAppOptions
            {
                HostKind = TerminalHostKind.Fullscreen,
            });

        var definitions = new[]
        {
            new CodeAltaProviderDocument
            {
                ProviderKey = "provider-1",
                Enabled = true,
                ProviderType = "openai-chat",
                ApiKey = "key-1",
            },
        };

        var dialog = CreateDialog(() => definitions, getFocusTarget: () => root);

        InvokeTerminalApp(app, "BeginRun");
        try
        {
            dialog.Show();
            WaitUntil(() => GetProviderCount(dialog) == 1, app);
            WaitUntil(() => GetStatusMarkupText(dialog).Contains("loaded", StringComparison.OrdinalIgnoreCase), app);

            Assert.IsFalse(GetStatusMarkupText(dialog).Contains("Loading provider configuration", StringComparison.OrdinalIgnoreCase));
            Assert.IsTrue(GetChangeSummaryMarkupText(dialog).Contains("No unsaved changes", StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(GetSaveButton(dialog).IsEnabled);

            var provider = GetProviders(dialog)[0];
            provider.UseDefaultDisplayName = false;
            provider.DisplayName = "Updated provider";
            TickTerminalApp(app);

            Assert.IsTrue(GetStatusMarkupText(dialog).Contains("Unsaved model provider changes", StringComparison.OrdinalIgnoreCase));
            Assert.IsTrue(GetChangeSummaryMarkupText(dialog).Contains("Unsaved changes", StringComparison.OrdinalIgnoreCase));
            Assert.IsTrue(GetSaveButton(dialog).IsEnabled);
        }
        finally
        {
            InvokeTerminalApp(app, "EndRun");
        }
    }

    [TestMethod]
    public void ModelProvidersDialog_SaveIgnoresTransientFailedTestResult()
    {
        using var session = Terminal.Open(new InMemoryTerminalBackend(new TerminalSize(120, 40)), new TerminalOptions { ImplicitStartInput = true }, force: true);
        var root = new TextBlock("Root");
        var app = new TerminalApp(
            root,
            session.Instance,
            new TerminalAppOptions
            {
                HostKind = TerminalHostKind.Fullscreen,
            });

        IReadOnlyList<CodeAltaProviderDocument> definitionsFromDisk =
        [
            new CodeAltaProviderDocument
            {
                ProviderKey = "provider-1",
                Enabled = true,
                ProviderType = "openai-chat",
                ApiKey = "key-1",
            },
        ];
        var saveCount = 0;

        var dialog = CreateDialog(
            () => definitionsFromDisk,
            definitions =>
            {
                saveCount++;
                definitionsFromDisk = definitions.ToArray();
                return Task.CompletedTask;
            },
            _ => Task.FromResult(new ProviderTestResult(false, "Endpoint temporarily rejected model discovery.", 0)),
            () => root);

        InvokeTerminalApp(app, "BeginRun");
        try
        {
            dialog.Show();
            WaitUntil(() => GetProviderCount(dialog) == 1, app);

            var provider = GetProviders(dialog)[0];
            provider.UseDefaultDisplayName = false;
            provider.DisplayName = "Updated provider";
            InvokeStartTest(dialog, provider);
            WaitUntil(() => provider.LastTestState == ViewModels.ModelProviderLastTestState.Failed, app);

            InvokeStartSave(dialog);
            WaitUntil(() => !HasUnsavedChanges(dialog), app);

            Assert.AreEqual(1, saveCount);
            Assert.IsTrue(BuildStatusMarkup(dialog).Contains("saved", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            InvokeTerminalApp(app, "EndRun");
        }
    }

    [TestMethod]
    public void ModelProvidersDialog_DisabledCodexSubscriptionWithoutOptInDoesNotBlockSave()
    {
        using var session = Terminal.Open(new InMemoryTerminalBackend(new TerminalSize(120, 40)), new TerminalOptions { ImplicitStartInput = true }, force: true);
        var root = new TextBlock("Root");
        var app = new TerminalApp(
            root,
            session.Instance,
            new TerminalAppOptions
            {
                HostKind = TerminalHostKind.Fullscreen,
            });

        IReadOnlyList<CodeAltaProviderDocument> definitionsFromDisk =
        [
            new CodeAltaProviderDocument
            {
                ProviderKey = "provider-1",
                Enabled = true,
                ProviderType = "openai-chat",
                ApiKey = "key-1",
            },
            new CodeAltaProviderDocument
            {
                ProviderKey = "codex",
                Enabled = false,
                ProviderType = "codex",
                Experimental = false,
            },
        ];
        var saveCount = 0;

        var dialog = CreateDialog(
            () => definitionsFromDisk,
            definitions =>
            {
                saveCount++;
                definitionsFromDisk = definitions.ToArray();
                return Task.CompletedTask;
            },
            getFocusTarget: () => root);

        InvokeTerminalApp(app, "BeginRun");
        try
        {
            dialog.Show();
            WaitUntil(() => GetProviderCount(dialog) == 2, app);

            var provider = GetProviders(dialog)[0];
            provider.UseDefaultDisplayName = false;
            provider.DisplayName = "Updated provider";

            InvokeStartSave(dialog);
            WaitUntil(() => !HasUnsavedChanges(dialog), app);

            Assert.AreEqual(1, saveCount);
            Assert.IsTrue(BuildStatusMarkup(dialog).Contains("saved", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            InvokeTerminalApp(app, "EndRun");
        }
    }

    [TestMethod]
    public void ModelProvidersDialog_ReopenUsesAlreadyLoadedDefinitions()
    {
        using var session = Terminal.Open(new InMemoryTerminalBackend(new TerminalSize(120, 40)), new TerminalOptions { ImplicitStartInput = true }, force: true);
        var root = new TextBlock("Root");
        var app = new TerminalApp(
            root,
            session.Instance,
            new TerminalAppOptions
            {
                HostKind = TerminalHostKind.Fullscreen,
            });
        var loadCount = 0;
        var definitions = new[]
        {
            new CodeAltaProviderDocument
            {
                ProviderKey = "provider-1",
                Enabled = true,
                ProviderType = "openai-chat",
                ApiKey = "key-1",
            },
        };
        var dialog = CreateDialog(
            () =>
            {
                loadCount++;
                return definitions;
            },
            getFocusTarget: () => root);

        InvokeTerminalApp(app, "BeginRun");
        try
        {
            dialog.Show();
            WaitUntil(() => GetProviderCount(dialog) == 1, app);
            InvokeClose(dialog);
            Assert.IsFalse(IsDialogOpen(dialog));

            dialog.Show();
            TickTerminalApp(app);

            Assert.AreEqual(1, loadCount, "Reopening the already-loaded dialog should not flash a redundant loading state.");
        }
        finally
        {
            InvokeTerminalApp(app, "EndRun");
        }
    }

    [TestMethod]
    public void ModelProvidersDialog_EscapeStillClosesAfterTwoSuccessfulTests()
    {
        using var session = Terminal.Open(new InMemoryTerminalBackend(new TerminalSize(120, 40)), new TerminalOptions { ImplicitStartInput = true }, force: true);
        var root = new TextBlock("Root");
        var app = new TerminalApp(
            root,
            session.Instance,
            new TerminalAppOptions
            {
                HostKind = TerminalHostKind.Fullscreen,
            });

        var definitions = new[]
        {
            new CodeAltaProviderDocument
            {
                ProviderKey = "provider-1",
                Enabled = true,
                ProviderType = "openai-chat",
                ApiKey = "key-1",
            },
            new CodeAltaProviderDocument
            {
                ProviderKey = "provider-2",
                Enabled = true,
                ProviderType = "openai-chat",
                ApiKey = "key-2",
            },
        };

        var dialog = CreateDialog(() => definitions, getFocusTarget: () => root);

        InvokeTerminalApp(app, "BeginRun");
        try
        {
            dialog.Show();
            WaitUntil(() => GetProviderCount(dialog) == 2, app);

            var providers = GetProviders(dialog);
            InvokeStartTest(dialog, providers[0]);
            WaitUntil(() => providers[0].LastTestState == ViewModels.ModelProviderLastTestState.Success, app);

            InvokeSetSelectedProviderIndex(dialog, 1);
            TickTerminalApp(app);

            InvokeStartTest(dialog, providers[1]);
            WaitUntil(() => providers[1].LastTestState == ViewModels.ModelProviderLastTestState.Success, app);

            var backend = (InMemoryTerminalBackend)session.Instance.Backend;
            backend.PushEvent(new TerminalKeyEvent
            {
                Key = TerminalKey.Escape,
            });
            TickTerminalApp(app);

            Assert.IsFalse(IsDialogOpen(dialog), "The providers dialog should still accept keyboard input after two successful tests.");
        }
        finally
        {
            InvokeTerminalApp(app, "EndRun");
        }
    }

    private static ModelProvidersDialog CreateDialog(
        Func<IReadOnlyList<CodeAltaProviderDocument>> loadDefinitions,
        Func<IReadOnlyList<CodeAltaProviderDocument>, Task>? saveDefinitionsAsync = null,
        Func<CodeAltaProviderDocument, Task<ProviderTestResult>>? testProviderAsync = null,
        Func<Visual?>? getFocusTarget = null)
    {
        return new ModelProvidersDialog(
            new TestModelProviderDialogService(loadDefinitions, saveDefinitionsAsync, testProviderAsync),
            () => new Rectangle(0, 0, 120, 40),
            getFocusTarget ?? (() => null));
    }

    private static void WaitUntil(Func<bool> predicate, TerminalApp app)
    {
        for (var i = 0; i < 100; i++)
        {
            TickTerminalApp(app);
            if (predicate())
            {
                return;
            }

            Thread.Sleep(10);
        }

        Assert.Fail("Timed out waiting for dialog state.");
    }

    private static IReadOnlyList<ViewModels.ModelProviderEditorItemViewModel> GetProviders(ModelProvidersDialog dialog)
        => (IReadOnlyList<ViewModels.ModelProviderEditorItemViewModel>)typeof(ModelProvidersDialog)
            .GetField("_providers", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(dialog)!;

    private static int GetProviderCount(ModelProvidersDialog dialog) => GetProviders(dialog).Count;

    private static bool IsDialogOpen(ModelProvidersDialog dialog)
        => ((Dialog)typeof(ModelProvidersDialog)
            .GetField("_dialog", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(dialog)!).App is not null;

    private static void InvokeStartTest(ModelProvidersDialog dialog, ViewModels.ModelProviderEditorItemViewModel item)
        => typeof(ModelProvidersDialog)
            .GetMethod("StartTest", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(dialog, [item]);

    private static void InvokeStartSave(ModelProvidersDialog dialog)
        => typeof(ModelProvidersDialog)
            .GetMethod("StartSave", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(dialog, null);

    private static void InvokeClose(ModelProvidersDialog dialog)
        => typeof(ModelProvidersDialog)
            .GetMethod("Close", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(dialog, null);

    private static void InvokeLoadDefinitionsIntoDialog(
        ModelProvidersDialog dialog,
        IReadOnlyList<CodeAltaProviderDocument> definitions,
        string emptyStatusText,
        string loadedStatusText)
        => typeof(ModelProvidersDialog)
            .GetMethod("LoadDefinitionsIntoDialog", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(dialog, [definitions, emptyStatusText, loadedStatusText]);

    private static bool HasUnsavedChanges(ModelProvidersDialog dialog)
        => (bool)typeof(ModelProvidersDialog)
            .GetMethod("HasUnsavedChanges", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(dialog, null)!;

    private static string BuildStatusMarkup(ModelProvidersDialog dialog)
        => (string)typeof(ModelProvidersDialog)
            .GetMethod("BuildStatusMarkup", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(dialog, null)!;

    private static string GetStatusMarkupText(ModelProvidersDialog dialog)
        => ((Markup)typeof(ModelProvidersDialog)
            .GetField("_statusMarkup", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(dialog)!).Text ?? string.Empty;

    private static string GetChangeSummaryMarkupText(ModelProvidersDialog dialog)
        => ((Markup)typeof(ModelProvidersDialog)
            .GetField("_changeSummaryMarkup", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(dialog)!).Text ?? string.Empty;

    private static Button GetSaveButton(ModelProvidersDialog dialog)
        => (Button)typeof(ModelProvidersDialog)
            .GetField("_saveButton", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(dialog)!;

    private static void InvokeSetSelectedProviderIndex(ModelProvidersDialog dialog, int index)
        => typeof(ModelProvidersDialog)
            .GetMethod("SetSelectedProviderIndex", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(dialog, [index]);

    private static void TickTerminalApp(TerminalApp app)
        => typeof(TerminalApp).GetMethod("Tick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(app, [null]);

    private static void InvokeTerminalApp(TerminalApp app, string methodName)
        => typeof(TerminalApp).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(app, null);

    private sealed class TestModelProviderDialogService : IModelProviderDialogService
    {
        private readonly Func<IReadOnlyList<CodeAltaProviderDocument>> _loadDefinitions;
        private readonly Func<IReadOnlyList<CodeAltaProviderDocument>, Task> _saveDefinitionsAsync;
        private readonly Func<CodeAltaProviderDocument, Task<ProviderTestResult>> _testProviderAsync;

        public TestModelProviderDialogService(
            Func<IReadOnlyList<CodeAltaProviderDocument>> loadDefinitions,
            Func<IReadOnlyList<CodeAltaProviderDocument>, Task>? saveDefinitionsAsync,
            Func<CodeAltaProviderDocument, Task<ProviderTestResult>>? testProviderAsync)
        {
            _loadDefinitions = loadDefinitions;
            _saveDefinitionsAsync = saveDefinitionsAsync ?? (_ => Task.CompletedTask);
            _testProviderAsync = testProviderAsync ?? (definition => Task.FromResult(new ProviderTestResult(true, $"Connected successfully · {definition.ProviderKey}", 1)));
        }

        public IReadOnlyList<CodeAltaProviderDocument> LoadDefinitions() => _loadDefinitions();

        public Task SaveDefinitionsAsync(IReadOnlyList<CodeAltaProviderDocument> definitions) => _saveDefinitionsAsync(definitions);

        public Task<ProviderTestResult> TestProviderAsync(CodeAltaProviderDocument definition) => _testProviderAsync(definition);

        public Task<ProviderTestResult> LoginWithBrowserAsync(CodeAltaProviderDocument definition, Action<string> reportStatus)
            => Task.FromResult(new ProviderTestResult(false, "Browser login is unavailable in this host.", 0));

        public Task<ProviderTestResult> LoginWithDeviceCodeAsync(CodeAltaProviderDocument definition, Action<string> reportStatus)
            => Task.FromResult(new ProviderTestResult(false, "Device-code login is unavailable in this host.", 0));

        public Task<ProviderTestResult> LogoutAsync(CodeAltaProviderDocument definition)
            => Task.FromResult(new ProviderTestResult(false, "Logout is unavailable in this host.", 0));

        public Task<ProviderTestResult> TestAuthenticationAsync(CodeAltaProviderDocument definition) => TestProviderAsync(definition);

        public Task<ProviderTestResult> ListModelsAsync(CodeAltaProviderDocument definition) => TestProviderAsync(definition);

        public Task<ProviderTestResult> ListAccountsAsync(CodeAltaProviderDocument definition)
            => Task.FromResult(new ProviderTestResult(false, "Account/workspace listing is unavailable in this host.", 0));
    }
}
