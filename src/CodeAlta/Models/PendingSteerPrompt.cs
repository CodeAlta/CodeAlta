using CodeAlta.Presentation.Prompting;

namespace CodeAlta.Models;

internal sealed class PendingSteerPrompt
{
    public PendingSteerPrompt(string text)
        : this(PromptSubmission.TextOnly(text))
    {
    }

    public PendingSteerPrompt(PromptSubmission submission)
    {
        ArgumentNullException.ThrowIfNull(submission);
        if (!submission.HasContent)
        {
            throw new ArgumentException("Pending steer prompt text or image attachments are required.", nameof(submission));
        }

        Id = Guid.CreateVersion7().ToString();
        Submission = submission.Copy();
    }

    public string Id { get; }

    public PromptSubmission Submission { get; }

    public string Text => Submission.Text;

    public IReadOnlyList<PromptImageAttachment> Images => Submission.Images;
}
