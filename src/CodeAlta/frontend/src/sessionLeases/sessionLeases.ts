// Sessions another process holds (the command line, another instance), and the sessions this window has a tab of, which
// the host holds so that another process does not run them while they are open.
import type { SessionDisplayView } from "#neoastra";
import { restoreSessionTabs, sessionTabsKey, type SessionTab } from "../sessionTabs";
import type { sessionLeasesMessages } from "./messages";

/** The kinds of holder the host names; anything else is another process. */
export type ElsewhereKind = "altacli" | "desktop" | "tui" | "other";

/** The kind of a holder as the activity of the host says it; null when no other process holds the session. */
export function elsewhereKind(value: string | null | undefined): ElsewhereKind | null {
  if (!value) return null;
  return value === "altacli" || value === "desktop" || value === "tui" ? value : "other";
}

/** What the window says of a session another process holds: the English key of the shell's dictionary. */
export function elsewhereLabel(kind: ElsewhereKind): keyof typeof sessionLeasesMessages {
  switch (kind) {
    case "altacli": return "Running in altacli";
    case "tui": return "Open in CodeAlta TUI";
    case "desktop": return "Open in another CodeAlta Desktop";
    default: return "In use by another process";
  }
}

/**
 * What has the timeline of a session another process holds read again: its revision changes with the length of the
 * journal, as the live view of a session of this window changes with its run.
 */
export function elsewhereRefreshView(sessionId: string, journalLength: string | null | undefined): SessionDisplayView {
  return { sessionId, revision: `elsewhere:${journalLength ?? 0}`, configuration: null, conversationWait: null, lifecycle: null, metadataTruncated: false,
    queuedPromptCount: null, statusKind: null, statusMessage: null, text: [], toolActivities: [], transportTruncated: false,
    evictedTextItems: "0", evictedToolActivities: "0", unsupportedEvents: "0" };
}

const releasedRevision = ":released";

/**
 * The view that has the timeline of a session read again while another process holds it ({@link elsewhereRefreshView}),
 * given the one of the previous rendering: `journalLength` is null once no other process holds the session. The timeline
 * is read once more when the other process lets it go, since its last records can come with the release, after the last
 * length the window was told. Null for a session no other process held while it was shown.
 */
export function elsewhereFollowView(sessionId: string, journalLength: string | null, previous: SessionDisplayView | null): SessionDisplayView | null {
  const shown = previous?.sessionId === sessionId ? previous : null;
  if (journalLength !== null) {
    const view = elsewhereRefreshView(sessionId, journalLength);
    return shown?.revision === view.revision ? shown : view;
  }
  if (!shown) return null;
  return shown.revision.endsWith(releasedRevision) ? shown : { ...shown, revision: shown.revision + releasedRevision };
}

/** The read-only part of Storage that lists the tabs kept for every space. */
export type TabStorage = Pick<Storage, "length" | "key" | "getItem">;

/**
 * The sessions the window has a tab of: those of the space it shows (`current`, from memory, newer than what is kept),
 * and those kept for every other space (`currentKey` is the key of the shown space, left out). A storage that cannot be
 * read gives the shown space alone.
 */
export function openSessionIds(current: readonly SessionTab[], storage: TabStorage | null, currentKey: string | null): string[] {
  const ids = new Set(current.map(tab => tab.sessionId));
  if (storage) {
    try {
      for (let index = 0; index < storage.length; index++) {
        const key = storage.key(index);
        if (key === null || key === currentKey || key !== sessionTabsKey && !key.startsWith(`${sessionTabsKey}.`)) continue;
        const kept = restoreSessionTabs(() => storage.getItem(key));
        for (const tab of kept?.open ?? []) ids.add(tab.sessionId);
      }
    } catch { /* The other spaces are told with the next reading. */ }
  }

  return [...ids].filter(id => id.length > 0 && id.length <= 256 && id === id.trim()).sort();
}

/**
 * Tells the host the sessions the window has a tab of, when they change: one call at a time, the newest set next, a set
 * already told not again. A call that fails is told again with the next change.
 */
export function createOpenSessionsPublisher(send: (epoch: string, sessionIds: readonly string[]) => Promise<boolean>) {
  let told: string | null = null;
  let wanted: { epoch: string; ids: readonly string[]; signature: string } | null = null;
  let running = false;
  async function drain() {
    running = true;
    try {
      while (wanted && wanted.signature !== told) {
        const next = wanted;
        let accepted = false;
        try { accepted = await send(next.epoch, next.ids); } catch { accepted = false; }
        if (accepted) told = next.signature;
        else { if (wanted === next) wanted = null; told = null; }
      }
    } finally { running = false; }
  }

  return {
    /** Tells the host this set, unless it is the one it was told last. */
    publish(epoch: string, sessionIds: readonly string[]): Promise<void> {
      const signature = `${epoch}\n${sessionIds.join("\n")}`;
      wanted = { epoch, ids: sessionIds, signature };
      return running ? Promise.resolve() : drain();
    },
    /** Forgets what the host was told: it forgot it (the page lost its watch), and the next set is told again. */
    forget() { told = null; },
  };
}
