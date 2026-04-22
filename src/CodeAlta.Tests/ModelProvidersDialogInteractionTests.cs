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

        var dialog = new ModelProvidersDialog(
            () => definitions,
            _ => Task.CompletedTask,
            definition => Task.FromResult(new ProviderTestResult(true, $"Connected successfully · {definition.ProviderKey}", 1)),
            () => new Rectangle(0, 0, 120, 40),
            () => root);

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

    private static void InvokeSetSelectedProviderIndex(ModelProvidersDialog dialog, int index)
        => typeof(ModelProvidersDialog)
            .GetMethod("SetSelectedProviderIndex", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(dialog, [index]);

    private static void TickTerminalApp(TerminalApp app)
        => typeof(TerminalApp).GetMethod("Tick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(app, [null]);

    private static void InvokeTerminalApp(TerminalApp app, string methodName)
        => typeof(TerminalApp).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(app, null);
}
