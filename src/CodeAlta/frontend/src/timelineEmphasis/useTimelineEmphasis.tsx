import { createContext, useContext, useState, useSyncExternalStore, type ReactNode } from "react";
import { defaultTimelineEmphasis, timelineEmphasisKey, type TimelineEmphasis } from "./timelineEmphasis";
import { createTimelineEmphasisStore, type TimelineEmphasisStore } from "./timelineEmphasisStore";

/** The window-owned preference, not a process-wide mutable store. */
export const TimelineEmphasisContext = createContext<TimelineEmphasisStore | null>(null);

/** Keeps the setting for the lifetime of the window, even when storage is unavailable or all consumers unmount. */
export function TimelineEmphasisProvider({ children }: { children: ReactNode }) {
  const [store] = useState(() => createTimelineEmphasisStore(() => localStorage.getItem(timelineEmphasisKey), value => localStorage.setItem(timelineEmphasisKey, value)));
  return <TimelineEmphasisContext.Provider value={store}>{children}</TimelineEmphasisContext.Provider>;
}

const defaults = () => defaultTimelineEmphasis;
const noIssue = () => undefined;
const noSubscription = () => () => {};
const noUpdate = () => {};

/** Follows the window's setting; standalone previews without a provider show the defaults. */
export function useTimelineEmphasis(): readonly [TimelineEmphasis, (value: TimelineEmphasis) => void] {
  const store = useContext(TimelineEmphasisContext);
  const value = useSyncExternalStore(store?.subscribe ?? noSubscription, store?.getSnapshot ?? defaults, store?.getSnapshot ?? defaults);
  return [value, store?.update ?? noUpdate];
}

/** Follows the window's save diagnostic without making timeline consumers subscribe to it. */
export function useTimelineEmphasisIssue() {
  const store = useContext(TimelineEmphasisContext);
  return useSyncExternalStore(store?.subscribe ?? noSubscription, store?.getIssueSnapshot ?? noIssue, store?.getIssueSnapshot ?? noIssue);
}
