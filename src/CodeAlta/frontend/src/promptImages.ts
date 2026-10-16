import type { SessionPromptImage } from "#neoastra";
import { createOwnerChangeSignal } from "./ownerChangeSignal";

// The attachments of a prompt are images (always PNG: the page converts them) and files of any other type. The host
// gives each file to the model in a form it reads: a PDF as a document or converted to Markdown, a text as its content,
// an office document converted to Markdown, any other file as its path.
export const imageLimits = Object.freeze({ text: 32768 });
/** Largest file attached, and largest total of the attachments of one prompt, in bytes. */
export const attachmentLimits = Object.freeze({ fileBytes: 20 * 1024 * 1024, totalBytes: 64 * 1024 * 1024 });
export const imageHelp = "Paste PNG, JPEG, WebP, GIF or BMP images. Normal Send requires an observed supported model. Queue and Steer refuse images.";
export const imagePasteFailure = "Could not read the pasted image. Paste a valid PNG, JPEG, WebP, GIF or BMP image. Original attachments are retained.";
const imageTypes = ["image/png", "image/jpeg", "image/jpg", "image/webp", "image/gif", "image/bmp", "image/x-ms-bmp"];
const mediaTypePattern = /^[A-Za-z0-9!#$&^_.+-]+\/[A-Za-z0-9!#$&^_.+-]+$/;
/** An attachment that is an image; any other is a file, or a link (`isLinkAttachment`). */
export function isImageAttachment(attachment: SessionPromptImage): boolean { return attachment.mediaType === "image/png"; }

/**
 * The links of the attach menu: an issue or a folder, shown above the prompt like the files. They never reach the host as
 * attachments: when the prompt is sent, their Markdown links are added at the end of its text (`withLinkAttachments`),
 * one per line, as `#` and `@` put them in the text. Their content is the link, as UTF-8.
 */
export const linkMediaTypes = Object.freeze({ issue: "application/vnd.codealta.issue-link", folder: "application/vnd.codealta.folder-link" });
export function isLinkAttachment(attachment: SessionPromptImage): boolean {
  return attachment.mediaType === linkMediaTypes.issue || attachment.mediaType === linkMediaTypes.folder;
}
/** A link attachment: its title is what the chip shows (`#18 Fix the parser`, `docs`), its link what the prompt gets. */
export function linkAttachment(kind: keyof typeof linkMediaTypes, title: string, link: string): SessionPromptImage {
  const clean = title.replace(/[\u0000-\u001f\u007f-\u009f]/gu, " ").trim();
  return Object.freeze({ title: clean.length > 80 ? clean.slice(0, 79).trimEnd() + "…" : clean || link.slice(0, 80),
    mediaType: linkMediaTypes[kind], base64: toBase64(new TextEncoder().encode(link)) });
}
/** The Markdown link of a link attachment. */
export function attachmentLink(attachment: SessionPromptImage): string {
  return new TextDecoder().decode(Uint8Array.from(atob(attachment.base64), value => value.charCodeAt(0)));
}
/** The text and the attachments a prompt is sent with: the links of the link attachments at the end of the text, one per line. */
export function withLinkAttachments(text: string, images: readonly SessionPromptImage[] | null | undefined) {
  const links = (images ?? []).filter(isLinkAttachment);
  if (!links.length) return { text, images: images ?? null };
  const added = links.map(attachmentLink).filter(link => !text.includes(link));
  const rest = (images ?? []).filter(image => !isLinkAttachment(image));
  return { text: added.length ? (text.trimEnd() ? `${text.trimEnd()}\n\n` : "") + added.join("\n") : text, images: rest.length ? rest : null };
}
/** Whether an attachment of this type is read as an image (converted to PNG), not attached as a file. */
export function isImageFile(file: { type: string }): boolean { return imageTypes.includes(file.type); }
export function freezeImages(images: readonly SessionPromptImage[] | null | undefined): readonly SessionPromptImage[] {
  return Object.freeze((images ?? []).map(image => Object.freeze({ title: image.title, mediaType: image.mediaType, base64: image.base64 })));
}
export function validImages(images: readonly SessionPromptImage[] | null | undefined): boolean {
  if (images == null) return true;
  return Array.isArray(images) && images.every(image => image && typeof image.mediaType === "string"
    && typeof image.title === "string" && image.title.trim().length > 0 && image.title.length <= 80 && !/[\u0000-\u001f\u007f-\u009f]/u.test(image.title)
    && typeof image.base64 === "string"
    // A link: at most 4096 bytes. A file: a plain media type, a name without a folder, at most the largest file.
    && (image.mediaType === "image/png" || isLinkAttachment(image) && decodedBytes(image.base64) <= 4096
      || image.mediaType.length <= 128 && mediaTypePattern.test(image.mediaType)
      && !/[\\/]/.test(image.title) && decodedBytes(image.base64) <= attachmentLimits.fileBytes)
    && canonicalBase64(image.base64));
}
/** The number of bytes the attachments hold, from the length of their base64. */
export function attachmentBytes(images: readonly SessionPromptImage[]): number {
  return images.reduce((total, image) => total + decodedBytes(image.base64), 0);
}
function decodedBytes(base64: string): number {
  return base64.length / 4 * 3 - (base64.endsWith("==") ? 2 : base64.endsWith("=") ? 1 : 0);
}
/** A file name as the title of an attachment: at most 80 characters, its extension kept, no control character or separator. */
export function fileTitle(name: string): string {
  const clean = name.replace(/[\u0000-\u001f\u007f-\u009f\\/]/gu, "").trim() || "file";
  if (clean.length <= 80) return clean;
  const dot = clean.lastIndexOf(".");
  const extension = dot > 0 && clean.length - dot <= 17 ? clean.slice(dot) : "";
  return clean.slice(0, 80 - extension.length - 1).trimEnd() + "…" + extension;
}
// Avoid a repeated-group regexp: ordinary multi-megabyte payloads can overflow its stack.
function canonicalBase64(value: string): boolean {
  if (!value.length || value.length % 4 !== 0 || /[^A-Za-z0-9+/=]/.test(value)) return false;
  try { return btoa(atob(value)) === value; } catch { return false; }
}
/** The key the composer of a session keeps its images under, among the image drafts of the window. */
export const composerImageKey = (epoch: string | null, sessionId: string, projectId: string | null, projectPath: string | null) =>
  JSON.stringify([epoch, sessionId, projectId, projectPath]);
export function createImageDrafts() {
  const entries = new Map<string, readonly SessionPromptImage[]>();
  const reads = new Set<string>();
  const empty: readonly SessionPromptImage[] = Object.freeze([]);
  const change = createOwnerChangeSignal();
  let copying = false;
  return {
    subscribe: change.subscribe, getSnapshot: change.getSnapshot,
    /** All image drafts and pending pastes, including sessions not mounted in the current space. No image payloads. */
    retentionKeys: (): readonly string[] => [...new Set([...entries.keys(), ...reads])],
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

/** Why a file cannot be attached: its message names the file; `unreadable` is a file the page could not read (a folder). */
export class AttachmentError extends Error {
  constructor(name: string, readonly unreadable = false) { super(name); }
}

/**
 * Reads a file the user attached (chosen, dropped or pasted): an image as a PNG, any other file as it is.
 * @param imageTitle The title of a pasted image (the clipboard names every image alike); null keeps the name of the file.
 */
export async function readAttachedFile(file: File, imageTitle: string | null): Promise<SessionPromptImage> {
  if (isImageFile(file)) {
    try { return await readPastedImage(file, imageTitle ?? fileTitle(file.name || "image")); }
    catch { /* An image the browser cannot decode is attached as a file. */ }
  }
  if (file.size === 0 || file.size > attachmentLimits.fileBytes) throw new AttachmentError(file.name);
  let bytes: Uint8Array;
  try { bytes = new Uint8Array(await file.arrayBuffer()); }
  catch { throw new AttachmentError(file.name, true); }
  if (bytes.length === 0 || bytes.length > attachmentLimits.fileBytes) throw new AttachmentError(file.name);
  const mediaType = file.type && file.type !== "image/png" && file.type.length <= 128 && mediaTypePattern.test(file.type) ? file.type : "application/octet-stream";
  return Object.freeze({ title: fileTitle(file.name), mediaType, base64: toBase64(bytes) });
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
