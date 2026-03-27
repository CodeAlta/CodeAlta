namespace CodeAlta.Models;

internal sealed class PendingSteerPrompt
{
    public PendingSteerPrompt(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        Id = Guid.CreateVersion7().ToString();
        Text = text.Trim();
    }

    public string Id { get; }

    public string Text { get; }
}
