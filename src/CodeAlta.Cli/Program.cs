using CodeAlta.Cli;

// The worker of a sandboxed session is this executable too: its standard streams are the frames of the worker.
if (args is [CodeAlta.Sandbox.Worker.SandboxWorker.Option]) return await CodeAlta.Sandbox.Worker.SandboxWorker.RunAsync();

// The console is taken over first: standard output is the protocol's alone, and a stray write goes to standard error.
var streams = CliStreams.FromConsole();
var code = await CliApplication.RunAsync(args, streams, CliEnvironment.FromProcess());
// The run disposed what it started and flushed its output: a thread a library left behind does not keep the process.
Environment.Exit(code);
return code;
