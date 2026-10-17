import { createContext, useContext, useSyncExternalStore } from "react";
import type { LocalSearchHub, LocalSearchState } from "./localSearchHub";

/** The local search of the host, for the pages that change what it keeps out; null where the window has no host of its own. */
export const LocalSearchContext = createContext<LocalSearchHub | null>(null);

const none: LocalSearchState = { loaded: false, available: false, status: null };
const subscribeNothing = () => () => { };

/** The hub of the local search and what it says now. */
export function useLocalSearch(): Readonly<{ hub: LocalSearchHub | null; state: LocalSearchState }> {
  const hub = useContext(LocalSearchContext);
  const snapshot = hub ? hub.getSnapshot : () => none;
  // The page is also rendered on a server in tests: it is the same snapshot there.
  const state = useSyncExternalStore(hub?.subscribe ?? subscribeNothing, snapshot, snapshot);
  return { hub, state };
}
