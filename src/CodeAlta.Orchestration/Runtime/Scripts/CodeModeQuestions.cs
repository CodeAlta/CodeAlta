namespace CodeAlta.Orchestration.Runtime.Scripts;

/// <summary>A question a script asks the user with <c>ask(question, options)</c>.</summary>
/// <param name="SessionId">The session that runs the script.</param>
/// <param name="RunId">The run of the script.</param>
/// <param name="Question">The question.</param>
public sealed record CodeModeQuestionRequest(string SessionId, string RunId, string Question)
{
    /// <summary>Gets the answers the user chooses from; empty for a text the user writes.</summary>
    public IReadOnlyList<string> Choices { get; init; } = [];

    /// <summary>Gets the answer the user gives by leaving the question empty, or null.</summary>
    public string? Default { get; init; }

    /// <summary>Gets the step of the script that asks, or null.</summary>
    public string? StepId { get; init; }
}

/// <summary>How a question of a script ended.</summary>
public enum CodeModeQuestionOutcome
{
    /// <summary>The user answered.</summary>
    Answered,

    /// <summary>The user dismissed the question, or CodeAlta closed before an answer.</summary>
    Cancelled,

    /// <summary>Nobody can be asked: a run of an automation, a host without a window.</summary>
    Unavailable,
}

/// <summary>The end of a question of a script.</summary>
/// <param name="Outcome">How it ended.</param>
/// <param name="Answer">The answer of the user, when answered: the choice taken, the text written, or the default.</param>
/// <param name="Message">Why it was not answered, for the error the script receives.</param>
public sealed record CodeModeQuestionAnswer(CodeModeQuestionOutcome Outcome, string? Answer, string? Message);

/// <summary>Where the questions of scripts (<c>ask()</c>) reach the user: the place where the user answers the questions of sessions.</summary>
public interface ICodeModeQuestions
{
    /// <summary>
    /// Asks the user, and waits for the answer. The questions of one run come one at a time. Waiting is cancelled with
    /// the run: the question then leaves the place where it waits.
    /// </summary>
    /// <param name="request">The question.</param>
    /// <param name="cancellationToken">Withdraws the question: the run stopped.</param>
    /// <returns>How it ended.</returns>
    /// <exception cref="OperationCanceledException">The run stopped.</exception>
    Task<CodeModeQuestionAnswer> AskAsync(CodeModeQuestionRequest request, CancellationToken cancellationToken);
}
