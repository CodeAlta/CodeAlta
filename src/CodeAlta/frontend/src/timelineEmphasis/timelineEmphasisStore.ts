import { persistTimelineEmphasis, restoreTimelineEmphasis, type TimelineEmphasis } from "./timelineEmphasis";

/** One window's preference: storage is read once, and a failed write leaves the in-memory setting in force. */
export function createTimelineEmphasisStore(read: () => string | null, write: (value: string) => void) {
  let value = restoreTimelineEmphasis(read);
  let issue: "unsaved" | undefined;
  const listeners = new Set<() => void>();
  return {
    getSnapshot: () => value,
    getIssueSnapshot: () => issue,
    subscribe: (listener: () => void) => { listeners.add(listener); return () => { listeners.delete(listener); }; },
    update: (next: TimelineEmphasis) => {
      if (next === value) return;
      value = next;
      issue = persistTimelineEmphasis(write, next) ? undefined : "unsaved";
      for (const listener of listeners) listener();
    },
  };
}

/** The instance owned by the window, shared by its settings and timelines. */
export type TimelineEmphasisStore = ReturnType<typeof createTimelineEmphasisStore>;
