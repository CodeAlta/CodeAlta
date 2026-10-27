using System.Collections.Concurrent;
using System.Text.Json;
using CodeAlta.CodeMode;

namespace CodeAlta.CodeMode.Tests;

/// <summary>A call handler whose functions are delegates, for tests.</summary>
internal sealed class DelegateHandler : ICodeModeCallHandler
{
    private readonly Dictionary<string, Func<CodeModeCall, CancellationToken, Task<CodeModeCallResult>>> _functions = new(StringComparer.Ordinal);

    public ConcurrentQueue<CodeModeCall> Calls { get; } = new();

    public DelegateHandler On(string path, Func<CodeModeCall, CancellationToken, Task<CodeModeCallResult>> function)
    {
        _functions[path] = function;
        return this;
    }

    public DelegateHandler On(string path, Func<JsonElement, object?> function)
        => On(path, (call, _) =>
        {
            using var document = JsonDocument.Parse(call.ArgumentsJson);
            var value = function(document.RootElement.Clone());
            return Task.FromResult(CodeModeCallResult.Success(value is null ? null : JsonSerializer.Serialize(value, value.GetType(), TestJsonContext.Default)));
        });

    public async ValueTask<CodeModeCallResult> InvokeAsync(CodeModeCall call, CancellationToken cancellationToken)
    {
        Calls.Enqueue(call);
        return _functions.TryGetValue(call.Path, out var function)
            ? await function(call, cancellationToken)
            : CodeModeCallResult.Failure(new CodeModeCallError("ToolError", "no test function " + call.Path));
    }
}

/// <summary>An observer that records what it sees.</summary>
internal sealed class RecordingObserver : ICodeModeRunObserver
{
    public ConcurrentQueue<string> Events { get; } = new();

    public void CallStarted(CodeModeCall call) => Events.Enqueue("call " + call.Path);

    public void StepStarted(string stepId) => Events.Enqueue("step " + stepId);

    public void Output(CodeModeOutputEntry entry) => Events.Enqueue("output " + entry.Text);
}

[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
[System.Text.Json.Serialization.JsonSerializable(typeof(int))]
[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, object>))]
[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, string>))]
[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, int>))]
[System.Text.Json.Serialization.JsonSerializable(typeof(JsonElement))]
internal sealed partial class TestJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
