using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugins.Tests;

[TestClass]
public sealed class PluginChangeNotificationServiceTests
{
    [TestMethod]
    public void Constructor_InteractiveRequiresExplicitSink()
    {
        var error = Assert.ThrowsExactly<ArgumentNullException>(() => new PluginChangeNotificationService(new PluginChangeNotificationOptions { Interactive = true }));
        Assert.AreEqual("toastSink", error.ParamName);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Notify_SinkFailurePreservesCommittedStatus(bool cancellation)
    {
        Exception failure = cancellation ? new OperationCanceledException(new CancellationToken(true)) : new InvalidOperationException("sink");
        var service = new PluginChangeNotificationService(new PluginChangeNotificationOptions { Interactive = true }, _ => throw failure);
        var root = new PluginRoot { RootPath = "unused", Scope = PluginScope.Global };
        Exception? observed = null;
        try { service.Notify([new PluginSourceChange { Root = root, PackageId = "inert", Kind = PluginSourceChangeKind.Changed }]); }
        catch (Exception error) { observed = error; }
        Assert.AreSame(failure, observed);
        Assert.AreEqual("Plugins: 1 changed", service.FooterStatus);
        PluginChangeNotification? opened = null;
        service.OpenManagementRequested += (_, notification) => opened = notification;
        service.OpenManagementForChangedPlugins();
        Assert.IsNotNull(opened);
        Assert.AreEqual("1 plugin changed", opened.Message);
        Assert.AreEqual(0, service.HeadlessMessages.Count);
    }

    [TestMethod]
    public void Notify_EmptyChangesClearsStatusWithoutClearingHistory()
    {
        var service = new PluginChangeNotificationService();
        var root = new PluginRoot { RootPath = "unused", Scope = PluginScope.Global };
        service.Notify([new PluginSourceChange { Root = root, Kind = PluginSourceChangeKind.UnknownRescanRequired }]);
        Assert.IsNull(service.Notify([]));
        Assert.IsNull(service.FooterStatus);
        CollectionAssert.AreEqual(new[] { "1 plugin changed" }, service.HeadlessMessages.ToArray());
    }

    [TestMethod]
    public void Notify_NullChangesFailsBeforeSink()
    {
        var service = new PluginChangeNotificationService(new PluginChangeNotificationOptions { Interactive = true }, _ => throw new AssertFailedException("Sink invoked."));
        var error = Assert.ThrowsExactly<ArgumentNullException>(() => service.Notify(null!));
        Assert.AreEqual("changes", error.ParamName);
        Assert.IsNull(service.FooterStatus);
    }

    [TestMethod]
    public void NotifyCoalescesToastsFooterStatusAndActionDispatch()
    {
        var toastMessages = new List<string>();
        var service = new PluginChangeNotificationService(new PluginChangeNotificationOptions { Interactive = true }, toastMessages.Add);
        var root = new PluginRoot { RootPath = "plugins", Scope = PluginScope.Global };
        PluginChangeNotification? opened = null;
        service.OpenManagementRequested += (_, notification) => opened = notification;

        var notification = service.Notify([
            new PluginSourceChange { Root = root, PackageId = "b", Kind = PluginSourceChangeKind.Changed },
            new PluginSourceChange { Root = root, PackageId = "a", Kind = PluginSourceChangeKind.Added },
            new PluginSourceChange { Root = root, PackageId = "a", Kind = PluginSourceChangeKind.Changed },
        ]);

        Assert.IsNotNull(notification);
        CollectionAssert.AreEqual(new[] { "a", "b" }, notification.PackageIds.ToArray());
        Assert.AreEqual("Plugins: 2 changed", service.FooterStatus);
        CollectionAssert.AreEqual(new[] { "2 plugins changed" }, toastMessages.ToArray());

        service.OpenManagementForChangedPlugins();
        Assert.AreSame(notification, opened);
    }

    [TestMethod]
    public void NotifyUsesHeadlessFallbackAndClearRemovesFooterStatus()
    {
        var service = new PluginChangeNotificationService(new PluginChangeNotificationOptions { Interactive = false });
        var root = new PluginRoot { RootPath = "plugins", Scope = PluginScope.Global };

        service.Notify([
            new PluginSourceChange { Root = root, Kind = PluginSourceChangeKind.UnknownRescanRequired },
        ]);

        Assert.AreEqual("Plugins: 1 changed", service.FooterStatus);
        CollectionAssert.AreEqual(new[] { "1 plugin changed" }, service.HeadlessMessages.ToArray());

        service.Clear();

        Assert.IsNull(service.FooterStatus);
    }
}
