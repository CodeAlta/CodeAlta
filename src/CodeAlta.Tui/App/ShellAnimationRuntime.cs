using System.Diagnostics;
using XenoAtom.Terminal.UI;

namespace CodeAlta.Tui.App;

internal sealed class ShellAnimationRuntime
{
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

    public State<float> WelcomePhase01 { get; } = new(0f);

    public State<float> ThinkingPhase01 { get; } = new(0f);

    public void Advance()
    {
        var elapsedTicks = _stopwatch.Elapsed.Ticks;
        WelcomePhase01.Value = ComputeLoopAnimationPhase(elapsedTicks, TimeSpan.TicksPerSecond * 6L);
        ThinkingPhase01.Value = ComputeLoopAnimationPhase(elapsedTicks, TimeSpan.TicksPerSecond * 5L);
    }

    internal static float ComputeLoopAnimationPhase(long ticks, long cycleTicks)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cycleTicks);

        var normalizedTicks = ((ticks % cycleTicks) + cycleTicks) % cycleTicks;
        return (float)(normalizedTicks / (double)cycleTicks);
    }
}
