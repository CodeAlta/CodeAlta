import { NeoRpcClient, type NeoRpcCallOptions } from "@neoastra/client";

/**
 * How the page calls its own host. The host runs on this computer, and a message to it is never lost while the
 * connection lives (NeoAstra reports a closed connection itself): a call that has not answered yet is slow, not
 * failed. So no call to the host is ever given up on a timer. A call ends with its answer, with its `AbortSignal`
 * (its owner went away), or with the end of the connection; a slow call is logged, to the console and to the log of
 * the host, while it waits and once it answers. A call a limit of the host refused before running it is sent again.
 */

/** How long a call may wait before it is logged as slow: long enough that a busy computer is not a false alarm. */
export const slowCallAfter = 10_000;

export type CallReport = (level: "warning" | "information", message: string) => void;

export type SlowCallWatch = Readonly<{
  /** A call starts: the returned function says it ended, however it ended. */
  start(name: string): () => void;
  /** The calls that wait longer than {@link slowCallAfter} now. */
  slow(): number;
  /** A limit of the host refused a call before running it: it is sent again. */
  refused?(name: string): void;
}>;

type Timers = Readonly<{
  now(): number;
  setTimeout(callback: () => void, milliseconds: number): unknown;
  clearTimeout(handle: unknown): void;
}>;

const realTimers: Timers = { now: () => Date.now(), setTimeout: (callback, milliseconds) => setTimeout(callback, milliseconds), clearTimeout: handle => clearTimeout(handle as ReturnType<typeof setTimeout>) };

/** The calls the watch never reports: the report itself, which would report on its own slowness. */
const unreported: ReadonlySet<string> = new Set(["boot.report"]);

/** Watches the calls of the page: a call that waits longer than {@link slowCallAfter} is reported once, and once more when it ends. */
export function createSlowCallWatch(report: CallReport, timers: Timers = realTimers, after = slowCallAfter): SlowCallWatch {
  let slow = 0;
  const safeReport: CallReport = (level, message) => { try { report(level, message); } catch { /* A report never breaks a call. */ } };
  return {
    start(name) {
      if (unreported.has(name)) return () => {};
      const started = timers.now();
      let flagged = false;
      let ended = false;
      const handle = timers.setTimeout(() => {
        if (ended) return;
        flagged = true;
        slow++;
        safeReport("warning", `${name} has not answered for ${Math.round((timers.now() - started) / 1000)} s; the call goes on.`);
      }, after);
      return () => {
        if (ended) return;
        ended = true;
        timers.clearTimeout(handle);
        if (!flagged) return;
        slow--;
        safeReport("information", `${name} answered after ${((timers.now() - started) / 1000).toFixed(1)} s.`);
      };
    },
    slow: () => slow,
    refused(name) {
      if (!unreported.has(name)) safeReport("warning", `${name} was refused by a limit of the host before it ran; it is sent again.`);
    },
  };
}

/** The first and the longest wait before a refused call is sent again. */
export const refusedRetryFirst = 50, refusedRetryLongest = 1_000;

/**
 * Whether the host refused a call before running it. NeoAstra answers `too_many_requests` to an invocation or a
 * subscription it did not admit (its rate, its concurrency, the concurrency of the command): the handler never ran, so
 * the same call can be sent again without doing anything twice. A channel is refused only after its handler ran, so a
 * channel is never sent again: its limit is set high enough instead.
 */
export function refusedBeforeRunning(error: unknown): boolean {
  return typeof error === "object" && error !== null && (error as { code?: unknown }).code === "too_many_requests";
}

/** Sends a call until the host admits it, its signal aborts it, or the connection ends. */
export async function sendUntilAdmitted<T>(send: () => Promise<T>, signal: AbortSignal | undefined, refused: () => void,
  wait: (milliseconds: number, signal?: AbortSignal) => Promise<void> = delay): Promise<T> {
  let pause = refusedRetryFirst;
  let told = false;
  for (;;) {
    try { return await send(); }
    catch (error) {
      if (!refusedBeforeRunning(error) || signal?.aborted) throw error;
      if (!told) { told = true; refused(); }
      await wait(pause, signal);
      pause = Math.min(pause * 2, refusedRetryLongest);
    }
  }
}

function delay(milliseconds: number, signal?: AbortSignal): Promise<void> {
  return new Promise((resolve, reject) => {
    if (signal?.aborted) { reject(signal.reason ?? new DOMException("The operation was aborted.", "AbortError")); return; }
    const timer = setTimeout(() => { signal?.removeEventListener("abort", aborted); resolve(); }, milliseconds);
    const aborted = () => { clearTimeout(timer); reject(signal?.reason ?? new DOMException("The operation was aborted.", "AbortError")); };
    signal?.addEventListener("abort", aborted, { once: true });
  });
}

/** The options of a call to the local host: its signal and its contract, never a timeout. */
export function withoutTimeout(options: NeoRpcCallOptions | undefined): NeoRpcCallOptions | undefined {
  if (!options || options.timeoutMilliseconds === undefined) return options;
  const { timeoutMilliseconds: _ignored, ...rest } = options;
  return rest;
}

const installed = Symbol.for("codealta.localCalls");

/**
 * Applies the policy to every call of the page, the generated client of the host and the clients of canvases alike: they
 * all go through {@link NeoRpcClient}. Installed once, before the first call; installing again changes nothing.
 */
export function installLocalCallPolicy(watch: SlowCallWatch, prototype: object = NeoRpcClient.prototype): void {
  const target = prototype as Record<PropertyKey, unknown> & {
    invoke(command: string, args: unknown, options?: NeoRpcCallOptions): Promise<unknown>;
    invokeChannel(command: string, args: unknown, options?: NeoRpcCallOptions): Promise<unknown>;
    subscribe(event: string, handler: (value: unknown) => void, options?: NeoRpcCallOptions): Promise<unknown>;
  };
  if (target[installed]) return;
  target[installed] = true;
  const invoke = target.invoke;
  const invokeChannel = target.invokeChannel;
  const subscribe = target.subscribe;
  const refused = (name: string) => () => watch.refused?.(name);
  target.invoke = function (this: unknown, command, args, options) {
    const end = watch.start(command);
    const sent = withoutTimeout(options);
    const call = sendUntilAdmitted(() => invoke.call(this, command, args, sent), options?.signal, refused(command));
    void call.then(end, end);
    return call;
  };
  // A channel and a subscription are watched until they open: what they bring afterwards comes when it comes.
  target.invokeChannel = function (this: unknown, command, args, options) {
    const end = watch.start(command);
    const call = invokeChannel.call(this, command, args, withoutTimeout(options));
    void call.then(end, end);
    return call;
  };
  target.subscribe = function (this: unknown, event, handler, options) {
    const end = watch.start(`${event} (subscription)`);
    const sent = withoutTimeout(options);
    const call = sendUntilAdmitted(() => subscribe.call(this, event, handler, sent), options?.signal, refused(`${event} (subscription)`));
    void call.then(end, end);
    return call;
  };
}
