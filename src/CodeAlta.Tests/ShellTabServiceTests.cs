using CodeAlta.App;
using CodeAlta.App.Events;
using CodeAlta.Catalog;
using CodeAlta.Models;
using CodeAlta.Threading;
using XenoAtom.Terminal.UI.Controls;

namespace CodeAlta.Tests;

[TestClass]
public sealed class ShellTabServiceTests
{
    [TestMethod]
    public void OpenOrGetTab_AddsStableProjectionHandleWithoutReplacingExistingTab()
    {
        var service = new InMemoryShellTabService();
        var descriptor = CreateDescriptor("tab-1", viewModel: new object());
        var changeCount = 0;
        service.TabsChanged += (_, _) => changeCount++;

        var opened = service.OpenOrGetTab(descriptor);
        var duplicate = service.OpenOrGetTab(descriptor with { ViewModel = new object() });

        Assert.AreEqual(1, service.GetTabs().Count);
        Assert.AreEqual(opened, duplicate);
        Assert.IsTrue(opened.IsSelected);
        Assert.AreSame(descriptor.ViewModel, opened.ViewModel);
        Assert.AreEqual(1, changeCount);
    }

    [TestMethod]
    public async Task SelectTabAsync_UpdatesSelectedSnapshotAndRaisesChange()
    {
        var service = new InMemoryShellTabService();
        service.OpenOrGetTab(CreateDescriptor("tab-1"));
        service.OpenOrGetTab(CreateDescriptor("tab-2"));
        var changes = 0;
        service.TabsChanged += (_, _) => changes++;

        await service.SelectTabAsync(new ShellTabId("tab-2"));

        Assert.IsTrue(service.TryGetTab(new ShellTabId("tab-2"), out var selected));
        Assert.IsTrue(selected.IsSelected);
        Assert.IsTrue(service.TryGetTab(new ShellTabId("tab-1"), out var previous));
        Assert.IsFalse(previous.IsSelected);
        Assert.AreEqual(1, changes);
    }

    [TestMethod]
    public async Task TabsChanged_ReportsOpenAndSelectionMutationKinds()
    {
        var service = new InMemoryShellTabService();
        var changes = new List<ShellTabChangedEventArgs>();
        service.TabsChanged += (_, args) => changes.Add(args);

        service.OpenOrGetTab(CreateDescriptor("tab-1"));
        service.OpenOrGetTab(CreateDescriptor("tab-2"));
        await service.SelectTabAsync(new ShellTabId("tab-2"));
        await service.CloseTabAsync(new ShellTabId("tab-1"), ShellTabCloseReason.UserDetached);

        Assert.IsTrue(changes[0].OpenTabsChanged);
        Assert.IsTrue(changes[0].SelectedTabChanged);
        Assert.IsTrue(changes[1].OpenTabsChanged);
        Assert.IsFalse(changes[1].SelectedTabChanged);
        Assert.IsFalse(changes[2].OpenTabsChanged);
        Assert.IsTrue(changes[2].SelectedTabChanged);
        Assert.IsTrue(changes[3].OpenTabsChanged);
        Assert.IsFalse(changes[3].SelectedTabChanged);
    }

    [TestMethod]
    public async Task Mutations_PublishFrontendTabEventsWhenConfigured()
    {
        var publisher = new FrontendEventPublisher(new InlineUiDispatcher());
        var events = new List<ShellFrontendEvent>();
        using var subscription = publisher.Subscribe(events.Add);
        var service = new InMemoryShellTabService();
        service.SetFrontendEvents(publisher);

        service.OpenOrGetTab(CreateDescriptor("tab-1"));
        service.OpenOrGetTab(CreateDescriptor("tab-2"));
        await service.SelectTabAsync(new ShellTabId("tab-2"));

        Assert.IsInstanceOfType<OpenTabsChangedEvent>(events[0]);
        Assert.IsInstanceOfType<SelectedTabChangedEvent>(events[1]);
        Assert.IsInstanceOfType<OpenTabsChangedEvent>(events[2]);
        Assert.IsInstanceOfType<SelectedTabChangedEvent>(events[3]);
        Assert.AreEqual("tab-2", ((SelectedTabChangedEvent)events[3]).SelectedTab?.TabId.Value);
    }

    [TestMethod]
    public async Task CloseTabAsync_RemovesClosableTabWithoutTouchingProjectionHandle()
    {
        var service = new InMemoryShellTabService();
        var viewModel = new object();
        service.OpenOrGetTab(CreateDescriptor("tab-1", viewModel));

        var closed = await service.CloseTabAsync(new ShellTabId("tab-1"), ShellTabCloseReason.UserDetached);

        Assert.IsTrue(closed);
        Assert.IsFalse(service.TryGetTab(new ShellTabId("tab-1"), out _));
        Assert.AreEqual(0, service.GetTabs().Count);
    }

    [TestMethod]
    public async Task CloseTabAsync_LeavesPinnedTabsOpen()
    {
        var service = new InMemoryShellTabService();
        service.OpenOrGetTab(CreateDescriptor("tab-1") with { CanClose = false });

        var closed = await service.CloseTabAsync(new ShellTabId("tab-1"), ShellTabCloseReason.UserDetached);

        Assert.IsFalse(closed);
        Assert.IsTrue(service.TryGetTab(new ShellTabId("tab-1"), out var tab));
        Assert.IsFalse(tab.CanClose);
    }

    [TestMethod]
    public async Task CloseTabAsync_InvokesDescriptorCloseHook()
    {
        var service = new InMemoryShellTabService();
        ShellTabCloseReason? closeReason = null;
        service.OpenOrGetTab(CreateDescriptor("tab-1") with
        {
            OnClosedAsync = reason =>
            {
                closeReason = reason;
                return ValueTask.CompletedTask;
            },
        });

        var closed = await service.CloseTabAsync(new ShellTabId("tab-1"), ShellTabCloseReason.Replaced);

        Assert.IsTrue(closed);
        Assert.AreEqual(ShellTabCloseReason.Replaced, closeReason);
    }


    [TestMethod]
    public void DescriptorValidation_RejectsMissingProjectionSurfaces()
    {
        var service = new InMemoryShellTabService();
        var descriptor = CreateDescriptor("tab-1") with { Content = null! };

        Assert.ThrowsExactly<ArgumentNullException>(() => service.OpenOrGetTab(descriptor));
    }

    [TestMethod]
    public void Associations_ValidateRequiredIds()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new ShellTabId(""));
        Assert.ThrowsExactly<ArgumentException>(() => new ShellTabAssociation.Thread(
            "",
            new PromptSessionId("prompt-1"),
            ProjectId.NewVersion7(),
            new ModelProviderId("provider-1")));
        Assert.ThrowsExactly<ArgumentException>(() => new ShellTabAssociation.Plugin("plugin-1", "   "));
    }

    [TestMethod]
    public async Task PluginShellTabService_OpensStablePluginTabWithProjectionHandleAndCloseHook()
    {
        var tabs = new InMemoryShellTabService();
        var service = new PluginShellTabService(tabs, hasInteractiveUi: true);
        var viewModel = new object();
        ShellTabCloseReason? closeReason = null;
        var request = new PluginShellTabRequest
        {
            PluginId = "plugin-1",
            SurfaceKey = "stats",
            Header = new TextBlock("Stats"),
            Content = new TextBlock("content"),
            ViewModel = viewModel,
            OnClosedAsync = reason =>
            {
                closeReason = reason;
                return ValueTask.CompletedTask;
            },
        };

        var opened = service.OpenOrGetPluginTab(request);
        var duplicate = service.OpenOrGetPluginTab(request with { ViewModel = new object() });
        var closed = await service.ClosePluginTabAsync("plugin-1", "stats", ShellTabCloseReason.PluginUnloaded);

        Assert.IsNotNull(opened);
        Assert.AreEqual(ShellTabKind.Plugin, opened.Kind);
        Assert.AreSame(viewModel, opened.ViewModel);
        Assert.AreEqual(opened, duplicate);
        Assert.IsTrue(closed);
        Assert.AreEqual(ShellTabCloseReason.PluginUnloaded, closeReason);
    }

    [TestMethod]
    public async Task PluginShellTabService_IsNoOpWhenHeadless()
    {
        var tabs = new InMemoryShellTabService();
        var service = new PluginShellTabService(tabs, hasInteractiveUi: false);

        var opened = service.OpenOrGetPluginTab(new PluginShellTabRequest
        {
            PluginId = "plugin-1",
            SurfaceKey = "stats",
            Header = new TextBlock("Stats"),
            Content = new TextBlock("content"),
            ViewModel = new object(),
        });
        var closed = await service.ClosePluginTabAsync("plugin-1", "stats", ShellTabCloseReason.PluginUnloaded);

        Assert.IsNull(opened);
        Assert.IsFalse(closed);
        Assert.AreEqual(0, tabs.GetTabs().Count);
    }

    private static ShellTabDescriptor CreateDescriptor(string tabId, object? viewModel = null)
    {
        var projectId = ProjectId.NewVersion7();
        return new ShellTabDescriptor
        {
            TabId = new ShellTabId(tabId),
            Kind = ShellTabKind.Thread,
            Association = new ShellTabAssociation.Thread(
                "thread-1",
                new PromptSessionId("prompt-1"),
                projectId,
                new ModelProviderId("provider-1")),
            Header = new TextBlock(tabId),
            Content = new TextBlock("content"),
            ViewModel = viewModel ?? new object(),
        };
    }

    private sealed class InlineUiDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;

        public void Post(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);
            action();
        }

        public Task InvokeAsync(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);
            action();
            return Task.CompletedTask;
        }

        public Task<T> InvokeAsync<T>(Func<T> action)
        {
            ArgumentNullException.ThrowIfNull(action);
            return Task.FromResult(action());
        }
    }
}
