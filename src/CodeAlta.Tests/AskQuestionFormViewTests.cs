using System.Collections;
using System.Reflection;
using CodeAlta.LiveTool;
using CodeAlta.Views;
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
    public void Source_UsesNoBorderTabsScrollViewerAndDoesNotUseCheckBoxesForAnswers()
    {
        var source = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "Views", "AskQuestionFormView.cs"));

        StringAssert.Contains(source, ".Style(TabControlStyle.NoBorder)");
        StringAssert.Contains(source, "LEFT/RIGHT questions · UP/DOWN choices · ENTER select/submit · ESC cancel");
        StringAssert.Contains(source, ".Tone(ControlTone.Primary)");
        StringAssert.Contains(source, ".Tone(ControlTone.Error)");
        StringAssert.Contains(source, "HorizontalAlignment = Align.Start");
        StringAssert.Contains(source, "NextQuestionGlyph = \"→\"");
        StringAssert.Contains(source, "LastQuestionGlyph = \"✓\"");
        StringAssert.Contains(source, "OptionList<AskChoiceOption>");
        StringAssert.Contains(source, "choiceList.KeyDown");
        StringAssert.Contains(source, "new ScrollViewer");
        Assert.IsFalse(source.Contains("CheckBox", StringComparison.Ordinal));
    }

    [TestMethod]
    public void SessionTabs_RouteLeftRightToActiveAskBeforeDefaultTabNavigation()
    {
        var source = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "Views", "SessionTabHostView.cs"));

        StringAssert.Contains(source, "CodeAlta.SessionTabs.AskPreviousQuestion");
        StringAssert.Contains(source, "CodeAlta.SessionTabs.AskNextQuestion");
        StringAssert.Contains(source, "CodeAlta.SessionTabs.AskConsumeUp");
        StringAssert.Contains(source, "CodeAlta.SessionTabs.AskConsumeDown");
        StringAssert.Contains(source, "Gesture = new KeyGesture(TerminalKey.Left)");
        StringAssert.Contains(source, "Gesture = new KeyGesture(TerminalKey.Right)");
        StringAssert.Contains(source, "Gesture = new KeyGesture(TerminalKey.Up)");
        StringAssert.Contains(source, "Gesture = new KeyGesture(TerminalKey.Down)");
        StringAssert.Contains(source, "ConsumesGestureWhenUnavailable = false");
        StringAssert.Contains(source, "ExecuteActiveAskCommand(\"CodeAlta.Ask.Previous\")");
        StringAssert.Contains(source, "ExecuteActiveAskCommand(\"CodeAlta.Ask.Next\")");
        StringAssert.Contains(source, "TryGetSelectedTabId()");
        StringAssert.Contains(source, "ReferenceEquals(e.OriginalSource, SessionTabControl)");
    }

    [TestMethod]
    public void FileAsk_RoutesAttachedFileToTimelineReplacement()
    {
        var coordinatorSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "App", "AskModeCoordinator.cs"));
        var workspaceViewModelSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "ViewModels", "SessionWorkspaceViewModel.cs"));
        var tabHostSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "Views", "SessionTabHostView.cs"));
        var fileReviewSource = File.ReadAllText(Path.Combine(GetCodeAltaSourceRoot(), "Views", "AskFileReviewView.cs"));

        StringAssert.Contains(coordinatorSource, "AskFileReviewView.Create(ask.Request.File, GetAskFileRootCandidates(session))");
        StringAssert.Contains(coordinatorSource, "form.AddFileReviewCommands(fileReview)");
        StringAssert.Contains(coordinatorSource, "TryEnterAskMode(sessionId, form.Root, fileReview?.Root)");
        StringAssert.Contains(workspaceViewModelSource, "Func<string, Visual, Visual?, bool>? _enterAskMode");
        StringAssert.Contains(tabHostSource, "splitter.First = fileReview");
        Assert.IsFalse(tabHostSource.Contains("AskFileReviewRatio", StringComparison.Ordinal));
        StringAssert.Contains(fileReviewSource, "new CodeEditor(new CodeEditorConfig");
        StringAssert.Contains(fileReviewSource, "CodeAlta.Ask.FileComment.Insert");
        StringAssert.Contains(fileReviewSource, "CodeAlta.Ask.FileComment.Clear");
        StringAssert.Contains(fileReviewSource, "TextEditorAutoSizeMode.Height");
        StringAssert.Contains(fileReviewSource, "File context:");
        StringAssert.Contains(fileReviewSource, "User Comment");
        StringAssert.Contains(fileReviewSource, "UiPalette.GetChatGroupStyle");
        StringAssert.Contains(fileReviewSource, "ChatTimelineTone.User");
        StringAssert.Contains(fileReviewSource, "Clear comments");
        StringAssert.Contains(fileReviewSource, "TrySave");
        StringAssert.Contains(fileReviewSource, "CreateReviewSnapshot");
    }

    [TestMethod]
    public void FileAsk_CreateReviewSnapshotIncludesActiveCommentText()
    {
        var path = Path.Combine(Path.GetTempPath(), "CodeAlta.Tests." + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            File.WriteAllText(path, "line 1" + Environment.NewLine + "line 2");
            var view = AskFileReviewView.Create(new AltaAskFile { Path = path }, []);

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

    private static string GetCodeAltaSourceRoot()
    {
        var directory = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(directory))
        {
            var candidate = Path.Combine(directory, "CodeAlta", "CodeAlta.csproj");
            if (File.Exists(candidate))
            {
                return Path.GetDirectoryName(candidate)!;
            }

            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new DirectoryNotFoundException("Could not find CodeAlta source root.");
    }
}
