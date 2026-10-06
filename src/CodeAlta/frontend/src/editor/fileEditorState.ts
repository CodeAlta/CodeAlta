import type { ProjectFileReadResponse, ProjectFileWriteResponse } from "#neoastra";
import type { MessageKey } from "../localization";

/** What the editor last read or wrote: the text, the revision a save must still match, and the text with `\n` line ends. */
export type FileBaseline = Readonly<{ content: string; revision: string; lines: string }>;
export type FileNotice = Readonly<{ key: MessageKey; intent: "warning" | "danger" }>;
export type FileEditorState = Readonly<{
  phase: "loading" | "ready" | "failed";
  /** Why the file cannot be shown, while `phase` is "failed". */
  failure: MessageKey | null;
  baseline: FileBaseline | null;
  content: string;
  dirty: boolean;
  readOnly: boolean;
  /** A read is in flight; with a baseline the previous text stays visible until it completes. */
  loading: boolean;
  saving: boolean;
  /** The file changed on disk since it was read: the next step is Reload or Overwrite. */
  conflict: boolean;
  notice: FileNotice | null;
}>;

/** Largest text the host accepts (1 MiB on disk; every encoding needs at least one byte per UTF-16 unit). */
export const maximumFileLength = 1024 * 1024;

export const initialFileEditorState: FileEditorState = Object.freeze({ phase: "loading", failure: null, baseline: null, content: "",
  dirty: false, readOnly: false, loading: true, saving: false, conflict: false, notice: null });

// The editor shows a file with mixed line endings in its dominant one; that alone is not an edit.
const lines = (text: string) => text.replace(/\r\n?/gu, "\n");
const baselineOf = (content: string, revision: string): FileBaseline => ({ content, revision, lines: lines(content) });
const edited = (baseline: FileBaseline, content: string) => content !== baseline.content && lines(content) !== baseline.lines;

/** The message for a file that could not be read. */
export function fileReadFailure(status: string): MessageKey {
  switch (status) {
    case "binary": return "This file is not text and cannot be edited here.";
    case "too_large": return "This file is larger than 1 MiB and cannot be edited here.";
    case "not_found": return "This file no longer exists.";
    case "outside_root": return "This file is outside the project folder.";
    case "archived_project": return "This project is archived; its files cannot be edited.";
    case "unknown_project": case "project_unavailable": return "The project folder is unavailable.";
    case "stale_epoch": return "The host changed. Reload the window to edit files.";
    case "unavailable": return "File editing requires an owned host.";
    default: return "The file could not be read.";
  }
}

export function fileLoading(state: FileEditorState): FileEditorState {
  return { ...state, phase: state.baseline ? state.phase : "loading", failure: null, loading: true, notice: null };
}

/** A completed read replaces the text and every pending edit; a refused one shows why. */
export function fileLoaded(response: Pick<ProjectFileReadResponse, "status" | "content" | "revision" | "readOnly">): FileEditorState {
  if (response.status !== "ok" || response.content === null || response.revision === null)
    return { ...initialFileEditorState, phase: "failed", loading: false, failure: fileReadFailure(response.status === "ok" ? "read_failed" : response.status) };
  return { ...initialFileEditorState, phase: "ready", loading: false, baseline: baselineOf(response.content, response.revision),
    content: response.content, readOnly: response.readOnly };
}

export function fileEdited(state: FileEditorState, content: string): FileEditorState {
  if (!state.baseline || content === state.content) return state;
  return { ...state, content, dirty: edited(state.baseline, content) };
}

/** A save is sent for changed, in-limit text of a writable file; an overwrite answers a conflict. */
export function canSaveFile(state: FileEditorState, overwrite = false): boolean {
  return state.phase === "ready" && !!state.baseline && !state.loading && !state.saving && !state.readOnly && state.content.length <= maximumFileLength
    && (overwrite ? state.conflict : state.dirty);
}

export function fileSaving(state: FileEditorState): FileEditorState {
  return { ...state, saving: true, notice: null };
}

/** Applies the host's answer to a save of `submitted`. Only "ok" moves the baseline. */
export function fileSaved(state: FileEditorState, submitted: string, response: Pick<ProjectFileWriteResponse, "status" | "revision">): FileEditorState {
  const next = { ...state, saving: false };
  if (!state.baseline) return next;
  switch (response.status) {
    case "ok": {
      if (!response.revision) return { ...next, notice: { key: "The save did not complete; reload to see what is on disk.", intent: "danger" } };
      const baseline = baselineOf(submitted, response.revision);
      return { ...next, baseline, dirty: edited(baseline, state.content), conflict: false, notice: null };
    }
    case "conflict": return { ...next, conflict: true, notice: null };
    case "read_only": return { ...next, readOnly: true, notice: { key: "The file is read-only; nothing was saved.", intent: "warning" } };
    case "too_large": return { ...next, notice: { key: "The text is larger than 1 MiB; nothing was saved.", intent: "danger" } };
    case "not_found": return { ...next, notice: { key: "This file no longer exists.", intent: "danger" } };
    case "binary": return { ...next, notice: { key: "This file is not text and cannot be edited here.", intent: "danger" } };
    case "archived_project": return { ...next, notice: { key: "This project is archived; its files cannot be edited.", intent: "warning" } };
    case "stale_epoch": return { ...next, notice: { key: "The host changed. Reload the window to edit files.", intent: "danger" } };
    case "unavailable": return { ...next, notice: { key: "File editing requires an owned host.", intent: "warning" } };
    default: return { ...next, notice: { key: "The file could not be written; nothing was saved.", intent: "danger" } };
  }
}

/** Keeps editing after a conflict without choosing; the next save meets the same question. */
export function fileConflictDismissed(state: FileEditorState): FileEditorState {
  return state.conflict ? { ...state, conflict: false } : state;
}

/** The request failed without an answer: the file may or may not have been written. */
export function fileSaveUnknown(state: FileEditorState): FileEditorState {
  return { ...state, saving: false, notice: { key: "The save did not complete; reload to see what is on disk.", intent: "danger" } };
}

/** The one-word state shown in the editor footer. */
export function fileStatus(state: FileEditorState): MessageKey {
  return state.loading ? "Loading…" : state.phase === "failed" ? "Unavailable" : state.saving ? "Saving…"
    : state.conflict ? "Changed on disk" : state.dirty ? "Modified" : state.readOnly ? "Read-only" : "Saved";
}
