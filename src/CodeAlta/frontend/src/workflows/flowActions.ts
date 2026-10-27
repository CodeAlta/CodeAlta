import { createContext } from "react";
import type { GraphState } from "./runGraph";
import type { RunPhase } from "./runFlow";

/**
 * What the nodes of a graph can do: open or close the group of the sessions of a step, and run the script again from a step
 * (null where the session of the run cannot be asked). Given by the block or the tab around the graph.
 */
export type FlowActions = Readonly<{ toggle(stepId: string): void; rerunFrom: ((key: string) => void) | null; busy: boolean }>;
export const FlowActionsContext = createContext<FlowActions | null>(null);

/** The label of the state of a step or a session: an English key of the translations. */
export const graphStateLabel = (state: GraphState) => state === "pending" ? "Pending" : state === "running" ? "Running" : state === "waiting" ? "Waiting"
  : state === "completed" ? "Done" : state === "failed" ? "Failed" : state === "interrupted" ? "Interrupted" : "Stopped";

/** The label of the phase of a run, as its pill says it: an English key of the translations. */
export const runPhaseLabel = (phase: RunPhase) => phase === "running" ? "Running" : phase === "waiting" ? "Waiting for you" : phase === "sleeping" ? "Sleeping"
  : phase === "completed" ? "Completed" : phase === "failed" ? "Failed" : phase === "interrupted" ? "Interrupted" : "Stopped";
