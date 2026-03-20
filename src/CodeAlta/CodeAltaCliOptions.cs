namespace CodeAlta;

internal sealed class CodeAltaCliOptions
{
    private CodeAltaCliOptions(bool testMode, TimeSpan? testDuration)
    {
        TestMode = testMode;
        TestDuration = testDuration;
    }

    public bool TestMode { get; }

    public TimeSpan? TestDuration { get; }

    public static bool TryParse(
        IReadOnlyList<string> args,
        out CodeAltaCliOptions? options,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);

        var testMode = false;
        TimeSpan? testDuration = null;

        for (var index = 0; index < args.Count; index++)
        {
            var arg = args[index];
            switch (arg)
            {
                case "--test":
                    testMode = true;
                    break;
                case "--test-duration":
                    if (index + 1 >= args.Count)
                    {
                        options = null;
                        error = "Specify a duration in seconds after --test-duration.";
                        return false;
                    }

                    if (!int.TryParse(args[++index], out var durationSeconds) || durationSeconds <= 0)
                    {
                        options = null;
                        error = "The value for --test-duration must be a positive integer number of seconds.";
                        return false;
                    }

                    testDuration = TimeSpan.FromSeconds(durationSeconds);
                    break;
                default:
                    options = null;
                    error = $"Unknown argument '{arg}'. Supported arguments: --test [--test-duration <seconds>].";
                    return false;
            }
        }

        if (!testMode && testDuration is not null)
        {
            options = null;
            error = "--test-duration requires --test.";
            return false;
        }

        options = new CodeAltaCliOptions(
            testMode,
            testMode ? testDuration ?? TimeSpan.FromSeconds(10) : null);
        error = null;
        return true;
    }
}