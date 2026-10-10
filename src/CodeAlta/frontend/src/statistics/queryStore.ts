import type { QueryHeader } from "./types";

// The results the canvas has read, by the question that asked for them. A page that is left keeps its last results here, so
// coming back shows them at once; a change the plugin announces marks the entries of the days it touched as stale, and the
// page that is shown asks for those again. One store belongs to one canvas: nothing here is shared between canvases.

/** What a question returned, and whether the numbers moved since. */
export type QueryEntry<T = unknown> = Readonly<{ data: T; stale: boolean }>;

/** The days a change touches, `yyyymmdd`; null for every day. */
export type DayRange = Readonly<{ from: number; to: number }> | null;

type Held = { data: unknown; stale: boolean; from: number; to: number; compareFrom: number; compareTo: number };

const dayNumber = (day: string | undefined): number => day ? Number(day.replaceAll("-", "")) : 0;

/** The header a result starts with, when it is one of the plugin's. */
export const headerOf = (data: unknown): QueryHeader | undefined => {
  if (typeof data !== "object" || data === null) return undefined;
  const query = (data as { query?: unknown }).query;
  return typeof query === "object" && query !== null && "from" in query ? query as QueryHeader : undefined;
};

/** Whether a range of days touches the period of a result, or the period it was compared with. */
function touches(held: Held, range: DayRange): boolean {
  if (range === null || held.from === 0) return true;
  return (held.from <= range.to && held.to >= range.from) || (held.compareFrom !== 0 && held.compareFrom <= range.to && held.compareTo >= range.from);
}

/** Holds results by key, least recently used out first. */
export class QueryStore {
  private readonly _entries = new Map<string, Held>();
  private readonly _listeners = new Set<() => void>();
  private readonly _log: { epoch: number; range: DayRange }[] = [];
  private _epoch = 0;

  /** Creates a store that keeps at most `limit` results. */
  constructor(private readonly _limit = 96) { }

  /** The number of results held. */
  get size(): number { return this._entries.size; }

  /** The number of changes announced so far: a read that began before a change that touches it is stale when it ends. */
  get epoch(): number { return this._epoch; }

  /** The result of a question, and whether it is stale; undefined when it was never read. Reading moves it to the end of the order. */
  get<T>(key: string): QueryEntry<T> | undefined {
    const held = this._entries.get(key);
    if (!held) return undefined;
    this._entries.delete(key);
    this._entries.set(key, held);
    return { data: held.data as T, stale: held.stale };
  }

  /** Keeps a result. `startedAt` is the epoch the read began at: a change announced since that touches the result leaves it stale. */
  set(key: string, data: unknown, startedAt: number = this._epoch): void {
    const header = headerOf(data);
    const held: Held = { data, stale: false, from: dayNumber(header?.from), to: dayNumber(header?.to), compareFrom: dayNumber(header?.compareFrom), compareTo: dayNumber(header?.compareTo) };
    held.stale = this._log.some(item => item.epoch > startedAt && touches(held, item.range));
    this._entries.delete(key);
    this._entries.set(key, held);
    while (this._entries.size > this._limit) this._entries.delete(this._entries.keys().next().value as string);
  }

  /** Marks the results that touch these days stale, and tells the listeners. */
  invalidate(range: DayRange): number {
    this._epoch++;
    this._log.push({ epoch: this._epoch, range });
    if (this._log.length > 64) this._log.shift();
    let marked = 0;
    for (const held of this._entries.values()) {
      if (!held.stale && touches(held, range)) { held.stale = true; marked++; }
    }
    if (marked > 0) for (const listener of [...this._listeners]) listener();
    return marked;
  }

  /** Forgets everything. */
  clear(): void {
    this._entries.clear();
    this._log.length = 0;
    this._epoch++;
    for (const listener of [...this._listeners]) listener();
  }

  /** Listens for results that became stale or were cleared. Returns the function that stops listening. */
  subscribe(listener: () => void): () => void {
    this._listeners.add(listener);
    return () => { this._listeners.delete(listener); };
  }
}

/** Joins what several changes touched into the one range that covers them. */
export function joinRanges(left: DayRange | undefined, right: DayRange): DayRange {
  if (left === undefined) return right;
  if (left === null || right === null) return null;
  return { from: Math.min(left.from, right.from), to: Math.max(left.to, right.to) };
}
