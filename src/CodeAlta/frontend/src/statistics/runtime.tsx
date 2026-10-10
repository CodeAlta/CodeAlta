import { createContext, useCallback, useContext, useEffect, useMemo, useReducer, useRef, useState, type ReactNode } from "react";
import { useText } from "./text";
import type { StatisticsApi, StatisticsContext, StatisticsEvent } from "./api";
import { createFormatter, type Formatter } from "./format";
import { providerNamer } from "./labels";
import { decodeFrame, encodeFrame, firstDayOfWeek, frameReducer, initialFrame, localToday, requestOf, type Frame, type FrameAction } from "./frame";
import { QueryStore, joinRanges, type DayRange } from "./queryStore";
import type { HistoryChoice, QueryHeader, StatisticsRequest, StatisticsStatus } from "./types";

// The state a canvas shares between its bar, its history bar and its pages: the frame, the state of the reading of the history,
// the results it has read, and the controls. One provider per canvas; a page reads it with `useStatistics`.

/** How long changes announced by the plugin are gathered before the pages that show those days ask again. */
export const dataChangeDelayMs = 1000;

/** What the history controls last did. */
export type HistoryControls = Readonly<{
  /** True while one of the controls waits for the plugin. */
  busy: boolean;
  /** Why the last control failed; null otherwise. */
  error: string | null;
  choose: (choice: HistoryChoice) => Promise<void>;
  pause: () => Promise<void>;
  resume: () => Promise<void>;
  stopHere: () => Promise<void>;
  forgetDeleted: () => Promise<number | null>;
  /** Null when the binding of the plugin cannot reset the statistics. */
  reset: (() => Promise<void>) | null;
  clearError: () => void;
}>;

/** What every part of the canvas reads. */
export type StatisticsRuntime = Readonly<{
  api: StatisticsApi;
  context: StatisticsContext;
  store: QueryStore;
  frame: Frame;
  /** The frame the canvas opened with: what "Reset" goes back to. */
  openFrame: Frame;
  dispatch: (action: FrameAction) => void;
  /** Today, `yyyy-MM-dd`. */
  today: string;
  /** The first day of the week, 0 for Sunday. */
  weekStart: number;
  fmt: Formatter;
  /** The state of the reading of the history; null until the plugin has answered. */
  status: StatisticsStatus | null;
  /** True while the canvas is shown: hidden, it asks nothing. */
  visible: boolean;
  history: HistoryControls;
  /** The request of a page: the frame, plus what the page adds. */
  request: (extra?: Partial<StatisticsRequest>) => StatisticsRequest;
  /** The name of a provider from its key, as the window names it; the key when the window does not know it. */
  providerName: (key: string) => string;
  /** The color index of a series name, the same everywhere in this canvas (first seen, first served). */
  colorIndex: (key: string) => number;
  /** Puts the frame back as the canvas opened. */
  resetFrame: () => void;
  /** What the question of the page that is shown last said about its period: the days `all` and `auto` came to, the filters it ignored, how much was read. */
  header: QueryHeader | null;
  /** The length of that period in days; null before the first answer. */
  periodDays: number | null;
  reportQuery: (header: QueryHeader) => void;
}>;

const RuntimeContext = createContext<StatisticsRuntime | null>(null);

/** The runtime of the canvas around the caller. Throws when there is no `StatisticsProvider`. */
export function useStatistics(): StatisticsRuntime {
  const value = useContext(RuntimeContext);
  if (!value) throw new Error("useStatistics must be used inside a StatisticsProvider.");
  return value;
}

/** A context that only names a runtime; for tests that render one block. */
export const StatisticsRuntimeContext = RuntimeContext;

const frameKey = (instanceId: string) => `codealta.statistics.frame.v1:${instanceId}`;

function browserStorage(): Pick<Storage, "getItem" | "setItem" | "removeItem"> | null {
  try { return typeof localStorage === "undefined" ? null : localStorage; } catch { return null; }
}

const messageOf = (error: unknown) => error instanceof Error ? error.message : String(error);
const isAbort = (error: unknown) => error instanceof Error && error.name === "AbortError";

/** Provides the runtime of one canvas to its children. */
export function StatisticsProvider({ api, context, children }: Readonly<{ api: StatisticsApi; context: StatisticsContext; children: ReactNode }>) {
  const { t, locale } = useText();
  const storage = context.storage === undefined ? browserStorage() : context.storage;
  const storageRef = useRef(storage);
  storageRef.current = storage;
  const [store] = useState(() => new QueryStore());
  const colors = useRef(new Map<string, number>());
  const spaceName = context.spaces?.find(space => space.id === context.spaceId)?.name ?? null;
  const open = useMemo(() => initialFrame({ spaceId: context.spaceId, spaceName, projectId: context.projectId, projectName: context.projectName }),
    [context.spaceId, spaceName, context.projectId, context.projectName]);
  const openRef = useRef(open);
  openRef.current = open;
  const [frame, dispatch] = useReducer(frameReducer, undefined, () => decodeFrame(storage?.getItem(frameKey(context.instanceId)), open));
  const [today, setToday] = useState(() => context.today ?? localToday());
  const [header, setHeader] = useState<QueryHeader | null>(null);
  const reportQuery = useCallback((next: QueryHeader) => setHeader(known => known && JSON.stringify(known) === JSON.stringify(next) ? known : next), []);
  const periodDays = header ? Math.max(1, Math.round((Date.parse(`${header.to}T00:00:00Z`) - Date.parse(`${header.from}T00:00:00Z`)) / 86_400_000) + 1) : null;
  const [status, setStatus] = useState<StatisticsStatus | null>(null);
  const [busy, setBusy] = useState(false);
  const [controlError, setControlError] = useState<string | null>(null);
  const visible = context.visible;
  const visibleRef = useRef(visible);
  visibleRef.current = visible;

  // The frame survives a reload: one short query string per canvas instance.
  useEffect(() => {
    try { storageRef.current?.setItem(frameKey(context.instanceId), encodeFrame(frame)); } catch { /* a full or refused storage keeps the frame in memory only */ }
  }, [frame, context.instanceId]);

  // Today moves on while the canvas is hidden overnight: it is read again when the canvas is shown.
  useEffect(() => {
    if (visible && !context.today) setToday(localToday());
  }, [visible, context.today]);

  // The state of the history, and what the plugin announces. What arrives while the canvas is hidden is kept and applied when it is shown.
  const latestStatus = useRef<StatisticsStatus | null>(null);
  const pending = useRef<{ range: DayRange | undefined; timer: ReturnType<typeof setTimeout> | null }>({ range: undefined, timer: null });
  useEffect(() => {
    let alive = true;
    const controller = new AbortController();
    const pendingState = pending.current;
    const flush = () => {
      pendingState.timer = null;
      const range = pendingState.range;
      pendingState.range = undefined;
      if (range !== undefined) store.invalidate(range);
    };
    const listen = (event: StatisticsEvent) => {
      if (!alive) return;
      if (event.kind === "status") {
        latestStatus.current = event.status;
        if (visibleRef.current) setStatus(event.status);
        return;
      }
      pendingState.range = joinRanges(pendingState.range, { from: event.change.fromDay, to: event.change.toDay });
      pendingState.timer ??= setTimeout(flush, dataChangeDelayMs);
    };
    const off = api.subscribe(listen);
    api.status(controller.signal).then(next => {
      if (!alive) return;
      latestStatus.current = next;
      setStatus(next);
    }, error => { if (alive && !isAbort(error)) setControlError(messageOf(error)); });
    return () => {
      alive = false;
      controller.abort();
      off();
      if (pendingState.timer !== null) clearTimeout(pendingState.timer);
      pendingState.timer = null;
      pendingState.range = undefined;
    };
  }, [api, store]);

  // Shown again: the status the canvas missed.
  useEffect(() => {
    if (visible && latestStatus.current) setStatus(latestStatus.current);
  }, [visible]);

  // The weeks are those of the plugin when it says where they start, so that a chart and `alta statistics` cut the same weeks.
  const weekStart = useMemo(() => context.weekStart ?? firstDayOfWeek(locale), [context.weekStart, locale]);
  const fmt = useMemo(() => createFormatter(locale, { credits: amount => t("{amount} AI credits", { amount }), none: "–" }), [locale]); // eslint-disable-line react-hooks/exhaustive-deps
  const request = useCallback((extra?: Partial<StatisticsRequest>) => requestOf(frame, weekStart, extra), [frame, weekStart]);
  const colorIndex = useCallback((key: string) => {
    let index = colors.current.get(key);
    if (index === undefined) { index = colors.current.size; colors.current.set(key, index); }
    return index;
  }, []);
  const resetFrame = useCallback(() => dispatch({ type: "reset", frame: openRef.current }), []);
  const providerName = useMemo(() => providerNamer(context.providers), [context.providers]);

  const history = useMemo<HistoryControls>(() => {
    const run = async <T,>(action: () => Promise<T>): Promise<T | null> => {
      setBusy(true);
      setControlError(null);
      try { return await action(); } catch (error) { setControlError(messageOf(error)); return null; } finally { setBusy(false); }
    };
    const apply = async (action: () => Promise<StatisticsStatus>) => {
      const next = await run(action);
      if (next) { latestStatus.current = next; setStatus(next); }
    };
    return {
      busy, error: controlError,
      choose: choice => apply(() => api.chooseHistory(choice)),
      pause: () => apply(() => api.pause()),
      resume: () => apply(() => api.resume()),
      stopHere: () => apply(() => api.stopHere()),
      forgetDeleted: async () => { const count = await run(() => api.forgetDeleted()); if (count !== null) store.invalidate(null); return count; },
      reset: api.resetStatistics ? () => apply(async () => { const next = await api.resetStatistics!(); store.clear(); return next; }) : null,
      clearError: () => setControlError(null),
    };
  }, [api, busy, controlError, store]);

  const value = useMemo<StatisticsRuntime>(() => ({
    api, context, store, frame, openFrame: open, dispatch, today, weekStart, fmt, status, visible, history, request, providerName, colorIndex, resetFrame, header, periodDays, reportQuery,
  }), [api, context, store, frame, open, today, weekStart, fmt, status, visible, history, request, providerName, colorIndex, resetFrame, header, periodDays, reportQuery]);
  return <RuntimeContext.Provider value={value}>{children}</RuntimeContext.Provider>;
}
