using System.Collections;
using System.Reflection;
using System.Text;
using CodeAlta.Catalog;
using CodeAlta.LiveTool;
using CodeAlta.Tui.Views;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Text;

namespace CodeAlta.Tests;

[TestClass]
public sealed class AskQuestionFormViewTests
{
    [TestMethod]
    public void ChoiceQuestion_UsesOptionListAsInitialFocusAndSubmitsSelectedChoice()
    {
        var form = new AskQuestionFormView(CreateQueuedAsk(new AltaAskRequest
        {
            Questions =
            [
                new AltaAskQuestion
                {
                    Title = "Decision",
                    Question = "Choose one.",
                    Choices =
                    [
                        new AltaAskChoice { Title = "Recommended" },
                        new AltaAskChoice { Title = "Alternative" },
                    ],
                },
            ],
        }));
        IReadOnlyList<AltaAskAnswer>? submitted = null;
        form.Submitted += (_, answers) => submitted = answers;

        AssertOptionList(form.InitialFocusTarget, selectedIndex: 0);
        Assert.IsTrue(form.InitialFocusTarget.AutoFocus);
        SetSelectedIndex(form.InitialFocusTarget, 1);
        form.SubmitOrAdvance();

        Assert.IsNotNull(submitted);
        CollectionAssert.AreEqual(new[] { 1 }, submitted[0].SelectedChoiceIndexes.ToArray());
    }

    [TestMethod]
    public void FreeformOnlyQuestion_FocusesTextBoxAndSubmitsText()
    {
        var form = new AskQuestionFormView(CreateQueuedAsk(new AltaAskRequest
        {
            Questions =
            [
                new AltaAskQuestion
                {
                    Title = "Notes",
                    Question = "What should change?",
                    Freeform = new AltaAskFreeform { Placeholder = "Type notes" },
                },
            ],
        }));
        IReadOnlyList<AltaAskAnswer>? submitted = null;
        form.Submitted += (_, answers) => submitted = answers;

        var textBox = (TextBox)form.InitialFocusTarget;
        Assert.IsTrue(textBox.AutoFocus);
        textBox.Text = "Looks good";
        form.SubmitOrAdvance();

        Assert.IsNotNull(submitted);
        Assert.AreEqual("Looks good", submitted[0].FreeformText);
        CollectionAssert.AreEqual(Array.Empty<int>(), submitted[0].SelectedChoiceIndexes.ToArray());
    }

    [TestMethod]
    public void QuestionTabs_ShowDirectionalProgressGlyphs()
    {
        var form = new AskQuestionFormView(CreateQueuedAsk(new AltaAskRequest
        {
            Questions =
            [
                new AltaAskQuestion { Title = "First", Question = "First?" },
                new AltaAskQuestion { Title = "Second", Question = "Second?" },
                new AltaAskQuestion { Title = "Third", Question = "Third?" },
            ],
        }));

        Assert.AreEqual("First →", ((TextBlock)form.Tabs.Tabs[0].Header).Text);
        Assert.AreEqual("Second →", ((TextBlock)form.Tabs.Tabs[1].Header).Text);
        Assert.AreEqual("Third ✓", ((TextBlock)form.Tabs.Tabs[2].Header).Text);
    }

    [TestMethod]
    public void QuestionDescription_UsesDimMarkup()
    {
        var form = new AskQuestionFormView(CreateQueuedAsk(new AltaAskRequest
        {
            Questions =
            [
                new AltaAskQuestion
                {
                    Title = "Decision",
                    Question = "Choose one.",
                    Description = "Extra context.",
                    Choices = [new AltaAskChoice { Title = "Recommended" }],
                },
            ],
        }));

        var scrollViewer = Assert.IsInstanceOfType<ScrollViewer>(form.Tabs.Tabs[0].Content);
        var page = Assert.IsInstanceOfType<VStack>(scrollViewer.Content);
        var description = Assert.IsInstanceOfType<Markup>(page.Children[1]);

        Assert.AreEqual("[dim]Extra context.[/]", description.Text);
        Assert.IsTrue(description.Wrap);
    }

    [TestMethod]
    public void FileAsk_CodeEditorWrapsLinesByDefault()
    {
        var path = Path.Combine(Path.GetTempPath(), "CodeAlta.Tests." + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            File.WriteAllText(path, "This is a long attached file line that should wrap in the ask review editor by default.");

            var view = AskFileReviewView.Create(new AltaAskFile { Path = path }, [], new TextFileCodec());

            Assert.IsNotNull(view);
            Assert.IsNotNull(view.Editor);
            Assert.IsTrue(view.Editor.WordWrap);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [TestMethod]
    public void FileAsk_CreateReviewSnapshotIncludesActiveCommentText()
    {
        var path = Path.Combine(Path.GetTempPath(), "CodeAlta.Tests." + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            File.WriteAllText(path, "line 1" + Environment.NewLine + "line 2");
            var view = AskFileReviewView.Create(new AltaAskFile { Path = path }, [], new TextFileCodec());

            Assert.IsNotNull(view);
            typeof(AskFileReviewView)
                .GetMethod("InsertCommentAtCurrentLine", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(view, null);

            var comments = (IDictionary)typeof(AskFileReviewView)
                .GetField("_comments", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(view)!;
            Assert.AreEqual(1, comments.Count);
            var entry = comments.Values.Cast<object>().Single();
            var textArea = (TextArea)entry.GetType().GetProperty("TextArea", BindingFlags.Instance | BindingFlags.Public)!.GetValue(entry)!;
            textArea.TextDocument = new TextDocument("Consider this line.");

            var snapshot = view.CreateReviewSnapshot();

            Assert.AreEqual(1, snapshot.Comments.Count);
            Assert.AreEqual(1, snapshot.Comments[0].Line);
            Assert.AreEqual("Consider this line.", snapshot.Comments[0].Text);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [TestMethod]
    [DataRow(65001, false)]
    [DataRow(65001, true)]
    [DataRow(1200, true)]
    [DataRow(1201, true)]
    [DataRow(12000, true)]
    [DataRow(12001, true)]
    public void FileAsk_SavePreservesEncodingBomAndLiteralNewlines(int codePage, bool bom)
    {
        var directory = Directory.CreateTempSubdirectory();
        var path = Path.Combine(directory.FullName, "review.txt");
        try
        {
            var encoding = codePage == 65001 ? new UTF8Encoding(bom) : Encoding.GetEncoding(codePage);
            const string text = "héllo 🌍\r\nsecond\nthird\rlast";
            File.WriteAllText(path, text, encoding);
            var view = AskFileReviewView.Create(new AltaAskFile { Path = path }, [], new TextFileCodec())!;
            view.Editor!.TextDocument.Insert(0, "edited ");
            Assert.IsTrue(view.HasUnsavedChanges);
            Assert.IsTrue(view.TrySave(out var error), error);
            Assert.IsFalse(view.HasUnsavedChanges);
            CollectionAssert.AreEqual(encoding.GetPreamble().Concat(encoding.GetBytes("edited " + text)).ToArray(), File.ReadAllBytes(path));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void FileAsk_ConflictRetainsDirtyEditsAndDoesNotMarkReviewSaved(bool delete)
    {
        var directory = Directory.CreateTempSubdirectory();
        var path = Path.Combine(directory.FullName, "review.txt");
        try
        {
            File.WriteAllText(path, "original");
            var store = new TextFileCodec();
            var loaded = store.Load(path);
            var view = AskFileReviewView.Create(new AltaAskFile { Path = path }, [], store)!;
            view.Editor!.TextDocument.Insert(0, "my edits ");
            if (delete)
            {
                File.Delete(path);
            }
            else
            {
                File.WriteAllText(path, "external");
                File.SetLastWriteTimeUtc(path, loaded.LastWriteTimeUtc.UtcDateTime);
            }

            Assert.IsFalse(view.TrySave(out var error));
            Assert.IsFalse(string.IsNullOrWhiteSpace(error));
            Assert.IsTrue(view.HasUnsavedChanges);
            Assert.IsFalse(view.CreateReviewSnapshot().FileModifiedAndSaved);
            Assert.IsFalse(view.TrySave(out _), "A retry must not silently accept the conflicting revision.");
            Assert.AreEqual("my edits original", CodeAlta.Tui.Presentation.Editing.CodeEditorFactory.GetText(view.Editor));
            if (delete)
            {
                Assert.IsFalse(File.Exists(path));
            }
            else
            {
                Assert.AreEqual("external", File.ReadAllText(path));
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static AltaQueuedAsk CreateQueuedAsk(AltaAskRequest request)
        => new()
        {
            AskId = "ask-test",
            SessionId = "session-test",
            Request = request,
            Caller = new AltaCallerIdentity { Kind = "agent", SourceSessionId = "session-test" },
            CreatedAt = DateTimeOffset.UtcNow,
        };

    private static void AssertOptionList(Visual visual, int selectedIndex)
    {
        Assert.IsTrue(visual.GetType().IsGenericType);
        Assert.AreEqual(typeof(OptionList<>), visual.GetType().GetGenericTypeDefinition());
        Assert.AreEqual(selectedIndex, GetSelectedIndex(visual));
    }

    private static int GetSelectedIndex(Visual visual)
        => (int)visual.GetType().GetProperty(nameof(OptionList<object>.SelectedIndex))!.GetValue(visual)!;

    private static void SetSelectedIndex(Visual visual, int selectedIndex)
        => visual.GetType().GetProperty(nameof(OptionList<object>.SelectedIndex))!.SetValue(visual, selectedIndex);
}
