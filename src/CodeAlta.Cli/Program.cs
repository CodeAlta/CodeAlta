using CodeAlta.Cli;

// The console is taken over first: standard output is the protocol's alone, and a stray write goes to standard error.
var streams = CliStreams.FromConsole();
var code = await CliApplication.RunAsync(args, streams, CliEnvironment.FromProcess());
// The run disposed what it started and flushed its output: a thread a library left behind does not keep the process.
Environment.Exit(code);
return code;
