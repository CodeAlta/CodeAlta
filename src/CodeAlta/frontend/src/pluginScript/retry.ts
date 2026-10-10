// When a call or a stream of a script asks again after the connection to its plugin ended.

/** The first wait, which doubles with each try that failed, and the longest one. */
export const retryDelayMilliseconds = 500;
export const retryDelayLimitMilliseconds = 8000;

/** The most times a call asks again by itself: about half a minute, after which it waits for a reason to ask (`reload`, the tab shown again, a new connection). */
export const retryLimit = 6;

/**
 * The wait before the try that follows `failures` failed ones, or null when `limit` tries were made: whoever asked stops asking by itself.
 * A stream has no limit: it is followed for as long as the component is shown.
 */
export function retryDelay(failures: number, limit: number = Infinity): number | null {
  return failures >= limit ? null : Math.min(retryDelayLimitMilliseconds, retryDelayMilliseconds * 2 ** Math.min(Math.max(failures, 0), 4));
}
