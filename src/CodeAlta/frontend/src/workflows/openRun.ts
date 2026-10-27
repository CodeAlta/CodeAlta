import { createContext } from "react";

/** Opens the tab of a run of a script: its id, and its title for the strip until the tab has read it. */
export type OpenRun = (run: Readonly<{ runId: string; title: string | null }>) => void;

/** Where Open run on the card of a run, and a run of the Workflows tab, open the tab of the run; null where there is no tab to open. */
export const OpenRunContext = createContext<OpenRun | null>(null);
