import type { MarkdownLinkScope } from "./markdownLinks";
import { imageDataUrl } from "./timelineImages";

/** A picture of the disk that a link of the Markdown names, as the window draws it: a `data:` URL, and the full path of its file. */
export type MarkdownImage = Readonly<{ url: string; path: string }>;

/**
 * Draws the pictures of the disk that the Markdown of the window names, and shows their file in the file manager.
 * `peek` answers without reading: undefined for a picture not read yet, null for one that cannot be drawn.
 */
export type MarkdownImages = Readonly<{
  peek(address: string, scope: MarkdownLinkScope | null): MarkdownImage | null | undefined;
  load(address: string, scope: MarkdownLinkScope | null): Promise<MarkdownImage | null>;
  reveal(address: string, scope: MarkdownLinkScope | null): void;
  /** The system, which names its file manager; null where a file cannot be shown. */
  platform: string | null;
}>;

type ImageRequest = { expectedHostEpoch: string; address: string; sessionId: string | null; projectId: string | null; directory: string | null };
type Invoke = (request: ImageRequest, options: { timeoutMilliseconds: number }) => Promise<unknown>;

// What another read cannot change: the picture is remembered as one that is not drawn.
const settled = ["not_found", "not_image", "too_large", "invalid_request"];

/**
 * Reads the pictures of the disk through `markdownLinks.image` and keeps the newest ones: at most `maximumEntries`
 * pictures and `maximumCharacters` of URL text, so that a text rendered again, as a message being written is, draws
 * them at once. One read runs per picture at a time and at most `concurrency` reads run together.
 */
export function createMarkdownImageCache(invoke: Invoke, limits: { maximumEntries?: number; maximumCharacters?: number;
  concurrency?: number; timeoutMilliseconds?: number } = {}) {
  const maximumEntries = limits.maximumEntries ?? 48;
  const maximumCharacters = limits.maximumCharacters ?? 64 * 1024 * 1024;
  const concurrency = limits.concurrency ?? 3;
  const timeoutMilliseconds = limits.timeoutMilliseconds ?? 60_000;
  // Insertion order is the age: a hit moves its entry to the end.
  const entries = new Map<string, MarkdownImage | null>();
  const loading = new Map<string, Promise<MarkdownImage | null>>();
  const waiting: (() => void)[] = [];
  let running = 0;
  let characters = 0;
  const size = (image: MarkdownImage | null) => image ? image.url.length : 0;
  const keyOf = (request: ImageRequest) => JSON.stringify([request.expectedHostEpoch, request.address, request.sessionId, request.projectId, request.directory]);

  function remember(key: string, image: MarkdownImage | null) {
    if (size(image) > maximumCharacters) return;
    entries.set(key, image);
    characters += size(image);
    for (const [oldest, value] of entries) {
      if (entries.size <= maximumEntries && characters <= maximumCharacters) break;
      entries.delete(oldest);
      characters -= size(value);
    }
  }
  async function slot<T>(work: () => Promise<T>): Promise<T> {
    if (running >= concurrency) await new Promise<void>(resolve => waiting.push(resolve));
    else running++;
    // A finished read hands its slot to the next waiting one, so `running` stays at the limit.
    try { return await work(); }
    finally { const next = waiting.shift(); if (next) next(); else running--; }
  }
  async function read(key: string, request: ImageRequest): Promise<MarkdownImage | null> {
    try {
      const reply = await invoke(request, { timeoutMilliseconds }) as { status?: unknown; hostEpoch?: unknown; mediaType?: unknown; base64?: unknown; fullPath?: unknown } | null;
      if (!reply || reply.hostEpoch !== request.expectedHostEpoch) return null;
      const url = reply.status === "ok" ? imageDataUrl(reply.mediaType, reply.base64) : null;
      if (url && typeof reply.fullPath === "string" && reply.fullPath) {
        const image: MarkdownImage = { url, path: reply.fullPath };
        remember(key, image);
        return image;
      }
      if (typeof reply.status === "string" && settled.includes(reply.status)) remember(key, null);
    } catch { /* A failed transport is a failed read: the link stays a link, and the next render asks again. */ }
    return null;
  }
  function peek(request: ImageRequest): MarkdownImage | null | undefined {
    const key = keyOf(request);
    if (!entries.has(key)) return undefined;
    const image = entries.get(key)!;
    entries.delete(key); entries.set(key, image);
    return image;
  }
  function load(request: ImageRequest): Promise<MarkdownImage | null> {
    const key = keyOf(request);
    const known = peek(request);
    if (known !== undefined) return Promise.resolve(known);
    let pending = loading.get(key);
    if (!pending) {
      pending = slot(() => read(key, request)).finally(() => loading.delete(key));
      loading.set(key, pending);
    }
    return pending;
  }
  return {
    /** The picture a request names when it was read: undefined when it was not, null when it cannot be drawn. */
    peek,
    /** Reads the picture a request names, once at a time; null when it cannot be drawn. */
    load,
    /** Entries and URL characters currently kept. */
    usage: () => ({ entries: entries.size, characters }),
  };
}

/** The request of a link to a picture, for the host of an epoch. */
export function markdownImageRequest(expectedHostEpoch: string, address: string, scope: MarkdownLinkScope | null): ImageRequest {
  return { expectedHostEpoch, address, sessionId: scope?.sessionId ?? null, projectId: scope?.projectId ?? null, directory: scope?.directory ?? null };
}
