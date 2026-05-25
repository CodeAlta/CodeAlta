namespace CodeAlta.Agent.OpenAI;

internal sealed class OpenAIRequestHeaderContext
{
    private readonly AsyncLocal<IReadOnlyDictionary<string, string>?> _current = new();

    public IReadOnlyDictionary<string, string>? Current => _current.Value;

    public IDisposable Push(IReadOnlyDictionary<string, string>? headers)
    {
        var prior = _current.Value;
        _current.Value = headers;
        return new Scope(this, prior);
    }

    private sealed class Scope(OpenAIRequestHeaderContext owner, IReadOnlyDictionary<string, string>? prior) : IDisposable
    {
        public void Dispose() => owner._current.Value = prior;
    }
}
