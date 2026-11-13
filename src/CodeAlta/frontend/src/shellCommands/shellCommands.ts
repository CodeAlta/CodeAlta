import type { SessionPromptImage } from "#neoastra";
import { toBase64 } from "../promptImages";

/**
 * One event of a command the host runs: the folder it started in, a part of its output, its exit code (with the end of an
 * output too long to be streamed whole), or why it did not run.
 */
export type ShellCommandEvent = Readonly<{ kind: string; value?: string | null; status?: string | null; exitCode?: number | null; omitted?: number | null }>;
/** Asks the host to run a command in the folder of a session; aborting the signal ends it. */
export type ShellCommandRun = (sessionId: string, command: string, signal: AbortSignal) => Promise<AsyncIterable<ShellCommandEvent>>;

export type ShellCommandState = "running" | "done" | "stopped" | "failed";
/** A command run from the prompt (`!`), as it was shown. */
export type ShellCommandEntry = Readonly<{ id: number; command: string;
  /** What it wrote, standard output and standard error together, as the host sent it. */
  output: string;
  /** The characters of its output past what the host streams. */
  omitted: number;
  /** The end of what the host did not stream, which it sends once the command ended. */
  tail: string;
  state: ShellCommandState;
  /** Its exit code, once it ended by itself. */
  exitCode: number | null;
  /** The folder it ran in, once it started. */
  folder: string | null;
  /** When it started and ended, as ISO dates. */
  startedAt: string; endedAt: string | null;
  /** Why it did not run: the status of the host (`missing_folder`, `stale_epoch`…), with what the host said. */
  failure: Readonly<{ status: string; message: string | null }> | null }>;
/** What the panel of a session shows: one command, the list of the commands, or nothing. */
export type ShellCommandView = Readonly<{ entries: readonly ShellCommandEntry[]; shown: number | null; listing: boolean }>;

/** How many commands a session keeps; the oldest goes first. */
export const shellCommandLimit = 50;
/** Longest command, as the host takes it. */
export const shellCommandMaxLength = 16_384;
/** Most commands that run at once in a session: each holds a channel of the window while it runs. */
export const shellCommandConcurrency = 4;
const empty: ShellCommandView = Object.freeze({ entries: Object.freeze([]), shown: null, listing: false });
// What a command writes is shown in steps: a command that writes a lot does not draw the panel at each piece.
const outputDelay = 50;

/**
 * The shell commands of the sessions (a prompt that starts with `!`), for the lifetime of a window: run by the host in
 * the folder of the session, kept here by session, the latest last. Also whether the prompt of a session is in shell
 * mode. A command goes on when its panel is closed; it ends when it is stopped or removed, or when the window closes.
 */
export function createShellCommands(start: ShellCommandRun, limit = shellCommandLimit) {
  const views = new Map<string, ShellCommandView>();
  const modes = new Set<string>();
  const running = new Map<number, AbortController>();
  const listeners = new Set<() => void>();
  let next = 1;

  const notify = () => { for (const listener of [...listeners]) listener(); };
  const set = (sessionId: string, view: ShellCommandView) => { views.set(sessionId, view); notify(); };
  const update = (sessionId: string, id: number, change: (entry: ShellCommandEntry) => ShellCommandEntry) => {
    const view = views.get(sessionId);
    if (!view?.entries.some(entry => entry.id === id)) return;
    set(sessionId, { ...view, entries: view.entries.map(entry => entry.id === id ? change(entry) : entry) });
  };
  const stop = (sessionId: string, id: number) => {
    const controller = running.get(id);
    if (!controller) return;
    running.delete(id);
    controller.abort();
    update(sessionId, id, entry => entry.state === "running" ? { ...entry, state: "stopped", endedAt: new Date().toISOString() } : entry);
  };

  async function run(sessionId: string, id: number, command: string, controller: AbortController, onEnd?: (entry: ShellCommandEntry) => void) {
    let pending = "";
    let timer: ReturnType<typeof setTimeout> | null = null;
    const flush = () => {
      if (timer !== null) { clearTimeout(timer); timer = null; }
      if (!pending) return;
      const text = pending; pending = "";
      update(sessionId, id, entry => ({ ...entry, output: entry.output + text }));
    };
    const end = (change: (entry: ShellCommandEntry) => ShellCommandEntry) => {
      flush();
      update(sessionId, id, entry => entry.state === "running" ? change({ ...entry, endedAt: new Date().toISOString() }) : entry);
    };
    try {
      const events = await start(sessionId, command, controller.signal);
      for await (const event of events) {
        if (controller.signal.aborted) return;
        if (event.kind === "started") update(sessionId, id, entry => ({ ...entry, folder: event.value ?? null }));
        else if (event.kind === "output" && event.value) {
          pending += event.value;
          timer ??= setTimeout(flush, outputDelay);
        } else if (event.kind === "exit") end(entry => ({ ...entry, state: "done", exitCode: event.exitCode ?? -1, omitted: event.omitted ?? 0, tail: event.value ?? "" }));
        else if (event.kind === "error") end(entry => ({ ...entry, state: "failed", failure: { status: event.status || "failed", message: event.value || null } }));
      }
      // A stream that ended without saying how is a command whose end is not known.
      if (!controller.signal.aborted) end(entry => ({ ...entry, state: "failed", failure: { status: "failed", message: null } }));
    } catch (error) {
      if (!controller.signal.aborted) end(entry => ({ ...entry, state: "failed", failure: { status: "failed", message: error instanceof Error ? error.message : null } }));
    } finally {
      flush();
      if (running.get(id) === controller) running.delete(id);
      const entry = views.get(sessionId)?.entries.find(item => item.id === id);
      if (entry && entry.state !== "running") onEnd?.(entry);
    }
  }

  return {
    /** What the panel of a session shows: the same object until it changes. */
    view(sessionId: string): ShellCommandView { return views.get(sessionId) ?? empty; },
    subscribe(listener: () => void): () => void {
      listeners.add(listener);
      return () => { listeners.delete(listener); };
    },
    /** How many commands the session keeps. */
    count(sessionId: string): number { return views.get(sessionId)?.entries.length ?? 0; },
    /** How many commands of the session still run. */
    runningCount(sessionId: string): number { return views.get(sessionId)?.entries.filter(entry => entry.state === "running").length ?? 0; },
    /** The commands of the session, the latest last, once each in a row: what the prompt recalls in shell mode. */
    commands(sessionId: string): readonly string[] {
      return (views.get(sessionId)?.entries ?? []).map(entry => entry.command).filter((command, index, all) => command !== all[index + 1]);
    },
    /** Whether the prompt of a session takes a shell command. */
    mode(sessionId: string): boolean { return modes.has(sessionId); },
    setMode(sessionId: string, on: boolean) {
      if (modes.has(sessionId) === on) return;
      if (on) modes.add(sessionId); else modes.delete(sessionId);
      notify();
    },
    /**
     * Runs a command in the folder of a session and shows it as it runs; `onEnd` is told once it ended, whatever the way.
     * Null when it is not run: blank, too long, or {@link shellCommandConcurrency} commands of the session still run.
     */
    run(sessionId: string, command: string, onEnd?: (entry: ShellCommandEntry) => void): number | null {
      const text = command.trim();
      if (!text || text.length > shellCommandMaxLength) return null;
      if ((views.get(sessionId)?.entries.filter(entry => entry.state === "running").length ?? 0) >= shellCommandConcurrency) return null;
      const id = next++;
      const entry: ShellCommandEntry = { id, command: text, output: "", omitted: 0, tail: "", state: "running", exitCode: null, folder: null,
        startedAt: new Date().toISOString(), endedAt: null, failure: null };
      const current = views.get(sessionId) ?? empty;
      const kept = [...current.entries, entry];
      // The oldest that ended go first; one that runs is kept.
      while (kept.length > limit) {
        const index = kept.findIndex(item => item.state !== "running");
        if (index < 0 || index === kept.length - 1) break;
        kept.splice(index, 1);
      }
      const controller = new AbortController();
      running.set(id, controller);
      set(sessionId, { entries: kept, shown: id, listing: false });
      void run(sessionId, id, text, controller, onEnd);
      return id;
    },
    /** Ends a command that runs, and what it started. */
    stop(sessionId: string, id: number) { stop(sessionId, id); },
    /** Removes a command from the list, and ends it if it runs. */
    remove(sessionId: string, id: number) {
      stop(sessionId, id);
      const view = views.get(sessionId);
      if (!view?.entries.some(entry => entry.id === id)) return;
      const entries = view.entries.filter(entry => entry.id !== id);
      set(sessionId, { entries, shown: view.shown === id ? null : view.shown, listing: view.listing || view.shown === id && entries.length > 0 });
    },
    /** Removes every command of the session that ended. */
    clear(sessionId: string) {
      const view = views.get(sessionId);
      if (!view) return;
      const entries = view.entries.filter(entry => entry.state === "running");
      set(sessionId, { entries, shown: entries.some(entry => entry.id === view.shown) ? view.shown : null, listing: view.listing });
    },
    /** Shows the commands of the session, for one to be shown again. */
    list(sessionId: string) { set(sessionId, { ...(views.get(sessionId) ?? empty), shown: null, listing: true }); },
    /** Shows a command run before, with its output. */
    show(sessionId: string, id: number) {
      const view = views.get(sessionId) ?? empty;
      if (!view.entries.some(entry => entry.id === id)) return;
      set(sessionId, { ...view, shown: id, listing: false });
    },
    /** Whether the panel of a session shows something: a command or the list. */
    isOpen(sessionId: string): boolean {
      const view = views.get(sessionId);
      return !!view && (view.shown !== null || view.listing);
    },
    /** Closes the panel of a session; its commands go on. */
    close(sessionId: string) {
      const view = views.get(sessionId);
      if (!view || view.shown === null && !view.listing) return;
      set(sessionId, { ...view, shown: null, listing: false });
    },
    /** Ends every command that runs: the window goes away. */
    dispose() {
      for (const controller of running.values()) controller.abort();
      running.clear();
      listeners.clear();
    },
  };
}

export type ShellCommands = ReturnType<typeof createShellCommands>;

/** The output as a terminal leaves it, in plain text: no escape sequence, and a line rewritten in place (`\r`) as it ended. */
export function plainOutput(output: string): string {
  return output
    // eslint-disable-next-line no-control-regex
    .replace(/\x1b\][^\x07\x1b]*(?:\x07|\x1b\\)|\x1b\[[0-?]*[ -/]*[@-~]|\x1b[@-Z\\-_]/gu, "")
    .replace(/\r\n/gu, "\n")
    .split("\n").map(line => line.slice(line.lastIndexOf("\r") + 1)).join("\n");
}

/**
 * The output of a command as plain text, as the panel shows it and copies it: what the host streamed and, for an output
 * longer than that, a line that counts what is left out, then the end of the output.
 */
export function shellOutputText(entry: ShellCommandEntry): string {
  const output = plainOutput(entry.output), tail = plainOutput(entry.tail);
  const gap = entry.omitted - entry.tail.length;
  return gap > 0 ? `${output.replace(/\n?$/u, "\n")}[… ${gap} characters …]\n${tail}` : output + tail;
}

/** Most characters of an output the model gets: its start and its end, as the agent gets those of a command of its own. */
export const shellOutputLimits = Object.freeze({ head: 10_000, tail: 20_000 });
/** The media type of the output of a shell command attached to a prompt: a text the host gives the model as it is. */
export const shellOutputMediaType = "text/x-shell-output";

/** How a command ended, in the words the model reads. */
function ending(entry: ShellCommandEntry): string {
  if (entry.state === "done") return `it exited with code ${entry.exitCode}`;
  if (entry.state === "stopped") return "the user stopped it before it ended";
  if (entry.state === "running") return "it was still running";
  return `it could not run (${entry.failure?.status ?? "failed"}${entry.failure?.message ? `: ${entry.failure.message}` : ""})`;
}

/**
 * The output of a command as an attachment of the next prompt: the model reads what the user ran, where, how it ended and
 * what it wrote; a long output keeps its start and its end. The same entry gives the same attachment.
 */
export function shellAttachment(entry: ShellCommandEntry): SessionPromptImage {
  // An output the host did not stream whole ends with what it sent of its end; any other is cut here if it is too long.
  const output = plainOutput(entry.output), tail = plainOutput(entry.tail);
  const gap = entry.omitted - entry.tail.length;
  const whole = gap > 0 ? null : output + tail;
  const cut = whole === null ? output.length - shellOutputLimits.head + gap : whole.length - shellOutputLimits.head - shellOutputLimits.tail;
  const kept = cut <= 0 ? whole! : `${output.slice(0, shellOutputLimits.head)}\n[… ${cut} characters omitted …]\n${whole === null ? tail : whole.slice(-shellOutputLimits.tail)}`;
  const text = `The user ran this command in a shell${entry.folder ? `, in ${entry.folder}` : ""}; ${ending(entry)}.\n\n$ ${entry.command}\n${kept || "(no output)"}\n`;
  // A title is a file name: one line, no separator of folders, at most 80 characters.
  // eslint-disable-next-line no-control-regex
  const line = `$ ${entry.command}`.replace(/[\u0000-\u001f\u007f-\u009f]+/gu, " ").replace(/\//gu, "∕").replace(/\\/gu, "⧵").trim();
  const title = line.length > 80 ? line.slice(0, 79).trimEnd() + "…" : line;
  return Object.freeze({ title, mediaType: shellOutputMediaType, base64: toBase64(new TextEncoder().encode(text)) });
}
