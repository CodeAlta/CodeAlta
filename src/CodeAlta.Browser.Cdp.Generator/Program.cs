using CodeAlta.Browser.Cdp.Generator;
using XenoAtom.Terminal;

using var session = Terminal.Open();
var app = GeneratorCliOptions.CreateCommandApp(ExecuteAsync);
return await app.RunAsync(args).ConfigureAwait(false);

static async ValueTask<int> ExecuteAsync(GeneratorCliOptions options)
{
    ArgumentNullException.ThrowIfNull(options);
    // The default output is the library beside the generator: bin/<configuration>/<tfm> is three folders down.
    var outputDirectory = options.OutputDirectory is { Length: > 0 } output
        ? Path.GetFullPath(output)
        : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "CodeAlta.Browser", "Cdp", "generated"));
    var methodsFile = options.MethodsFile is { Length: > 0 } methods ? Path.GetFullPath(methods) : Path.Combine(AppContext.BaseDirectory, "cdp-methods.json");

    string[] files;
    string revision;
    if (options.ProtocolDirectory is { } directory)
    {
        files = [await File.ReadAllTextAsync(Path.Combine(directory, "browser_protocol.json")).ConfigureAwait(false), await File.ReadAllTextAsync(Path.Combine(directory, "js_protocol.json")).ConfigureAwait(false)];
        revision = options.Revision ?? "local";
    }
    else
    {
        revision = options.Revision!;
        using var http = new HttpClient();
        files =
        [
            await http.GetStringAsync($"https://raw.githubusercontent.com/ChromeDevTools/devtools-protocol/{revision}/json/browser_protocol.json").ConfigureAwait(false),
            await http.GetStringAsync($"https://raw.githubusercontent.com/ChromeDevTools/devtools-protocol/{revision}/json/js_protocol.json").ConfigureAwait(false),
        ];
    }

    Terminal.WriteLine($"Protocol: {options.ProtocolDirectory ?? "ChromeDevTools/devtools-protocol"} at {revision}");
    Terminal.WriteLine($"Methods:  {methodsFile}");
    Terminal.WriteLine($"Output:   {outputDirectory}");

    var emitter = new CdpEmitter(CdpProtocol.Parse(files), CdpMethodMap.Load(methodsFile), GeneratorCliOptions.Namespace);
    var generated = emitter.EmitAll(revision);
    await OutputDirectoryCleaner.CleanAsync(outputDirectory).ConfigureAwait(false);
    Directory.CreateDirectory(outputDirectory);
    foreach (var (fileName, content) in generated) await File.WriteAllTextAsync(Path.Combine(outputDirectory, fileName), content).ConfigureAwait(false);
    Terminal.WriteLine($"Generated {generated.Count} files.");
    foreach (var warning in emitter.Warnings) Terminal.WriteLine($"  WARN: {warning}");
    return emitter.Warnings.Count == 0 ? 0 : 1;
}
