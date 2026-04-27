using CodeAlta.Presentation.Prompting;

namespace CodeAlta.Models;

internal sealed class QueuedThreadPrompt
{
    public QueuedThreadPrompt(string text, int remainingCount = 1)
        : this(PromptSubmission.TextOnly(text), remainingCount)
    {
    }

    public QueuedThreadPrompt(PromptSubmission submission, int remainingCount = 1)
    {
        ArgumentNullException.ThrowIfNull(submission);
        if (!submission.HasContent)
        {
            throw new ArgumentException("Queued prompt text or image attachments are required.", nameof(submission));
        }

        Submission = submission.Copy();
        RemainingCount = ValidateRemainingCount(remainingCount);
    }

    public string Id { get; } = Guid.NewGuid().ToString("N");

    public PromptSubmission Submission { get; private set; }

    public string Text => Submission.Text;

    public IReadOnlyList<PromptImageAttachment> Images => Submission.Images;

    public int RemainingCount { get; private set; }

    public void UpdateText(string text)
    {
        var updated = PromptSubmission.Create(text, Submission.Images);
        if (!updated.HasContent)
        {
            throw new ArgumentException("Queued prompt text or image attachments are required.", nameof(text));
        }

        Submission = updated;
    }

    public void UpdateRemainingCount(int remainingCount)
        => RemainingCount = ValidateRemainingCount(remainingCount);

    private static int ValidateRemainingCount(int remainingCount)
    {
        if (remainingCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(remainingCount), remainingCount, "Queued prompt count must be greater than zero.");
        }

        return remainingCount;
    }
}
