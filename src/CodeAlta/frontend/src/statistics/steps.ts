import type { BoxStats } from "../charts";
import type { DistributionStep } from "./types";

// The fixed steps a distribution comes in (each about 19% wider than the one before): merged into the few bins a histogram
// draws, and read for percentiles and for the box of a tool.

/** A group of consecutive steps drawn as one bar. */
export type StepBin = Readonly<{ lower: number; upper: number; count: number }>;

/** The number of values in the steps. */
export const stepsCount = (steps: readonly DistributionStep[]): number => steps.reduce((total, step) => total + step.count, 0);

/** The upper edge of a step; the last step has none, so it is taken one step (19%) above its lower edge. */
export const stepUpper = (step: DistributionStep): number => step.upper ?? Math.max(step.lower + 1, step.lower * 1.19);

/**
 * The value below which the fraction `p` (0 to 1) of the values fall, interpolated inside the step it is in, as the plugin
 * does (exact within a step). Undefined without values.
 */
export function percentileOfSteps(steps: readonly DistributionStep[], p: number): number | undefined {
  const count = stepsCount(steps);
  if (count === 0) return undefined;
  const target = count * Math.min(1, Math.max(0, p));
  let seen = 0;
  for (const step of steps) {
    if (seen + step.count >= target) {
      const inside = step.count === 0 ? 0 : (target - seen) / step.count;
      return step.lower + (stepUpper(step) - step.lower) * inside;
    }
    seen += step.count;
  }
  const last = steps[steps.length - 1];
  return stepUpper(last);
}

/**
 * Merges consecutive steps into at most `maxBins` bins of about the same number of steps, keeping the edges. The steps are not
 * contiguous (empty ones are left out), so a bin runs from the lower edge of its first step to the upper edge of its last.
 */
export function binSteps(steps: readonly DistributionStep[], maxBins = 24): StepBin[] {
  if (steps.length === 0) return [];
  const first = steps[0].lower, last = stepUpper(steps[steps.length - 1]);
  // The steps are on a logarithmic scale: bins are cut at equal ratios between the first and the last edge.
  const span = Math.max(last / Math.max(1, first), 1.0001);
  const bins = Math.max(1, Math.min(maxBins, steps.length));
  const factor = span ** (1 / bins);
  const result: StepBin[] = Array.from({ length: bins }, (_, index) => ({ lower: first * factor ** index, upper: first * factor ** (index + 1), count: 0 }));
  for (const step of steps) {
    const middle = Math.sqrt(step.lower * stepUpper(step));
    const index = Math.min(bins - 1, Math.max(0, Math.floor(Math.log(middle / Math.max(1, first)) / Math.log(factor))));
    result[index] = { ...result[index], count: result[index].count + step.count };
  }
  return result;
}

/** The box of a distribution: the quartiles, the 90th percentile and the extremes, from its steps; null without values. */
export function boxStatsOfSteps(steps: readonly DistributionStep[]): BoxStats | null {
  const count = stepsCount(steps);
  if (count === 0) return null;
  return {
    count, min: steps[0].lower, q1: percentileOfSteps(steps, 0.25)!, median: percentileOfSteps(steps, 0.5)!, q3: percentileOfSteps(steps, 0.75)!,
    p90: percentileOfSteps(steps, 0.9)!, max: stepUpper(steps[steps.length - 1]),
  };
}

/** The steps of several distributions as one: the plugin cuts every distribution at the same edges, so the counts of a step add up. */
export function mergeSteps(lists: readonly (readonly DistributionStep[])[]): DistributionStep[] {
  const merged = new Map<number, DistributionStep>();
  for (const steps of lists) {
    for (const step of steps) {
      const known = merged.get(step.lower);
      merged.set(step.lower, known ? { ...known, count: known.count + step.count } : step);
    }
  }
  return [...merged.values()].sort((a, b) => a.lower - b.lower);
}

/** The index of the bin a value falls in, or -1 when it is outside every bin. */
export function binOf(bins: readonly StepBin[], value: number | undefined): number {
  if (value === undefined || bins.length === 0) return -1;
  const index = bins.findIndex((bin, at) => value >= bin.lower && (value < bin.upper || at === bins.length - 1));
  return index;
}
