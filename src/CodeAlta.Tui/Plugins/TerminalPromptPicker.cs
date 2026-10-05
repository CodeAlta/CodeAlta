using CodeAlta.Catalog;
using CodeAlta.Plugins.Abstractions;
using CodeAlta.Plugins.Tui;
using CodeAlta.Tui.Presentation.Styling;
using CodeAlta.Tui.Views;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Input;
using XenoAtom.Terminal.UI.Styling;
using XenoAtom.Terminal.UI.Templating;
using Command = XenoAtom.Terminal.UI.Commands.Command;
using CommandImportance = XenoAtom.Terminal.UI.Commands.CommandImportance;

namespace CodeAlta.Tui.Plugins;

/// <summary>The part of a prompt that opens a plugin picker: its trigger character and the text typed after it.</summary>
/// <param name="Start">The index of the trigger character.</param>
/// <param name="End">The index after the token, which the chosen item replaces.</param>
/// <param name="Query">The text between the trigger character and the caret.</param>
internal readonly record struct PromptPickerToken(int Start, int End, string Query)
{
    private const int MaximumQueryLength = 256;

    /// <summary>
    /// Finds the token at the caret: the trigger character at the start of the prompt or after white space
    /// or an opening bracket, followed by the query typed so far.
    /// </summary>
    public static PromptPickerToken? Find(char trigger, string text, int caret)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (caret < 0 || caret > text.Length) return null;
        var at = caret == 0 ? -1 : text.LastIndexOf(trigger, caret - 1);
        if (at < 0 || at > 0 && !(char.IsWhiteSpace(text[at - 1]) || text[at - 1] is '(' or '[' or '{')) return null;
        var query = text[(at + 1)..caret];
        if (query.Length > MaximumQueryLength || query.Any(static value => char.IsWhiteSpace(value) || value is '(' or ')' or '[' or ']' or '{' or '}')
            || caret < text.Length && text[caret] == trigger) return null;
        var end = caret;
        while (end < text.Length && !(char.IsWhiteSpace(text[end]) || text[end] is ',' or ':' or ';' or '!' or '?' or '(' or ')' or '[' or ']' or '{' or '}')) end++;
        return new(at, end, query);
    }

    /// <summary>Replaces the token with the text of the chosen item.</summary>
    /// <returns>The new prompt and the caret after the inserted text.</returns>
    public (string Text, int Caret) Replace(string text, string insert)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(insert);
        return (string.Concat(text.AsSpan(0, Start), insert, text.AsSpan(End)), Start + insert.Length);
    }
}

/// <summary>
/// The picker of a plugin for one prompt editor of the terminal application. Typing the trigger character
/// at a word start opens a search dialog; the plugin supplies the items for the text typed in it. Enter
/// replaces the token with the text of the selected item, Escape leaves the prompt as typed.
/// </summary>
internal sealed class TerminalPromptPickerAttachment : IAsyncDisposable
{
    private readonly IPluginTerminalPromptEditorHost _host;
    private readonly PluginPromptPickerContribution _picker;
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<PluginPromptPickerItem>?>> _search;
    private TerminalPromptPickerDialog? _dialog;
    private PromptPickerToken _token;
    private string _tokenText = string.Empty;
    private string? _dismissedText;
    private CancellationTokenSource? _searching;
    private long _generation;
    private bool _disposed;

    /// <summary>Attaches the picker to a prompt editor.</summary>
    /// <param name="host">The prompt editor.</param>
    /// <param name="picker">The contribution.</param>
    /// <param name="search">Searches the items for a query; null means the search failed.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public TerminalPromptPickerAttachment(IPluginTerminalPromptEditorHost host, PluginPromptPickerContribution picker,
        Func<string, CancellationToken, Task<IReadOnlyList<PluginPromptPickerItem>?>> search)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(picker);
        ArgumentNullException.ThrowIfNull(search);
        _host = host;
        _picker = picker;
        _search = search;
        _host.EditorStateChanged += OnEditorStateChanged;
    }

    /// <summary>Gets a value indicating whether the picker dialog is shown.</summary>
    internal bool IsOpen => _dialog is { IsOpen: true };

    /// <summary>Gets the dialog while it is shown.</summary>
    internal TerminalPromptPickerDialog? Dialog => _dialog;

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _disposed = true;
        _host.EditorStateChanged -= OnEditorStateChanged;
        CancelSearch();
        Dispatch(() => { _dialog?.Close(); _dialog = null; });
        return ValueTask.CompletedTask;
    }

    private void OnEditorStateChanged(object? sender, EventArgs e)
    {
        if (_disposed || IsOpen) return;
        var text = _host.Text ?? string.Empty;
        // A picker closed without a choice stays closed until the prompt changes.
        if (_dismissedText is not null && !string.Equals(_dismissedText, text, StringComparison.Ordinal)) _dismissedText = null;
        if (_dismissedText is not null || PromptPickerToken.Find(_picker.Trigger, text, _host.CaretIndex) is not { } token) return;
        if (_host.Visual.App is not { } app) return;
        _token = token;
        _tokenText = text;
        _dialog = new TerminalPromptPickerDialog(_picker.Title, token.Query, StartSearch, Accept, Dismiss);
        _dialog.Show(app);
        StartSearch(token.Query);
    }

    private void StartSearch(string query)
    {
        CancelSearch();
        var cancellation = _searching = new CancellationTokenSource();
        var generation = ++_generation;
        _dialog?.SetLoading();
        _ = SearchAsync(query, generation, cancellation.Token);
    }

    private async Task SearchAsync(string query, long generation, CancellationToken cancellationToken)
    {
        IReadOnlyList<PluginPromptPickerItem>? items;
        try
        {
            items = await _search(query, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception)
        {
            items = null; // The plugin's own failure: the picker says the items could not be loaded.
        }

        Dispatch(() =>
        {
            if (generation == _generation && !cancellationToken.IsCancellationRequested) _dialog?.SetItems(items);
        });
    }

    private void Accept(PluginPromptPickerItem item)
    {
        CloseDialog();
        if (!string.Equals(_host.Text ?? string.Empty, _tokenText, StringComparison.Ordinal)) return;
        var (text, caret) = _token.Replace(_tokenText, item.InsertText);
        _dismissedText = text;
        _host.Text = text;
        _host.CaretIndex = caret;
        _host.FocusPromptEditor();
    }

    private void Dismiss()
    {
        CloseDialog();
        _dismissedText = _host.Text ?? string.Empty;
        _host.FocusPromptEditor();
    }

    private void CloseDialog()
    {
        CancelSearch();
        _generation++;
        _dialog?.Close();
        _dialog = null;
    }

    private void CancelSearch()
    {
        _searching?.Cancel();
        _searching?.Dispose();
        _searching = null;
    }

    private void Dispatch(Action action)
    {
        try
        {
            var dispatcher = _host.Visual.Dispatcher;
            if (dispatcher.CheckAccess()) action(); else dispatcher.Post(action);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
        {
            // The editor is no longer part of a running application.
        }
    }
}

/// <summary>The search dialog of a plugin picker: a query, the matching items, and what the keys do.</summary>
internal sealed class TerminalPromptPickerDialog
{
    private const int PageStep = 8;
    private readonly Dialog _dialog;
    private readonly TextBox _query;
    private readonly OptionList<PluginPromptPickerItem> _list;
    private readonly TextBlock _status;
    private readonly Action<string> _queryChanged;
    private readonly Action<PluginPromptPickerItem> _accept;
    private readonly Action _dismiss;
    private bool _open;

    /// <summary>Creates the dialog; nothing is shown until <see cref="Show"/>.</summary>
    public TerminalPromptPickerDialog(string title, string query, Action<string> queryChanged, Action<PluginPromptPickerItem> accept, Action dismiss)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(query);
        _queryChanged = queryChanged ?? throw new ArgumentNullException(nameof(queryChanged));
        _accept = accept ?? throw new ArgumentNullException(nameof(accept));
        _dismiss = dismiss ?? throw new ArgumentNullException(nameof(dismiss));

        _query = new TextBox().Placeholder(title).HorizontalAlignment(Align.Stretch);
        _query.Text = query;
        _query.KeyDown((_, e) => HandleKey(e));
        _query.TextDocument.Changed += (_, _) => { if (_open) _queryChanged(_query.Text ?? string.Empty); };

        _list = new OptionList<PluginPromptPickerItem>()
            .ActivateOnClick(true)
            .HorizontalAlignment(Align.Stretch)
            .VerticalAlignment(Align.Stretch)
            .ItemActivated((_, _) => AcceptSelected());
        _list.ItemTemplate = new DataTemplate<PluginPromptPickerItem>(
            static (DataTemplateValue<PluginPromptPickerItem> value, in DataTemplateContext _) => BuildRow(value.GetValue()), null);
        _list.KeyDown((_, e) => HandleKey(e));
        _status = new TextBlock(string.Empty) { Wrap = false, IsSelectable = false };

        var results = new ScrollViewer(_list, focusable: false)
            .HorizontalScrollEnabled(false).VerticalScrollEnabled(true)
            .HorizontalAlignment(Align.Stretch).VerticalAlignment(Align.Stretch).MinHeight(5);
        var content = new DockLayout().Top(_query).Content(results).Bottom(_status)
            .HorizontalAlignment(Align.Stretch).VerticalAlignment(Align.Stretch);
        _dialog = new Dialog()
            .Title(title)
            .BottomRightText(new Markup($"[dim]{SR.T("Arrows select · Enter insert · Esc close")}[/]"))
            .IsModal(true)
            .IsDraggable(true)
            .Padding(1)
            .Content(content)
            .Style(DialogStyle.Rounded);
        _dialog.AddCommand(new Command
        {
            Id = "CodeAlta.PluginPromptPicker.Close",
            LabelMarkup = SR.T("Close"),
            DescriptionMarkup = SR.T("Close the picker."),
            Gesture = new KeyGesture(TerminalKey.Escape),
            Importance = CommandImportance.Primary,
            Execute = _ => _dismiss(),
        });
    }

    /// <summary>Gets a value indicating whether the dialog is shown.</summary>
    public bool IsOpen => _open;

    /// <summary>Gets the items shown.</summary>
    internal IReadOnlyList<PluginPromptPickerItem> Items => [.. _list.Items];

    /// <summary>Gets the selected item index, or -1.</summary>
    internal int SelectedIndex => _list.SelectedIndex;

    /// <summary>Gets the status line.</summary>
    internal string Status => _status.Text ?? string.Empty;

    /// <summary>Shows the dialog in the application, with the focus in the query.</summary>
    public void Show(TerminalApp app)
    {
        ArgumentNullException.ThrowIfNull(app);
        ResponsiveDialogSize.Apply(_dialog, app.Root.GetAbsoluteBounds(), minWidth: 60, minHeight: 16, widthFactor: 0.45, heightFactor: 0.50);
        _dialog.Show();
        _open = true;
        app.Focus(_query);
    }

    /// <summary>Closes the dialog.</summary>
    public void Close()
    {
        if (!_open) return;
        _open = false;
        _dialog.Close();
    }

    /// <summary>Says that the items are being searched.</summary>
    public void SetLoading() => _status.Text = SR.T("Loading…");

    /// <summary>Shows the items of a search; null means the search failed.</summary>
    public void SetItems(IReadOnlyList<PluginPromptPickerItem>? items)
    {
        _list.Items.Clear();
        foreach (var item in items ?? []) _list.Items.Add(item);
        _list.SelectedIndex = _list.Items.Count == 0 ? -1 : 0;
        _status.Text = items is null ? SR.T("The items could not be loaded.") : items.Count == 0 ? SR.T("No item matches.") : string.Empty;
    }

    /// <summary>Moves the selection, as the arrow keys do.</summary>
    internal bool Move(int delta)
    {
        if (_list.Items.Count == 0) return false;
        _list.SelectedIndex = Math.Clamp(Math.Max(_list.SelectedIndex, 0) + delta, 0, _list.Items.Count - 1);
        return true;
    }

    /// <summary>Inserts the selected item, as Enter does.</summary>
    internal bool AcceptSelected()
    {
        if (_list.SelectedIndex < 0 || _list.SelectedIndex >= _list.Items.Count) return false;
        _accept(_list.Items[_list.SelectedIndex]);
        return true;
    }

    private void HandleKey(KeyEventArgs e)
    {
        var handled = e.Key switch
        {
            TerminalKey.Up => Move(-1),
            TerminalKey.Down => Move(1),
            TerminalKey.PageUp => Move(-PageStep),
            TerminalKey.PageDown => Move(PageStep),
            TerminalKey.Enter => AcceptSelected() || true,
            _ => false,
        };
        if (handled) e.Handled = true;
    }

    private static Visual BuildRow(PluginPromptPickerItem item)
    {
        var label = new TextBlock(item.Label) { Wrap = false, IsSelectable = false };
        if (string.IsNullOrWhiteSpace(item.Description)) return new OptionListItem(label, null);
        TextBlock? description = null;
        description = new TextBlock(item.Description) { Wrap = false, IsSelectable = false }
            .Style(() => TextBlockStyle.Default with { Foreground = UiPalette.GetPromptPlaceholderColor(description!.GetTheme()) });
        return new OptionListItem(label, description);
    }
}
