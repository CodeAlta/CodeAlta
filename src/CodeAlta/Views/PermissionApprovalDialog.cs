using CodeAlta.Agent;
using CodeAlta.Presentation.Formatting;
using CodeAlta.Presentation.Styling;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Commands;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Extensions.Markdown;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Input;
using XenoAtom.Terminal.UI.Styling;

namespace CodeAlta.Views;

internal sealed class PermissionApprovalDialog
{
    private readonly AgentPermissionRequest _request;
    private readonly TaskCompletionSource<AgentPermissionDecision> _tcs;
    private readonly Func<Rectangle?> _getBounds;
    private readonly Func<Visual?> _getFocusTarget;
    private readonly Dialog _dialog;

    public PermissionApprovalDialog(
        AgentPermissionRequest request,
        TaskCompletionSource<AgentPermissionDecision> tcs,
        Func<Rectangle?> getBounds,
        Func<Visual?> getFocusTarget)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(tcs);
        ArgumentNullException.ThrowIfNull(getBounds);
        ArgumentNullException.ThrowIfNull(getFocusTarget);

        _request = request;
        _tcs = tcs;
        _getBounds = getBounds;
        _getFocusTarget = getFocusTarget;

        var markdown = ChatMarkdownFormatter.FormatChatPermissionRequestMarkdown(request);
        var details = new MarkdownControl(markdown)
        {
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Stretch,
            Options = MarkdownRenderOptions.Default with
            {
                WrapCodeBlocks = true,
                MaxCodeBlockHeight = 12,
            },
        };

        var allowOnceButton = new Button("Allow Once")
        {
            Tone = ControlTone.Primary,
        };
        allowOnceButton.Click(() => SetResult(AgentPermissionDecisionKind.AllowOnce));

        var allowSessionButton = new Button("Allow for Session")
        {
            Tone = ControlTone.Success,
        };
        allowSessionButton.Click(() => SetResult(AgentPermissionDecisionKind.AllowForSession));

        var denyButton = new Button("Deny")
        {
            Tone = ControlTone.Error,
        };
        denyButton.Click(() => SetResult(AgentPermissionDecisionKind.Deny));

        var buttonRow = new HStack(allowOnceButton, allowSessionButton, denyButton)
        {
            HorizontalAlignment = Align.End,
            Spacing = 2,
        };

        var headerText = request switch
        {
            AgentCommandPermissionRequest => "Shell Command",
            AgentFileChangePermissionRequest => "File Change",
            _ => "Permission Request",
        };

        var header = new Markup($"[bold]{TerminalIcons.MdShieldPlusOutline} {headerText}[/]");

        var content = new DockLayout()
            .Top(header)
            .Content(new ScrollViewer(details, focusable: false).Stretch())
            .Bottom(buttonRow)
            .HorizontalAlignment(Align.Stretch)
            .VerticalAlignment(Align.Stretch);

        _dialog = new Dialog()
            .Title("Permission Required")
            .IsModal(true)
            .Padding(1)
            .Content(content);
        ResponsiveDialogSize.Apply(_dialog, getBounds(), minWidth: 64, minHeight: 16, widthFactor: 0.40, heightFactor: 0.36);
        _dialog.AddCommand(new Command
        {
            Id = "CodeAlta.Permission.Cancel",
            LabelMarkup = "Cancel",
            DescriptionMarkup = "Cancel the permission request.",
            Gesture = new KeyGesture(TerminalKey.Escape),
            Importance = CommandImportance.Primary,
            Execute = _ => SetResult(AgentPermissionDecisionKind.Cancel),
        });
    }

    public void Show()
        => _dialog.Show();

    private void SetResult(AgentPermissionDecisionKind kind)
    {
        _tcs.TrySetResult(new AgentPermissionDecision(kind));
        _dialog.Close();
        if (_getFocusTarget() is { } focusTarget)
        {
            _dialog.App?.Focus(focusTarget);
        }
    }
}
