/**
 * How long to look again for a session that is not listed yet, between the attempts: a run that just started on
 * another machine is listed once that machine reports it, a second or two later.
 */
export const sessionLookupDelays: readonly number[] = [500, 1000, 1500, 2000];

/**
 * Looks for something with `find`, then again after each delay while it is not found and `alive` says the caller still
 * wants it. Resolves to whether it was found.
 */
export async function findWithPatience(find: () => Promise<boolean>, alive: () => boolean, delays: readonly number[] = sessionLookupDelays,
  wait: (milliseconds: number) => Promise<void> = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds))): Promise<boolean> {
  if (await find()) return true;
  for (const delay of delays) {
    await wait(delay);
    if (!alive()) return false;
    if (await find()) return true;
  }

  return false;
}
