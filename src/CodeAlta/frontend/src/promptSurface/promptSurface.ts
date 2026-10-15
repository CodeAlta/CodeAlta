/** How long before its end the countdown of the prompt cache takes the warning colour. */
export const cacheEndingMilliseconds = 3 * 60_000;

/** What the countdown of the prompt cache shows: the minutes left, the warning near the end, "expired" after. */
export type CacheCountdownView = Readonly<{ state: "live" | "ending" | "expired"; text: string; remainingMilliseconds: number }>;

/**
 * The countdown of a prompt cache that expires at `expiresAt` (an ISO time), seen at `now`; null when the time is
 * not known. The minutes are rounded up, so that "1m" is never shown for a cache that is already gone.
 */
export function cacheCountdown(expiresAt: string | null | undefined, now: number): CacheCountdownView | null {
  if (!expiresAt) return null;
  const end = Date.parse(expiresAt);
  if (!Number.isFinite(end)) return null;
  const remaining = end - now;
  if (remaining <= 0) return { state: "expired", text: "expired", remainingMilliseconds: 0 };
  const minutes = Math.ceil(remaining / 60_000);
  const text = minutes >= 60 ? `${Math.floor(minutes / 60)}h${minutes % 60 ? ` ${minutes % 60}m` : ""}` : `${minutes}m`;
  return { state: remaining <= cacheEndingMilliseconds ? "ending" : "live", text, remainingMilliseconds: remaining };
}

/** How often the countdown is drawn again: every few seconds near the end, every half minute otherwise. */
export function cacheCountdownTick(view: CacheCountdownView | null): number | null {
  if (!view || view.state === "expired") return null;
  return view.remainingMilliseconds <= cacheEndingMilliseconds ? 5_000 : 30_000;
}

/** The share of a ring of `radius` that `percent` fills, as the dash of its circle. */
export function ringDash(percent: number | null, radius: number): string {
  const circumference = 2 * Math.PI * radius;
  const filled = Math.max(0, Math.min(100, percent ?? 0)) / 100 * circumference;
  return `${filled.toFixed(2)} ${circumference.toFixed(2)}`;
}

/**
 * What the Agent chip says of the reasoning of a selection: the effort it shows, and what its tooltip says when it
 * shows none. A model whose provider says its list of efforts is complete runs without an effort it does not list: the
 * chip shows none, and says that the model has no reasoning effort, or that the saved one is not used. Elsewhere the
 * saved effort is shown as it is: the provider may still send it.
 */
export function reasoningShown(effort: string | null, model: Readonly<{ name: string; efforts: readonly string[]; effortsComplete?: boolean | null }> | undefined)
  : Readonly<{ effort: string | null; note: "none" | "unused" | null }> {
  if (!model?.effortsComplete) return { effort, note: null };
  if (model.efforts.length === 0) return { effort: null, note: "none" };
  return effort && !model.efforts.includes(effort) ? { effort: null, note: "unused" } : { effort, note: null };
}

/** The first part of a plugin status text ("1/1" of "1/1 · tools not loaded"), shown where the whole does not fit. */
export function shortStatusText(text: string): string {
  const separator = text.indexOf(" · ");
  return separator < 0 ? text : text.slice(0, separator);
}
