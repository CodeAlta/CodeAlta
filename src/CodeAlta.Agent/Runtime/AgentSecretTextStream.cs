namespace CodeAlta.Agent.Runtime;

/// <summary>
/// The masking of one text that comes in parts, made by <see cref="AgentSecretMasker.CreateStream"/>: a secret split
/// between two parts is masked as a secret held by one. What could still begin a secret is held back, never as much
/// as the longest secret, until the next part or the end of the text tells. It is used by one writer at a time.
/// </summary>
public sealed class AgentSecretTextStream
{
    private readonly AgentSecretMasker _masker;
    private string _held = string.Empty;

    internal AgentSecretTextStream(AgentSecretMasker masker) => _masker = masker;

    /// <summary>Gets the end of the text held back: it could begin a secret.</summary>
    public int HeldLength => _held.Length;

    /// <summary>Takes the next part of the text.</summary>
    /// <param name="part">The part.</param>
    /// <returns>What can be shown now, masked; empty when all of it is held back.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="part"/> is <see langword="null"/>.</exception>
    public string Write(string part)
    {
        ArgumentNullException.ThrowIfNull(part);
        var text = _held.Length == 0 ? part : string.Concat(_held, part);
        return _masker.MaskPart(text, last: false, out _held);
    }

    /// <summary>Ends the text: what was held back is given, masked.</summary>
    /// <returns>The end of the text; empty when nothing was held back.</returns>
    public string Flush()
    {
        var text = _held;
        _held = string.Empty;
        return _masker.MaskPart(text, last: true, out _);
    }
}
