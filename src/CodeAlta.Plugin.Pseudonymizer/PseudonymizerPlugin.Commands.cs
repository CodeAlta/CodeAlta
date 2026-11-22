using System.Text;
using CodeAlta.Agent.Runtime;
using CodeAlta.Plugins.Abstractions;
using CodeAlta.Pseudonymization;

namespace CodeAlta.Plugin.Pseudonymizer;

public sealed partial class PseudonymizerPlugin
{
    // The commands that show the user what the pseudonymization does: on a text of theirs, without sending anything, and
    // in a session, with the pseudonyms it sent.
    private IEnumerable<PluginCommandContribution> ShowingCommands()
    {
        yield return new PluginCommandContribution
        {
            Name = "pseudonymizer-try",
            Label = "Pseudonymizer: try a text",
            Description = "Shows what a text would become before it reaches a provider, on this machine: nothing is sent.",
            SearchText = "pseudonymizer try test preview text personal data",
            Availability = PluginCommandAvailability.InteractiveUi,
            Handler = TryAsync,
        };
        yield return new PluginCommandContribution
        {
            Name = "pseudonymizer-masked",
            Label = "Pseudonymizer: masked data of this session",
            Description = "Lists the pseudonyms the session sent and the values they stand for, which stay on this machine.",
            SearchText = "pseudonymizer masked data pseudonyms session table",
            Availability = PluginCommandAvailability.SessionSelected,
            Handler = ShowMaskedAsync,
        };
    }

    private async ValueTask<PluginCommandResult> TryAsync(PluginCommandContext context, CancellationToken cancellationToken)
    {
        var text = await context.Ui.EditTextAsync("Pseudonymizer: paste a text to try", string.Empty, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text)) return PluginCommandResult.Cancelled;
        var (recognizers, problems) = await TryRecognizersAsync(context.ProjectId, cancellationToken).ConfigureAwait(false);
        var table = new PseudonymTable();
        var boundary = new PseudonymizingBoundary(new PiiAnalyzer(recognizers), table, new PseudonymCache(), Options(context.ProjectId));
        string hidden;
        try
        {
            hidden = await boundary.PseudonymizeAsync(text, cancellationToken).ConfigureAwait(false);
        }
        catch (AgentProviderBoundaryException exception)
        {
            await context.Ui.NotifyAsync("Pseudonymizer: " + exception.Message, CancellationToken.None).ConfigureAwait(false);
            return PluginCommandResult.Handled;
        }

        var html = new StringBuilder();
        html.Append("<p>Nothing was sent: this is what a provider would read of the text, and the pseudonyms that stay on this machine.</p>");
        foreach (var problem in problems) html.Append($"<p class=\"{PluginHtml.WarningClass}\">{PluginHtml.Encode(problem)}</p>");
        html.Append("<h3>What the provider would read</h3>").Append(PluginHtml.Code(hidden, "text"));
        html.Append("<h3>Your text</h3>").Append(PluginHtml.Code(text, "text"));
        AppendTable(html, table.Entries, counts: false);
        await context.Ui.ShowDialogAsync(new PluginDialogRequest { Title = "Pseudonymizer: try a text", Html = html.ToString() }, cancellationToken).ConfigureAwait(false);
        return PluginCommandResult.Handled;
    }

    private async ValueTask<PluginCommandResult> ShowMaskedAsync(PluginCommandContext context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(context.SessionId)) return PluginCommandResult.NotHandled;
        IReadOnlyList<PseudonymEntry> entries;
        try
        {
            entries = EntriesOf(context.SessionId) ?? SessionPseudonyms.Load(SessionPath(context.SessionId), _protector(SessionsDirectory)).Table.Entries;
        }
        catch (AgentProviderBoundaryException exception)
        {
            await context.Ui.NotifyAsync("Pseudonymizer: " + exception.Message, CancellationToken.None).ConfigureAwait(false);
            return PluginCommandResult.Handled;
        }

        var html = new StringBuilder();
        html.Append(entries.Count == 0
            ? "<p>This session sent no pseudonym: it is not confidential, or nothing it sent held personal data.</p>"
            : "<p>The provider read these pseudonyms; the values stay on this machine, encrypted.</p>");
        AppendTable(html, entries, counts: true);
        await context.Ui.ShowDialogAsync(new PluginDialogRequest { Title = "Pseudonymizer: masked data of this session", Html = html.ToString() }, cancellationToken).ConfigureAwait(false);
        return PluginCommandResult.Handled;
    }

    // The recognizers of a try: those of the language of the project, or the identifiers alone with a note when its
    // model cannot run. A model that only waits for its ONNX Runtime gets it first, as a request does.
    private async ValueTask<(List<PiiRecognizer> Recognizers, List<string> Problems)> TryRecognizersAsync(string? projectId, CancellationToken cancellationToken)
    {
        var language = LanguageOf(projectId);
        var recognizers = RecognizersOf(language, out var failure);
        if (IsRuntimeMissing(failure))
        {
            await _models!.InstallRuntimeAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            recognizers = RecognizersOf(language, out failure);
        }

        List<string> problems = failure is not null
            ? [$"The model that finds names cannot run ({failure.Message}): names are not found in this try, and a session of a pseudonymized project would send nothing."]
            : language == PseudonymizerLanguages.Identifiers ? ["This project hides identifiers only: names are not looked for."] : [];
        return (recognizers, problems);
    }

    private static void AppendTable(StringBuilder html, IReadOnlyList<PseudonymEntry> entries, bool counts)
    {
        if (entries.Count == 0) return;
        html.Append("<h3>Pseudonyms</h3><table><thead><tr><th>Pseudonym</th><th>Kind</th><th>Value</th>");
        if (counts) html.Append("<th>Times sent</th>");
        html.Append("</tr></thead><tbody>");
        foreach (var entry in entries)
        {
            html.Append("<tr><td><code>").Append(PluginHtml.Encode(entry.Token)).Append("</code></td><td>").Append(PluginHtml.Encode(entry.Entity))
                .Append("</td><td>").Append(PluginHtml.Encode(entry.Value)).Append("</td>");
            if (counts) html.Append("<td>").Append(entry.Count).Append("</td>");
            html.Append("</tr>");
        }

        html.Append("</tbody></table>");
    }
}
