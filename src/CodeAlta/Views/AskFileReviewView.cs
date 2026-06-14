using System.Globalization;
using System.Text;
using CodeAlta.LiveTool;
using CodeAlta.Models;
using CodeAlta.Presentation.Editing;
using CodeAlta.Presentation.Styling;
using XenoAtom.Ansi;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Commands;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Input;
using XenoAtom.Terminal.UI.Styling;
using XenoAtom.Terminal.UI.Text;

namespace CodeAlta.Views;

internal sealed class AskFileReviewView
{
    private readonly Dictionary<int, AskFileCommentEntry> _comments = new();
    private readonly State<int> _commentVersion = new(0);
    private readonly State<int> _fileStateVersion = new(0);
    private readonly TextDocument? _document;
    private readonly string _fullPath;
    private readonly string _displayPath;
    private ITextSnapshot? _lastFileSnapshot;
    private string _savedText;
    private string? _saveError;
    private int _lastEditorLine = 1;
    private bool _hasSavedUserChanges;

    private AskFileReviewView(string fullPath, string displayPath, string? text, string? loadError)
    {
        _fullPath = fullPath;
        _displayPath = displayPath;
        _savedText = text ?? string.Empty;

        if (loadError is null)
        {
            Editor = CreateEditor(_savedText, fullPath);
            _document = (TextDocument)Editor.TextDocument;
            _lastFileSnapshot = _document.CurrentSnapshot;
            _document.Changed += OnDocumentChanged;
            Editor.LeftMargins.Insert(0, CodeEditor.CreateDiffIndicatorMargin(GetCommentMarker, GetCommentMarkerStyle));
            Editor.AddCommand(new Command
            {
                Id = "CodeAlta.Ask.FileComment.Insert",
                LabelMarkup = "Add line comment",
                DescriptionMarkup = "Insert a user comment below the current file line without changing the file text.",
                Gesture = new KeyGesture(TerminalChar.CtrlK, TerminalModifiers.Ctrl),
                Importance = CommandImportance.Primary,
                Execute = _ => InsertCommentAtCurrentLine(),
            });
            Editor.AddCommand(CreateFocusAdjacentCommentCommand(forward: true));
            Editor.AddCommand(CreateFocusAdjacentCommentCommand(forward: false));
            Editor.AddCommand(CreateSaveCommand());
            Body = CodeEditorFactory.CreateScrollViewer(Editor);
        }
        else
        {
            Body = CreateUnavailableContent(loadError);
        }

        var clearButton = new Button("Clear comments");
        clearButton.Click(ClearComments);
        var header = new Footer()
            .Left(new Markup(BuildHeaderMarkup))
            .Right(new HStack(new Markup(BuildCommentCountMarkup), clearButton)
            {
                Spacing = 1,
                HorizontalAlignment = Align.End,
            });

        Root = new DockLayout()
            .Top(header)
            .Content(new Border(Body.Stretch())
            {
                HorizontalAlignment = Align.Stretch,
                VerticalAlignment = Align.Stretch,
            })
            .Bottom(new Markup(BuildFooterMarkup)
            {
                Wrap = true,
                Margin = new Thickness(1, 0, 1, 0),
            })
            .HorizontalAlignment(Align.Stretch)
            .VerticalAlignment(Align.Stretch);
        Root.AddCommand(CreateSaveCommand());
        Root.AddCommand(CreateClearCommentsCommand());
        Root.AddCommand(CreateFocusEditorCommand());
    }

    public Visual Root { get; }

    public Visual Body { get; }

    public CodeEditor? Editor { get; }

    public bool HasUnsavedChanges => Editor is not null && !string.Equals(GetEditorText(), _savedText, StringComparison.Ordinal);

    public static AskFileReviewView? Create(AltaAskFile? file, IReadOnlyList<string> rootCandidates)
    {
        if (string.IsNullOrWhiteSpace(file?.Path))
        {
            return null;
        }

        var resolution = ResolveFilePath(file.Path!, rootCandidates);
        return TryReadText(resolution.FullPath, out var text, out var error)
            ? new AskFileReviewView(resolution.FullPath, resolution.DisplayPath, text, loadError: null)
            : new AskFileReviewView(resolution.FullPath, resolution.DisplayPath, text: null, error);
    }

    public void AddQuestionFocusCommand(AskQuestionFormView form)
    {
        ArgumentNullException.ThrowIfNull(form);
        Root.AddCommand(new Command
        {
            Id = "CodeAlta.Ask.FocusQuestions",
            LabelMarkup = "Focus ask questions",
            DescriptionMarkup = "Move focus from the attached file editor back to the ask questions.",
            Sequence = new KeySequence(
                new KeyGesture(TerminalChar.CtrlG, TerminalModifiers.Ctrl),
                new KeyGesture(TerminalChar.CtrlN, TerminalModifiers.Ctrl)),
            Presentation = CommandPresentation.None,
            Execute = _ => form.FocusCurrentQuestionInput(),
        });
    }

    public void FocusEditor()
    {
        if (Editor is null)
        {
            return;
        }

        Editor.GoToLine(_lastEditorLine);
        Editor.App?.Focus(Editor);
    }

    public void ClearComments()
    {
        if (Editor is null || _comments.Count == 0)
        {
            return;
        }

        foreach (var lineIndex in _comments.Keys.ToArray())
        {
            Editor.RemoveLineVisual(lineIndex);
        }

        _comments.Clear();
        TouchComments();
    }

    public bool TrySave(out string error)
    {
        error = string.Empty;
        if (Editor is null)
        {
            error = "The attached file is not available for editing.";
            return false;
        }

        try
        {
            var text = GetEditorText();
            File.WriteAllText(_fullPath, text);
            _savedText = text;
            _hasSavedUserChanges = true;
            _saveError = null;
            TouchFileState();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
        {
            error = ex.Message;
            _saveError = error;
            TouchFileState();
            return false;
        }
    }

    public AltaAskFileReview CreateReviewSnapshot()
    {
        foreach (var comment in _comments.Values)
        {
            UpdateCommentSubmission(comment);
        }

        return new AltaAskFileReview
        {
            FileModifiedAndSaved = _hasSavedUserChanges,
            Comments = _comments.Values
                .Where(static comment => comment.IsSubmitted && !string.IsNullOrWhiteSpace(comment.SubmittedText))
                .OrderBy(static comment => comment.LineIndex)
                .Select(static comment => new AltaAskFileComment
                {
                    Line = comment.LineIndex + 1,
                    Text = comment.SubmittedText,
                })
                .ToArray(),
        };
    }

    public Command CreateClearCommentsCommand()
        => new()
        {
            Id = "CodeAlta.Ask.FileComment.Clear",
            LabelMarkup = "Clear file comments",
            DescriptionMarkup = "Clear all comments attached to the ask file.",
            Sequence = new KeySequence(
                new KeyGesture(TerminalChar.CtrlL, TerminalModifiers.Ctrl),
                new KeyGesture(TerminalChar.CtrlK, TerminalModifiers.Ctrl)),
            Presentation = CommandPresentation.None,
            CanExecute = _ => _comments.Count > 0,
            Execute = _ => ClearComments(),
        };

    public Command CreateFocusEditorCommand()
        => new()
        {
            Id = "CodeAlta.Ask.FocusFileEditor",
            LabelMarkup = "Focus file editor",
            DescriptionMarkup = "Move focus to the attached ask file editor.",
            Sequence = new KeySequence(
                new KeyGesture(TerminalChar.CtrlG, TerminalModifiers.Ctrl),
                new KeyGesture(TerminalChar.CtrlE, TerminalModifiers.Ctrl)),
            Presentation = CommandPresentation.None,
            CanExecute = _ => Editor is not null,
            Execute = _ => FocusEditor(),
        };

    public Command CreateFocusAdjacentCommentCommand(bool forward)
        => new()
        {
            Id = forward ? "CodeAlta.Ask.FileComment.FocusNext" : "CodeAlta.Ask.FileComment.FocusPrevious",
            LabelMarkup = forward ? "Next file comment" : "Previous file comment",
            DescriptionMarkup = forward
                ? "Move focus from the file editor to the next file comment."
                : "Move focus from the file editor to the previous file comment.",
            Gesture = new KeyGesture(forward ? TerminalChar.CtrlN : TerminalChar.CtrlP, TerminalModifiers.Ctrl),
            Presentation = CommandPresentation.None,
            CanExecute = _ => _comments.Count > 0,
            Execute = _ => FocusAdjacentComment(GetCurrentEditorLineIndex(), forward),
        };

    private CodeEditor CreateEditor(string text, string fullPath)
    {
        return CodeEditorFactory.Create(
            text,
            new CodeEditorFactoryOptions
            {
                Config = new CodeEditorConfig { GoToLine = CodeEditorGoToLineConfig.Disabled },
                FileName = fullPath,
                AutoFocus = false,
                WordWrap = true,
                MinHeight = 8,
            });
    }

    public Command CreateSaveCommand()
        => new()
        {
            Id = "CodeAlta.Ask.File.Save",
            LabelMarkup = "Save ask file",
            DescriptionMarkup = "Save user edits to the attached ask file.",
            Gesture = new KeyGesture(TerminalChar.CtrlS, TerminalModifiers.Ctrl),
            Presentation = CommandPresentation.None,
            CanExecute = _ => HasUnsavedChanges,
            Execute = target =>
            {
                _ = TrySave(out var ignored);
            },
        };

    private void InsertCommentAtCurrentLine()
    {
        if (Editor is null)
        {
            return;
        }

        var lineIndex = Math.Max(0, Editor.Line - 1);
        _lastEditorLine = lineIndex + 1;
        if (_comments.TryGetValue(lineIndex, out var existing))
        {
            FocusComment(existing);
            return;
        }

        var entry = CreateCommentEntry(lineIndex);
        _comments.Add(lineIndex, entry);
        Editor.SetLineVisual(lineIndex, entry.Group);
        TouchComments();
        FocusComment(entry);
    }

    private AskFileCommentEntry CreateCommentEntry(int lineIndex)
    {
        var commentDocument = new TextDocument(string.Empty);
        var textArea = new TextArea()
            .Placeholder("Enter a user comment... Enter newline · Esc done · Ctrl+D delete · Ctrl+N/P comments")
            .AutoSizeMode(TextEditorAutoSizeMode.Height)
            .MinHeight(1)
            .HorizontalAlignment(Align.Stretch);
        textArea.TextDocument = commentDocument;
        var textAreaScroller = new ScrollViewer(textArea, focusable: false)
            .HorizontalScrollEnabled(false)
            .VerticalScrollEnabled(true)
            .MaxHeight(8)
            .HorizontalAlignment(Align.Stretch);

        var entry = new AskFileCommentEntry(lineIndex, textArea);
        commentDocument.Changed += (_, _) =>
        {
            if (entry.IsSubmitted && !string.Equals(GetText(entry.TextArea.TextDocument).Trim(), entry.SubmittedText, StringComparison.Ordinal))
            {
                entry.IsSubmitted = false;
                entry.Group.BottomRightText = null;
                TouchComments();
            }

            UpdateCommentHeight(entry);
            Editor?.NotifyLineVisualChanged(entry.LineIndex);
        };
        var deleteButton = new Button("Delete") { Tone = ControlTone.Error };
        deleteButton.Click(() => DeleteComment(entry.LineIndex));

        var helpText = new TextBlock("Ctrl+D delete · Esc done · Ctrl+N/P comments")
        {
            IsSelectable = false,
        };
        helpText.SetStyle(TextBlockStyle.Key, TextBlockStyle.Default with { Foreground = Colors.TerminalBrightBlack });

        Group? group = null;
        group = new Group(CreateUserCommentHeader(lineIndex))
        {
            TopRightText = deleteButton,
            BottomLeftText = helpText,
            Padding = new Thickness(1, 0, 1, 0),
            HorizontalAlignment = Align.Stretch,
            Content = textAreaScroller,
        }.Style(() => UiPalette.GetChatGroupStyle(group!.GetTheme(), ChatTimelineTone.User));
        entry.Group = group;

        textArea.AddCommand(new Command
        {
            Id = $"CodeAlta.Ask.FileComment.Done.{lineIndex}",
            LabelMarkup = "Done editing comment",
            DescriptionMarkup = "Keep this line comment and return focus to the file editor.",
            Gesture = new KeyGesture(TerminalKey.Escape),
            Presentation = CommandPresentation.None,
            Execute = _ => FinishComment(entry),
        });
        textArea.AddCommand(new Command
        {
            Id = $"CodeAlta.Ask.FileComment.Discard.{lineIndex}",
            LabelMarkup = "Discard comment",
            DescriptionMarkup = "Discard this line comment.",
            Gesture = new KeyGesture(TerminalChar.CtrlD, TerminalModifiers.Ctrl),
            Presentation = CommandPresentation.None,
            Execute = _ => DeleteComment(entry.LineIndex),
        });
        textArea.AddCommand(new Command
        {
            Id = $"CodeAlta.Ask.FileComment.Next.{lineIndex}",
            LabelMarkup = "Next comment",
            DescriptionMarkup = "Move focus to the next file comment.",
            Gesture = new KeyGesture(TerminalChar.CtrlN, TerminalModifiers.Ctrl),
            Presentation = CommandPresentation.None,
            Execute = _ => FocusAdjacentComment(entry.LineIndex, forward: true),
        });
        textArea.AddCommand(new Command
        {
            Id = $"CodeAlta.Ask.FileComment.Previous.{lineIndex}",
            LabelMarkup = "Previous comment",
            DescriptionMarkup = "Move focus to the previous file comment.",
            Gesture = new KeyGesture(TerminalChar.CtrlP, TerminalModifiers.Ctrl),
            Presentation = CommandPresentation.None,
            Execute = _ => FocusAdjacentComment(entry.LineIndex, forward: false),
        });
        UpdateCommentHeight(entry);

        return entry;
    }

    private void FinishComment(AskFileCommentEntry entry)
    {
        UpdateCommentSubmission(entry);
        if (Editor is not null)
        {
            _lastEditorLine = Math.Min(entry.LineIndex + 2, Math.Max(1, Editor.TextDocument.CurrentSnapshot.LineCount));
            Editor.GoToLine(_lastEditorLine);
            Editor.App?.Focus(Editor);
        }
    }

    private void DeleteComment(int lineIndex)
    {
        if (Editor is null || !_comments.Remove(lineIndex))
        {
            return;
        }

        Editor.RemoveLineVisual(lineIndex);
        _lastEditorLine = lineIndex + 1;
        Editor.GoToLine(_lastEditorLine);
        Editor.App?.Focus(Editor);
        TouchComments();
    }

    private void UpdateCommentSubmission(AskFileCommentEntry entry)
    {
        var text = GetText(entry.TextArea.TextDocument).Trim();
        if (text.Length > 0)
        {
            entry.IsSubmitted = true;
            entry.SubmittedText = text;
            entry.Group.BottomRightText = new Markup("[success]✅[/]");
        }
        else
        {
            entry.IsSubmitted = false;
            entry.SubmittedText = string.Empty;
            entry.Group.BottomRightText = null;
        }

        Editor?.NotifyLineVisualChanged(entry.LineIndex);
        TouchComments();
    }

    private static void UpdateCommentHeight(AskFileCommentEntry entry)
    {
        var lineCount = Math.Max(1, entry.TextArea.TextDocument.CurrentSnapshot.LineCount);
        entry.TextArea.MinHeight = Math.Min(8, lineCount);
    }

    private static Markup CreateUserCommentHeader(int lineIndex)
        => new($"[accent]{TerminalIcons.MdAccount}[/] [bold]User Comment[/] [dim]- line {(lineIndex + 1).ToString(CultureInfo.InvariantCulture)}[/]");

    private void FocusAdjacentComment(int lineIndex, bool forward)
    {
        if (_comments.Count == 0)
        {
            return;
        }

        if (_comments.TryGetValue(lineIndex, out var current))
        {
            UpdateCommentSubmission(current);
        }

        var ordered = _comments.Keys.Order().ToArray();
        var index = Array.BinarySearch(ordered, lineIndex);
        if (index < 0)
        {
            index = ~index;
            if (!forward)
            {
                index--;
            }
        }
        else
        {
            index += forward ? 1 : -1;
        }

        if (index < 0)
        {
            index = ordered.Length - 1;
        }
        else if (index >= ordered.Length)
        {
            index = 0;
        }

        var target = _comments[ordered[index]];
        FocusComment(target);
    }

    private void FocusComment(AskFileCommentEntry entry)
    {
        _lastEditorLine = entry.LineIndex + 1;
        Editor?.GoToLine(_lastEditorLine);
        (Editor?.App ?? entry.TextArea.App)?.Focus(entry.TextArea);
    }

    private int GetCurrentEditorLineIndex()
        => Math.Max(0, (Editor?.Line ?? _lastEditorLine) - 1);

    private void OnDocumentChanged(object? sender, TextDocumentChangedEventArgs e)
    {
        MoveCommentsForDocumentChange(e);
        if (Editor is not null)
        {
            _lastEditorLine = Editor.Line;
        }

        _lastFileSnapshot = _document?.CurrentSnapshot;
        TouchFileState();
    }

    private void MoveCommentsForDocumentChange(TextDocumentChangedEventArgs e)
    {
        if (Editor is null || _lastFileSnapshot is null || _comments.Count == 0 || e.NewLineCount == e.OldLineCount)
        {
            return;
        }

        var lineDelta = e.NewLineCount - e.OldLineCount;
        var changeLine = _lastFileSnapshot.GetLineIndexFromPosition(Math.Clamp(e.Position, 0, _lastFileSnapshot.Length));
        var removedEndLine = _lastFileSnapshot.GetLineIndexFromPosition(Math.Clamp(e.Position + e.RemovedLength, 0, _lastFileSnapshot.Length));
        var maxLineIndex = Math.Max(0, e.NewLineCount - 1);
        var moved = new Dictionary<int, AskFileCommentEntry>();
        var changed = false;

        foreach (var (lineIndex, entry) in _comments.OrderBy(static pair => pair.Key))
        {
            var newLineIndex = lineIndex;
            if (lineIndex > removedEndLine)
            {
                newLineIndex = Math.Clamp(lineIndex + lineDelta, 0, maxLineIndex);
            }
            else if (lineIndex >= changeLine)
            {
                newLineIndex = Math.Clamp(changeLine, 0, maxLineIndex);
            }

            newLineIndex = FindAvailableLineIndex(newLineIndex, maxLineIndex, moved);

            entry.LineIndex = newLineIndex;
            moved[newLineIndex] = entry;
            changed |= newLineIndex != lineIndex;
        }

        if (!changed)
        {
            return;
        }

        Editor.ClearLineVisuals();
        _comments.Clear();
        foreach (var (lineIndex, entry) in moved.OrderBy(static pair => pair.Key))
        {
            _comments[lineIndex] = entry;
            Editor.SetLineVisual(lineIndex, entry.Group);
        }

        TouchComments();
    }

    private static int FindAvailableLineIndex(int preferredLineIndex, int maxLineIndex, Dictionary<int, AskFileCommentEntry> comments)
    {
        if (!comments.ContainsKey(preferredLineIndex))
        {
            return preferredLineIndex;
        }

        for (var candidate = preferredLineIndex + 1; candidate <= maxLineIndex; candidate++)
        {
            if (!comments.ContainsKey(candidate))
            {
                return candidate;
            }
        }

        for (var candidate = preferredLineIndex - 1; candidate >= 0; candidate--)
        {
            if (!comments.ContainsKey(candidate))
            {
                return candidate;
            }
        }

        return preferredLineIndex;
    }

    private string BuildHeaderMarkup()
    {
        _ = _fileStateVersion.Value;
        var modified = HasUnsavedChanges ? " [warning]*[/]" : string.Empty;
        return $"[bold]File context:[/] {AnsiMarkup.Escape(_displayPath)}{modified}";
    }

    private string BuildCommentCountMarkup()
    {
        _ = _commentVersion.Value;
        var count = _comments.Count;
        return count == 1 ? "[dim]1 comment[/]" : $"[dim]{count} comments[/]";
    }

    private string BuildFooterMarkup()
    {
        _ = _fileStateVersion.Value;
        var status = _saveError is null ? string.Empty : $" · [error]Save failed: {AnsiMarkup.Escape(_saveError)}[/]";
        return $"[dim]Ctrl+K comment · Ctrl+N/P comments · Ctrl+S save · Ctrl+G Ctrl+N questions · Ctrl+L Ctrl+K clear comments · Ctrl+F find[/]{status}";
    }

    private Rune? GetCommentMarker(int lineIndex)
        => _comments.ContainsKey(lineIndex) ? new Rune('■') : null;

    private Style GetCommentMarkerStyle(int lineIndex)
        => _comments.ContainsKey(lineIndex)
            ? Style.None.WithForeground(Colors.DeepSkyBlue) | TextStyle.Bold
            : Style.None;

    private string GetEditorText()
        => Editor is null ? string.Empty : CodeEditorFactory.GetText(Editor);

    private static string GetText(ITextDocument document)
        => CodeEditorFactory.GetText(document);

    private void TouchComments()
        => _commentVersion.Value++;

    private void TouchFileState()
        => _fileStateVersion.Value++;

    private static Visual CreateUnavailableContent(string message)
        => new ScrollViewer(new TextBlock(message)
        {
            Wrap = true,
            Margin = new Thickness(1),
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Stretch,
        }, focusable: false)
            .HorizontalScrollEnabled(false)
            .VerticalScrollEnabled(true)
            .Stretch();

    private static bool TryReadText(string fullPath, out string text, out string error)
    {
        try
        {
            if (!File.Exists(fullPath))
            {
                text = string.Empty;
                error = $"Attached ask file was not found: {fullPath}";
                return false;
            }

            text = File.ReadAllText(fullPath);
            error = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
        {
            text = string.Empty;
            error = $"Attached ask file could not be loaded: {ex.Message}";
            return false;
        }
    }

    private static AskFileResolution ResolveFilePath(string path, IReadOnlyList<string> rootCandidates)
    {
        var normalizedPath = path.Trim();
        if (Path.IsPathFullyQualified(normalizedPath))
        {
            var fullPath = Path.GetFullPath(normalizedPath);
            return new AskFileResolution(fullPath, fullPath);
        }

        foreach (var root in rootCandidates)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            var candidate = Path.GetFullPath(Path.Combine(root, normalizedPath));
            if (File.Exists(candidate))
            {
                return new AskFileResolution(candidate, path.Replace('\\', '/'));
            }
        }

        var fallbackRoot = rootCandidates.FirstOrDefault(static root => !string.IsNullOrWhiteSpace(root)) ?? Environment.CurrentDirectory;
        var fallback = Path.GetFullPath(Path.Combine(fallbackRoot, normalizedPath));
        return new AskFileResolution(fallback, path.Replace('\\', '/'));
    }

    private sealed record AskFileResolution(string FullPath, string DisplayPath);

    private sealed class AskFileCommentEntry
    {
        public AskFileCommentEntry(int lineIndex, TextArea textArea)
        {
            LineIndex = lineIndex;
            TextArea = textArea;
        }

        public int LineIndex { get; set; }

        public TextArea TextArea { get; }

        public Group Group { get; set; } = null!;

        public bool IsSubmitted { get; set; }

        public string SubmittedText { get; set; } = string.Empty;
    }
}
