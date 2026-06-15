using CodeAlta.Catalog;
using CodeAlta.LiveTool;
using XenoAtom.Ansi;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Commands;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Input;
using XenoAtom.Terminal.UI.Styling;
using XenoAtom.Terminal.UI.Templating;

namespace CodeAlta.Views;

internal sealed class AskQuestionFormView
{
    private const string NextQuestionGlyph = "→";
    private const string LastQuestionGlyph = "✓";

    private readonly AltaAskRequest _request;
    private readonly State<int> _helpVersion = new(0);
    private readonly bool[] _visited;
    private readonly OptionList<AskChoiceOption>?[] _choiceLists;
    private readonly TextBox?[] _freeformBoxes;
    private bool _hasFileReviewCommands;

    public AskQuestionFormView(AltaQueuedAsk ask)
    {
        ArgumentNullException.ThrowIfNull(ask);
        _request = ask.Request;
        _visited = new bool[_request.Questions.Count];
        _choiceLists = new OptionList<AskChoiceOption>?[_request.Questions.Count];
        _freeformBoxes = new TextBox?[_request.Questions.Count];

        var pages = _request.Questions
            .Select((question, index) => new TabPage(BuildQuestionTabHeader(question, index), BuildQuestionPage(question, index)))
            .ToArray();
        Tabs = new TabControl(pages)
            .Style(TabControlStyle.NoBorder)
            .HorizontalAlignment(Align.Stretch)
            .VerticalAlignment(Align.Stretch);
        Tabs.SelectionChanged((_, e) =>
        {
            SelectedIndex = e.NewIndex;
            FocusSelectedQuestionInput();
        });
        if (Tabs.Tabs.Count > 0)
        {
            Tabs.SelectedIndex = 0;
        }

        InitialFocusTarget = GetQuestionFocusTarget(0) ?? Tabs;
        Root = new DockLayout(
            top: null,
            content: Tabs,
            bottom: BuildBottomBar())
        {
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Stretch,
            Margin = new Thickness(1),
        };
        AddNavigationCommands(Root);
        Root.KeyDown((_, e) => HandleKeyDown(e));
    }

    public event EventHandler<IReadOnlyList<AltaAskAnswer>>? Submitted;

    public event EventHandler? CancelRequested;

    public Visual Root { get; }

    public TabControl Tabs { get; }

    public Visual InitialFocusTarget { get; }

    public int SelectedIndex { get; private set; }

    public IReadOnlyList<bool> Visited => _visited;

    public void AddFileReviewCommands(AskFileReviewView fileReview)
    {
        ArgumentNullException.ThrowIfNull(fileReview);
        _hasFileReviewCommands = true;
        _helpVersion.Value++;
        Root.AddCommand(fileReview.CreateSaveCommand());
        Root.AddCommand(fileReview.CreateClearCommentsCommand());
        Root.AddCommand(fileReview.CreateFocusEditorCommand());
        Root.AddCommand(new Command
        {
            Id = "CodeAlta.Ask.FocusQuestions",
            LabelMarkup = SR.T("Focus ask questions"),
            DescriptionMarkup = SR.T("Move focus back to the active ask question."),
            Sequence = new KeySequence(
                new KeyGesture(TerminalChar.CtrlG, TerminalModifiers.Ctrl),
                new KeyGesture(TerminalChar.CtrlN, TerminalModifiers.Ctrl)),
            Presentation = CommandPresentation.None,
            Execute = _ => FocusCurrentQuestionInput(),
        });
    }

    public void FocusCurrentQuestionInput()
        => FocusSelectedQuestionInput();

    public void Next()
    {
        MarkVisited();
        if (_request.Questions.Count == 0)
        {
            return;
        }

        Tabs.SelectedIndex = Math.Min(Tabs.SelectedIndex + 1, _request.Questions.Count - 1);
        SelectedIndex = Tabs.SelectedIndex;
        FocusSelectedQuestionInput();
    }

    public void Previous()
    {
        MarkVisited();
        if (_request.Questions.Count == 0)
        {
            return;
        }

        Tabs.SelectedIndex = Math.Max(Tabs.SelectedIndex - 1, 0);
        SelectedIndex = Tabs.SelectedIndex;
        FocusSelectedQuestionInput();
    }

    public void SubmitOrAdvance()
    {
        MarkVisited();
        var nextUnvisited = Array.FindIndex(_visited, static visited => !visited);
        if (nextUnvisited >= 0)
        {
            Tabs.SelectedIndex = nextUnvisited;
            SelectedIndex = nextUnvisited;
            FocusSelectedQuestionInput();
            return;
        }

        Submitted?.Invoke(this, CollectAnswers());
    }

    private Visual BuildQuestionTabHeader(AltaAskQuestion question, int index)
    {
        var title = question.Title ?? SR.T("Question {0}", index + 1);
        var progressGlyph = index + 1 < _request.Questions.Count ? NextQuestionGlyph : LastQuestionGlyph;
        return new TextBlock($"{title} {progressGlyph}");
    }

    private Visual BuildBottomBar()
    {
        return new VStack(
            new Markup(BuildHelpMarkup) { Wrap = true },
            new HStack(
            [
                new Button(new TextBlock(SR.T("Submit")))
                    .Tone(ControlTone.Primary)
                    .Click(SubmitOrAdvance),
                new Button(new TextBlock(SR.T("Cancel")))
                    .Tone(ControlTone.Error)
                    .Click(() => CancelRequested?.Invoke(this, EventArgs.Empty)),
            ])
            {
                Spacing = 1,
                HorizontalAlignment = Align.Start,
            })
        {
            Spacing = 1,
            HorizontalAlignment = Align.Stretch,
        };
    }

    private string BuildHelpMarkup()
    {
        _ = _helpVersion.Value;
        return _hasFileReviewCommands
            ? $"[dim]{SR.T("LEFT/RIGHT questions · UP/DOWN choices · ENTER select/submit · ESC cancel · Ctrl+G Ctrl+E file editor")}[/]"
            : $"[dim]{SR.T("LEFT/RIGHT questions · UP/DOWN choices · ENTER select/submit · ESC cancel")}[/]";
    }

    private Visual BuildQuestionPage(AltaAskQuestion question, int index)
    {
        var children = new List<Visual>
        {
            new TextBlock(question.Question ?? question.Title ?? SR.T("Question {0}", index + 1)) { Wrap = true },
        };
        if (!string.IsNullOrWhiteSpace(question.Description))
        {
            children.Add(CreateDimMarkup(question.Description));
        }

        if (question.Choices.Count > 0)
        {
            var choices = question.Choices
                .Select((choice, choiceIndex) => new AskChoiceOption(choiceIndex, $"{choiceIndex + 1}. {choice.Title}", choice.Description))
                .ToArray();
            var choiceList = new OptionList<AskChoiceOption>(choices, selectedIndex: 0)
            {
                AutoFocus = true,
                ItemTemplate = new DataTemplate<AskChoiceOption>((value, in _) =>
                {
                    var option = value.GetValue();
                    if (string.IsNullOrWhiteSpace(option.Description))
                    {
                        return new TextBlock(option.Title) { Wrap = true };
                    }

                    return new VStack(
                        new TextBlock(option.Title) { Wrap = true },
                        CreateDimMarkup(option.Description))
                    {
                        Spacing = 0,
                        HorizontalAlignment = Align.Stretch,
                    };
                }, null),
                HorizontalAlignment = Align.Stretch,
                VerticalAlignment = Align.Stretch,
            };
            choiceList.KeyDown((_, e) => HandleQuestionNavigationKeyDown(e));
            choiceList.ItemActivated((_, _) => SubmitOrAdvance());
            _choiceLists[index] = choiceList;
            children.Add(choiceList);
        }

        if (question.Freeform is not null)
        {
            if (!string.IsNullOrWhiteSpace(question.Freeform.Title))
            {
                children.Add(new TextBlock(question.Freeform.Title) { Wrap = true });
            }

            var freeform = new TextBox()
                .Placeholder(question.Freeform.Placeholder ?? string.Empty)
                .HorizontalAlignment(Align.Stretch);
            freeform.AutoFocus = question.Choices.Count == 0;
            _freeformBoxes[index] = freeform;
            children.Add(freeform);
        }

        return new ScrollViewer(new VStack(children.ToArray())
        {
            Spacing = 1,
            Margin = new Thickness(1, 1, 1, 1),
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Stretch,
        }, focusable: false)
            .HorizontalScrollEnabled(false)
            .VerticalScrollEnabled(true)
            .Stretch();
    }

    private void AddNavigationCommands(Visual visual)
    {
        visual.AddCommand(new Command { Id = "CodeAlta.Ask.Next", LabelMarkup = SR.T("Next"), DescriptionMarkup = SR.T("Next ask question."), Gesture = new KeyGesture(TerminalChar.CtrlN, TerminalModifiers.Ctrl), Execute = _ => Next() });
        visual.AddCommand(new Command { Id = "CodeAlta.Ask.Previous", LabelMarkup = SR.T("Previous"), DescriptionMarkup = SR.T("Previous ask question."), Gesture = new KeyGesture(TerminalChar.CtrlP, TerminalModifiers.Ctrl), Execute = _ => Previous() });
        visual.AddCommand(new Command { Id = "CodeAlta.Ask.SubmitOrAdvance", LabelMarkup = SR.T("Submit"), DescriptionMarkup = SR.T("Validate/advance or submit ask answers."), Gesture = new KeyGesture(TerminalKey.Enter), Execute = _ => SubmitOrAdvance() });
        visual.AddCommand(new Command { Id = "CodeAlta.Ask.Cancel", LabelMarkup = SR.T("Cancel"), DescriptionMarkup = SR.T("Cancel ask mode."), Gesture = new KeyGesture(TerminalKey.Escape), Execute = _ => CancelRequested?.Invoke(this, EventArgs.Empty) });
    }

    private static Markup CreateDimMarkup(string text)
        => new($"[dim]{AnsiMarkup.Escape(text)}[/]") { Wrap = true };

    private void HandleKeyDown(KeyEventArgs e)
    {
        if (HandleQuestionNavigationKeyDown(e))
        {
            return;
        }

        switch (e.Key)
        {
            case TerminalKey.Up:
            case TerminalKey.Down:
                e.Handled = true;
                break;
            case TerminalKey.Enter:
                SubmitOrAdvance();
                e.Handled = true;
                break;
            case TerminalKey.Escape:
                CancelRequested?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
                break;
        }
    }

    private bool HandleQuestionNavigationKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case TerminalKey.Right:
                Next();
                e.Handled = true;
                return true;
            case TerminalKey.Left:
                Previous();
                e.Handled = true;
                return true;
            default:
                return false;
        }
    }

    private Visual? GetQuestionFocusTarget(int questionIndex)
    {
        if ((uint)questionIndex >= (uint)_request.Questions.Count)
        {
            return null;
        }

        return _choiceLists[questionIndex] is { } choiceList ? choiceList : _freeformBoxes[questionIndex];
    }

    private void FocusSelectedQuestionInput()
    {
        if (GetQuestionFocusTarget(SelectedIndex) is { } target)
        {
            target.App?.Focus(target);
        }
    }

    private void MarkVisited()
    {
        if ((uint)SelectedIndex < (uint)_visited.Length)
        {
            _visited[SelectedIndex] = true;
        }
    }

    private IReadOnlyList<AltaAskAnswer> CollectAnswers()
    {
        var answers = new AltaAskAnswer[_request.Questions.Count];
        for (var index = 0; index < _request.Questions.Count; index++)
        {
            var selectedChoices = _choiceLists[index] is { } choiceList && (uint)choiceList.SelectedIndex < (uint)choiceList.Items.Count
                ? [choiceList.Items[choiceList.SelectedIndex].ChoiceIndex]
                : Array.Empty<int>();
            answers[index] = new AltaAskAnswer
            {
                QuestionIndex = index,
                SelectedChoiceIndexes = selectedChoices,
                FreeformText = _freeformBoxes[index]?.Text,
            };
        }

        return answers;
    }

    private sealed record AskChoiceOption(int ChoiceIndex, string Title, string? Description);
}
