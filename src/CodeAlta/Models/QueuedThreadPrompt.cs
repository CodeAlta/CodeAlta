namespace CodeAlta.Models;

internal sealed class QueuedThreadPrompt
{
    public QueuedThreadPrompt(string text, int remainingCount = 1)
    {
        Text = NormalizeText(text);
        RemainingCount = ValidateRemainingCount(remainingCount);
    }

    public string Id { get; } = Guid.NewGuid().ToString("N");

    public string Text { get; private set; }

    public int RemainingCount { get; private set; }

    public void UpdateText(string text)
        => Text = NormalizeText(text);

    public void UpdateRemainingCount(int remainingCount)
        => RemainingCount = ValidateRemainingCount(remainingCount);

    private static string NormalizeText(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        return text.Trim();
    }

    private static int ValidateRemainingCount(int remainingCount)
    {
        if (remainingCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(remainingCount), remainingCount, "Queued prompt count must be greater than zero.");
        }

        return remainingCount;
    }
}
