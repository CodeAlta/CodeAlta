using CodeAlta.Presentation.Editing;

namespace CodeAlta.Tests;

[TestClass]
public sealed class FileEditorSessionStateTests
{
    [TestMethod]
    public void UpdateEditorText_ClearsDirtyWhenTextMatchesSavedSnapshot()
    {
        var state = new FileEditorSessionState("alpha", new DateTimeOffset(2026, 4, 22, 8, 0, 0, TimeSpan.Zero));

        state.UpdateEditorText("beta");
        Assert.IsTrue(state.IsDirty);

        state.UpdateEditorText("alpha");
        Assert.IsFalse(state.IsDirty);
    }

    [TestMethod]
    public void RefreshExternalState_TracksMissingAndChangedFiles()
    {
        var savedAt = new DateTimeOffset(2026, 4, 22, 8, 0, 0, TimeSpan.Zero);
        var state = new FileEditorSessionState("alpha", savedAt);

        state.RefreshExternalState(existsOnDisk: true, currentWriteTimeUtc: savedAt.AddMinutes(1));
        Assert.IsTrue(state.HasExternalChanges);
        Assert.IsTrue(state.ExistsOnDisk);

        state.RefreshExternalState(existsOnDisk: false, currentWriteTimeUtc: null);
        Assert.IsTrue(state.HasExternalChanges);
        Assert.IsFalse(state.ExistsOnDisk);
    }

    [TestMethod]
    public void MarkSaved_ResetsDirtyAndExternalFlags()
    {
        var savedAt = new DateTimeOffset(2026, 4, 22, 8, 0, 0, TimeSpan.Zero);
        var state = new FileEditorSessionState("alpha", savedAt);

        state.UpdateEditorText("beta");
        state.RefreshExternalState(existsOnDisk: true, currentWriteTimeUtc: savedAt.AddMinutes(1));

        state.MarkSaved("beta", savedAt.AddMinutes(2));

        Assert.IsFalse(state.IsDirty);
        Assert.IsFalse(state.HasExternalChanges);
        Assert.IsTrue(state.ExistsOnDisk);
        Assert.AreEqual(savedAt.AddMinutes(2), state.SavedWriteTimeUtc);
    }
}
