using System.Text;

namespace CodeAlta.Plugin.Mcp;

// What the variables of a file of another tool stand for.
internal readonly record struct McpExternalContext(string? ProjectDirectory, string UserHome);

// The variables other tools accept in their MCP files, brought to what CodeAlta resolves when it starts a server:
// ${NAME} and ${NAME:-default} for an environment variable. The folders are resolved here; a value only another
// tool can give (${input:...} of Visual Studio Code) leaves the server out.
internal static class McpExternalVariables
{
    private const int MaximumNameLength = 64;

    public static bool TryNormalize(
        McpServerDefinition definition,
        McpExternalContext context,
        out McpServerDefinition normalized,
        out McpSkipReason reason,
        out string message)
    {
        var state = new State(definition, context);
        var command = state.Normalize(definition.Command);
        var args = definition.Args.Count == 0 ? definition.Args : definition.Args.Select(arg => state.Normalize(arg)!).ToArray();
        var cwd = state.Normalize(definition.Cwd);
        var url = state.Normalize(definition.Url);
        var env = state.Normalize(definition.Env);
        var headers = state.Normalize(definition.Headers);
        if (state.Variable is { } variable)
        {
            normalized = definition;
            reason = state.Reason;
            message = state.Reason == McpSkipReason.InputVariable
                ? $"MCP server '{definition.Key}' uses '${{{variable}}}', a value Visual Studio Code asks for."
                : $"MCP server '{definition.Key}' uses the variable '${{{variable}}}', which CodeAlta does not resolve.";
            return false;
        }

        normalized = definition with { Command = command, Args = args, Cwd = ResolveWorkingDirectory(definition, cwd, context), Url = url, Env = env, Headers = headers };
        reason = default;
        message = string.Empty;
        return true;
    }

    // A server of a project starts in the folder of the project, as it does in the tool the file is written for.
    private static string? ResolveWorkingDirectory(McpServerDefinition definition, string? cwd, McpExternalContext context)
    {
        if (definition.Transport != McpTransportKind.Stdio || definition.SourceScope != McpConfigScope.Project || context.ProjectDirectory is not { } project)
        {
            return cwd;
        }

        if (string.IsNullOrWhiteSpace(cwd))
        {
            return project;
        }

        return cwd.Contains("${", StringComparison.Ordinal) || Path.IsPathRooted(cwd) ? cwd : Path.GetFullPath(Path.Combine(project, cwd));
    }

    private static bool IsEnvironmentReference(ReadOnlySpan<char> name)
    {
        var separator = name.IndexOf(":-", StringComparison.Ordinal);
        var variable = separator < 0 ? name : name[..separator];
        return variable.Length > 0 && variable.IndexOf(':') < 0 && !variable.ContainsAny(' ', '\t');
    }

    private sealed class State(McpServerDefinition definition, McpExternalContext context)
    {
        public string? Variable { get; private set; }

        public McpSkipReason Reason { get; private set; }

        public IReadOnlyDictionary<string, string> Normalize(IReadOnlyDictionary<string, string> values)
        {
            if (values.Count == 0)
            {
                return values;
            }

            var result = new Dictionary<string, string>(values.Count, StringComparer.Ordinal);
            foreach (var (key, value) in values)
            {
                result[key] = Normalize(value)!;
            }

            return result;
        }

        public string? Normalize(string? value)
        {
            if (value is null || Variable is not null)
            {
                return value;
            }

            var start = value.IndexOf("${", StringComparison.Ordinal);
            if (start < 0)
            {
                return value;
            }

            var builder = new StringBuilder(value.Length);
            var offset = 0;
            while (start >= 0)
            {
                var end = value.IndexOf('}', start + 2);
                if (end < 0)
                {
                    break;
                }

                builder.Append(value, offset, start - offset);
                var name = value.AsSpan(start + 2, end - start - 2);
                if (!TryResolve(name, builder))
                {
                    Variable = name.Length <= MaximumNameLength ? name.ToString() : string.Concat(name[..MaximumNameLength], "...");
                    return value;
                }

                offset = end + 1;
                start = value.IndexOf("${", offset, StringComparison.Ordinal);
            }

            return builder.Append(value, offset, value.Length - offset).ToString();
        }

        private bool TryResolve(ReadOnlySpan<char> name, StringBuilder builder)
        {
            if (name.StartsWith("input:", StringComparison.Ordinal))
            {
                Reason = McpSkipReason.InputVariable;
                return false;
            }

            Reason = McpSkipReason.UnknownVariable;
            if (name.StartsWith("env:", StringComparison.Ordinal))
            {
                var variable = name[4..];
                if (!IsEnvironmentReference(variable))
                {
                    return false;
                }

                builder.Append("${").Append(variable).Append('}');
                return true;
            }

            switch (name)
            {
                case "workspaceFolder" when context.ProjectDirectory is { } project:
                    builder.Append(project);
                    return true;
                case "workspaceFolderBasename" when context.ProjectDirectory is { } project:
                    builder.Append(Path.GetFileName(Path.TrimEndingDirectorySeparator(project)));
                    return true;
                case "userHome":
                    builder.Append(context.UserHome);
                    return true;
                case "pathSeparator" or "/":
                    builder.Append(Path.DirectorySeparatorChar);
                    return true;
            }

            if (name.Length == 0)
            {
                builder.Append("${}");
                return true;
            }

            // Visual Studio Code names an environment variable ${env:NAME}: a bare name is one of its own variables there.
            if (definition.SourceFlavor == McpConfigFlavor.Vscode || !IsEnvironmentReference(name))
            {
                return false;
            }

            builder.Append("${").Append(name).Append('}');
            return true;
        }
    }
}
