using System.Globalization;
using CodeAlta.Catalog;
using CodeAlta.LiveTool;
using CodeAlta.Models;
using CodeAlta.Presentation.Editing;
using XenoAtom.Ansi;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using CodeAlta.Presentation.Styling;
using XenoAtom.Terminal.UI.Collections;
using XenoAtom.Terminal.UI.Commands;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Input;
using XenoAtom.Terminal.UI.Styling;
using XenoAtom.Terminal.UI.Templating;
using XenoAtom.Terminal.UI.Text;
using UiCommand = XenoAtom.Terminal.UI.Commands.Command;

namespace CodeAlta.Views;

internal sealed class ReminderManagementDialog
{
    private const string PromptEditorLanguageId = "markdown";
    private const string PromptEditorFileName = "reminder.md";

    private readonly AltaReminderService _reminders;
    private readonly SessionViewDescriptor _session;
    private readonly Func<Rectangle?> _getBounds;
    private readonly Func<Visual?> _getFocusTarget;
    private readonly Action<string, StatusTone> _setStatus;
    private readonly Action _onRemindersChanged;
    private readonly Dialog _dialog;
    private readonly ListBox<ReminderRow> _reminderList;
    private readonly BindableList<ReminderRow> _rows;
    private readonly State<int> _selectedIndex = new(-1);
    private readonly TextBox _durationBox;
    private readonly TextBox _repeatBox;
    private readonly CodeEditor _contentEditor;
    private readonly Markup _summaryMarkup;
    private readonly Markup _detailMarkup;
    private readonly Markup _statusMarkup;
    private string _summaryText = string.Empty;
    private string _statusText = $"[dim]{SR.T("Create schedules a delayed prompt for this session. Delete cancels the selected reminder.")}[/]";

    public ReminderManagementDialog(
        AltaReminderService reminders,
        SessionViewDescriptor session,
        Func<Rectangle?> getBounds,
        Func<Visual?> getFocusTarget,
        Action<string, StatusTone> setStatus,
        Action onRemindersChanged)
    {
        ArgumentNullException.ThrowIfNull(reminders);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(getBounds);
        ArgumentNullException.ThrowIfNull(getFocusTarget);
        ArgumentNullException.ThrowIfNull(setStatus);
        ArgumentNullException.ThrowIfNull(onRemindersChanged);

        _reminders = reminders;
        _session = session;
        _getBounds = getBounds;
        _getFocusTarget = getFocusTarget;
        _setStatus = setStatus;
        _onRemindersChanged = onRemindersChanged;

        _reminderList = new ListBox<ReminderRow>()
            .MinWidth(36)
            .Stretch();
        _rows = _reminderList.Items;
        _reminderList.SelectedIndex(_selectedIndex.Bind.Value);
        _reminderList.ItemTemplate = new DataTemplate<ReminderRow>(
            static (DataTemplateValue<ReminderRow> value, in DataTemplateContext _) => BuildReminderListItem(value.GetValue()),
            null);
        _reminderList.AddCommand(new UiCommand
        {
            Id = "CodeAlta.Reminders.DeleteSelected",
            LabelMarkup = SR.T("Delete Reminder"),
            DescriptionMarkup = SR.T("Delete the selected reminder."),
            Gesture = new KeyGesture(TerminalKey.Delete),
            CanExecute = _ => GetSelectedRow() is not null,
            Execute = _ => DeleteSelectedReminder(),
        });

        _durationBox = new TextBox
        {
            Text = "300",
            HorizontalAlignment = Align.Stretch,
        }.Placeholder(SR.T("seconds or TimeSpan, e.g. 300 or 00:05:00"));
        _repeatBox = new TextBox
        {
            Text = "1",
            HorizontalAlignment = Align.Stretch,
        }.Placeholder(SR.T("repeat count"));
        _contentEditor = CreateMarkdownEditor(SR.T("Check on this session."));

        _summaryMarkup = new Markup(() => _summaryText) { Wrap = true };
        _detailMarkup = new Markup(() => GetSelectedRow() is { } row
            ? BuildDetailMarkup(row.Descriptor)
            : $"[dim]{SR.T("Select a reminder to inspect its schedule.")}[/]") { Wrap = true };
        _statusMarkup = new Markup(() => _statusText) { Wrap = true };

        _dialog = BuildDialog();
    }

    public void Show()
    {
        Reload(null);
        ResponsiveDialogSize.Apply(_dialog, _getBounds(), minWidth: 108, minHeight: 28, widthFactor: 0.84, heightFactor: 0.78);
        _dialog.Show();
        _dialog.App?.Focus(_contentEditor);
    }

    private Dialog BuildDialog()
    {
        var closeButton = new Button(new TextBlock($"{TerminalIcons.MdClose} {SR.T("Close")}"))
        {
            HorizontalAlignment = Align.End,
            VerticalAlignment = Align.Start,
        };
        closeButton.Click(Close);

        var createButton = new Button($"{TerminalIcons.MdTimerOutline} {SR.T("Create")}")
            .Tone(ControlTone.Success)
            .Click(CreateReminder);
        var loadButton = new Button($"{TerminalIcons.MdFileEditOutline} {SR.T("Load Message")}")
            .IsEnabled(() => GetSelectedRow() is not null)
            .Click(LoadSelectedMessage);
        var updateButton = new Button($"{TerminalIcons.MdContentSaveCheckOutline} {SR.T("Save Message")}")
            .Tone(ControlTone.Success)
            .IsEnabled(() => GetSelectedRow() is not null)
            .Click(UpdateSelectedMessage);
        var deleteButton = new Button($"{TerminalIcons.MdTrashCanOutline} {SR.T("Delete")}")
            .Tone(ControlTone.Error)
            .IsEnabled(() => GetSelectedRow() is not null)
            .Click(DeleteSelectedReminder);
        var refreshButton = new Button($"{TerminalIcons.MdRefresh} {SR.T("Refresh")}")
            .Click(() => Reload(GetSelectedRow()?.Descriptor.ReminderId));

        var toolbar = new HStack(createButton, loadButton, updateButton, deleteButton, refreshButton)
        {
            Spacing = 1,
            HorizontalAlignment = Align.Stretch,
        };

        var intro = new Markup(() =>
            $"[dim]{SR.T("Managing active reminders for session {0} ({1}).", $"[bold]{AnsiMarkup.Escape(_session.Title)}[/]", $"[bold]{AnsiMarkup.Escape(_session.SessionId)}[/]")}[/]")
        {
            Wrap = true,
        };

        var form = new Grid
            {
                HorizontalAlignment = Align.Stretch,
            }
            .Rows(
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto })
            .Columns(
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Star(1) });
        form.Cell(new TextBlock(SR.T("Delay")) { VerticalAlignment = Align.Center }, 0, 0);
        form.Cell(_durationBox.Stretch(), 0, 1);
        form.Cell(new TextBlock(SR.T("Repeat")) { VerticalAlignment = Align.Center }, 1, 0);
        form.Cell(_repeatBox.Stretch(), 1, 1);

        var editorFrame = new Border(
            new ScrollViewer(_contentEditor.Stretch(), focusable: false)
                .HorizontalAlignment(Align.Stretch)
                .VerticalAlignment(Align.Stretch))
        {
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Stretch,
        };

        var leftPane = new VStack(
            new Group(SR.T("Active Reminders"))
                .Style(GroupStyle.Rounded)
                .Content(_reminderList.Stretch())
                .Padding(new Thickness(1, 0, 1, 0))
                .HorizontalAlignment(Align.Stretch)
                .VerticalAlignment(Align.Stretch),
            _detailMarkup)
        {
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Stretch,
            Spacing = 1,
        };

        var rightPane = new Grid
            {
                HorizontalAlignment = Align.Stretch,
                VerticalAlignment = Align.Stretch,
            }
            .Rows(
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Star(1) })
            .Columns(new ColumnDefinition { Width = GridLength.Star(1) });
        rightPane.Cell(form, 0, 0);
        rightPane.Cell(new TextBlock(SR.T("Reminder prompt (Markdown)")), 1, 0);
        rightPane.Cell(new Markup($"[dim]{SR.T("The prompt is sent to this session when the reminder fires.")}[/]") { Wrap = true }, 2, 0);
        rightPane.Cell(editorFrame, 3, 0);

        var splitter = new HSplitter(leftPane, rightPane)
        {
            Ratio = 0.34,
            MinFirst = 34,
            MinSecond = 60,
        };

        var content = new Grid
            {
                HorizontalAlignment = Align.Stretch,
                VerticalAlignment = Align.Stretch,
            }
            .Rows(
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Star(1) },
                new RowDefinition { Height = GridLength.Auto })
            .Columns(new ColumnDefinition { Width = GridLength.Star(1) });
        content.Cell(toolbar, 0, 0);
        content.Cell(intro, 1, 0);
        content.Cell(_summaryMarkup, 2, 0);
        content.Cell(splitter, 3, 0);
        content.Cell(_statusMarkup, 4, 0);

        var dialog = new Dialog()
            .Title(SR.T("Reminders"))
            .TopRightText(closeButton)
            .BottomLeftText(new Markup($"[dim]{SR.T("Ctrl+Enter Create · Ctrl+E Load · Ctrl+S Save message · Delete Remove · Ctrl+R Refresh · Esc Close")}[/]"))
            .IsModal(true)
            .Padding(1)
            .Content(content);
        dialog.AddCommand(new UiCommand
        {
            Id = "CodeAlta.Reminders.Create",
            LabelMarkup = SR.T("Create Reminder"),
            DescriptionMarkup = SR.T("Create a reminder for the current session."),
            Gesture = new KeyGesture(TerminalKey.Enter, TerminalModifiers.Ctrl),
            Importance = CommandImportance.Primary,
            Execute = _ => CreateReminder(),
        });
        dialog.AddCommand(new UiCommand
        {
            Id = "CodeAlta.Reminders.LoadSelectedMessage",
            LabelMarkup = SR.T("Load Reminder Message"),
            DescriptionMarkup = SR.T("Load the selected reminder message into the editor."),
            Gesture = new KeyGesture(TerminalChar.CtrlE, TerminalModifiers.Ctrl),
            CanExecute = _ => GetSelectedRow() is not null,
            Execute = _ => LoadSelectedMessage(),
        });
        dialog.AddCommand(new UiCommand
        {
            Id = "CodeAlta.Reminders.UpdateSelectedMessage",
            LabelMarkup = SR.T("Save Reminder Message"),
            DescriptionMarkup = SR.T("Save the editor text as the selected reminder message."),
            Gesture = new KeyGesture(TerminalChar.CtrlS, TerminalModifiers.Ctrl),
            CanExecute = _ => GetSelectedRow() is not null,
            Execute = _ => UpdateSelectedMessage(),
        });
        dialog.AddCommand(new UiCommand
        {
            Id = "CodeAlta.Reminders.Refresh",
            LabelMarkup = SR.T("Refresh Reminders"),
            DescriptionMarkup = SR.T("Reload reminders for this session."),
            Gesture = new KeyGesture(TerminalChar.CtrlR, TerminalModifiers.Ctrl),
            Execute = _ => Reload(GetSelectedRow()?.Descriptor.ReminderId),
        });
        dialog.AddCommand(new UiCommand
        {
            Id = "CodeAlta.Reminders.Close",
            LabelMarkup = SR.T("Close"),
            DescriptionMarkup = SR.T("Close the reminders dialog."),
            Gesture = new KeyGesture(TerminalKey.Escape),
            Importance = CommandImportance.Primary,
            Execute = _ => Close(),
        });
        return dialog;
    }

    private void LoadSelectedMessage()
    {
        var row = GetSelectedRow();
        if (row is null)
        {
            return;
        }

        if (!_reminders.TryGetContent(row.Descriptor.ReminderId, out var content))
        {
            SetDialogStatus($"[warning]{SR.T("Reminder was not found.")}[/]", SR.T("Reminder was not found."), StatusTone.Warning);
            Reload(null);
            _onRemindersChanged();
            return;
        }

        SetEditorText(content ?? string.Empty);
        SetDialogStatus($"[success]{SR.T("Reminder message loaded.")}[/]", SR.T("Reminder message loaded."), StatusTone.Info);
        _dialog.App?.Focus(_contentEditor);
    }

    private void UpdateSelectedMessage()
    {
        var row = GetSelectedRow();
        if (row is null)
        {
            return;
        }

        var content = GetEditorText(_contentEditor).Trim();
        if (string.IsNullOrWhiteSpace(content))
        {
            SetDialogStatus($"[warning]{SR.T("Reminder prompt content is required.")}[/]", SR.T("Reminder prompt content is required."), StatusTone.Warning);
            _dialog.App?.Focus(_contentEditor);
            return;
        }

        try
        {
            if (!_reminders.TryUpdateContent(row.Descriptor.ReminderId, content, out var descriptor))
            {
                SetDialogStatus($"[warning]{SR.T("Reminder was not found.")}[/]", SR.T("Reminder was not found."), StatusTone.Warning);
                Reload(null);
                _onRemindersChanged();
                return;
            }

            SetDialogStatus($"[success]{SR.T("Reminder message updated.")}[/]", SR.T("Reminder message updated."), StatusTone.Info);
            Reload(descriptor!.ReminderId);
            _onRemindersChanged();
        }
        catch (ArgumentException ex)
        {
            SetDialogStatus($"[error]{AnsiMarkup.Escape(ex.Message)}[/]", ex.Message, StatusTone.Error);
        }
    }

    private void CreateReminder()
    {
        var content = GetEditorText(_contentEditor).Trim();
        if (string.IsNullOrWhiteSpace(content))
        {
            SetDialogStatus($"[warning]{SR.T("Reminder prompt content is required.")}[/]", SR.T("Reminder prompt content is required."), StatusTone.Warning);
            _dialog.App?.Focus(_contentEditor);
            return;
        }

        if (!TryParseDuration(_durationBox.Text, out var duration, out var durationError))
        {
            SetDialogStatus($"[warning]{AnsiMarkup.Escape(durationError)}[/]", durationError, StatusTone.Warning);
            _dialog.App?.Focus(_durationBox);
            return;
        }

        if (!TryParseRepeat(_repeatBox.Text, out var repeat, out var repeatError))
        {
            SetDialogStatus($"[warning]{AnsiMarkup.Escape(repeatError)}[/]", repeatError, StatusTone.Warning);
            _dialog.App?.Focus(_repeatBox);
            return;
        }

        try
        {
            var descriptor = _reminders.Create(new AltaReminderCreateRequest
            {
                TargetSessionId = _session.SessionId,
                Content = content,
                Duration = duration,
                RepeatCount = repeat,
                SourceSessionId = _session.SessionId,
                SourceProjectId = _session.ProjectRef,
                Cwd = string.IsNullOrWhiteSpace(_session.WorkingDirectory) ? null : _session.WorkingDirectory,
            });
            SetDialogStatus($"[success]{SR.T("Reminder created.")}[/]", SR.T("Reminder created."), StatusTone.Info);
            Reload(descriptor.ReminderId);
            _onRemindersChanged();
        }
        catch (ArgumentOutOfRangeException ex)
        {
            SetDialogStatus($"[error]{AnsiMarkup.Escape(ex.Message)}[/]", ex.Message, StatusTone.Error);
        }
        catch (ArgumentException ex)
        {
            SetDialogStatus($"[error]{AnsiMarkup.Escape(ex.Message)}[/]", ex.Message, StatusTone.Error);
        }
    }

    private void DeleteSelectedReminder()
    {
        var row = GetSelectedRow();
        if (row is null)
        {
            return;
        }

        if (_reminders.TryDelete(row.Descriptor.ReminderId, out _))
        {
            SetDialogStatus($"[success]{SR.T("Reminder deleted.")}[/]", SR.T("Reminder deleted."), StatusTone.Info);
            Reload(null);
            _onRemindersChanged();
            return;
        }

        SetDialogStatus($"[warning]{SR.T("Reminder was not found.")}[/]", SR.T("Reminder was not found."), StatusTone.Warning);
        Reload(null);
        _onRemindersChanged();
    }

    private void Reload(string? preferredReminderId)
    {
        var reminders = _reminders.List(_session.SessionId, includeCompleted: false)
            .Select(static descriptor => new ReminderRow(descriptor))
            .ToArray();
        _rows.Clear();
        _rows.AddRange(reminders);
        _summaryText = reminders.Length == 0
            ? $"[dim]{SR.T("No active reminders for this session.")}[/]"
            : $"[primary]{TerminalIcons.MdTimerOutline} {SR.T("{0} active reminder(s)", reminders.Length)}[/]";

        var selectedIndex = -1;
        if (!string.IsNullOrWhiteSpace(preferredReminderId))
        {
            selectedIndex = Array.FindIndex(reminders, row => string.Equals(row.Descriptor.ReminderId, preferredReminderId, StringComparison.OrdinalIgnoreCase));
        }

        if (selectedIndex < 0 && reminders.Length > 0)
        {
            selectedIndex = 0;
        }

        _selectedIndex.Value = selectedIndex;
    }

    private ReminderRow? GetSelectedRow()
    {
        var index = _selectedIndex.Value;
        return index >= 0 && index < _rows.Count ? _rows[index] : null;
    }

    private void SetDialogStatus(string markup, string shellStatus, StatusTone tone)
    {
        _statusText = markup;
        _setStatus(shellStatus, tone);
    }

    private void Close()
    {
        _dialog.Close();
        if (_getFocusTarget() is { } focusTarget)
        {
            focusTarget.App?.Focus(focusTarget);
        }
    }

    private static Visual BuildReminderListItem(ReminderRow row)
    {
        var descriptor = row.Descriptor;
        var due = descriptor.DueAt is null ? SR.T("not scheduled") : FormatDue(descriptor.DueAt.Value);
        var repeat = descriptor.RepeatCount == 1
            ? string.Empty
            : $" · {descriptor.FiredCount}/{descriptor.RepeatCount}";
        return new VStack(
            new Markup($"[bold]{TerminalIcons.MdTimerOutline} {AnsiMarkup.Escape(due)}[/]") { Wrap = false },
            new Markup($"[dim]{AnsiMarkup.Escape(descriptor.ContentPreview)}[/]") { Wrap = false },
            new Markup($"[dim]{AnsiMarkup.Escape(descriptor.ReminderId)}{repeat}[/]") { Wrap = false });
    }

    private static string BuildDetailMarkup(AltaReminderDescriptor descriptor)
    {
        var due = descriptor.DueAt is null ? SR.T("not scheduled") : FormatDue(descriptor.DueAt.Value);
        var created = descriptor.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);
        var builder = new System.Text.StringBuilder();
        builder.Append("[bold]").Append(AnsiMarkup.Escape(descriptor.ReminderId)).AppendLine("[/]");
        builder.Append("[dim]").Append(SR.T("Due")).Append(":[/] ").Append(AnsiMarkup.Escape(due)).AppendLine();
        builder.Append("[dim]").Append(SR.T("Delay")).Append(":[/] ").Append(AnsiMarkup.Escape(FormatDuration(descriptor.Duration))).AppendLine();
        builder.Append("[dim]").Append(SR.T("Repeat")).Append(":[/] ").Append(descriptor.FiredCount.ToString(CultureInfo.InvariantCulture)).Append('/').Append(descriptor.RepeatCount.ToString(CultureInfo.InvariantCulture)).AppendLine();
        builder.Append("[dim]").Append(SR.T("Created")).Append(":[/] ").Append(AnsiMarkup.Escape(created)).AppendLine();
        builder.Append("[dim]").Append(SR.T("Prompt preview")).AppendLine(":[/]");
        builder.Append(AnsiMarkup.Escape(descriptor.ContentPreview));
        return builder.ToString();
    }

    private static bool TryParseDuration(string? value, out TimeSpan duration, out string error)
    {
        duration = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            error = SR.T("Delay is required.");
            return false;
        }

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            if (seconds > 0 && !double.IsInfinity(seconds) && !double.IsNaN(seconds))
            {
                try
                {
                    duration = TimeSpan.FromSeconds(seconds);
                    error = string.Empty;
                    return true;
                }
                catch (OverflowException)
                {
                    error = SR.T("Delay is too large.");
                    return false;
                }
            }

            error = SR.T("Delay must be greater than zero seconds.");
            return false;
        }

        if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var parsed) && parsed > TimeSpan.Zero)
        {
            duration = parsed;
            error = string.Empty;
            return true;
        }

        error = SR.T("Delay must be a positive number of seconds or a positive TimeSpan such as 00:05:00.");
        return false;
    }

    private static bool TryParseRepeat(string? value, out int repeat, out string error)
    {
        repeat = 1;
        if (string.IsNullOrWhiteSpace(value))
        {
            error = string.Empty;
            return true;
        }

        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out repeat) && repeat > 0)
        {
            error = string.Empty;
            return true;
        }

        repeat = 1;
        error = SR.T("Repeat must be a positive integer.");
        return false;
    }

    private static string FormatDue(DateTimeOffset dueAt)
    {
        var local = dueAt.ToLocalTime();
        var remaining = dueAt - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            return local.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);
        }

        return SR.T("in {0} ({1})", FormatDuration(remaining), local.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalDays >= 1)
        {
            return $"{Math.Floor(duration.TotalDays).ToString(CultureInfo.InvariantCulture)}d {duration.Hours.ToString(CultureInfo.InvariantCulture)}h";
        }

        if (duration.TotalHours >= 1)
        {
            return $"{Math.Floor(duration.TotalHours).ToString(CultureInfo.InvariantCulture)}h {duration.Minutes.ToString(CultureInfo.InvariantCulture)}m";
        }

        if (duration.TotalMinutes >= 1)
        {
            return $"{Math.Floor(duration.TotalMinutes).ToString(CultureInfo.InvariantCulture)}m {duration.Seconds.ToString(CultureInfo.InvariantCulture)}s";
        }

        return $"{Math.Max(1, (int)Math.Ceiling(duration.TotalSeconds)).ToString(CultureInfo.InvariantCulture)}s";
    }

    private static CodeEditor CreateMarkdownEditor(string text)
        => CodeEditorFactory.Create(
            text,
            new CodeEditorFactoryOptions
            {
                FileName = PromptEditorFileName,
                LanguageId = PromptEditorLanguageId,
                MinHeight = 10,
            });

    private static string GetEditorText(CodeEditor editor)
    {
        return CodeEditorFactory.GetText(editor);
    }

    private void SetEditorText(string text)
    {
        _contentEditor.TextDocument = new TextDocument(text);
        _contentEditor.SyntaxHighlighter = CodeEditorFactory.CreateSyntaxHighlighter(PromptEditorFileName, PromptEditorLanguageId);
    }

    private sealed record ReminderRow(AltaReminderDescriptor Descriptor);
}
