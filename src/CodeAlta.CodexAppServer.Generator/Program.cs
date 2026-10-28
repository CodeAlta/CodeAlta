using CodeAlta.CodexAppServer.Generator;
using XenoAtom.Terminal;

using var session = Terminal.Open();
var app = GeneratorCliOptions.CreateCommandApp(ExecuteAsync);
return await app.RunAsync(args).ConfigureAwait(false);

static async ValueTask<int> ExecuteAsync(GeneratorCliOptions options)
{
    ArgumentNullException.ThrowIfNull(options);
    var source = options.SchemaDirectory is { } schemaDirectory
        ? CodexSchemaSource.FromDirectory(schemaDirectory)
        : await CodexSchemaSource.FromRepositoryAsync(options.CodexRepositoryDirectory!, options.SourceRef!, CancellationToken.None).ConfigureAwait(false);
    try
    {
        // The default output is the protocol library beside the generator: bin/<configuration>/<tfm> is three folders down.
        var outputDirectory = options.OutputDirectory is { Length: > 0 } output
            ? Path.GetFullPath(output)
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "CodeAlta.CodexAppServer", "generated"));
        var methodsFile = options.MethodsFile is { Length: > 0 } methods
            ? Path.GetFullPath(methods)
            : Path.Combine(AppContext.BaseDirectory, "codex-app-server-methods.json");
        var bundle = Path.Combine(source.SchemaDirectory, "codex_app_server_protocol.v2.schemas.json");

        Terminal.WriteLine($"Source:    {source.SourceRef ?? source.SchemaDirectory}{(source.Commit is { } commit ? $" ({commit})" : string.Empty)}");
        Terminal.WriteLine($"Methods:   {methodsFile}");
        Terminal.WriteLine($"Output:    {outputDirectory}");
        Terminal.WriteLine();

        var map = CodexMethodMap.Load(methodsFile, source.SchemaDirectory);
        var definitions = await SchemaWalker.LoadDefinitionsAsync(bundle, GeneratorCliOptions.ProtocolNamespace, map.RootTypes).ConfigureAwait(false);
        Terminal.WriteLine($"{map.Requests.Count} requests, {map.Notifications.Count} notifications, {map.ServerRequests.Count} server requests: {definitions.Count} types.");

        var emitter = new CSharpEmitter(definitions, GeneratorCliOptions.ProtocolNamespace);
        var filesByNamespace = emitter.EmitAll(definitions);

        await OutputDirectoryCleaner.CleanAsync(outputDirectory).ConfigureAwait(false);
        Directory.CreateDirectory(outputDirectory);
        var protocolDirectory = Path.Combine(outputDirectory, "Protocol");
        Directory.CreateDirectory(protocolDirectory);

        var count = 0;
        foreach (var (ns, files) in filesByNamespace)
        {
            var relative = ns == GeneratorCliOptions.ProtocolNamespace
                ? string.Empty
                : ns[(GeneratorCliOptions.ProtocolNamespace.Length + 1)..].Replace('.', Path.DirectorySeparatorChar);
            var directory = Path.Combine(protocolDirectory, relative);
            Directory.CreateDirectory(directory);
            foreach (var (fileName, content) in files)
            {
                await File.WriteAllTextAsync(Path.Combine(directory, fileName), content).ConfigureAwait(false);
                count++;
            }
        }

        await File.WriteAllTextAsync(Path.Combine(protocolDirectory, $"{GeneratorCliOptions.ContextName}.gen.cs"), emitter.EmitSerializerContext(GeneratorCliOptions.ContextName)).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "CodexAppServerClient.Methods.gen.cs"),
            CodexMethodEmitter.EmitClientMethods(map, GeneratorCliOptions.ClientNamespace, GeneratorCliOptions.ProtocolNamespace, GeneratorCliOptions.ContextName)).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "CodexMethods.gen.cs"),
            CodexMethodEmitter.EmitServerMessages(map, GeneratorCliOptions.ClientNamespace, GeneratorCliOptions.ProtocolNamespace, GeneratorCliOptions.ContextName)).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "CodexAppServerProtocol.gen.cs"),
            CodexMethodEmitter.EmitProtocolInfo(GeneratorCliOptions.ClientNamespace, source)).ConfigureAwait(false);
        count += 4;
        Terminal.WriteLine($"Generated {count} files.");

        if (emitter.Warnings.Count > 0)
        {
            Terminal.WriteLine();
            Terminal.WriteLine($"Warnings ({emitter.Warnings.Count}):");
            foreach (var warning in emitter.Warnings)
            {
                Terminal.WriteLine($"  WARN: {warning}");
            }
        }

        return 0;
    }
    finally
    {
        source.Cleanup();
    }
}
