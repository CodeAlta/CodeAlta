using CodeAlta.Server;

return await new ServerApplication(Console.Out, Console.Error) { KeepsLog = true }.RunAsync(args);
