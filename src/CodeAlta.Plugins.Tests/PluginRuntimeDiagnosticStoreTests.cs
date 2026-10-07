using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugins.Tests;

[TestClass]
public sealed class PluginRuntimeDiagnosticStoreTests
{
    [TestMethod]
    public void TheDiagnosticsOfAPackage_AreReplaced_AndCallbackFailuresDoNotGrowWithoutEnd()
    {
        var store = new PluginRuntimeDiagnosticStore();
        store.Add(PluginRuntimeDiagnostic.Error(PluginRuntimeDiagnosticSource.Build, "Plugin build failed.", "notes"));
        store.Add(PluginRuntimeDiagnostic.Info(PluginRuntimeDiagnosticSource.Build, "Plugin build finished.", "other"));

        Assert.AreEqual(1, store.RemoveWhere(static diagnostic => diagnostic.PackageId == "notes"));
        Assert.AreEqual("other", store.GetSnapshot().Single().PackageId);
        Assert.ThrowsExactly<ArgumentNullException>(() => store.RemoveWhere(null!));

        // A status item that fails each time it is asked: the oldest failures go, what the builds said stays.
        for (var index = 0; index < PluginRuntimeDiagnosticStore.MaximumCount * 2; index++)
        {
            store.Add(new PluginRuntimeDiagnostic { Severity = CodeAlta.Plugins.Abstractions.PluginDiagnosticSeverity.Error, Source = PluginRuntimeDiagnosticSource.Callback, Message = "Status contribution failed.", RuntimeKey = "plugin:notes", Metadata = new Dictionary<string, string> { ["Index"] = index.ToString(System.Globalization.CultureInfo.InvariantCulture) } });
        }

        var kept = store.GetSnapshot();
        Assert.IsTrue(kept.Count <= PluginRuntimeDiagnosticStore.MaximumCount);
        Assert.AreEqual("other", kept[0].PackageId);
        Assert.AreEqual((PluginRuntimeDiagnosticStore.MaximumCount * 2 - 1).ToString(System.Globalization.CultureInfo.InvariantCulture), kept[^1].Metadata["Index"]);
    }

    [TestMethod]
    public void StoreKeepsPluginDiagnosticsOutsideConversationHistoryModels()
    {
        var store = new PluginRuntimeDiagnosticStore();
        var exception = new InvalidOperationException("Boom");

        store.Add(PluginRuntimeDiagnostic.Error(
            PluginRuntimeDiagnosticSource.Callback,
            "Callback failed.",
            "hello",
            "plugin.cs",
            exception));
        store.Add(PluginRuntimeDiagnostic.Warning(
            PluginRuntimeDiagnosticSource.SourceChange,
            "Source changed.",
            "hello",
            "plugin.cs"));

        Assert.AreEqual(2, store.GetSnapshot().Count);
        Assert.AreEqual(2, store.GetByPackage("hello").Count);
        Assert.AreEqual(1, store.GetBySource(PluginRuntimeDiagnosticSource.Callback).Count);
        var errors = store.GetByMinimumSeverity(PluginDiagnosticSeverity.Error);
        Assert.AreEqual(1, errors.Count);
        Assert.IsNotNull(errors[0].Exception);
        Assert.AreEqual("Boom", errors[0].Exception!.Message);

        store.Clear();

        Assert.AreEqual(0, store.GetSnapshot().Count);
    }
}
