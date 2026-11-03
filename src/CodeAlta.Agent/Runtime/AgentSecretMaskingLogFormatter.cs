using XenoAtom.Logging;

namespace CodeAlta.Agent.Runtime;

/// <summary>
/// Formats the lines of a log as another formatter does, with the secrets of the machine replaced by
/// <see cref="AgentSecretMasker.Mask"/>: a value given to a provider, an MCP server or a tool never lands in a log file.
/// </summary>
/// <param name="Inner">The formatter of the lines.</param>
/// <param name="Masker">What masks the secrets.</param>
public sealed record AgentSecretMaskingLogFormatter(LogFormatter Inner, AgentSecretMasker Masker) : LogFormatter
{
    /// <inheritdoc />
    public override bool TryFormat(LogMessage logMessage, Span<char> destination, out int charsWritten, ref LogMessageFormatSegments segments)
    {
        if (!Inner.TryFormat(logMessage, destination, out charsWritten, ref segments)) return false;
        charsWritten = Masker.MaskInPlace(destination[..charsWritten]);
        return true;
    }
}
