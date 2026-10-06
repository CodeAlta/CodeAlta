import type { ProjectFileImageResponse, ProjectFileReadResponse, ProjectFileStat, ProjectFileWriteResponse } from "#neoastra";
import type { MessageKey } from "../localization";
import { fileReadFailure, maximumFileLength, type FileNotice } from "./fileEditorState";

export type DocumentKind = "text" | "image";
/** A file open in the code editor: what was read from the disk and what became of it since. */
export type EditorDocument = Readonly<{
  /** What the document keeps when its file is renamed: the editor holds its text under this number. */
  id: number;
  kind: DocumentKind;
  phase: "loading" | "ready" | "failed";
  /** Why the file cannot be shown, while `phase` is "failed". */
  failure: MessageKey | null;
  /** The text last read from the disk; the editor holds what was typed since. */
  text: string;
  /** Changes each time `text` was read again and must replace what the editor holds. */
  generation: number;
  /** The content hash a save must still find on the disk. */
  revision: string | null;
  /** The size and write time of the file as it was read: another stamp means that something else changed it. */
  stamp: string | null;
  encoding: string | null;
  /** The size of the file in bytes. */
  length: number;
  dirty: boolean; readOnly: boolean; saving: boolean;
  /** The file changed on the disk and is being read again. */
  reloading: boolean;
  /** The file changed on the disk while it holds edits: the next step is Reload or Overwrite. */
  conflict: boolean;
  /** The file is no longer on the disk. */
  missing: boolean;
  notice: FileNotice | null;
  image: Readonly<{ url: string; mediaType: string }> | null;
}>;

const rasterImages = new Set(["png", "jpg", "jpeg", "gif", "webp", "bmp", "ico", "avif"]);
const extension = (path: string) => { const name = path.slice(path.lastIndexOf("/") + 1), dot = name.lastIndexOf("."); return dot > 0 ? name.slice(dot + 1).toLowerCase() : ""; };
/** Whether a file is shown as a picture (read as bytes) or edited as text. */
export const documentKind = (path: string): DocumentKind => rasterImages.has(extension(path)) ? "image" : "text";
export type PreviewKind = "svg" | "markdown";
/** The rendering a text file also has: a drawing for SVG, a formatted page for Markdown. */
export function documentPreview(path: string): PreviewKind | null {
  const type = extension(path);
  return type === "svg" ? "svg" : type === "md" || type === "markdown" ? "markdown" : null;
}

export const newDocument = (path: string, id = 0): EditorDocument => ({ id, kind: documentKind(path), phase: "loading", failure: null, text: "", generation: 0, revision: null,
  stamp: null, encoding: null, length: 0, dirty: false, readOnly: false, saving: false, reloading: false, conflict: false, missing: false, notice: null, image: null });

/**
 * Applies a read. The first one shows the file or why it cannot be shown. A later one (the file changed on the
 * disk) replaces the text unless the disk holds what was read before; a later one that fails keeps the text.
 * With `discard` the text read replaces what was typed: the reload that was asked for.
 */
export function documentRead(document: EditorDocument, response: Pick<ProjectFileReadResponse, "status" | "content" | "revision" | "readOnly" | "stamp" | "encoding" | "length">,
  discard = false): EditorDocument {
  if (response.status === "ok" && response.content !== null && response.revision !== null) {
    const same = document.phase === "ready" && document.revision === response.revision;
    // Edited while it was read again: what was typed is kept, and the choice is the one of a conflict.
    if (!discard && !same && document.phase === "ready" && document.dirty) return { ...document, stamp: response.stamp, reloading: false, conflict: true, missing: false };
    const kept = same && !discard;
    return { ...document, phase: "ready", failure: null, text: kept ? document.text : response.content, generation: kept ? document.generation : document.generation + 1,
      revision: response.revision, stamp: response.stamp, encoding: response.encoding, length: response.length, readOnly: response.readOnly,
      dirty: kept && document.dirty, reloading: false, conflict: false, missing: false, notice: null };
  }
  const status = response.status === "ok" ? "read_failed" : response.status;
  if (document.phase !== "ready") return { ...document, phase: "failed", failure: fileReadFailure(status), reloading: false };
  return { ...document, reloading: false, missing: status === "not_found" || document.missing };
}

/** Applies the read of a picture; `url` shows the bytes that were read. */
export function documentImageRead(document: EditorDocument, response: Pick<ProjectFileImageResponse, "status" | "mediaType" | "stamp" | "length">, url: string | null): EditorDocument {
  if (response.status === "ok" && response.mediaType && url)
    return { ...document, phase: "ready", failure: null, image: { url, mediaType: response.mediaType }, stamp: response.stamp, length: response.length,
      generation: document.generation + 1, reloading: false, missing: false };
  if (document.phase === "ready") return { ...document, reloading: false, missing: response.status === "not_found" || document.missing };
  const failure: MessageKey = response.status === "too_large" ? "This image is larger than 16 MiB and cannot be shown here."
    : response.status === "unsupported_type" ? "This file is not an image the editor can show." : fileReadFailure(response.status === "ok" ? "read_failed" : response.status);
  return { ...document, phase: "failed", failure, reloading: false };
}

/** What the editor holds differs from what the disk last held, or no longer does. */
export function documentEdited(document: EditorDocument, dirty: boolean): EditorDocument {
  return document.dirty === dirty || document.phase !== "ready" ? document : { ...document, dirty };
}

/** A save is sent for the changed text of a writable file; an overwrite answers a conflict. */
export function canSaveDocument(document: EditorDocument, overwrite = false): boolean {
  return document.kind === "text" && document.phase === "ready" && !!document.revision && !document.saving && !document.reloading && !document.readOnly
    && (overwrite ? document.conflict : document.dirty);
}

export const documentSaving = (document: EditorDocument): EditorDocument => ({ ...document, saving: true, notice: null });

/** Applies the host's answer to a save. Only "ok" moves what the disk is known to hold. */
export function documentSaved(document: EditorDocument, response: Pick<ProjectFileWriteResponse, "status" | "revision" | "stamp">): EditorDocument {
  const next = { ...document, saving: false };
  switch (response.status) {
    case "ok":
      if (!response.revision) return { ...next, notice: { key: "The save did not complete; reload to see what is on disk.", intent: "danger" } };
      return { ...next, revision: response.revision, stamp: response.stamp ?? next.stamp, conflict: false, missing: false, notice: null };
    case "conflict": return { ...next, conflict: true, notice: null };
    case "read_only": return { ...next, readOnly: true, notice: { key: "The file is read-only; nothing was saved.", intent: "warning" } };
    case "too_large": return { ...next, notice: { key: "The text is larger than 1 MiB; nothing was saved.", intent: "danger" } };
    case "not_found": return { ...next, missing: true, notice: { key: "This file no longer exists.", intent: "danger" } };
    case "binary": return { ...next, notice: { key: "This file is not text and cannot be edited here.", intent: "danger" } };
    case "archived_project": return { ...next, notice: { key: "This project is archived; its files cannot be edited.", intent: "warning" } };
    case "stale_epoch": return { ...next, notice: { key: "The host changed. Reload the window to edit files.", intent: "danger" } };
    case "unavailable": return { ...next, notice: { key: "File editing requires an owned host.", intent: "warning" } };
    default: return { ...next, notice: { key: "The file could not be written; nothing was saved.", intent: "danger" } };
  }
}

/** The request failed without an answer: the file may or may not have been written. */
export const documentSaveUnknown = (document: EditorDocument): EditorDocument =>
  ({ ...document, saving: false, notice: { key: "The save did not complete; reload to see what is on disk.", intent: "danger" } });

/** The text is too long to be sent at all. */
export const documentTooLarge = (text: string) => text.length > maximumFileLength;

/** Keeps editing after a conflict without choosing; the next save meets the same question. */
export const documentConflictDismissed = (document: EditorDocument): EditorDocument => document.conflict ? { ...document, conflict: false } : document;

/**
 * Compares a file with what the disk says of it now. A file without edits that changed is read again
 * (`reloading`); one with edits is in conflict, once for each change; one that is gone is marked missing.
 */
export function documentChecked(document: EditorDocument, stat: Pick<ProjectFileStat, "status" | "stamp" | "readOnly"> | undefined): EditorDocument {
  if (!stat || document.phase !== "ready" || document.saving || document.reloading) return document;
  if (stat.status === "not_found") return document.missing ? document : { ...document, missing: true };
  if (stat.status !== "ok" || stat.stamp === null) return document;
  const readOnly = document.kind === "text" ? stat.readOnly : document.readOnly;
  if (stat.stamp === document.stamp) return document.missing || document.readOnly !== readOnly ? { ...document, missing: false, readOnly } : document;
  // The stamp is taken as seen: a file left in conflict is not flagged again until it changes once more.
  return document.dirty ? { ...document, stamp: stat.stamp, conflict: true, missing: false, readOnly }
    : { ...document, reloading: true, missing: false, readOnly };
}

/** The one-word state shown in the status bar. */
export function documentStatus(document: EditorDocument): MessageKey {
  return document.phase === "loading" ? "Loading…" : document.phase === "failed" ? "Unavailable" : document.saving ? "Saving…"
    : document.missing ? "Deleted on disk" : document.conflict ? "Changed on disk" : document.dirty ? "Modified" : document.readOnly ? "Read-only" : "Saved";
}

/** A size in bytes as a short text: "812 B", "14.2 KB", "3.1 MB". */
export function formatFileSize(bytes: number, locale: string): string {
  const format = (value: number, unit: string) => `${value.toLocaleString(locale, { maximumFractionDigits: value < 10 ? 1 : 0 })} ${unit}`;
  return bytes < 1024 ? format(bytes, "B") : bytes < 1024 * 1024 ? format(bytes / 1024, "KB") : format(bytes / (1024 * 1024), "MB");
}
