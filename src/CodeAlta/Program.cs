using XenoAtom.Logging;

if (!CodeAltaCliOptions.TryParse(args, out var options, out var error))
{
    Console.Error.WriteLine(error);
    Environment.ExitCode = 1;
    return;
}

var cancellationTokenSource = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellationTokenSource.Cancel();
};

if (options!.TestDuration is { } testDuration)
{
    cancellationTokenSource.CancelAfter(testDuration);
}

await using var app = await CodeAltaApp.CreateAsync(cancellationTokenSource.Token).ConfigureAwait(false);
if (options.TestMode)
{
    var logger = LogManager.GetLogger("CodeAlta.Program");
    var testDurationText = options.TestDuration!.Value.TotalSeconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
    if (LogManager.IsInitialized && logger.IsEnabled(LogLevel.Debug))
    {
        logger.Debug($"Starting CodeAlta terminal smoke test for {testDurationText}s.");
    }

    Console.WriteLine($"[CodeAlta] Starting terminal smoke test for {testDurationText}s.");
}

await app.RunAsync(cancellationTokenSource.Token).ConfigureAwait(false);

if (options.TestMode)
{
    var logger = LogManager.GetLogger("CodeAlta.Program");
    if (LogManager.IsInitialized && logger.IsEnabled(LogLevel.Debug))
    {
        logger.Debug("CodeAlta terminal smoke test exited cleanly.");
    }

    Console.WriteLine("[CodeAlta] Terminal smoke test exited cleanly.");
}
