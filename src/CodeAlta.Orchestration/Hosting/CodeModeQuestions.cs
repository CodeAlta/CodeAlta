using CodeAlta.Catalog;
using CodeAlta.LiveTool;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Orchestration.Runtime.Scripts;

namespace CodeAlta.Orchestration.Hosting;

/// <summary>
/// The questions of scripts (<c>ask()</c>) as the host asks them: in the ask panel of the session that runs the script,
/// where the questions of <c>alta ask</c> wait, with the waiting mark of the session. The answer goes back to the script,
/// not to the session. The questions of one run come one at a time, with the approvals of its calls; its card says that it
/// waits. A session of an automation, and a host that asks nothing (<c>altacli</c>), have nobody to ask.
/// </summary>
/// <param name="host">The host.</param>
public sealed class CodeModeQuestions(CodeAltaHost host) : ICodeModeQuestions
{
    private readonly CodeAltaHost _host = host ?? throw new ArgumentNullException(nameof(host));

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    public async Task<CodeModeQuestionAnswer> AskAsync(CodeModeQuestionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_host.Commands.Asks.Enabled)
        {
            return Unavailable("Nobody can be asked here: ask() waits for an answer in a session of CodeAlta Desktop. Give the value as an argument of the script instead.");
        }

        var session = await _host.RuntimeService.TryGetResumableSessionAsync(request.SessionId, cancellationToken).ConfigureAwait(false);
        if (session?.CreatedBy?.Kind == AltaActorProvenance.AutomationKind)
        {
            return Unavailable("An automation runs with nobody to answer: ask() is not available in its scripts. Give the value as an argument of the script instead.");
        }

        if (_host.RuntimeService.Permissions.SessionReviewTarget?.Invoke(request.SessionId) is not { } target
            || _host.CodeModeRuns.Find(request.SessionId, request.RunId) is not { Running: true } run)
        {
            return Unavailable("The session of the script is not open: its question cannot be shown.");
        }

        var choices = request.Choices;
        var question = new AltaAskQuestion
        {
            Title = Shorten(run.Title ?? "Script", 120),
            Question = request.Question,
            Description = request.Default is { } fallback ? $"Leave it empty for: {Shorten(fallback, 200)}" : null,
            Choices = [.. choices.Select(static choice => new AltaAskChoice { Title = choice })],
            Freeform = choices.Count == 0 ? new AltaAskFreeform { Placeholder = request.Default is null ? null : Shorten(request.Default, 500) } : null,
        };
        var origin = new OwnedAskHandle(Guid.NewGuid(), target.RuntimeId, target.Attachment.Ordinal, target.ProviderId.Value, request.SessionId, request.RunId, "", 0);
        using var turn = await _host.CodeModeRuns.WaitForUserAsync(run, "question", request.StepId, Shorten(request.Question, 300), cancellationToken).ConfigureAwait(false);
        IReadOnlyList<AltaAskAnswer>? answers;
        try
        {
            answers = await _host.Commands.Asks.AskForScriptAsync(origin, new AltaAskRequest { Questions = [question] }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(CodeModeQuestionOutcome.Cancelled, null, "CodeAlta closed before the user answered.");
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            return Unavailable($"The question could not be asked: {exception.Message}");
        }

        if (answers is not [var answer, ..])
        {
            return new(CodeModeQuestionOutcome.Cancelled, null, "The user dismissed the question.");
        }

        var text = answer.SelectedChoiceIndexes is [var index, ..] && index < choices.Count ? choices[index]
            : !string.IsNullOrEmpty(answer.FreeformText) ? answer.FreeformText
            : request.Default ?? string.Empty;
        return new(CodeModeQuestionOutcome.Answered, text, null);
    }

    private static CodeModeQuestionAnswer Unavailable(string message) => new(CodeModeQuestionOutcome.Unavailable, null, message);

    private static string Shorten(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";
}
