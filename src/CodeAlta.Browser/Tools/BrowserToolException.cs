namespace CodeAlta.Browser.Tools;

/// <summary>
/// A tool that cannot do what it was asked: the message says why and, where known, what to do instead, since the
/// caller is often a model that has only this text to go by.
/// </summary>
/// <param name="code">A short code, such as <c>stale</c>, <c>dialog-open</c> or <c>not-allowed</c>.</param>
/// <param name="message">The message for the caller.</param>
/// <param name="inner">The failure that caused it, or null.</param>
internal sealed class BrowserToolException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>Gets the code.</summary>
    public string Code { get; } = code;
}
