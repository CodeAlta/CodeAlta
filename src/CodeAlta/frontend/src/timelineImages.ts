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

/** The images of one row and what reads them; a row without a reader shows its images as unavailable. */
export type TimelineImagePart = Readonly<{ source: TimelineImageSource | undefined; images: ReadonlyArray<TimelineImage> }>;

/**
 * The images of several rows as one list, read through one source: the strip under a folded line shows the images
 * the model read during that work, and its viewer goes from one to the next across the rows. An image keeps the title
 * of its row; the key changes with the rows and their images.
 */
export function combinedImageSource(parts: ReadonlyArray<TimelineImagePart>): Readonly<{ images: TimelineImage[]; source: TimelineImageSource }> {
  const images: TimelineImage[] = [];
  const places: { part: TimelineImagePart; index: number }[] = [];
  for (const part of parts) {
    for (const image of part.images) {
      places.push({ part, index: image.index });
      images.push({ index: images.length, title: image.title, mediaType: image.mediaType });
    }
  }
  const key = JSON.stringify(parts.map(part => [part.source?.key ?? null, part.images.length]));
  const place = (index: number) => places[index];
  return { images, source: { key,
    peek: index => { const at = place(index); return !at ? failed : at.part.source ? at.part.source.peek(at.index) : failed; },
    load: index => { const at = place(index); return !at?.part.source ? Promise.resolve(failed) : at.part.source.load(at.index); } } };
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
  const cacheKey = (request: ImageRequest, rewinds: number) => JSON.stringify([request.expectedEpoch, request.sessionId, rewinds, request.offset, request.index]);
  function load(request: ImageRequest, rewinds: number): Promise<TimelineImageState> {
    const key = cacheKey(request, rewinds);
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
    /**
     * The reader of one session's images under one host epoch. `rewinds` is the number of times the session was
     * rewound: a rewind writes its journal again, and an offset then names another record than the one kept here.
     */
    reader(expectedEpoch: string, sessionId: string, rewinds = 0): TimelineImageReader {
      return offset => ({ key: JSON.stringify([expectedEpoch, sessionId, rewinds, offset]),
        peek: index => peek(cacheKey({ expectedEpoch, sessionId, offset, index }, rewinds)),
        load: index => load({ expectedEpoch, sessionId, offset, index }, rewinds) });
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

/** Puts a PNG on the clipboard; the PNG may still be in the making. */
export type ImageClipboardWriter = (png: Promise<Blob>) => Promise<void>;

/**
 * The PNG of a timeline image's `data:` URL: a PNG is taken as it is, another type is drawn again as PNG,
 * the one image type the clipboard of the page accepts.
 */
export async function timelineImagePng(url: string, redraw: (url: string) => Promise<Blob> = redrawAsPng): Promise<Blob> {
  const match = /^data:(image\/[a-z0-9.+-]+);base64,([A-Za-z0-9+/=]+)$/.exec(url);
  if (!match) throw new Error("Not an image of the timeline.");
  if (match[1] !== "image/png") return redraw(url);
  return new Blob([Uint8Array.from(atob(match[2]), value => value.charCodeAt(0))], { type: "image/png" });
}

/** Copies a timeline image to the clipboard; resolves false when nothing was copied. */
export async function copyTimelineImage(url: string, write: ImageClipboardWriter = writeClipboardPng,
  png: (url: string) => Promise<Blob> = timelineImagePng): Promise<boolean> {
  // The PNG is handed over before it is made: the clipboard is opened while the page still has the user's click.
  const made = png(url);
  made.catch(() => { /* The write fails with it. */ });
  try { await write(made); return true; } catch { return false; }
}

function writeClipboardPng(png: Promise<Blob>): Promise<void> {
  if (!navigator.clipboard?.write || typeof ClipboardItem === "undefined") return Promise.reject(new Error("The clipboard takes no image."));
  return navigator.clipboard.write([new ClipboardItem({ "image/png": png })]);
}

// A `data:` image does not taint the canvas: its pixels can be read back.
async function redrawAsPng(url: string): Promise<Blob> {
  const image = new Image();
  image.src = url;
  await image.decode();
  const canvas = document.createElement("canvas");
  canvas.width = image.naturalWidth; canvas.height = image.naturalHeight;
  const context = canvas.getContext("2d");
  if (!context) throw new Error("The image could not be drawn.");
  context.drawImage(image, 0, 0);
  return new Promise((resolve, reject) => canvas.toBlob(blob => blob ? resolve(blob) : reject(new Error("The image could not be drawn.")), "image/png"));
}
