import type { ToolCallRecord, ToolText } from "./toolCall";

type ReadRequest = { expectedEpoch: string; sessionId: string; offset: string; outputOffset: string | null; part: string | null; position: number };
type Invoke = (request: ReadRequest, options: { timeoutMilliseconds: number }) => Promise<unknown>;

/** What a read of a tool call gives: its record, or nothing when the host does not serve it. */
export type ToolCallRead = Readonly<{ status: "ready"; record: ToolCallRecord } | { status: "failed" }>;

/** Reads the whole record of a tool call of one session, by the journal offsets of its row. */
export type ToolCallReader = Readonly<{
  /** The record when it was read before, without reading. */
  peek(offset: string, outputOffset: string | null): ToolCallRead | undefined;
  /** Reads the record. A read goes to its end whoever asked for it, so that the next one finds the record. */
  read(offset: string, outputOffset: string | null): Promise<ToolCallRead>;
}>;

const failed: ToolCallRead = Object.freeze({ status: "failed" });
// A refusal that another read can lift is not remembered.
const transient = ["capacity", "closed", "read_failed", "stale_epoch", "unavailable"];
type Part = { text: string; length: number; more: boolean };

function part(value: unknown): Part | null | undefined {
  if (value === null || value === undefined) return null;
  const { text, length, more } = value as Record<string, unknown>;
  return typeof text === "string" && typeof length === "number" && typeof more === "boolean" ? { text, length, more } : undefined;
}

const optional = (value: unknown): string | null => typeof value === "string" ? value : null;
const paths = (value: unknown): string[] => Array.isArray(value) ? value.filter((item): item is string => typeof item === "string") : [];

/**
 * Reads tool calls through `toolCalls.read` and keeps the newest records: at most `maximumEntries` of them
 * and `maximumCharacters` of text, so that a call opened again is shown at once. A long text is read part
 * after part, up to `maximumTextCharacters`; what lies beyond is reported as partial.
 */
export function createToolCallCache(invoke: Invoke, limits: { maximumEntries?: number; maximumCharacters?: number;
  maximumTextCharacters?: number; timeoutMilliseconds?: number; retryDelayMilliseconds?: number } = {}) {
  const maximumEntries = limits.maximumEntries ?? 24;
  const maximumCharacters = limits.maximumCharacters ?? 16 * 1024 * 1024;
  const maximumTextCharacters = limits.maximumTextCharacters ?? 4 * 1024 * 1024;
  const timeoutMilliseconds = limits.timeoutMilliseconds ?? 30_000;
  const retryDelayMilliseconds = limits.retryDelayMilliseconds ?? 400;
  // Insertion order is the age: a hit moves its entry to the end.
  const entries = new Map<string, { read: ToolCallRead; size: number }>();
  const loading = new Map<string, Promise<ToolCallRead>>();
  let characters = 0;

  function peek(key: string): ToolCallRead | undefined {
    const entry = entries.get(key);
    if (entry) { entries.delete(key); entries.set(key, entry); }
    return entry?.read;
  }
  function remember(key: string, read: ToolCallRead) {
    const size = read.status === "ready" ? (read.record.arguments?.text.length ?? 0) + (read.record.output?.text.length ?? 0) + (read.record.diff?.text.length ?? 0) : 0;
    if (size > maximumCharacters) return;
    entries.set(key, { read, size });
    characters += size;
    for (const [oldest, value] of entries) {
      if (entries.size <= maximumEntries && characters <= maximumCharacters) break;
      entries.delete(oldest);
      characters -= value.size;
    }
  }
  async function ask(request: ReadRequest): Promise<{ status: string; reply: Record<string, unknown> | null }> {
    for (let attempt = 0; ; attempt++) {
      let status = "read_failed", reply: Record<string, unknown> | null = null;
      try {
        const value = await invoke(request, { timeoutMilliseconds });
        if (value && typeof value === "object" && typeof (value as { status?: unknown }).status === "string") {
          reply = value as Record<string, unknown>;
          status = reply.status as string;
        }
      } catch { /* A failed transport is a failed read. */ }
      // The host admits eight reads at a time and the timeline shares them.
      if (status === "capacity" && attempt < 2) { await new Promise(resolve => setTimeout(resolve, retryDelayMilliseconds)); continue; }
      return { status, reply };
    }
  }
  // Reads the rest of a text the first reply started, within the limit of the page.
  async function complete(request: ReadRequest, name: string, first: Part | null): Promise<ToolText | null> {
    if (!first) return null;
    let value = first.text, more = first.more;
    while (more && value.length < maximumTextCharacters) {
      const { status, reply } = await ask({ ...request, part: name, position: value.length });
      const next = status === "ok" ? part(reply!.text) : undefined;
      if (!next || !next.text.length && next.more) break;
      value += next.text;
      more = next.more;
    }
    return { text: value, partial: more };
  }
  async function load(key: string, request: ReadRequest): Promise<ToolCallRead> {
    const { status, reply } = await ask(request);
    const call = status === "ok" && reply!.call && typeof reply!.call === "object" ? reply!.call as Record<string, unknown> : null;
    const given = call && part(call.arguments), output = call && part(call.output), diff = call && part(call.diff);
    if (!call || given === undefined || output === undefined || diff === undefined || typeof call.kind !== "string" || typeof call.phase !== "string") {
      if (!transient.includes(status)) remember(key, failed);
      return failed;
    }
    const record: ToolCallRecord = { kind: call.kind, phase: call.phase, name: optional(call.name), message: optional(call.message),
      command: optional(call.command), workingDirectory: optional(call.workingDirectory),
      exitCode: typeof call.exitCode === "number" ? call.exitCode : null, error: optional(call.error),
      arguments: await complete(request, "arguments", given), output: await complete(request, "output", output),
      diff: await complete(request, "diff", diff), readFiles: paths(call.readFiles), modifiedFiles: paths(call.modifiedFiles) };
    const read: ToolCallRead = { status: "ready", record };
    remember(key, read);
    return read;
  }
  return {
    /** The reader of one session's tool calls under one host epoch. */
    reader(expectedEpoch: string, sessionId: string): ToolCallReader {
      const key = (offset: string, outputOffset: string | null) => JSON.stringify([expectedEpoch, sessionId, offset, outputOffset]);
      return {
        peek: (offset, outputOffset) => peek(key(offset, outputOffset)),
        read(offset, outputOffset) {
          const name = key(offset, outputOffset);
          const known = peek(name);
          if (known) return Promise.resolve(known);
          let pending = loading.get(name);
          if (!pending) {
            pending = load(name, { expectedEpoch, sessionId, offset, outputOffset, part: null, position: 0 }).finally(() => loading.delete(name));
            loading.set(name, pending);
          }
          return pending;
        },
      };
    },
    /** Entries and text characters currently kept. */
    usage: () => ({ entries: entries.size, characters }),
  };
}
