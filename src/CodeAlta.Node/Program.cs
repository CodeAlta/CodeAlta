using CodeAlta.Hosting;
using CodeAlta.Node;

// The worker of a sandboxed session is this executable too: it runs the tools of the session inside its sandbox.
if (args is [CodeAlta.Sandbox.Worker.SandboxWorker.Option]) return await CodeAlta.Sandbox.Worker.SandboxWorker.RunAsync();

int code;
// The traces of the sessions go to the OpenTelemetry collector the environment names, if any.
using (CodeAltaTelemetry.StartFromEnvironment("altanode"))
{
    code = await new NodeApplication(Console.Out, Console.Error).RunAsync(args);
}

// The node disposed what it started: a thread a library left behind does not keep the process.
Environment.Exit(code);
return code;
