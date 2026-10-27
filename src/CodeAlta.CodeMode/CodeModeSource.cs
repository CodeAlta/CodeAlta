using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Acornima;
using Acornima.Ast;

namespace CodeAlta.CodeMode;

/// <summary>
/// The source of a script, prepared to run: its first-line options read, its descriptor read, and the slips LLMs
/// often make corrected. Line numbers stay those of the text the model sent, so that an error points at its line.
/// </summary>
public sealed partial class CodeModeSource
{
    // The script runs as the body of an async arrow function, opened on its first line so that lines keep their numbers.
    private const string WrapperStart = "(async () => {";
    private const string WrapperEnd = "\n})()";

    private CodeModeSource(string name, string original, string text, CodeModeExecOptions options, CodeModeDescriptor? descriptor, CodeModeRunError? error)
    {
        Name = name;
        Original = original;
        Text = text;
        Options = options;
        Descriptor = descriptor;
        Error = error;
        Hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(original)));
    }

    /// <summary>Gets the name of the source in stacks and errors, such as <c>script.js</c> or <c>lib:review-pr</c>.</summary>
    public string Name { get; }

    /// <summary>Gets the source as it was given.</summary>
    public string Original { get; }

    /// <summary>Gets the source that runs: same lines as <see cref="Original"/>, corrected.</summary>
    public string Text { get; }

    /// <summary>Gets the options of its first line.</summary>
    public CodeModeExecOptions Options { get; }

    /// <summary>Gets the descriptor of <c>export const meta</c>, or null.</summary>
    public CodeModeDescriptor? Descriptor { get; }

    /// <summary>Gets why the source cannot run, or null when it can.</summary>
    public CodeModeRunError? Error { get; }

    /// <summary>Gets the SHA-256 of <see cref="Original"/>, in hexadecimal.</summary>
    public string Hash { get; }

    /// <summary>Gets the code given to the engine.</summary>
    internal string Wrapped => WrapperStart + Text + WrapperEnd;

    /// <summary>Gets how many columns the wrapper adds to the first line.</summary>
    internal static int FirstLineOffset => WrapperStart.Length;

    /// <summary>Gets one line of <see cref="Original"/>, or null.</summary>
    /// <param name="line">The line, starting at 1.</param>
    /// <returns>The line without its end.</returns>
    public string? GetLine(int line)
    {
        var lines = Original.Split('\n');
        return line >= 1 && line <= lines.Length ? lines[line - 1].TrimEnd('\r') : null;
    }

    /// <summary>
    /// Gives the code of a source without its first-line options, its line ends as <c>\n</c> and trimmed: two sources with the
    /// same body run the same script, whatever their options say.
    /// </summary>
    /// <param name="code">The source.</param>
    /// <returns>The body; empty for a source that is only its options.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="code"/> is null.</exception>
    public static string BodyOf(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        var text = BlankFences(code.Replace("\r\n", "\n", StringComparison.Ordinal));
        var pragma = PragmaPattern().Match(text);
        return (pragma.Success ? text[(pragma.Index + pragma.Length)..] : text).Trim();
    }

    /// <summary>Gives the SHA-256 of the body of a source (<see cref="BodyOf"/>), in hexadecimal.</summary>
    /// <param name="code">The source.</param>
    /// <returns>The hash.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="code"/> is null.</exception>
    public static string BodyHashOf(string code) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(BodyOf(code))));

    /// <summary>Prepares a source. It never throws: a source that cannot run has an <see cref="Error"/>.</summary>
    /// <param name="code">The source.</param>
    /// <param name="name">The name of the source in stacks and errors.</param>
    /// <returns>The prepared source.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="code"/> is null.</exception>
    public static CodeModeSource Prepare(string code, string name = "script.js")
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var text = BlankFences(code.Replace("\r\n", "\n", StringComparison.Ordinal));

        var options = CodeModeExecOptions.None;
        var pragma = PragmaPattern().Match(text);
        if (pragma.Success)
        {
            var parsed = CodeModeExecOptions.TryParse(pragma.Groups["json"].Value, out var pragmaError);
            if (parsed is null)
            {
                return new(name, code, text, options, null, new(CodeModeErrorKind.Syntax, "SyntaxError", pragmaError!) { Location = new(1, 1) });
            }

            options = parsed;
        }

        Module module;
        try
        {
            module = new Parser(new ParserOptions
            {
                AllowReturnOutsideFunction = true,
                AllowAwaitOutsideFunction = true,
                EcmaVersion = EcmaVersion.Latest,
            }).ParseModule(text, name);
        }
        catch (ParseErrorException ex)
        {
            return new(name, code, text, options, null, SyntaxError(text, ex));
        }

        var edits = new List<(int Start, int End, string Replacement)>();
        CodeModeDescriptor? descriptor = null;
        var statements = new List<Statement>();
        var hasTopLevelReturn = false;
        foreach (var statement in module.Body)
        {
            switch (statement)
            {
                case ImportDeclaration import:
                    return new(name, code, text, options, null, new(CodeModeErrorKind.Syntax, "SyntaxError",
                        "`import` is not supported in scripts. Compose scripts with `await workflow(\"name\", args)`, or call the functions of `tools`, `alta` and `mcp`.")
                    { Location = Position(import) });
                case ExportAllDeclaration exportAll:
                    return new(name, code, text, options, null, new(CodeModeErrorKind.Syntax, "SyntaxError", "`export * from` is not supported in scripts.") { Location = Position(exportAll) });
                case ExportNamedDeclaration { Declaration: { } declaration } export:
                    // `export const meta = {…}` and other exported declarations run as plain declarations.
                    edits.Add((export.Start, declaration.Start, new string(' ', declaration.Start - export.Start)));
                    if (declaration is VariableDeclaration variables)
                    {
                        foreach (var declarator in variables.Declarations)
                        {
                            if (declarator is { Id: Identifier { Name: "meta" }, Init: ObjectExpression literal })
                            {
                                descriptor = CodeModeDescriptor.TryRead(literal, out var descriptorError);
                                if (descriptor is null)
                                {
                                    return new(name, code, text, options, null, new(CodeModeErrorKind.Syntax, "SyntaxError", descriptorError!) { Location = Position(declarator) });
                                }
                            }
                        }
                    }

                    statements.Add(declaration);
                    break;
                case ExportNamedDeclaration export:
                    edits.Add((export.Start, export.End, new string(' ', export.End - export.Start)));
                    break;
                case ExportDefaultDeclaration exportDefault:
                    edits.AddRange(UnwrapDefault(exportDefault));
                    hasTopLevelReturn = true;
                    break;
                case ReturnStatement:
                    hasTopLevelReturn = true;
                    statements.Add(statement);
                    break;
                default:
                    statements.Add(statement);
                    break;
            }
        }

        // Models sometimes define their own `step` or `agent`, as if the script ran elsewhere: theirs would run, and the run
        // would record no step and start no session.
        foreach (var statement in statements)
        {
            if (HiddenFunction(statement) is { } hidden)
            {
                return new(name, code, text, options, null, new(CodeModeErrorKind.Syntax, "SyntaxError",
                    $"`{hidden.Name}` is a function of scripts: a declaration of your own hides it, and the run records nothing of what it does. Remove the declaration and call `{hidden.Name}(...)` as it is.")
                { Location = Position(hidden) });
            }
        }

        if (!hasTopLevelReturn && statements.Count > 0)
        {
            edits.AddRange(ReturnTheResult(text, statements));
        }

        return new(name, code, Apply(text, edits), options, descriptor, null);
    }

    // The functions of the prelude that compose the work, which a script never needs to declare.
    private static readonly System.Collections.Frozen.FrozenSet<string> PreludeFunctions =
        System.Collections.Frozen.FrozenSet.Create(StringComparer.Ordinal, "step", "agent", "parallel", "pipeline", "phase", "workflow");

    // The name a top-level declaration gives to a function of the prelude, or null.
    private static Identifier? HiddenFunction(Statement statement) => statement switch
    {
        VariableDeclaration variables => variables.Declarations.Select(static declarator => declarator.Id).OfType<Identifier>()
            .FirstOrDefault(static id => PreludeFunctions.Contains(id.Name)),
        FunctionDeclaration { Id: { } id } when PreludeFunctions.Contains(id.Name) => id,
        ClassDeclaration { Id: { } id } when PreludeFunctions.Contains(id.Name) => id,
        _ => null,
    };

    // `export default async function run(args) {…}` runs its function; `export default async (args) => {…}` too.
    private static IEnumerable<(int Start, int End, string Replacement)> UnwrapDefault(ExportDefaultDeclaration exportDefault)
    {
        var declaration = exportDefault.Declaration;
        if (declaration is FunctionDeclaration { Id: { } id })
        {
            yield return (exportDefault.Start, declaration.Start, new string(' ', declaration.Start - exportDefault.Start));
            yield return (declaration.End, declaration.End, $"\nreturn {id.Name}(args);");
            yield break;
        }

        yield return (exportDefault.Start, declaration.Start, "return (");
        yield return (declaration.End, declaration.End, declaration is FunctionDeclaration or ArrowFunctionExpression or FunctionExpression ? ")(args)" : ")");
    }

    // The value of a script is what it returns. A lone function is called, and a final expression is returned, as
    // models trained on other code modes write them.
    private static IEnumerable<(int Start, int End, string Replacement)> ReturnTheResult(string text, List<Statement> statements)
    {
        if (statements is [FunctionDeclaration { Id: { } lone } function])
        {
            yield return (function.End, function.End, $"\nreturn {lone.Name}(args);");
            yield break;
        }

        if (statements[^1] is ExpressionStatement { Expression: var expression } last and not Directive)
        {
            if (statements.Count == 1 && expression is ArrowFunctionExpression or FunctionExpression)
            {
                yield return (expression.Start, expression.Start, "return (");
                yield return (expression.End, expression.End, ")(args)");
            }
            else if (!text.AsSpan(last.Start, last.End - last.Start).StartsWith("return", StringComparison.Ordinal))
            {
                yield return (expression.Start, expression.Start, "return ");
            }
        }
    }

    private static string Apply(string text, List<(int Start, int End, string Replacement)> edits)
    {
        if (edits.Count == 0)
        {
            return text;
        }

        var builder = new StringBuilder(text);
        foreach (var (start, end, replacement) in edits.OrderByDescending(static edit => edit.Start).ThenByDescending(static edit => edit.End))
        {
            builder.Remove(start, end - start).Insert(start, replacement);
        }

        return builder.ToString();
    }

    // A fenced block (```js … ```) keeps its lines: the fence lines become empty.
    private static string BlankFences(string text)
    {
        var lines = text.Split('\n');
        var first = Array.FindIndex(lines, static line => line.Trim().Length > 0);
        var last = Array.FindLastIndex(lines, static line => line.Trim().Length > 0);
        if (first < 0 || first == last || !lines[first].TrimStart().StartsWith("```", StringComparison.Ordinal) || lines[last].Trim() != "```")
        {
            return text;
        }

        lines[first] = string.Empty;
        lines[last] = string.Empty;
        return string.Join('\n', lines);
    }

    private static CodeModeRunError SyntaxError(string text, ParseErrorException ex)
    {
        var line = Math.Max(1, ex.LineNumber);
        var lines = text.Split('\n');
        var source = line <= lines.Length ? lines[line - 1] : string.Empty;
        var message = TypeScriptPattern().IsMatch(source)
            ? $"TypeScript syntax is not supported: scripts are plain JavaScript. Remove the type annotation on line {line}. ({ex.Description})"
            : ex.Description;
        return new(CodeModeErrorKind.Syntax, "SyntaxError", message) { Location = new(line, ex.Column + 1) };
    }

    private static CodeModeSourceLocation Position(Node node) => new(node.Location.Start.Line, node.Location.Start.Column + 1);

    [GeneratedRegex(@"\A[ \t]*//[ \t]*@(?:exec|options):[ \t]*(?<json>\{.*\})[ \t]*$", RegexOptions.Multiline)]
    private static partial Regex PragmaPattern();

    // Signs of TypeScript on the line of a syntax error: a type annotation, an interface, a type alias, `as` casts, generics.
    [GeneratedRegex(@"(?:[\w\)\]]\??\s*:\s*(?:string|number|boolean|any|unknown|void|never|object|Promise<|Record<|Array<|\{|\[|[A-Z]\w*)(?:\[\])?\s*[,;=\)\{]|\binterface\s+\w+|\btype\s+\w+\s*=|\bas\s+(?:string|number|boolean|any|unknown|const|[A-Z]\w*)\b|\w<\w+(?:\[\])?>\()")]
    private static partial Regex TypeScriptPattern();
}
