using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Plugins.Abstractions;

// A tool the model can call. The schema says which arguments it takes; the handler returns text to the model.
[Plugin("agent-tool", DisplayName = "Word count", Description = "Gives sessions a tool that counts the lines and words of a file.")]
public sealed class WordCountPlugin : PluginBase
{
    public override IEnumerable<PluginAgentToolContribution> GetAgentTools()
    {
        var schema = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "path": { "type": "string", "description": "The file to count: a path in the project folder, or a full path." }
              },
              "required": ["path"],
              "additionalProperties": false
            }
            """).RootElement.Clone();

        // The name is what the model calls: letters, digits, '_' and '-'. The description is all it knows of the tool.
        yield return AgentTool.Create(new AgentToolDefinition(
            new AgentToolSpec("word_count", "Counts the lines, the words and the characters of a text file.", schema),
            CountAsync));
    }

    private async Task<AgentToolResult> CountAsync(AgentToolInvocation invocation, CancellationToken cancellationToken)
    {
        if (!invocation.Arguments.TryGetProperty("path", out var value) || value.GetString() is not { Length: > 0 } path)
        {
            return Failure("The argument 'path' is required.");
        }

        // In CodeAlta Desktop this is the project of the session whose tool call is running.
        var project = Services.Workspace.SelectedProjectPath;
        var file = Path.IsPathRooted(path) || project is null ? path : Path.Combine(project, path);
        if (!File.Exists(file)) return Failure($"The file '{file}' does not exist.");

        var text = await File.ReadAllTextAsync(file, cancellationToken);
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        var lines = text.Length == 0 ? 0 : text.Count(static character => character == '\n') + 1;
        return new AgentToolResult(true, [new AgentToolResultItem.Text($"{lines} lines, {words} words, {text.Length} characters")]);
    }

    // A failed result tells the model what was wrong, so that it can call the tool again.
    private static AgentToolResult Failure(string message) => new(false, [new AgentToolResultItem.Text(message)], message);
}
