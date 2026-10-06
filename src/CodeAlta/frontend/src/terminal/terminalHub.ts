// The page's link to the terminals of the application: which there are, and what those the page shows write.
import type { TerminalItem, terminals } from "#neoastra";

/** What shows a terminal on the page. The hub gives it what the terminal wrote; it tells the hub what it took in. */
export type TerminalSink = Readonly<{
  /** What follows starts from an empty screen of this size, after the sequences in `modes`. */
  start(columns: number, rows: number, modes: string, replayed: boolean): void;
  /**
   * What the program wrote. `columns` and `rows` are 0 unless the size changes with this piece; `replayed`
   * says that everything written before the page asked for the terminal has now been given. `done` is
   * called once the piece has been taken in.
   */
  write(data: string, columns: number, rows: number, replayed: boolean, done: () => void): void;
}>;

export type TerminalsApi = Pick<typeof terminals, "watch" | "create" | "input" | "resize" | "rename" | "close" | "attach" | "detach" | "show" | "acknowledge" | "profiles">;
export type TerminalSystem = Readonly<{ platform: string; build: number; profiles: readonly Readonly<{ id: string; name: string }>[] }>;
type Timers = Readonly<{ set: (run: () => void, milliseconds: number) => unknown; clear: (timer: unknown) => void }>;

/** How long the page gathers what it took in before it tells the host: a few calls a second, whatever is written. */
export const acknowledgeMilliseconds = 60;
/** How long the page waits before it listens again after the host stopped telling. */
export const reconnectMilliseconds = 2000;
/** The most text one call types: more waits for the next call. */
export const inputChunk = 256 * 1024;

export type TerminalHub = ReturnType<typeof createTerminalHub>;

/**
 * Listens to the host for the page. One watch carries every terminal: the list each time a terminal changes,
 * and what the terminals shown on the page write. What is typed is sent in order, one call at a time.
 */
export function createTerminalHub(api: TerminalsApi, timers: Timers = { set: (run, milliseconds) => setTimeout(run, milliseconds), clear: timer => clearTimeout(timer as number) }) {
  let list: readonly TerminalItem[] = [];
  let system: TerminalSystem | null = null;
  let epoch: string | null = null;
  let feed: string | null = null;
  const listeners = new Set<() => void>();
  const revealers = new Set<(id: string) => void>();
  const sinks = new Map<string, TerminalSink>();
  const shown = new Set<string>();
  const taken = new Map<string, number>();
  const typed = new Map<string, string>();
  const typing = new Set<string>();
  const sizes = new Map<string, Readonly<{ columns: number; rows: number }>>();
  const sizing = new Set<string>();
  let acknowledging: unknown = null;

  const notify = () => { for (const listener of [...listeners]) listener(); };
  const quietly = (call: Promise<unknown>) => void call.catch(() => { /* The host is gone or the terminal is: the next list says which. */ });

  function acknowledge() {
    acknowledging = null;
    const current = feed, host = epoch;
    for (const [id, characters] of taken) {
      if (current && host && sinks.has(id)) quietly(api.acknowledge({ expectedEpoch: host, feed: current, id, characters }));
    }
    taken.clear();
  }

  function took(id: string, characters: number, from: string | null) {
    // What was given by a feed that is gone was never counted by the one there is now.
    if (from !== feed || !sinks.has(id)) return;
    taken.set(id, (taken.get(id) ?? 0) + characters);
    acknowledging ??= timers.set(acknowledge, acknowledgeMilliseconds);
  }

  async function type(id: string) {
    if (typing.has(id)) return;
    typing.add(id);
    try {
      for (let text = typed.get(id); text && epoch; text = typed.get(id)) {
        const piece = text.slice(0, inputChunk);
        if (piece.length === text.length) typed.delete(id); else typed.set(id, text.slice(piece.length));
        const reply = await api.input({ expectedEpoch: epoch, id, data: piece });
        // A terminal that takes nothing more has ended, or is far behind: what waits for it is dropped.
        if (reply.status !== "ok") typed.delete(id);
      }
    } catch { typed.delete(id); }
    finally { typing.delete(id); }
  }

  async function size(id: string) {
    if (sizing.has(id)) return;
    sizing.add(id);
    try {
      for (let wanted = sizes.get(id); wanted && epoch; wanted = sizes.get(id)) {
        sizes.delete(id);
        await api.resize({ expectedEpoch: epoch, id, columns: wanted.columns, rows: wanted.rows });
      }
    } catch { sizes.delete(id); }
    finally { sizing.delete(id); }
  }

  function attachAll() {
    if (!feed || !epoch) return;
    for (const id of sinks.keys()) {
      quietly(api.attach({ expectedEpoch: epoch, feed, id }));
      if (shown.has(id)) quietly(api.show({ expectedEpoch: epoch, feed, id, visible: true }));
    }
  }

  return {
    /** Calls `listener` when the list of terminals changes. */
    subscribe(listener: () => void) { listeners.add(listener); return () => { listeners.delete(listener); }; },
    /** Calls `listener` when the host asks for the tab of a terminal to be shown. */
    onReveal(listener: (id: string) => void) { revealers.add(listener); return () => { revealers.delete(listener); }; },
    /** The terminals there are, in the order they were created. */
    list: () => list,
    /** What the page's terminals must know of the system, once the host said it. */
    system: () => system,

    /** Listens to the host of an epoch until the returned function is called. */
    connect(hostEpoch: string) {
      const abort = new AbortController();
      let timer: unknown = null;
      epoch = hostEpoch;
      api.profiles({ expectedEpoch: hostEpoch }, { signal: abort.signal, timeoutMilliseconds: 8000 }).then(reply => {
        if (abort.signal.aborted || reply.status !== "ok") return;
        system = { platform: reply.platform, build: reply.build, profiles: reply.profiles };
        notify();
      }, () => { /* The terminals work without it, with the defaults of the page. */ });
      async function watch() {
        try {
          for await (const event of await api.watch({ expectedEpoch: hostEpoch }, { signal: abort.signal })) {
            if (abort.signal.aborted) return;
            if (event.kind === "feed") {
              feed = event.feed ?? null;
              taken.clear();
              attachAll();
            } else if (event.kind === "list") {
              list = event.terminals ?? [];
              notify();
            } else if (event.kind === "show") {
              // A session asked for the tab of a terminal to be shown.
              if (event.id) for (const reveal of [...revealers]) reveal(event.id);
            } else if (event.id) {
              const id = event.id, sink = sinks.get(id);
              // Nothing shows this terminal anymore.
              if (!sink) continue;
              if (event.kind === "start") {
                taken.delete(id);
                sink.start(event.columns, event.rows, event.data ?? "", event.replayed);
              } else if (event.kind === "data") {
                const data = event.data ?? "", from = feed;
                sink.write(data, event.columns, event.rows, event.replayed, () => took(id, data.length, from));
              }
            }
          }
        } catch { /* The watch ended: it starts again below, unless the page stopped listening. */ }
        feed = null;
        if (!abort.signal.aborted) timer = timers.set(() => void watch(), reconnectMilliseconds);
      }
      void watch();
      return () => {
        abort.abort();
        timers.clear(timer);
        if (epoch === hostEpoch) { epoch = null; feed = null; }
      };
    },

    /** The page starts showing a terminal: what it wrote, then what it writes, goes to the sink. */
    attach(id: string, sink: TerminalSink) {
      sinks.set(id, sink);
      if (feed && epoch) quietly(api.attach({ expectedEpoch: epoch, feed, id }));
    },
    /** The page stops showing a terminal. The terminal keeps running. */
    detach(id: string) {
      if (!sinks.delete(id)) return;
      shown.delete(id);
      taken.delete(id);
      if (feed && epoch) quietly(api.detach({ expectedEpoch: epoch, feed, id }));
    },
    /** The tab of a terminal is the one shown, or no longer is. */
    show(id: string, visible: boolean) {
      if (shown.has(id) === visible) return;
      if (visible) shown.add(id); else shown.delete(id);
      if (feed && epoch && sinks.has(id)) quietly(api.show({ expectedEpoch: epoch, feed, id, visible }));
    },
    /** Types text in a terminal. */
    input(id: string, data: string) {
      if (!data || !epoch) return;
      typed.set(id, (typed.get(id) ?? "") + data);
      void type(id);
    },
    /** Gives the screen of a terminal another size; of several sizes asked for in a row, the last one counts. */
    resize(id: string, columns: number, rows: number) {
      if (!epoch) return;
      sizes.set(id, { columns, rows });
      void size(id);
    },
    /** Creates a terminal in the folder of a session, of a project, or of the user. */
    async create(projectId: string | null, sessionId: string | null, integration = true, profile: string | null = null) {
      if (!epoch) return { status: "unavailable", terminal: null };
      const reply = await api.create({ expectedEpoch: epoch, projectId, sessionId, profile, columns: null, rows: null, integration }, { timeoutMilliseconds: 20_000 });
      // The terminal is listed at once: the host says so too, a moment before or after.
      const created = reply.terminal;
      if (reply.status === "ok" && created && !list.some(terminal => terminal.id === created.id)) {
        list = [...list, created];
        notify();
      }
      return reply;
    },
    /** Gives a terminal a title, or with an empty one the title it has by itself. */
    async rename(id: string, title: string) {
      if (!epoch) return "unavailable";
      return (await api.rename({ expectedEpoch: epoch, id, title })).status;
    },
    /** Closes a terminal: its program is ended. */
    close(id: string) {
      if (epoch) quietly(api.close({ expectedEpoch: epoch, id }));
    },
  };
}
