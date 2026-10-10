import { useCallback, useEffect, useReducer, useRef, useState } from "react";
import { headerOf } from "./queryStore";
import { useStatistics } from "./runtime";
import type { StatisticsRequest } from "./types";

/** What a block knows of its question. */
export type QueryState<T> = Readonly<{
  /** The last result, which may be the one of the question before this one (see `placeholder`) or a stale one. */
  data: T | undefined;
  /** True while the question is being asked and there is nothing at all to show. */
  loading: boolean;
  /** True while a question is in flight, with or without something to show. */
  refreshing: boolean;
  /** The result shown answers an earlier question: the filters or the period moved and the new answer is not there yet. */
  placeholder: boolean;
  error: Error | null;
  retry: () => void;
}>;

/** The key a question is held under. */
export const queryKey = (method: string, request: StatisticsRequest | null, ...rest: readonly unknown[]): string => JSON.stringify([method, request, ...rest]);

/**
 * Asks a question of the plugin and keeps its result in the store of the canvas.
 *
 * It asks only while `enabled` and the canvas is shown, a moment after the key settles (so a change of filter that is
 * undone at once, or a React effect that runs twice, asks nothing), cancels the question in flight when the key changes or
 * the block goes away, and asks again when the plugin announced a change in the days of the result, also when the change came
 * while the question was in flight (its answer is then kept as stale, and asked again at once). A result is kept after
 * the block is gone, so coming back to a page shows what it had. `reportsPeriod` marks the question whose period the bar shows
 * (the length of `all` and of `auto`).
 */
export function useStatisticsQuery<T>(key: string, fetch: (signal: AbortSignal) => Promise<T>, enabled = true, reportsPeriod = false): QueryState<T> {
  const { store, visible, reportQuery } = useStatistics();
  const [, rerender] = useReducer((count: number) => count + 1, 0);
  const [attempt, setAttempt] = useState(0);
  const [failure, setFailure] = useState<{ key: string; attempt: number; error: Error } | null>(null);
  const [asking, setAsking] = useState<string | null>(null);
  // Counts the answers that came back stale: a change was announced while the question was in flight, so it is asked again.
  const [round, setRound] = useState(0);
  const latest = useRef(fetch);
  latest.current = fetch;
  const previous = useRef<T | undefined>(undefined);

  useEffect(() => store.subscribe(rerender), [store]);

  const entry = store.get<T>(key);
  const needs = enabled && visible && (entry === undefined || entry.stale);
  const failed = failure !== null && failure.key === key && failure.attempt === attempt;

  useEffect(() => {
    if (!needs || failed) return;
    const controller = new AbortController();
    const epoch = store.epoch;
    let finished = false;
    const timer = setTimeout(() => {
      setAsking(key);
      latest.current(controller.signal).then(data => {
        finished = true;
        if (controller.signal.aborted) return;
        store.set(key, data, epoch);
        setFailure(null);
        setAsking(current => current === key ? null : current);
        // The store keeps the answer stale when the days it holds changed since the question began. Nothing else would ask again:
        // `needs` was true and stays true, and the store tells nobody about an entry that is already stale.
        if (store.get(key)?.stale) setRound(count => count + 1);
        rerender();
      }, (error: unknown) => {
        finished = true;
        if (controller.signal.aborted || (error instanceof Error && error.name === "AbortError")) return;
        setFailure({ key, attempt, error: error instanceof Error ? error : new Error(String(error)) });
        setAsking(current => current === key ? null : current);
      });
    }, 0);
    return () => { clearTimeout(timer); if (!finished) controller.abort(); setAsking(current => current === key ? null : current); };
  }, [needs, failed, key, attempt, round, store]);

  const data = entry?.data;
  if (data !== undefined) previous.current = data;
  const shown = data ?? previous.current;
  const shownHeader = reportsPeriod && enabled ? headerOf(shown) : undefined;
  useEffect(() => { if (shownHeader) reportQuery(shownHeader); }, [shownHeader, reportQuery]);
  const retry = useCallback(() => { setFailure(null); setAttempt(count => count + 1); }, []);
  return {
    data: shown, loading: shown === undefined && !(failed && failure), refreshing: asking === key || (needs && !failed), placeholder: data === undefined && shown !== undefined,
    error: failed && failure ? failure.error : null, retry,
  };
}
