using CodeAlta.Plugins.Abstractions;
using CodeAlta.Plugins.Tui;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;

// One plugin for both applications. Portable calls (context.Ui, PluginUi.*) work in both.
// Where the look matters, it gives two forms: an HTML fragment for the desktop application
// and XenoAtom.Terminal.UI controls for the terminal application.
[Plugin("desktop-and-terminal", DisplayName = "Notes", Description = "Keeps short notes and inserts them in the prompt.")]
public sealed class NotesPlugin : PluginBase
{
    private readonly List<string> _notes = ["Run the tests before the commit"];

    private bool IsDesktop => Context.Host.Frontend == PluginFrontends.Desktop;

    public override IEnumerable<PluginCommandContribution> GetCommands()
    {
        // Portable dialogs: each application shows its own.
        yield return Command.Shell("note-add", "Adds a note.", async (context, cancellationToken) =>
        {
            var text = await context.Ui.InputAsync("New note", cancellationToken: cancellationToken);
            if (string.IsNullOrWhiteSpace(text)) return PluginCommandResult.Cancelled;
            _notes.Add(text.Trim());
            return PluginCommandResult.Message($"{_notes.Count} notes.");
        }) with { Label = "Notes: add", KeyBinding = new PluginKeyBinding(new PluginKeyGesture(PluginKey.F9)) };

        // A dialog with its own content: a fragment with actions on the desktop, controls in the terminal.
        yield return Command.Shell("notes", "Shows the notes.", async (context, cancellationToken) =>
        {
            var close = new PluginDialogButton { Name = "close", Label = "Close", IsDefault = true, IsCancel = true };
            var request = IsDesktop
                ? PluginUi.HtmlDialog("Notes", ListHtml(), close) with
                {
                    // An element with data-alta-action calls this with the values of the named fields.
                    OnAction = (action, _) =>
                    {
                        if (action.Name == "remove" && int.TryParse(action.Value, out var index) && index >= 0 && index < _notes.Count) _notes.RemoveAt(index);
                        if (action.Name == "add" && action.Values.TryGetValue("text", out var text) && !string.IsNullOrWhiteSpace(text)) _notes.Add(text.Trim());
                        return ValueTask.FromResult(PluginDialogActionResult.Update(ListHtml()));
                    },
                }
                : PluginTui.CustomDialog("Notes", new VStack(_notes.Select(static note => (Visual)new TextBlock("• " + note)).ToArray())) with { Buttons = [close] };
            await context.Ui.ShowDialogAsync(request, cancellationToken);
            return PluginCommandResult.Handled;
        }) with { Label = "Notes: show" };
    }

    public override IEnumerable<PluginUiContribution> GetUiContributions()
    {
        // A status item; on the desktop a click runs the named command of this plugin.
        yield return new PluginStatusContribution
        {
            Region = PluginUiRegion.SessionStatus,
            Name = "notes-count",
            GetStatus = _ => new PluginStatusItem { Label = "Notes", Text = _notes.Count.ToString(), Command = "notes" },
        };

        // Content above the prompt: a native visual for the terminal, a fragment for the desktop, text for anything else.
        yield return PluginTui.Visual(PluginUiRegion.SessionFooter,
            _ => new Markup($"[dim]{_notes.Count} notes · /notes[/]"),
            _ => PluginRenderResult.FromHtml(
                $"""<span class="alta-row"><span class="alta-tag">{_notes.Count} notes</span> {PluginHtml.CommandButton("notes", "Show")}</span>""",
                $"{_notes.Count} notes"),
            "notes-footer");
    }

    // Typing ! in the prompt opens a picker in both applications; the chosen note replaces the token.
    public override IEnumerable<PluginPromptPickerContribution> GetPromptPickers()
    {
        yield return PluginUi.PromptPicker("notes", '!', "Notes", (context, _) =>
            ValueTask.FromResult<IReadOnlyList<PluginPromptPickerItem>>(
            [
                .. _notes.Where(note => note.Contains(context.Query, StringComparison.OrdinalIgnoreCase))
                    .Select(static note => new PluginPromptPickerItem { Label = note, InsertText = note + " " }),
            ]),
            "[!] to insert a note");
    }

    // Text of the plugin is encoded; markup is written by hand. Nothing in a fragment runs: the window acts on data-alta-* attributes.
    private string ListHtml()
    {
        var rows = string.Concat(_notes.Select(static (note, index) =>
            $"""<tr><td>{PluginHtml.Encode(note)}</td><td align="right"><button data-alta-action="remove" data-alta-value="{index}">Remove</button></td></tr>"""));
        return $"""
            <div class="alta-column">
              <table><tbody>{rows}</tbody></table>
              <div class="alta-row"><input name="text" placeholder="New note" data-alta-action="add"> {PluginHtml.ActionButton("add", "Add", primary: true)}</div>
            </div>
            """;
    }
}
