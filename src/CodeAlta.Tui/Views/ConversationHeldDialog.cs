using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Tui.Presentation.Styling;
using XenoAtom.Ansi;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Commands;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Input;
using XenoAtom.Terminal.UI.Styling;

namespace CodeAlta.Tui.Views;

/// <summary>
/// Asks what a run does when its Claude Code conversation is open in another process (VS Code, a terminal): wait until
/// it is free, continue as a copy, open it here anyway, or cancel the run (Escape). Only the choices the question offers
/// are shown. The answer is given once; the dialog closes with it, or when the question ends without it.
/// </summary>
internal sealed class ConversationHeldDialog : IDisposable
{
    private readonly Dialog _dialog;
    private readonly Action<AgentConversationHeldChoice> _answer;
    private readonly Func<Visual?> _getFocusTarget;
    private bool _disposed;

    public ConversationHeldDialog(
        SessionConversationHeldSnapshot question,
        Action<AgentConversationHeldChoice> answer,
        Func<Rectangle?> getBounds,
        Func<Visual?> getFocusTarget)
    {
        ArgumentNullException.ThrowIfNull(question);
        ArgumentNullException.ThrowIfNull(answer);
        ArgumentNullException.ThrowIfNull(getBounds);
        ArgumentNullException.ThrowIfNull(getFocusTarget);

        _answer = answer;
        _getFocusTarget = getFocusTarget;
        var origin = OriginText(question.Holder);
        var header = new Markup($"[bold]{TerminalIcons.CodWarning} {AnsiMarkup.Escape(SR.T("This Claude Code conversation is open in {0}", origin))}[/]");
        var lines = new List<Visual>();
        if (question.Holder.ProcessId is { } processId)
        {
            lines.Add(new TextBlock(SR.T("Process {0}", processId)));
        }

        if (question.Holder.WorkingDirectory is { Length: > 0 } folder && !string.IsNullOrWhiteSpace(folder))
        {
            lines.Add(new TextBlock(folder));
        }

        if (question.Choices.Contains(AgentConversationHeldChoice.OpenAnyway))
        {
            lines.Add(new TextBlock(SR.T("Open here anyway: both would write the same conversation.")));
        }

        var buttons = new List<Visual>();
        foreach (var choice in question.Choices)
        {
            if (choice == AgentConversationHeldChoice.Cancel)
            {
                continue;
            }

            var button = new Button(Label(choice))
            {
                Tone = choice switch
                {
                    AgentConversationHeldChoice.Wait => ControlTone.Primary,
                    AgentConversationHeldChoice.ContinueAsCopy => ControlTone.Success,
                    _ => ControlTone.Warning,
                },
            };
            button.Click(() => SetResult(choice));
            buttons.Add(button);
        }

        var buttonRow = new HStack([.. buttons])
        {
            HorizontalAlignment = Align.End,
            Spacing = 2,
        };

        var content = new DockLayout()
            .Top(header)
            .Content(new VStack([.. lines]) { Spacing = 0 }.Stretch())
            .Bottom(buttonRow)
            .HorizontalAlignment(Align.Stretch)
            .VerticalAlignment(Align.Stretch);

        _dialog = new Dialog()
            .Title(SR.T("Conversation Open Elsewhere"))
            .IsModal(true)
            .Padding(1)
            .Content(content);
        ResponsiveDialogSize.Apply(_dialog, getBounds(), minWidth: 76, minHeight: 12, widthFactor: 0.52, heightFactor: 0.3);
        _dialog.AddCommand(new Command
        {
            Id = "CodeAlta.ConversationHeld.Cancel",
            LabelMarkup = SR.T("Cancel"),
            DescriptionMarkup = SR.T("Cancel the run."),
            Gesture = new KeyGesture(TerminalKey.Escape),
            Importance = CommandImportance.Primary,
            Execute = _ => SetResult(AgentConversationHeldChoice.Cancel),
        });
    }

    public void Show()
        => _dialog.Show();

    /// <summary>The label of a choice, as its button says it.</summary>
    internal static string Label(AgentConversationHeldChoice choice) => choice switch
    {
        AgentConversationHeldChoice.Wait => SR.T("Wait Until It Is Free"),
        AgentConversationHeldChoice.ContinueAsCopy => SR.T("Continue as a Copy"),
        AgentConversationHeldChoice.OpenAnyway => SR.T("Open Here Anyway"),
        _ => SR.T("Cancel"),
    };

    /// <summary>Where the other process runs, in the language of the terminal when it is a known place.</summary>
    internal static string OriginText(AgentConversationHolder holder) => holder.Origin switch
    {
        AgentConversationHolderOrigin.VsCode => "VS Code",
        AgentConversationHolderOrigin.Terminal => SR.T("a terminal"),
        AgentConversationHolderOrigin.BackgroundSession => SR.T("a background session of Claude Code"),
        AgentConversationHolderOrigin.OtherApplication when holder.OriginText == "another application" => SR.T("another application"),
        _ => holder.OriginText,
    };

    private void SetResult(AgentConversationHeldChoice choice)
    {
        if (_disposed)
        {
            return;
        }

        Dispose();
        _answer(choice);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var app = _dialog.App;
        _dialog.Close();
        if (_getFocusTarget() is { } focusTarget)
        {
            app?.Focus(focusTarget);
        }
    }
}
