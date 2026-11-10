/**
 * The time the frontend tests give what they wait for: every time limit of a test (the `timeout` of a test, a wait
 * until the page shows something, the answer of a DevTools command, the start of a browser) is taken from here,
 * never written as a bare number.
 *
 * A CI runner is several times slower than a developer machine: on CI (the `CI` or `GITHUB_ACTIONS` variable is set,
 * as GitHub Actions does) each limit is {@link testTimeCiScale} times longer. `CODEALTA_TEST_TIME_SCALE` gives another
 * factor, on CI or not. A limit is how long a test waits before it fails: a wait that ends returns at once, so a
 * longer limit only makes a stuck test fail later. A time that is part of what a test checks (a delay the app is
 * given, a clock a test advances) is not a limit and stays as it is.
 *
 * The .NET tests do the same with `src/TestSupport/TestTime.cs`, with the same factor: a test of each side checks it.
 */

/** How many times longer each limit is on CI; the same as `TestTime.CiScale` of the .NET tests. */
export const testTimeCiScale = 4;

/** Whether the tests run on CI. */
export const testTimeOnCi = isSet(process.env.CI) || isSet(process.env.GITHUB_ACTIONS);

/** How many times longer each limit is in this run: 1 on a developer machine, {@link testTimeCiScale} on CI. */
export const testTimeScale = readScale(process.env.CODEALTA_TEST_TIME_SCALE, testTimeOnCi);

/** The limit of this run, in milliseconds, for a limit of `milliseconds` on a developer machine. */
export function testLimit(milliseconds: number): number {
  return Number.isFinite(milliseconds) && milliseconds > 0 ? Math.round(milliseconds * testTimeScale) : milliseconds;
}

/** Says that a limit ran out: how long, the factor of this run, and for what. */
export function testLimitRanOut(milliseconds: number, what: string): string {
  const scale = testTimeScale === 1 ? "" : ` (${testTimeScale} times the limit of a developer machine${testTimeOnCi ? ", on CI" : ""})`;
  return `Time limit ran out: ${milliseconds >= 1000 ? `${milliseconds / 1000} s` : `${milliseconds} ms`}${scale} without ${what}.`;
}

/** The factor that a variable gives, else that of CI or of a developer machine. */
export function readScale(given: string | undefined, onCi: boolean): number {
  const scale = Number(given);
  if (given?.trim() && Number.isFinite(scale) && scale > 0) return scale;
  return onCi ? testTimeCiScale : 1;
}

function isSet(value: string | undefined): boolean {
  return !!value && value !== "0" && value.toLowerCase() !== "false";
}
