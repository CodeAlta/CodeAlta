using CodeAlta.Plugins.Abstractions;
using CodeAlta.Plugins.Tui;
using XenoAtom.Terminal.UI.Controls;

// A dialog that shows more than fields and buttons: a table, a diagram and source code. A plugin has no script in
// the window. It writes Markdown, and the window renders it with its own renderer, the one of the timeline.
[Plugin("report-dialog", DisplayName = "Report dialog", Description = "Shows a report with a table, a diagram and code.")]
public sealed class ReportDialogPlugin : PluginBase
{
    private const string Report = """
        | Step | Result | Time |
        |---|---|---|
        | Restore | ok | 1.2 s |
        | Build | ok | 4.8 s |
        | Test | **2 failed** | 31 s |
        """;

    // Mermaid text: a flowchart here; sequence diagrams, pie charts and the others work the same way.
    private const string Flow = """
        flowchart LR
          restore[Restore] --> build[Build] --> test[Test]
          test -->|2 failed| fix[Fix]
          fix --> build
        """;

    private const string FailingTest = """
        [TestMethod]
        public void Adds() => Assert.AreEqual(5, Calculator.Add(2, 2));
        """;

    public override IEnumerable<PluginCommandContribution> GetCommands()
    {
        yield return Command.Shell("report", "Shows the build report.", async (context, cancellationToken) =>
        {
            var close = new PluginDialogButton { Name = "close", Label = "Close", IsDefault = true, IsCancel = true };
            var request = Context.Host.Frontend == PluginFrontends.Desktop
                ? PluginUi.HtmlDialog("Build report", BuildHtml(), close)
                // The terminal shows no HTML: the same report as text.
                : PluginTui.CustomDialog("Build report", new TextBlock(Report + "\n\n" + FailingTest)) with { Buttons = [close] };
            await context.Ui.ShowDialogForResultAsync(request, cancellationToken);
            return PluginCommandResult.Handled;
        }) with { Label = "Report: show" };
    }

    // Each helper writes one block. Markdown, Code and Diagram encode their text: it is content, never markup.
    private static string BuildHtml() => $"""
        <div class="alta-column">
          {PluginHtml.Markdown(Report)}
          {PluginHtml.Diagram(Flow)}
          <p class="alta-muted">The first failing test:</p>
          {PluginHtml.Code(FailingTest, "csharp")}
        </div>
        """;
}
