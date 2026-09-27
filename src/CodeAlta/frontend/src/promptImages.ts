import type { SessionPromptImage } from "#neoastra";
import { createOwnerChangeSignal } from "./ownerChangeSignal";

export const imageLimits = Object.freeze({ count: 3, bytes: 65_536, total: 98_304, text: 4096 });
export const imageHelp = "PNG: up to 3 images, 64 KiB each / 96 KiB total, 2048px per side / 4MP; RGB/RGBA 8-bit, non-interlaced. Text: empty or nonblank, up to 4096 characters. Normal Send requires an observed supported model. Queue and Steer refuse images.";
export function freezeImages(images: readonly SessionPromptImage[] | null | undefined): readonly SessionPromptImage[] {
  return Object.freeze((images ?? []).map(image => Object.freeze({ title: image.title, mediaType: image.mediaType, base64: image.base64 })));
}
export function validImages(images: readonly SessionPromptImage[] | null | undefined): boolean {
  if (images == null) return true;
  return Array.isArray(images) && images.length <= imageLimits.count && images.every(image => image && image.mediaType === "image/png"
    && typeof image.title === "string" && image.title.trim().length > 0 && image.title.length <= 80 && !/[\u0000-\u001f\u007f-\u009f]/u.test(image.title)
    && typeof image.base64 === "string" && image.base64.length <= Math.ceil(imageLimits.bytes / 3) * 4
    && /^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$/.test(image.base64))
    && images.reduce((sum, image) => sum + atob(image.base64).length, 0) <= imageLimits.total;
}
export function createImageDrafts() {
  const entries = new Map<string, readonly SessionPromptImage[]>();
  const reads = new Set<string>();
  const empty: readonly SessionPromptImage[] = Object.freeze([]);
  const change = createOwnerChangeSignal();
  let copying = false;
  return {
    subscribe: change.subscribe, getSnapshot: change.getSnapshot,
    get: (key: string) => entries.get(key) ?? empty,
    beginRead(key: string): (() => void) | null {
      if (copying || reads.has(key) || reads.size >= 8) return null;
      reads.add(key); return () => { reads.delete(key); };
    },
    replace(key: string, previous: readonly SessionPromptImage[], next: readonly SessionPromptImage[]) {
      if (copying || (entries.get(key) ?? empty) !== previous || !validImages(next) || next.length && !entries.has(key) && entries.size >= 8) return false;
      if (next.length) entries.set(key, freezeImages(next)); else entries.delete(key);
      change.changed(); return true;
    },
    // Reserve the in-memory destination before synchronous text persistence. No await,
    // eviction or source consumption; failure can leave storage uncertain, never images lost.
    copyToEmpty(source: string, original: readonly SessionPromptImage[], destination: string, persist: () => boolean) {
      if (copying || !original.length || entries.get(source) !== original || source === destination
        || entries.has(destination) || reads.has(destination) || entries.size >= 8 || !validImages(original)) return false;
      const copied = freezeImages(original);
      copying = true;
      try {
        if (!persist()) return false;
        entries.set(destination, copied);
      } catch { return false; }
      finally { copying = false; }
      change.changed(); return true;
    },
  };
}

export function pngHeader(bytes: Uint8Array): { width: number; height: number } {
  const invalid = () => new Error("Invalid or unsupported PNG. " + imageHelp);
  if (bytes.length < 57 || bytes.length > imageLimits.bytes || ![137, 80, 78, 71, 13, 10, 26, 10].every((b, i) => bytes[i] === b)) throw invalid();
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  let width = 0; let height = 0; let data = false; let dataEnded = false; let ended = false;
  for (let offset = 8; offset <= bytes.length - 12;) {
    const length = view.getUint32(offset); if (length > bytes.length - offset - 12) throw invalid();
    const type = String.fromCharCode(...bytes.subarray(offset + 4, offset + 8));
    let crc = 0xffffffff;
    for (const b of bytes.subarray(offset + 4, offset + 8 + length)) {
      crc ^= b;
      for (let bit = 0; bit < 8; bit++) crc = (crc >>> 1) ^ (crc & 1 ? 0xedb88320 : 0);
    }
    if ((~crc >>> 0) !== view.getUint32(offset + length + 8)) throw invalid();
    if (type === "IHDR") {
      if (offset !== 8 || length !== 13) throw invalid();
      width = view.getUint32(offset + 8); height = view.getUint32(offset + 12);
      if (!width || !height || width > 2048 || height > 2048 || width * height > 4_000_000 || bytes[offset + 16] !== 8
        || ![2, 6].includes(bytes[offset + 17]) || bytes[offset + 18] || bytes[offset + 19] || bytes[offset + 20]) throw invalid();
    } else if (!width) throw invalid();
    else if (type === "IDAT") { if (dataEnded) throw invalid(); data = true; }
    else if (type === "IEND") { if (length || !data || offset + 12 !== bytes.length) throw invalid(); ended = true; break; }
    else { if (!((type === "sRGB" && length === 1) || (type === "gAMA" && length === 4)
      || (type === "cHRM" && length === 32) || (type === "pHYs" && length === 9))) throw invalid(); if (data) dataEnded = true; }
    offset += length + 12;
  }
  if (!ended) throw invalid();
  return { width, height };
}

// Only called for files synchronously obtained from the originating user paste event.
// No navigator.clipboard, filesystem paths, URL fetching, HTML or blob URLs.
export async function readPastedPng(file: File, title: string): Promise<SessionPromptImage> {
  if (file.type !== "image/png" || file.size > imageLimits.bytes) throw new Error("Only PNG clipboard files up to 64 KiB are supported.");
  const bytes = new Uint8Array(await file.arrayBuffer());
  const dimensions = pngHeader(bytes);
  let binary = ""; for (const b of bytes) binary += String.fromCharCode(b);
  const base64 = btoa(binary);
  const image = new Image(); image.src = `data:image/png;base64,${base64}`;
  await image.decode();
  if (image.naturalWidth !== dimensions.width || image.naturalHeight !== dimensions.height) throw new Error("PNG dimensions do not match.");
  return Object.freeze({ title, mediaType: "image/png", base64 });
}
