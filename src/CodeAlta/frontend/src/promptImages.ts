import type { SessionPromptImage } from "#neoastra";
import { createOwnerChangeSignal } from "./ownerChangeSignal";

export const imageLimits = Object.freeze({ text: 32768 });
export const imageHelp = "Paste PNG, JPEG, WebP, GIF or BMP images. Normal Send requires an observed supported model. Queue and Steer refuse images.";
export const imagePasteFailure = "Could not read the pasted image. Paste a valid PNG, JPEG, WebP, GIF or BMP image. Original attachments are retained.";
export function freezeImages(images: readonly SessionPromptImage[] | null | undefined): readonly SessionPromptImage[] {
  return Object.freeze((images ?? []).map(image => Object.freeze({ title: image.title, mediaType: image.mediaType, base64: image.base64 })));
}
export function validImages(images: readonly SessionPromptImage[] | null | undefined): boolean {
  if (images == null) return true;
  return Array.isArray(images) && images.every(image => image && image.mediaType === "image/png"
    && typeof image.title === "string" && image.title.trim().length > 0 && image.title.length <= 80 && !/[\u0000-\u001f\u007f-\u009f]/u.test(image.title)
    && typeof image.base64 === "string" && canonicalBase64(image.base64));
}
// Avoid a repeated-group regexp: ordinary multi-megabyte payloads can overflow its stack.
function canonicalBase64(value: string): boolean {
  if (!value.length || value.length % 4 !== 0 || /[^A-Za-z0-9+/=]/.test(value)) return false;
  try { return btoa(atob(value)) === value; } catch { return false; }
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
  if (bytes.length < 57 || ![137, 80, 78, 71, 13, 10, 26, 10].every((b, i) => bytes[i] === b)) throw invalid();
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
      if (!width || !height || width > 0x7fffffff || height > 0x7fffffff || bytes[offset + 16] !== 8
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

// What the host accepts between the header and the end of a PNG, besides its image data.
const keptPngChunks = new Set(["IHDR", "IDAT", "IEND", "sRGB", "gAMA", "cHRM", "pHYs"]);

// A canvas writes the PNG the browser wants, not the narrow one the host takes: WebKit, the web view of the
// macOS application, adds an eXIf chunk. The chunks a decoder may skip (a lowercase first letter) and the host
// does not take are left out; nothing else is touched, so the header check still decides what a valid image is.
export function stripPngMetadata(bytes: Uint8Array): Uint8Array {
  if (bytes.length < 20 || ![137, 80, 78, 71, 13, 10, 26, 10].every((b, i) => bytes[i] === b)) return bytes;
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  const kept: Uint8Array[] = [bytes.subarray(0, 8)];
  let dropped = false; let offset = 8;
  while (offset < bytes.length) {
    if (offset > bytes.length - 12) return bytes;
    const length = view.getUint32(offset);
    if (length > bytes.length - offset - 12) return bytes;
    const type = String.fromCharCode(...bytes.subarray(offset + 4, offset + 8));
    const skippable = (bytes[offset + 4] & 0x20) !== 0;
    if (skippable && !keptPngChunks.has(type)) dropped = true; else kept.push(bytes.subarray(offset, offset + length + 12));
    offset += length + 12;
  }
  if (!dropped) return bytes;
  const result = new Uint8Array(kept.reduce((total, part) => total + part.length, 0));
  let position = 0;
  for (const part of kept) { result.set(part, position); position += part.length; }
  return result;
}

// Not String.fromCharCode(...bytes): the arguments of a multi-megabyte image overflow the stack.
function toBase64(bytes: Uint8Array): string {
  let binary = "";
  for (let offset = 0; offset < bytes.length; offset += 0x8000) binary += String.fromCharCode(...bytes.subarray(offset, offset + 0x8000));
  return btoa(binary);
}

// Only called for files synchronously obtained from the originating user paste event.
// No navigator.clipboard, filesystem paths, URL fetching, HTML or blob URLs. Decode first:
// clipboard PNGs may contain palettes, grayscale, interlacing or ordinary metadata that
// the deliberately narrow host wire format does not accept. Canvas strips those differences.
export async function readPastedImage(file: File, title: string): Promise<SessionPromptImage> {
  if (!["image/png", "image/jpeg", "image/jpg", "image/webp", "image/gif", "image/bmp", "image/x-ms-bmp"].includes(file.type)) throw new Error(imagePasteFailure);
  const bitmap = await createImageBitmap(file);
  try {
    const canvas = document.createElement("canvas");
    canvas.width = bitmap.width; canvas.height = bitmap.height;
    const context = canvas.getContext("2d");
    if (!context) throw new Error(imagePasteFailure);
    context.drawImage(bitmap, 0, 0);
    const dataUrl = canvas.toDataURL("image/png");
    const prefix = "data:image/png;base64,";
    if (!dataUrl.startsWith(prefix)) throw new Error(imagePasteFailure);
    const written = dataUrl.slice(prefix.length);
    const all = Uint8Array.from(atob(written), value => value.charCodeAt(0));
    const bytes = stripPngMetadata(all);
    const dimensions = pngHeader(bytes);
    if (dimensions.width !== bitmap.width || dimensions.height !== bitmap.height) throw new Error(imagePasteFailure);
    return Object.freeze({ title, mediaType: "image/png", base64: bytes === all ? written : toBase64(bytes) });
  } finally { bitmap.close(); }
}
