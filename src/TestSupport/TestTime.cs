using System.Globalization;
using System.Runtime.CompilerServices;

namespace CodeAlta.Testing;

/// <summary>
/// The time the tests give what they wait for: every time limit of a test (a wait, a deadline, the cancellation of a
/// token after a while) is taken from here, never written as a bare <see cref="TimeSpan"/>.
/// </summary>
/// <remarks>
/// A CI runner is several times slower than a developer machine, and runs every test project at once: on CI (the
/// <c>CI</c> or <c>GITHUB_ACTIONS</c> variable is set, as GitHub Actions does) each limit is <see cref="CiScale"/> times
/// longer. <c>CODEALTA_TEST_TIME_SCALE</c> gives another factor, on CI or not. A limit is how long a test waits before
/// it fails: a wait that ends returns at once, so a longer limit only makes a stuck test fail later. A time that is
/// part of what a test checks (a delay a product is configured with, a clock it advances) is not a limit and stays as it is,
/// with <c>// not a limit</c> at the end of its line. A timed wait of a task goes through <see cref="TestTimeExtensions"/>
/// (<c>WithinAsync</c>), which says where it ran out. <c>TestTimeGuardTests</c> (CodeAlta.Tests) reads the sources of the tests
/// and names each line that does otherwise. The frontend tests do the same with <c>src/CodeAlta/frontend/src/testTime.ts</c>,
/// with the same factor. This file is linked into every test project by <c>Directory.Build.props</c>.
/// </remarks>
internal static class TestTime
{
    /// <summary>How many times longer each limit is on CI.</summary>
    public const double CiScale = 4;

    /// <summary>Gets how many times longer each limit is in this run: 1 on a developer machine, <see cref="CiScale"/> on CI.</summary>
    public static double Scale { get; } = ReadScale();

    /// <summary>Gets whether the tests run on CI.</summary>
    public static bool OnCi { get; } = IsSet("CI") || IsSet("GITHUB_ACTIONS");

    /// <summary>Gives the limit of this run for a limit that suits a developer machine.</summary>
    /// <param name="limit">The limit on a developer machine.</param>
    /// <returns>The limit of this run, or <paramref name="limit"/> itself when it is infinite.</returns>
    public static TimeSpan Limit(TimeSpan limit) => limit == Timeout.InfiniteTimeSpan ? limit : limit * Scale;

    /// <summary>Gives the limit of this run for a limit of <paramref name="seconds"/> on a developer machine.</summary>
    /// <param name="seconds">The limit on a developer machine, in seconds.</param>
    /// <returns>The limit of this run.</returns>
    public static TimeSpan Seconds(double seconds) => Limit(TimeSpan.FromSeconds(seconds));

    /// <summary>Gives the limit of this run for a limit of <paramref name="milliseconds"/> on a developer machine.</summary>
    /// <param name="milliseconds">The limit on a developer machine, in milliseconds.</param>
    /// <returns>The limit of this run.</returns>
    public static TimeSpan Milliseconds(double milliseconds) => Limit(TimeSpan.FromMilliseconds(milliseconds));

    /// <summary>Gives the limit of this run for a limit of <paramref name="minutes"/> on a developer machine.</summary>
    /// <param name="minutes">The limit on a developer machine, in minutes.</param>
    /// <returns>The limit of this run.</returns>
    public static TimeSpan Minutes(double minutes) => Limit(TimeSpan.FromMinutes(minutes));

    /// <summary>Gives the limit of this run, in whole milliseconds, for an API that takes an <see cref="int"/>.</summary>
    /// <param name="milliseconds">The limit on a developer machine, in milliseconds.</param>
    /// <returns>The limit of this run, in milliseconds.</returns>
    public static int MillisecondsInt(int milliseconds) => (int)Math.Min(int.MaxValue, milliseconds * Scale);

    /// <summary>Describes a limit that ran out: where, how long, and for what.</summary>
    /// <param name="limit">The limit of this run that ran out.</param>
    /// <param name="what">What the test waited for: the expression it awaited.</param>
    /// <param name="file">The file of the test.</param>
    /// <param name="line">The line of the test.</param>
    /// <returns>The message of the failure.</returns>
    public static string RanOut(TimeSpan limit, string what, string file, int line)
    {
        var scale = Scale == 1 ? "" : string.Create(CultureInfo.InvariantCulture, $" ({Scale:0.##} times the limit of a developer machine{(OnCi ? ", on CI" : "")})");
        return string.Create(CultureInfo.InvariantCulture, $"Time limit ran out at {Path.GetFileName(file)}:{line}: {Format(limit)}{scale} without the end of `{what}`.");
    }

    private static string Format(TimeSpan limit)
        => limit.TotalSeconds >= 1 ? string.Create(CultureInfo.InvariantCulture, $"{limit.TotalSeconds:0.###} s") : string.Create(CultureInfo.InvariantCulture, $"{limit.TotalMilliseconds:0} ms");

    private static double ReadScale()
    {
        var given = Environment.GetEnvironmentVariable("CODEALTA_TEST_TIME_SCALE");
        if (double.TryParse(given, NumberStyles.Float, CultureInfo.InvariantCulture, out var scale) && scale > 0) return scale;
        return IsSet("CI") || IsSet("GITHUB_ACTIONS") ? CiScale : 1;
    }

    private static bool IsSet(string name)
        => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value && !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) && value != "0";
}

/// <summary>
/// The waits of the tests: <see cref="Task.WaitAsync(TimeSpan)"/> that, when its limit runs out, says where (file and
/// line), how long and for what (<see cref="TestTime.RanOut"/>) instead of "The operation has timed out."
/// </summary>
internal static class TestTimeExtensions
{
    /// <summary>Waits for <paramref name="task"/>, at most <paramref name="limit"/>.</summary>
    /// <param name="task">What the test waits for.</param>
    /// <param name="limit">The limit, from <see cref="TestTime"/>.</param>
    /// <param name="what">Filled by the compiler: the expression awaited.</param>
    /// <param name="file">Filled by the compiler: the file of the test.</param>
    /// <param name="line">Filled by the compiler: the line of the test.</param>
    /// <returns>A task that ends with <paramref name="task"/>.</returns>
    /// <exception cref="TimeoutException">The limit ran out first; its message says where and for what.</exception>
    public static async Task WithinAsync(this Task task, TimeSpan limit, [CallerArgumentExpression(nameof(task))] string what = "",
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        try { await task.WaitAsync(limit).ConfigureAwait(false); }
        catch (TimeoutException) when (!task.IsCompleted) { throw new TimeoutException(TestTime.RanOut(limit, what, file, line)); }
    }

    /// <summary>Waits for <paramref name="task"/>, at most <paramref name="limit"/> or until <paramref name="cancellationToken"/> is canceled.</summary>
    /// <param name="task">What the test waits for.</param>
    /// <param name="limit">The limit, from <see cref="TestTime"/>.</param>
    /// <param name="cancellationToken">Stops the wait.</param>
    /// <param name="what">Filled by the compiler: the expression awaited.</param>
    /// <param name="file">Filled by the compiler: the file of the test.</param>
    /// <param name="line">Filled by the compiler: the line of the test.</param>
    /// <returns>A task that ends with <paramref name="task"/>.</returns>
    /// <exception cref="TimeoutException">The limit ran out first; its message says where and for what.</exception>
    public static async Task WithinAsync(this Task task, TimeSpan limit, CancellationToken cancellationToken, [CallerArgumentExpression(nameof(task))] string what = "",
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        try { await task.WaitAsync(limit, cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException) when (!task.IsCompleted) { throw new TimeoutException(TestTime.RanOut(limit, what, file, line)); }
    }

    /// <summary>Waits for <paramref name="task"/> and gives its result, at most <paramref name="limit"/>.</summary>
    /// <typeparam name="T">The result of the task.</typeparam>
    /// <param name="task">What the test waits for.</param>
    /// <param name="limit">The limit, from <see cref="TestTime"/>.</param>
    /// <param name="what">Filled by the compiler: the expression awaited.</param>
    /// <param name="file">Filled by the compiler: the file of the test.</param>
    /// <param name="line">Filled by the compiler: the line of the test.</param>
    /// <returns>The result of <paramref name="task"/>.</returns>
    /// <exception cref="TimeoutException">The limit ran out first; its message says where and for what.</exception>
    public static async Task<T> WithinAsync<T>(this Task<T> task, TimeSpan limit, [CallerArgumentExpression(nameof(task))] string what = "",
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        try { return await task.WaitAsync(limit).ConfigureAwait(false); }
        catch (TimeoutException) when (!task.IsCompleted) { throw new TimeoutException(TestTime.RanOut(limit, what, file, line)); }
    }

    /// <summary>Waits for <paramref name="task"/> and gives its result, at most <paramref name="limit"/> or until <paramref name="cancellationToken"/> is canceled.</summary>
    /// <typeparam name="T">The result of the task.</typeparam>
    /// <param name="task">What the test waits for.</param>
    /// <param name="limit">The limit, from <see cref="TestTime"/>.</param>
    /// <param name="cancellationToken">Stops the wait.</param>
    /// <param name="what">Filled by the compiler: the expression awaited.</param>
    /// <param name="file">Filled by the compiler: the file of the test.</param>
    /// <param name="line">Filled by the compiler: the line of the test.</param>
    /// <returns>The result of <paramref name="task"/>.</returns>
    /// <exception cref="TimeoutException">The limit ran out first; its message says where and for what.</exception>
    public static async Task<T> WithinAsync<T>(this Task<T> task, TimeSpan limit, CancellationToken cancellationToken, [CallerArgumentExpression(nameof(task))] string what = "",
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        try { return await task.WaitAsync(limit, cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException) when (!task.IsCompleted) { throw new TimeoutException(TestTime.RanOut(limit, what, file, line)); }
    }
}
