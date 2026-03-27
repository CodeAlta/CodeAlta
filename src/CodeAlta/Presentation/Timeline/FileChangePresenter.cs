using System.Text;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Models;
using CodeAlta.Presentation.Formatting;
using CodeAlta.Presentation.Styling;
using CodeAlta.Threading;
using CodeAlta.Views;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Commands;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Input;
using XenoAtom.Terminal.UI.Styling;

namespace CodeAlta.Presentation.Timeline;

internal sealed class FileChangePresenter
{
    private const int FileChangeDialogLogCapacity = 20000;

    private readonly DocumentFlow _flow;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly Func<bool> _isAutoScrollEnabled;
    private readonly Action<DocumentFlowItem> _appendTimelineItem;
    private readonly Func<Rectangle?> _getDialogBounds;
    private readonly Dictionary<string, PendingFileChangeEntry> _pendingEntries = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FileChangeGroupState> _groups = [];
    private bool _pendingHasAggregateDiff;
    private DateTimeOffset? _pendingFirstSeenAt;
    private DateTimeOffset? _pendingLastUpdatedAt;

    public FileChangePresenter(
        DocumentFlow flow,
        IUiDispatcher uiDispatcher,
        Func<bool> isAutoScrollEnabled,
        Action<DocumentFlowItem> appendTimelineItem,
        Func<Rectangle?> getDialogBounds)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(uiDispatcher);
        ArgumentNullException.ThrowIfNull(isAutoScrollEnabled);
        ArgumentNullException.ThrowIfNull(appendTimelineItem);
        ArgumentNullException.ThrowIfNull(getDialogBounds);

        _flow = flow;
        _uiDispatcher = uiDispatcher;
        _isAutoScrollEnabled = isAutoScrollEnabled;
        _appendTimelineItem = appendTimelineItem;
        _getDialogBounds = getDialogBounds;
    }

    public void ObserveActivity(AgentActivityEvent activity)
    {
        ArgumentNullException.ThrowIfNull(activity);

        if (activity.Kind != AgentActivityKind.FileChange ||
            activity.Phase is AgentActivityPhase.Failed or AgentActivityPhase.Canceled ||
            _pendingHasAggregateDiff)
        {
            return;
        }

        var timestamp = activity.Timestamp;
        if (TryReadCodexChanges(activity.Details, out var codexChanges))
        {
            MergeChanges(codexChanges, timestamp, replaceExisting: false);
            return;
        }

        if (TryExtractDiff(activity.Details, out var diff))
        {
            MergeChanges(ParseUnifiedDiff(diff!), timestamp, replaceExisting: false);
        }
    }

    public void ObserveSessionUpdate(AgentSessionUpdateEvent update)
    {
        ArgumentNullException.ThrowIfNull(update);

        switch (update.Kind)
        {
            case AgentSessionUpdateKind.DiffUpdated:
                if (TryExtractAggregatedDiff(update.Details, out var diff))
                {
                    _pendingHasAggregateDiff = true;
                    MergeChanges(ParseUnifiedDiff(diff!), update.Timestamp, replaceExisting: true);
                    return;
                }

                if (!_pendingHasAggregateDiff &&
                    TryReadWorkspaceFileChanged(update.Details, out var workspaceChange))
                {
                    MergeChanges([workspaceChange!], update.Timestamp, replaceExisting: false);
                }

                break;

            case AgentSessionUpdateKind.Idle:
            case AgentSessionUpdateKind.Shutdown:
                FinalizePending(update.Timestamp);
                break;
        }
    }

    public void Reset()
    {
        foreach (var group in _groups)
        {
            foreach (var entry in group.Files.Values)
            {
                CloseDialog(entry);
            }
        }

        _groups.Clear();
        _pendingEntries.Clear();
        _pendingHasAggregateDiff = false;
        _pendingFirstSeenAt = null;
        _pendingLastUpdatedAt = null;
    }

    private void MergeChanges(IReadOnlyList<ParsedFileChange> changes, DateTimeOffset timestamp, bool replaceExisting)
    {
        if (changes.Count == 0)
        {
            return;
        }

        if (replaceExisting)
        {
            _pendingEntries.Clear();
        }

        _pendingFirstSeenAt ??= timestamp;
        _pendingLastUpdatedAt = timestamp;

        foreach (var change in changes)
        {
            if (string.IsNullOrWhiteSpace(change.FilePath))
            {
                continue;
            }

            if (!_pendingEntries.TryGetValue(change.FilePath, out var entry))
            {
                entry = new PendingFileChangeEntry(change.FilePath) { FirstSeenAt = timestamp };
                _pendingEntries[change.FilePath] = entry;
            }

            entry.LastUpdatedAt = timestamp;
            entry.Operation = change.Operation != FileChangeOperation.Unknown ? change.Operation : entry.Operation;

            if (replaceExisting)
            {
                entry.Additions = change.Additions;
                entry.Deletions = change.Deletions;
                entry.DiffText = change.DiffText;
            }
            else
            {
                entry.Additions += change.Additions;
                entry.Deletions += change.Deletions;
                entry.DiffText = PreferLongerText(entry.DiffText, change.DiffText);
            }
        }
    }

    private void FinalizePending(DateTimeOffset timestamp)
    {
        if (_pendingEntries.Count == 0)
        {
            _pendingHasAggregateDiff = false;
            _pendingFirstSeenAt = null;
            _pendingLastUpdatedAt = null;
            return;
        }

        var group = CreateGroup(timestamp);
        foreach (var pending in _pendingEntries.Values.OrderBy(static x => x.FilePath, StringComparer.OrdinalIgnoreCase))
        {
            var entry = CreateEntry(group, pending);
            entry.Button.Click(() => OpenDialog(entry));
            group.Files[pending.FilePath] = entry;
            group.TotalAdditions += entry.Additions;
            group.TotalDeletions += entry.Deletions;
        }

        group.LastUpdatedAt = _pendingLastUpdatedAt ?? timestamp;
        _groups.Add(group);
        _appendTimelineItem(group.Item);
        UpdateGroupVisual(group);
        UiDispatch.Post(_uiDispatcher, () =>
        {
            foreach (var entry in group.Files.Values)
            {
                group.ItemsHost.Children.Add(entry.Button);
            }

            _flow.ScrollToTailIfEnabled(_isAutoScrollEnabled());
        });

        _pendingEntries.Clear();
        _pendingHasAggregateDiff = false;
        _pendingFirstSeenAt = null;
        _pendingLastUpdatedAt = null;
    }

    private FileChangeGroupState CreateGroup(DateTimeOffset timestamp)
    {
        var group = UiDispatch.Invoke(
            _uiDispatcher,
            static timestampValue =>
            {
                var headerText = new Markup($"[{UiPalette.MutedMarkup}]{NerdFont.CodEdit}[/] [bold]Modified Files[/]");
                var summaryText = new Markup("[dim]Waiting for file changes...[/]");
                var timestampText = new Markup(string.Empty);
                var itemsHost = new WrapHStack { Spacing = 1, RunSpacing = 0 };
                var card = new Group(headerText, itemsHost)
                    .TopRightText(summaryText)
                    .BottomRightText(timestampText)
                    .Style(UiPalette.GetToolCallGroupStyle())
                    .HorizontalAlignment(Align.Stretch)
                    .VerticalAlignment(Align.Start);
                var item = new DocumentFlowItem { Content = new FlowDocument().Add(card), Alignment = DocumentFlowAlignment.Stretch };
                return new FileChangeGroupState(item, itemsHost, headerText, summaryText, timestampText) { LastUpdatedAt = timestampValue };
            },
            timestamp);

        ChatTimelineVisualFactory.ApplyTimestamp(group.TimestampText, timestamp);
        return group;
    }

    private FileChangeEntryState CreateEntry(FileChangeGroupState group, PendingFileChangeEntry pending)
    {
        var entry = UiDispatch.Invoke(
            _uiDispatcher,
            static state =>
            {
                var fileNameText = new Markup(string.Empty) { Wrap = false, HorizontalAlignment = Align.Stretch };
                var directoryText = new Markup(string.Empty) { Wrap = false, HorizontalAlignment = Align.Stretch };
                var countsText = new Markup(string.Empty) { Wrap = false, HorizontalAlignment = Align.End };
                var summaryLayout = new Grid
                    {
                        HorizontalAlignment = Align.Stretch,
                        VerticalAlignment = Align.Start,
                    }
                    .Rows(
                        new RowDefinition { Height = GridLength.Auto },
                        new RowDefinition { Height = GridLength.Auto })
                    .Columns(
                        new ColumnDefinition { Width = GridLength.Star(1) },
                        new ColumnDefinition { Width = GridLength.Auto });
                summaryLayout.Cell(fileNameText, 0, 0);
                summaryLayout.Cell(directoryText, 1, 0);
                summaryLayout.Cell(countsText, 1, 1);

                var button = new Button(summaryLayout)
                {
                    MinWidth = 18,
                    MaxWidth = 30,
                    HorizontalAlignment = Align.Start,
                    VerticalAlignment = Align.Start,
                };
                button.SetStyle(ButtonStyle.Key, UiPalette.GetToolChipButtonStyle(ToolCallDisplayStatus.Completed));
                return new FileChangeEntryState(state.pending.FilePath, button, fileNameText, directoryText, countsText)
                {
                    Group = state.group,
                    Operation = state.pending.Operation,
                    Additions = state.pending.Additions,
                    Deletions = state.pending.Deletions,
                    DiffText = state.pending.DiffText,
                    FirstSeenAt = state.pending.FirstSeenAt,
                    LastUpdatedAt = state.pending.LastUpdatedAt,
                };
            },
            (group, pending));

        UpdateEntryVisual(entry);
        return entry;
    }

    private void UpdateEntryVisual(FileChangeEntryState entry)
    {
        UiDispatch.Post(_uiDispatcher, () =>
        {
            entry.FileNameText.Text = FileChangeSummaryFormatter.BuildFileNameMarkup(entry);
            entry.DirectoryText.Text = FileChangeSummaryFormatter.BuildDirectoryMarkup(entry);
            entry.CountsText.Text = FileChangeSummaryFormatter.BuildCountsMarkup(entry);
            entry.Button.Tone = ControlTone.Default;
            entry.Button.SetStyle(ButtonStyle.Key, UiPalette.GetToolChipButtonStyle(ToolCallDisplayStatus.Completed));
        });
    }

    private void UpdateGroupVisual(FileChangeGroupState group)
    {
        UiDispatch.Post(_uiDispatcher, () =>
        {
            group.SummaryText.Text = FileChangeSummaryFormatter.BuildGroupSummaryMarkup(group);
            ChatTimelineVisualFactory.ApplyTimestamp(group.TimestampText, group.LastUpdatedAt);
        });
    }

    private void OpenDialog(FileChangeEntryState entry)
    {
        UiDispatch.Post(_uiDispatcher, () =>
        {
            if (entry.DetailDialog is { App: not null })
            {
                return;
            }

            var metadata = new MarkdownControl(string.Empty)
            {
                HorizontalAlignment = Align.Stretch,
                VerticalAlignment = Align.Start,
                Options = XenoAtom.Terminal.UI.Extensions.Markdown.MarkdownRenderOptions.Default with
                {
                    WrapCodeBlocks = true,
                    MaxCodeBlockHeight = 5,
                },
            };
            var wrapText = new State<bool>(false);
            var log = new LogControl
            {
                MaxCapacity = FileChangeDialogLogCapacity,
                HorizontalAlignment = Align.Stretch,
                VerticalAlignment = Align.Stretch,
            }.WrapText(wrapText);
            var statsText = new Markup(string.Empty);
            var detailsGroup = new Group("Details", metadata).Padding(new Thickness(1, 0, 1, 0)).HorizontalAlignment(Align.Stretch).VerticalAlignment(Align.Start);
            var outputGroup = new Group("Diff", log).TopRightText(new CheckBox("Wrap").IsChecked(wrapText)).Padding(0).HorizontalAlignment(Align.Stretch).VerticalAlignment(Align.Stretch);
            var bounds = _getDialogBounds() ?? entry.Button.GetAbsoluteBounds();
            var closeButton = new Button(new TextBlock($"{NerdFont.MdClose} Close")) { HorizontalAlignment = Align.End, VerticalAlignment = Align.Start, Tone = ControlTone.Error };
            closeButton.Click(() => CloseDialog(entry));
            var dialog = new Dialog()
                .Title(entry.FilePath)
                .TopRightText(closeButton)
                .BottomLeftText(statsText)
                .BottomRightText(new Markup("[dim]Ctrl+F Search[/]"))
                .IsModal(true)
                .Padding(1)
                .Content(new Grid()
                    .Rows(new RowDefinition { Height = GridLength.Auto }, new RowDefinition { Height = GridLength.Star(1) })
                    .Columns(new ColumnDefinition { Width = GridLength.Star(1) })
                    .Cell(detailsGroup, 0, 0)
                    .Cell(outputGroup, 1, 0));
            ResponsiveDialogSize.Apply(dialog, bounds, minWidth: 60, minHeight: 18, widthFactor: 0.85, heightFactor: 0.85);
            dialog.AddCommand(new Command { Id = "FileChangeDialog.Close", LabelMarkup = "Close", DescriptionMarkup = "Close file change details.", Gesture = new KeyGesture(TerminalKey.Escape), Importance = CommandImportance.Primary, Execute = _ => CloseDialog(entry) });
            dialog.KeyDown((_, e) => { if (e.Key == TerminalKey.Escape) { CloseDialog(entry); e.Handled = true; } });
            entry.DetailDialog = dialog;
            entry.DetailMetadata = metadata;
            entry.DetailLog = log;
            entry.DetailStatsText = statsText;
            UpdateDialogVisual(entry);
            dialog.Show();
        });
    }

    private void UpdateDialogVisual(FileChangeEntryState entry)
    {
        if (entry.DetailDialog is null || entry.DetailMetadata is null || entry.DetailLog is null || entry.DetailStatsText is null)
        {
            return;
        }

        UiDispatch.Post(_uiDispatcher, () =>
        {
            entry.DetailMetadata.Markdown = FileChangeSummaryFormatter.BuildDetailMarkdown(entry);
            entry.DetailStatsText.Text = FileChangeSummaryFormatter.BuildStatsMarkup(entry);
            entry.DetailLog.Clear();
            if (string.IsNullOrWhiteSpace(entry.DiffText))
            {
                entry.DetailLog.AppendMarkupLine($"[{UiPalette.MutedMarkup}]Diff unavailable.[/]");
            }
            else
            {
                foreach (var line in SplitLines(entry.DiffText!))
                {
                    entry.DetailLog.AppendMarkupLine(FileChangeSummaryFormatter.GetDiffLineMarkup(line));
                }
            }

            entry.DetailLog.ScrollToTail();
        });
    }

    private void CloseDialog(FileChangeEntryState entry)
    {
        var dialog = entry.DetailDialog;
        entry.DetailDialog = null;
        entry.DetailMetadata = null;
        entry.DetailLog = null;
        entry.DetailStatsText = null;
        if (dialog is not null)
        {
            UiDispatch.Post(_uiDispatcher, dialog.Close);
        }
    }

    private static bool TryReadCodexChanges(JsonElement? details, out IReadOnlyList<ParsedFileChange> changes)
    {
        changes = [];
        if (details is not { ValueKind: JsonValueKind.Object } detailObject ||
            !detailObject.TryGetProperty("changes", out var changeArray) ||
            changeArray.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var parsed = new List<ParsedFileChange>();
        foreach (var change in changeArray.EnumerateArray())
        {
            if (!TryGetString(change, "path", out var path))
            {
                continue;
            }

            TryGetString(change, "diff", out var diff);
            var operation = FileChangeOperation.Unknown;
            if (change.TryGetProperty("kind", out var kind) &&
                kind.ValueKind == JsonValueKind.Object &&
                TryGetString(kind, "type", out var type))
            {
                operation = type switch
                {
                    "add" => FileChangeOperation.Created,
                    "delete" => FileChangeOperation.Deleted,
                    "update" => FileChangeOperation.Modified,
                    _ => FileChangeOperation.Unknown,
                };
            }

            var additions = 0;
            var deletions = 0;
            if (!string.IsNullOrWhiteSpace(diff))
            {
                var parsedDiff = ParseUnifiedDiff(diff!).FirstOrDefault();
                if (parsedDiff is not null)
                {
                    additions = parsedDiff.Additions;
                    deletions = parsedDiff.Deletions;
                    if (operation == FileChangeOperation.Unknown)
                    {
                        operation = parsedDiff.Operation;
                    }
                }
            }

            parsed.Add(new ParsedFileChange(path!, additions, deletions, diff, operation));
        }

        changes = parsed;
        return parsed.Count > 0;
    }

    private static bool TryExtractAggregatedDiff(JsonElement? details, out string? diff)
    {
        diff = null;
        return details is { ValueKind: JsonValueKind.Object } detailObject &&
               TryGetString(detailObject, "diff", out diff);
    }

    private static bool TryReadWorkspaceFileChanged(JsonElement? details, out ParsedFileChange? change)
    {
        change = null;
        if (details is not { ValueKind: JsonValueKind.Object } detailObject ||
            !TryGetString(detailObject, "path", out var path))
        {
            return false;
        }

        var operation = FileChangeOperation.Unknown;
        if (TryGetString(detailObject, "operation", out var operationText))
        {
            operation = operationText switch
            {
                "create" => FileChangeOperation.Created,
                "update" => FileChangeOperation.Modified,
                _ => FileChangeOperation.Unknown,
            };
        }

        change = new ParsedFileChange(path!, 0, 0, DiffText: null, operation);
        return true;
    }

    private static bool TryExtractDiff(JsonElement? details, out string? diff)
    {
        diff = null;
        if (details is not { ValueKind: JsonValueKind.Object } detailObject)
        {
            return false;
        }

        if (TryResolveNestedString(detailObject, out diff, "result", "detailedContent") ||
            TryResolveNestedString(detailObject, out diff, "result", "content") ||
            TryResolveNestedString(detailObject, out diff, "output", "body"))
        {
            return LooksLikeDiff(diff);
        }

        return false;
    }

    private static bool TryResolveNestedString(JsonElement root, out string? value, params string[] path)
    {
        value = null;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var current = root;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
            {
                return false;
            }
        }

        value = current.ValueKind switch
        {
            JsonValueKind.String => current.GetString(),
            JsonValueKind.Object or JsonValueKind.Array => current.GetRawText(),
            _ => current.ToString(),
        };
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string? value)
    {
        value = null;
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString();
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool LooksLikeDiff(string? text)
        => !string.IsNullOrWhiteSpace(text) &&
           (text.Contains("diff --git", StringComparison.Ordinal) ||
            text.Contains("--- ", StringComparison.Ordinal) ||
            text.Contains("+++ ", StringComparison.Ordinal));

    private static IReadOnlyList<ParsedFileChange> ParseUnifiedDiff(string diff)
    {
        var lines = SplitLines(diff);
        var files = new List<ParsedFileChange>();
        DiffAccumulator? current = null;

        foreach (var line in lines)
        {
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                FinalizeCurrent(files, ref current);
                current = new DiffAccumulator();
                current.AppendLine(line);
                current.Path = ExtractPathFromDiffHeader(line);
                continue;
            }

            if (current is null)
            {
                if (line.StartsWith("--- ", StringComparison.Ordinal) || line.StartsWith("+++ ", StringComparison.Ordinal))
                {
                    current = new DiffAccumulator();
                }
                else
                {
                    continue;
                }
            }

            current.AppendLine(line);

            if (line.StartsWith("new file mode", StringComparison.Ordinal))
            {
                current.Operation = FileChangeOperation.Created;
            }
            else if (line.StartsWith("deleted file mode", StringComparison.Ordinal))
            {
                current.Operation = FileChangeOperation.Deleted;
            }
            else if (line.StartsWith("--- ", StringComparison.Ordinal))
            {
                current.OldPath = NormalizeDiffPath(line[4..].Trim());
            }
            else if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                current.NewPath = NormalizeDiffPath(line[4..].Trim());
                current.Path ??= current.NewPath ?? current.OldPath;
            }
            else if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                current.InHunk = true;
            }
            else if (current.InHunk && line.StartsWith('+') && !line.StartsWith("+++", StringComparison.Ordinal))
            {
                current.Additions++;
            }
            else if (current.InHunk && line.StartsWith('-') && !line.StartsWith("---", StringComparison.Ordinal))
            {
                current.Deletions++;
            }
        }

        FinalizeCurrent(files, ref current);
        return files;
    }

    private static void FinalizeCurrent(List<ParsedFileChange> files, ref DiffAccumulator? current)
    {
        if (current is null)
        {
            return;
        }

        var path = current.Path ?? current.NewPath ?? current.OldPath;
        if (!string.IsNullOrWhiteSpace(path))
        {
            var operation = current.Operation;
            if (operation == FileChangeOperation.Unknown)
            {
                operation = current.NewPath == "/dev/null"
                    ? FileChangeOperation.Deleted
                    : current.OldPath == "/dev/null"
                        ? FileChangeOperation.Created
                        : FileChangeOperation.Modified;
            }

            files.Add(new ParsedFileChange(path!, current.Additions, current.Deletions, current.Builder.ToString(), operation));
        }

        current = null;
    }

    private static string? ExtractPathFromDiffHeader(string line)
    {
        const string marker = " b/";
        var markerIndex = line.IndexOf(marker, StringComparison.Ordinal);
        return markerIndex < 0 ? null : NormalizeDiffPath(line[(markerIndex + 1)..].Trim());
    }

    private static string NormalizeDiffPath(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith("a/", StringComparison.Ordinal) || trimmed.StartsWith("b/", StringComparison.Ordinal))
        {
            trimmed = trimmed[2..];
        }

        return trimmed.Replace('\\', '/');
    }

    private static IReadOnlyList<string> SplitLines(string text)
        => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

    private static string? PreferLongerText(string? existing, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return existing;
        }

        return string.IsNullOrWhiteSpace(existing) || candidate!.Length >= existing.Length
            ? candidate
            : existing;
    }

    private sealed class PendingFileChangeEntry(string filePath)
    {
        public string FilePath { get; } = filePath.Replace('\\', '/');

        public FileChangeOperation Operation { get; set; }

        public int Additions { get; set; }

        public int Deletions { get; set; }

        public string? DiffText { get; set; }

        public DateTimeOffset FirstSeenAt { get; set; }

        public DateTimeOffset LastUpdatedAt { get; set; }
    }

    private sealed record ParsedFileChange(
        string FilePath,
        int Additions,
        int Deletions,
        string? DiffText,
        FileChangeOperation Operation);

    private sealed class DiffAccumulator
    {
        public StringBuilder Builder { get; } = new();

        public string? Path { get; set; }

        public string? OldPath { get; set; }

        public string? NewPath { get; set; }

        public FileChangeOperation Operation { get; set; }

        public int Additions { get; set; }

        public int Deletions { get; set; }

        public bool InHunk { get; set; }

        public void AppendLine(string line)
        {
            if (Builder.Length > 0)
            {
                Builder.AppendLine();
            }

            Builder.Append(line);
        }
    }
}
