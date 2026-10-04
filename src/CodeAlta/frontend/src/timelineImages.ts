import type { WindowSize } from "./windowGeometry";

/** One image of a user message: its position among the images of the message, and what to show for it. */
export type TimelineImage = Readonly<{ index: number; title: string; mediaType: string | null }>;

/** A loaded image as a `data:` URL (the page's policy allows no `blob:` image), or a failed read. */
export type TimelineImageState = Readonly<{ status: "ready"; url: string } | { status: "failed" }>;

/** The images of one message: `key` names the message, `peek` answers without reading. */
export type TimelineImageSource = Readonly<{ key: string;
  peek(index: number): TimelineImageState | undefined; load(index: number): Promise<TimelineImageState> }>;

/** Reads the images of a session's persisted messages, by journal offset. */
export type TimelineImageReader = (offset: string) => TimelineImageSource;

type ImageRequest = { expectedEpoch: string; sessionId: string; offset: string; index: number };
type Invoke = (request: ImageRequest, options: { timeoutMilliseconds: number }) => Promise<unknown>;

const failed: TimelineImageState = Object.freeze({ status: "failed" });
const mediaTypes = ["image/png", "image/jpeg", "image/gif", "image/webp", "image/bmp"];
const maximumTitleLength = 256;
const maximumImages = 64;
// A refusal that another read can lift is not remembered: the next card that shows the image asks again.
const transient = ["capacity", "closed", "read_failed", "stale_epoch", "unavailable"];

/** Accepts the image list of a history row; anything malformed shows no image. */
export function projectTimelineImages(images: unknown): TimelineImage[] | undefined {
  if (!Array.isArray(images) || images.length === 0 || images.length > maximumImages) return undefined;
  const result: TimelineImage[] = [];
  for (const [position, value] of images.entries()) {
    if (!value || typeof value !== "object") return undefined;
    const { index, title, mediaType } = value as Record<string, unknown>;
    if (index !== position || typeof title !== "string" || !title || title.length > maximumTitleLength
      || mediaType !== null && typeof mediaType !== "string") return undefined;
    result.push({ index, title, mediaType: mediaType ?? null });
  }
  return result;
}

/** The `data:` URL of an image, or null when it is not one of the image types the timeline shows. */
export function imageDataUrl(mediaType: unknown, base64: unknown): string | null {
  return typeof mediaType === "string" && mediaTypes.includes(mediaType) && typeof base64 === "string" && base64.length > 0
    && !/[^A-Za-z0-9+/=]/.test(base64) ? `data:${mediaType};base64,${base64}` : null;
}

/** The images of a prompt that is still being sent: the page already holds their URLs. */
export function inlineImageSource(key: string, images: readonly { url: string }[]): TimelineImageSource {
  const state = (index: number): TimelineImageState => images[index] ? { status: "ready", url: images[index].url } : failed;
  return { key, peek: state, load: async index => state(index) };
}

/**
 * Reads timeline images through `promptImages.read` and keeps the newest ones: at most `maximumEntries`
 * images and `maximumCharacters` of URL text, so that a card rendered again does not read its images again.
 * One read runs per image at a time and at most `concurrency` reads run together.
 */
export function createTimelineImageCache(invoke: Invoke, limits: { maximumEntries?: number; maximumCharacters?: number;
  concurrency?: number; timeoutMilliseconds?: number; retryDelayMilliseconds?: number } = {}) {
  const maximumEntries = limits.maximumEntries ?? 32;
  const maximumCharacters = limits.maximumCharacters ?? 48 * 1024 * 1024;
  const concurrency = limits.concurrency ?? 3;
  const timeoutMilliseconds = limits.timeoutMilliseconds ?? 30_000;
  const retryDelayMilliseconds = limits.retryDelayMilliseconds ?? 400;
  // Insertion order is the age: a hit moves its entry to the end.
  const entries = new Map<string, TimelineImageState>();
  const loading = new Map<string, Promise<TimelineImageState>>();
  const waiting: (() => void)[] = [];
  let running = 0;
  let characters = 0;
  const size = (state: TimelineImageState) => state.status === "ready" ? state.url.length : 0;

  function peek(key: string): TimelineImageState | undefined {
    const state = entries.get(key);
    if (state) { entries.delete(key); entries.set(key, state); }
    return state;
  }
  function remember(key: string, state: TimelineImageState) {
    if (size(state) > maximumCharacters) return;
    entries.set(key, state);
    characters += size(state);
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
  async function read(key: string, request: ImageRequest): Promise<TimelineImageState> {
    for (let attempt = 0; ; attempt++) {
      let status = "read_failed";
      try {
        const reply = await invoke(request, { timeoutMilliseconds }) as { status?: unknown; mediaType?: unknown; base64?: unknown } | null;
        if (reply && typeof reply.status === "string") status = reply.status;
        const url = status === "ok" ? imageDataUrl(reply!.mediaType, reply!.base64) : null;
        if (url) { const state: TimelineImageState = { status: "ready", url }; remember(key, state); return state; }
        if (status === "ok") status = "unsupported_type";
      } catch { /* A failed transport is a failed read: the card shows the image as unavailable. */ }
      // The host admits eight reads at a time and the timeline shares them.
      if (status === "capacity" && attempt < 2) { await new Promise(resolve => setTimeout(resolve, retryDelayMilliseconds)); continue; }
      if (!transient.includes(status)) remember(key, failed);
      return failed;
    }
  }
  function load(request: ImageRequest): Promise<TimelineImageState> {
    const key = JSON.stringify([request.expectedEpoch, request.sessionId, request.offset, request.index]);
    const known = peek(key);
    if (known) return Promise.resolve(known);
    let pending = loading.get(key);
    if (!pending) {
      pending = slot(() => read(key, request)).finally(() => loading.delete(key));
      loading.set(key, pending);
    }
    return pending;
  }
  return {
    /** The reader of one session's images under one host epoch. */
    reader(expectedEpoch: string, sessionId: string): TimelineImageReader {
      return offset => ({ key: JSON.stringify([expectedEpoch, sessionId, offset]),
        peek: index => peek(JSON.stringify([expectedEpoch, sessionId, offset, index])),
        load: index => load({ expectedEpoch, sessionId, offset, index }) });
    },
    /** Entries and URL characters currently kept. */
    usage: () => ({ entries: entries.size, characters }),
  };
}

/** Height of a window's title bar and the padding around the image, in CSS pixels. */
export const viewerChrome = Object.freeze({ width: 2 + 2 * 12, height: 2 + 41 + 2 * 12 });

/**
 * Default size of the image viewer: the image at its own size when it fits, otherwise scaled down, in a
 * window of at most 80% of the main window's width and height. An image that is not measured yet gets the
 * largest window.
 */
export function viewerWindowSize(viewport: WindowSize, image: WindowSize | null, minimum: WindowSize): WindowSize {
  const largest = { width: Math.floor(viewport.width * 0.8), height: Math.floor(viewport.height * 0.8) };
  if (!image || !(image.width > 0) || !(image.height > 0)) return largest;
  const room = { width: Math.max(1, largest.width - viewerChrome.width), height: Math.max(1, largest.height - viewerChrome.height) };
  const scale = Math.min(1, room.width / image.width, room.height / image.height);
  return { width: Math.min(largest.width, Math.max(minimum.width, Math.ceil(image.width * scale) + viewerChrome.width)),
    height: Math.min(largest.height, Math.max(minimum.height, Math.ceil(image.height * scale) + viewerChrome.height)) };
}

/** The image an arrow key or a button moves to: past the last one comes the first, and the reverse. */
export function steppedImageIndex(current: number, count: number, step: number): number {
  return count <= 0 ? 0 : ((current + step) % count + count) % count;
}
