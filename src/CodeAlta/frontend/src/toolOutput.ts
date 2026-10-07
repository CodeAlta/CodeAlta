import { useCallback, useSyncExternalStore } from "react";

type ObserveRequest = { expectedEpoch: string; sessionId: string; activityId: string };
type OutputItem = { status: string; text: string; start: string; total: string; isReset: boolean; isComplete: boolean };
type Open = (request: ObserveRequest, options: { signal: AbortSignal }) => Promise<AsyncIterable<OutputItem>>;

/**
 * What a running tool call wrote, as the page holds it. `text` is the newest part and `offset` its position in
 * everything the call wrote (`total`, in UTF-16 units); `lines` counts the lines of `text` and `last` is its last
 * line that says something. `ended` is set when the host has nothing more to send: the call ended, or its
 * output is the one of its record.
 */
export type ToolOutputState = Readonly<{ text: string; offset: number; total: number; lines: number; last: string; ended: boolean }>;

/** The live output of the tool calls of one session. A call is observed while something subscribes to it. */
export type ToolOutputs = Readonly<{
  subscribe(activityId: string, listener: () => void): () => void;
  get(activityId: string): ToolOutputState | undefined;
}>;

const empty: ToolOutputState = Object.freeze({ text: "", offset: 0, total: 0, lines: 0, last: "", ended: false });

function lastLine(text: string): string {
  let end = text.length;
  while (end > 0) {
    const start = text.lastIndexOf("\n", end - 1) + 1;
    const line = text.slice(start, end).trim();
    if (line) return line.length > 160 ? line.slice(0, 159) + "…" : line;
    end = start - 1;
  }
  return "";
}

function countNewlines(text: string): number {
  let count = 0;
  for (let index = text.indexOf("\n"); index >= 0; index = text.indexOf("\n", index + 1)) count++;
  return count;
}

/**
 * Follows the output of running tool calls through `toolCalls.observe`. One channel is open per call that
 * something on the page shows; it is closed with the last subscriber. At most `maximumCharacters` of a call
 * are kept: the newest ones.
 */
export function createToolOutputStore(open: Open, limits: { maximumCharacters?: number } = {}) {
  const maximumCharacters = limits.maximumCharacters ?? 2 * 1024 * 1024;
  type Call = { state: ToolOutputState; listeners: Set<() => void>; controller: AbortController };
  const calls = new Map<string, Call>();

  function publish(call: Call, state: ToolOutputState) {
    call.state = Object.freeze(state);
    for (const listener of [...call.listeners]) listener();
  }
  function apply(call: Call, item: OutputItem) {
    if (item.status !== "ok") { publish(call, { ...call.state, ended: true }); return; }
    const start = Number(item.start), total = Number(item.total);
    const previous = call.state;
    // An item that does not continue what is held replaces it: the first one, or text in between was not kept.
    const continues = !item.isReset && start === previous.offset + previous.text.length;
    let text = continues ? previous.text + item.text : item.text;
    let offset = continues ? previous.offset : Number.isFinite(start) ? start : 0;
    let lines = continues ? previous.lines + countNewlines(item.text) : countNewlines(text);
    if (text.length > maximumCharacters) {
      const dropped = text.length - maximumCharacters;
      lines -= countNewlines(text.slice(0, dropped));
      text = text.slice(dropped);
      offset += dropped;
    }
    publish(call, { text, offset, total: Number.isFinite(total) ? total : offset + text.length, lines,
      last: item.text.trim() || !continues ? lastLine(text) : previous.last, ended: item.isComplete });
  }
  async function follow(call: Call, request: ObserveRequest) {
    let iterator: AsyncIterator<OutputItem> | undefined;
    try {
      const stream = await open(request, { signal: call.controller.signal });
      iterator = stream[Symbol.asyncIterator]();
      while (!call.controller.signal.aborted) {
        const next = await iterator.next();
        if (next.done) break;
        apply(call, next.value);
        if (next.value.isComplete || next.value.status !== "ok") break;
      }
    } catch { /* A channel that fails ends the live output: the record of the call has the rest. */ }
    finally {
      try { await iterator?.return?.(); } catch { /* The channel is already gone. */ }
      if (!call.controller.signal.aborted && !call.state.ended) publish(call, { ...call.state, ended: true });
    }
  }
  return {
    /** The live output of one session under one host epoch. */
    session(expectedEpoch: string, sessionId: string): ToolOutputs {
      const name = (activityId: string) => JSON.stringify([expectedEpoch, sessionId, activityId]);
      return {
        subscribe(activityId, listener) {
          const key = name(activityId);
          let call = calls.get(key);
          if (!call) {
            call = { state: empty, listeners: new Set(), controller: new AbortController() };
            calls.set(key, call);
            void follow(call, { expectedEpoch, sessionId, activityId });
          }
          const observed = call;
          observed.listeners.add(listener);
          return () => {
            observed.listeners.delete(listener);
            if (observed.listeners.size) return;
            // The page may subscribe again at once (the details take over from the tile, a component mounts
            // twice): the channel is closed at the next task, if nothing wants it by then.
            setTimeout(() => {
              if (observed.listeners.size || calls.get(key) !== observed) return;
              calls.delete(key);
              observed.controller.abort();
            }, 0);
          };
        },
        get: activityId => calls.get(name(activityId))?.state,
      };
    },
    /** The calls being followed. */
    count: () => calls.size,
  };
}

/** What a running call wrote so far; undefined while it is not followed. It is followed only while `enabled`. */
export function useToolOutput(outputs: ToolOutputs | undefined, activityId: string | null | undefined, enabled: boolean): ToolOutputState | undefined {
  const active = enabled && outputs && activityId ? { outputs, activityId } : null;
  const subscribe = useCallback((listener: () => void) => active ? active.outputs.subscribe(active.activityId, listener) : () => { },
    [active?.outputs, active?.activityId]);
  const get = useCallback(() => active ? active.outputs.get(active.activityId) : undefined, [active?.outputs, active?.activityId]);
  return useSyncExternalStore(subscribe, get, () => undefined);
}
