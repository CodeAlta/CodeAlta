/**
 * A read of the host that several readers of the window share: every pane polls what it shows, and many of them read
 * the same thing (the receipts of the window, the providers, the permissions of one session). A read with the same
 * key that is already waiting is joined, and an answer younger than `freshMilliseconds` is given again, so N panes
 * cost the host one read where they cost N. Each caller keeps its own `AbortSignal`: aborting stops its wait, not
 * the read others wait for. A failed read is never given again, and `forget` makes the next read of a key a new one,
 * for what a change made here has just made out of date.
 */
export type SharedRead<TRequest, TResult> = ((request: TRequest, options?: Readonly<{ signal?: AbortSignal }>) => Promise<TResult>)
  & Readonly<{ forget: (request: TRequest) => void }>;

type Entry<TResult> = { promise: Promise<TResult>; settledAt: number | null };

export function createSharedRead<TRequest, TResult>(
  read: (request: TRequest, options: { signal: AbortSignal }) => Promise<TResult>,
  key: (request: TRequest) => string,
  freshMilliseconds: number,
  now: () => number = () => Date.now(),
): SharedRead<TRequest, TResult> {
  const entries = new Map<string, Entry<TResult>>();
  const start = (id: string, request: TRequest): Entry<TResult> => {
    const entry: Entry<TResult> = { promise: undefined!, settledAt: null };
    entry.promise = read(request, { signal: new AbortController().signal }).then(
      value => { entry.settledAt = now(); return value; },
      error => { if (entries.get(id) === entry) entries.delete(id); throw error; });
    entries.set(id, entry);
    return entry;
  };
  const shared = (request: TRequest, options?: Readonly<{ signal?: AbortSignal }>) => {
    const signal = options?.signal;
    if (signal?.aborted) return Promise.reject(signal.reason ?? new DOMException("The operation was aborted.", "AbortError"));
    const id = key(request);
    let entry = entries.get(id);
    if (!entry || (entry.settledAt !== null && now() - entry.settledAt >= freshMilliseconds)) entry = start(id, request);
    if (!signal) return entry.promise;
    const waited = entry.promise;
    return new Promise<TResult>((resolve, reject) => {
      const aborted = () => reject(signal.reason ?? new DOMException("The operation was aborted.", "AbortError"));
      signal.addEventListener("abort", aborted, { once: true });
      waited.then(value => { signal.removeEventListener("abort", aborted); resolve(value); },
        error => { signal.removeEventListener("abort", aborted); reject(error); });
    });
  };
  // The readers that already wait keep their read; the next one starts its own.
  return Object.assign(shared, { forget: (request: TRequest) => { entries.delete(key(request)); } });
}
