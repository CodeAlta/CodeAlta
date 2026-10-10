import { dayOfNumber, etaParts } from "./format";
import type { HistoryChoice, StatisticsStatus } from "./types";

// What the history bar and the first-time card say, as data: pure functions of a status, so the words are tested without a page.

/** The readable kind of bar a status needs. */
export type HistoryView = "none" | "starting" | "choice" | "reading" | "paused" | "stopped" | "failed" | "skipped";

/** Which bar or card the status asks for. `done` with skipped sessions keeps a quiet line; `done` alone shows nothing. */
export function historyView(status: StatisticsStatus | null): HistoryView {
  if (!status) return "starting";
  switch (status.state) {
    case "starting": return "starting";
    case "needsChoice": return "choice";
    case "reading": return "reading";
    case "paused": return "paused";
    case "stoppedHere": return "stopped";
    case "failed": return "failed";
    case "done": return status.skippedCount > 0 ? "skipped" : "none";
  }
}

/** The speed assumed before the reading has measured its own: the plugin reads between 430 and 660 MiB/s on a fast disk. */
export const assumedBytesPerSecond = 400 * 1024 * 1024;

/** The seconds a reading of `bytes` takes at `bytesPerSecond` (or at the assumed speed). */
export const readingSeconds = (bytes: number, bytesPerSecond?: number): number => bytes / Math.max(1, bytesPerSecond ?? assumedBytesPerSecond);

/** The share of the sessions read, from 0 to 1. */
export function progressOf(status: StatisticsStatus): number {
  if (status.sessionsTotal <= 0) return status.state === "done" ? 1 : 0;
  return Math.min(1, Math.max(0, status.sessionsDone / status.sessionsTotal));
}

/** The date the reading has reached as `yyyy-MM-dd`; null before the first session. */
export const reachedDay = (status: StatisticsStatus): string | null => status.oldestDateReached ? dayOfNumber(status.oldestDateReached) : null;

/** How long is left, as the unit and count a sentence needs; null when it cannot be told. */
export function timeLeft(status: StatisticsStatus): ReturnType<typeof etaParts> | null {
  return status.etaSeconds === undefined || status.etaSeconds === null || !Number.isFinite(status.etaSeconds) ? null : etaParts(status.etaSeconds);
}

/** The choices the menu "Read more history…" offers: the ones that go further back than what was chosen. */
export function readMoreChoices(status: StatisticsStatus | null): readonly HistoryChoice[] {
  const choice = status?.choice;
  if (!status || choice === undefined) return [];
  // "All" is all there is to read, unless the user stopped the reading on the way: the statistics then start where it stopped.
  if (choice === "all") return status.state === "stoppedHere" ? [{ kind: "all" }] : [];
  const current = choice.startsWith("days:") ? Number(choice.slice(5)) : 0;
  return [...[30, 90, 180, 365].filter(days => days > current).map((days): HistoryChoice => ({ kind: "days", days })), { kind: "all" }];
}

/** The text a choice is saved as, as the plugin writes it. */
export const choiceText = (choice: HistoryChoice): string => choice.kind === "all" ? "all" : choice.kind === "days" ? `days:${choice.days}` : "from-today";

/** Whether the state is one where the history can still be extended or its sessions forgotten. */
export const canReadMore = (status: StatisticsStatus | null): boolean => readMoreChoices(status).length > 0 && status?.state !== "reading" && status?.state !== "failed";

/** The sessions of the story told by a done reading: how many and since when, for the one notification. */
export function doneSummary(status: StatisticsStatus): Readonly<{ sessions: number; since: string | null }> {
  return { sessions: status.sessionsTotal, since: status.floorDay ? dayOfNumber(status.floorDay) : status.oldestDateReached ? dayOfNumber(status.oldestDateReached) : null };
}
