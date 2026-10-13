import { useEffect, useState } from "react";
import type { PromptChange } from "./systemPrompt";
import type { ToolText } from "./toolCall";

type ReadRequest = { expectedEpoch: string; sessionId: string; offset: string; part: string | null; position: number };
type Invoke = (request: ReadRequest, options: { timeoutMilliseconds: number }) => Promise<unknown>;

/** The texts of a prompt. */
export type PromptTexts = Readonly<{ system: ToolText | null; developer: ToolText | null }>;

/** A system prompt record, whole, with the prompt it replaced when the journal has it. */
export type SystemPromptRecord = PromptTexts & Readonly<{
  changed: boolean; hash: string; name: string | null; sourcePath: string | null; timestamp: string;
  tokens: number; systemTokens: number; developerTokens: number; applied: boolean; lossy: boolean;
  previous: (PromptTexts & Readonly<{ timestamp: string }>) | null;
  /** The lines the prompt added to and removed from `previous`, when the host counted them. */
  change: PromptChange | null;
}>;

/** What a read of a prompt gives: its record, or nothing when the host does not serve it. */
export type SystemPromptRead = Readonly<{ status: "ready"; record: SystemPromptRecord } | { status: "failed" }>;

/** Reads the system prompt records of one session, by the journal offset of their row. */
export type SystemPromptReader = Readonly<{
  /** The record when it was read before, without reading. */
  peek(offset: string): SystemPromptRead | undefined;
  /** Reads the record. A read goes to its end whoever asked for it, so that the next one finds the record. */
  read(offset: string): Promise<SystemPromptRead>;
  /** The size of the change of a prompt when it is known: null for a prompt that replaced none, undefined when it was not asked yet. */
  peekChange(offset: string): PromptChange | null | undefined;
  /** Asks for the size of the change alone, for a row. One is asked at a time: each costs the host a pass over the journal. */
  readChange(offset: string): Promise<PromptChange | null>;
}>;

const failed: SystemPromptRead = Object.freeze({ status: "failed" });
// A refusal that another read can lift is not remembered.
const transient = ["capacity", "closed", "read_failed", "stale_epoch", "unavailable"];
type Part = { text: string; length: number; more: boolean };

function part(value: unknown): Part | null | undefined {
  if (value === null || value === undefined) return null;
  const { text, length, more } = value as Record<string, unknown>;
  return typeof text === "string" && typeof length === "number" && typeof more === "boolean" ? { text, length, more } : undefined;
}

function change(value: unknown): PromptChange | null {
  const { added, removed } = (value ?? {}) as Record<string, unknown>;
  return typeof added === "number" && typeof removed === "number" ? { added, removed } : null;
}

const optional = (value: unknown): string | null => typeof value === "string" && value ? value : null;
const count = (value: unknown): number => typeof value === "number" && value >= 0 ? value : 0;
const size = (texts: PromptTexts | null) => (texts?.system?.text.length ?? 0) + (texts?.developer?.text.length ?? 0);

/**
 * Reads system prompts through `toolCalls.readPrompt` and keeps the newest records: at most `maximumEntries`
 * of them and `maximumCharacters` of text, so that a prompt opened again is shown at once. A long text is read
 * part after part, up to `maximumTextCharacters`; what lies beyond is reported as partial. The sizes of the
 * changes are kept apart, for the rows: they are two numbers each.
 */
export function createSystemPromptCache(invoke: Invoke, limits: { maximumEntries?: number; maximumCharacters?: number;
  maximumTextCharacters?: number; maximumChanges?: number; timeoutMilliseconds?: number; retryDelayMilliseconds?: number } = {}) {
  const maximumEntries = limits.maximumEntries ?? 12;
  const maximumCharacters = limits.maximumCharacters ?? 8 * 1024 * 1024;
  const maximumTextCharacters = limits.maximumTextCharacters ?? 4 * 1024 * 1024;
  const maximumChanges = limits.maximumChanges ?? 512;
  const timeoutMilliseconds = limits.timeoutMilliseconds ?? 30_000;
  const retryDelayMilliseconds = limits.retryDelayMilliseconds ?? 400;
  // Insertion order is the age: a hit moves its entry to the end.
  const entries = new Map<string, { read: SystemPromptRead; size: number }>();
  const loading = new Map<string, Promise<SystemPromptRead>>();
  const changes = new Map<string, PromptChange | null>();
  const counting = new Map<string, Promise<PromptChange | null>>();
  let characters = 0;
  // The reads of the rows follow each other.
  let queue: Promise<unknown> = Promise.resolve();

  function peek(key: string): SystemPromptRead | undefined {
    const entry = entries.get(key);
    if (entry) { entries.delete(key); entries.set(key, entry); }
    return entry?.read;
  }
  function remember(key: string, read: SystemPromptRead) {
    const length = read.status === "ready" ? size(read.record) + size(read.record.previous) : 0;
    if (length > maximumCharacters) return;
    entries.set(key, { read, size: length });
    characters += length;
    for (const [oldest, value] of entries) {
      if (entries.size <= maximumEntries && characters <= maximumCharacters) break;
      entries.delete(oldest);
      characters -= value.size;
    }
  }
  function rememberChange(key: string, value: PromptChange | null) {
    changes.delete(key);
    changes.set(key, value);
    for (const oldest of changes.keys()) {
      if (changes.size <= maximumChanges) break;
      changes.delete(oldest);
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
  // Reads the rest of a text the first reply started, at the offset of its own record, within the limit of the page.
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
  async function load(key: string, request: ReadRequest): Promise<SystemPromptRead> {
    const { status, reply } = await ask(request);
    const prompt = status === "ok" && reply!.prompt && typeof reply!.prompt === "object" ? reply!.prompt as Record<string, unknown> : null;
    const system = prompt && part(prompt.system), developer = prompt && part(prompt.developer);
    const before = prompt?.previous && typeof prompt.previous === "object" ? prompt.previous as Record<string, unknown> : null;
    const beforeSystem = before && part(before.system), beforeDeveloper = before && part(before.developer);
    if (!prompt || system === undefined || developer === undefined || beforeSystem === undefined || beforeDeveloper === undefined
      || typeof prompt.changed !== "boolean" || before && typeof before.offset !== "string") {
      if (!transient.includes(status)) remember(key, failed);
      return failed;
    }
    const previous = before && { timestamp: optional(before.timestamp) ?? "",
      system: await complete({ ...request, offset: before.offset as string }, "system", beforeSystem),
      developer: await complete({ ...request, offset: before.offset as string }, "developer", beforeDeveloper) };
    const record: SystemPromptRecord = { changed: prompt.changed, hash: optional(prompt.hash) ?? "", name: optional(prompt.name), sourcePath: optional(prompt.sourcePath),
      timestamp: optional(prompt.timestamp) ?? "", tokens: count(prompt.totalTokens), systemTokens: count(prompt.systemTokens), developerTokens: count(prompt.developerTokens),
      applied: prompt.applied !== false, lossy: prompt.lossy === true,
      system: await complete(request, "system", system), developer: await complete(request, "developer", developer), previous, change: change(prompt.change) };
    const read: SystemPromptRead = { status: "ready", record };
    remember(key, read);
    // What the window read is what the row asks for.
    if (record.change || !previous) rememberChange(key, record.change);
    return read;
  }
  async function loadChange(key: string, request: ReadRequest): Promise<PromptChange | null> {
    const known = changes.get(key);
    if (known !== undefined) return known;
    const { status, reply } = await ask({ ...request, part: "change" });
    const value = status === "ok" ? change(reply!.change) : null;
    // An answer is kept; a refusal that may lift is asked again by the next row that wants it.
    if (status === "ok" || !transient.includes(status)) rememberChange(key, value);
    return value;
  }
  return {
    /**
     * The reader of one session's prompts under one host epoch. `rewinds` is the number of times the session
     * was rewound: a rewind writes its journal again, and an offset then names another record than the one kept here.
     */
    reader(expectedEpoch: string, sessionId: string, rewinds = 0): SystemPromptReader {
      const key = (offset: string) => JSON.stringify([expectedEpoch, sessionId, rewinds, offset]);
      const request = (offset: string): ReadRequest => ({ expectedEpoch, sessionId, offset, part: null, position: 0 });
      return {
        peek: offset => peek(key(offset)),
        read(offset) {
          const name = key(offset);
          const known = peek(name);
          if (known) return Promise.resolve(known);
          let pending = loading.get(name);
          if (!pending) {
            pending = load(name, request(offset)).finally(() => loading.delete(name));
            loading.set(name, pending);
          }
          return pending;
        },
        peekChange: offset => changes.get(key(offset)),
        readChange(offset) {
          const name = key(offset);
          const known = changes.get(name);
          if (known !== undefined) return Promise.resolve(known);
          let pending = counting.get(name);
          if (!pending) {
            pending = queue.then(() => loadChange(name, request(offset))).finally(() => counting.delete(name));
            queue = pending.catch(() => { /* The next read does not wait on a failure. */ });
            counting.set(name, pending);
          }
          return pending;
        },
      };
    },
    /** Entries and text characters currently kept. */
    usage: () => ({ entries: entries.size, characters, changes: changes.size }),
  };
}

/**
 * The size of the change of a prompt, for its row: what the history page gave, else what the reader knows or
 * reads. Nothing is read for a prompt that replaced none, or while `wanted` is false.
 */
export function usePromptChange(reader: SystemPromptReader | null | undefined, offset: string | null, known: PromptChange | null, wanted: boolean): PromptChange | null {
  const ask = !!reader && !!offset && wanted && !known;
  const [read, setRead] = useState<{ reader: SystemPromptReader; offset: string; change: PromptChange | null } | null>(null);
  useEffect(() => {
    if (!ask) return;
    const kept = reader!.peekChange(offset!);
    if (kept !== undefined) { setRead({ reader: reader!, offset: offset!, change: kept }); return; }
    let active = true;
    void reader!.readChange(offset!).then(change => { if (active) setRead({ reader: reader!, offset: offset!, change }); }, () => { /* The row stays without it. */ });
    return () => { active = false; };
  }, [ask, reader, offset]);
  if (known) return known;
  if (!ask) return null;
  return read && read.reader === reader && read.offset === offset ? read.change : reader!.peekChange(offset!) ?? null;
}
