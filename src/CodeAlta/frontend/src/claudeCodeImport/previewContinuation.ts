import type { SessionPromptImage, WorkspaceSession } from "#neoastra";
import { draftStorageKey, transferPromptDraft, type createLocalDrafts } from "../promptDraft";
import type { createImageDrafts } from "../promptImages";
import type { ClaudeCodeImport } from "./claudeCodeSessions";

/** What the first message under a preview is: its text and the images pasted with it, where they are kept. */
export type PreviewPrompt = Readonly<{ text: string; images: readonly SessionPromptImage[]; imageKey: string; scope: string }>;

const previewScopePrefix = "claude-code-preview:";
/** The draft typed under the preview of a conversation: by project and conversation, kept until it is sent. */
export const previewDraftScope = (projectId: string, cliSessionId: string) => `${previewScopePrefix}${JSON.stringify([projectId, cliSessionId])}`;
/** Where the draft of a preview is stored, to be found again when the preview is opened again. */
export const previewDraftStorageKey = (scope: string) => `codealta.desktop.claudeCodePrompt.${scope.slice(previewScopePrefix.length)}`;
/** The key of the images pasted under the preview of a conversation, among the image drafts of the window. */
export const previewImageKey = (epoch: string | null, projectId: string, cliSessionId: string) => JSON.stringify(["claude-code-preview", epoch, projectId, cliSessionId]);

/** Empties the draft of a preview, in the window and in storage: its message was given to the session that continues the conversation. */
export function clearPreviewDraft(drafts: ReturnType<typeof createLocalDrafts>, scope: string, remove: (key: string) => void) {
  drafts.edit(scope, "");
  try { remove(previewDraftStorageKey(scope)); } catch { /* The window keeps it empty. */ }
}

/** The storage of the drafts of the sessions. */
export type DraftStorage = Readonly<{ read: (key: string) => string | null; write: (key: string, value: string) => void }>;

/**
 * Gives the first message of a preview to the session that continues the conversation: its text becomes the draft of
 * the session, and its images those of its composer, as the first prompt of a new session is given to it. Nothing is
 * given to a session that holds a draft, has images waiting, or sends something now: its own prompt is never replaced.
 * Says whether the session holds the message now; the message is then sent from there, and stays its draft when the
 * sending fails.
 */
export function handOffPreviewPrompt(storage: DraftStorage, imageDrafts: ReturnType<typeof createImageDrafts>, epoch: string,
  session: Readonly<Pick<WorkspaceSession, "id" | "scopeKind" | "projectId" | "workspacePath">>, prompt: PreviewPrompt, sending: boolean): boolean {
  if (sending || !prompt.text.trim() && !prompt.images.length) return false;
  if (!prompt.images.length) return transferPromptDraft(storage.read, storage.write, session.id, prompt.text);
  const projectId = session.scopeKind === "project" ? session.projectId : null;
  // The key the composer of the session keeps its images under.
  const destination = JSON.stringify([epoch, session.id, projectId, projectId === null ? null : session.workspacePath]);
  return imageDrafts.copyToEmpty(prompt.imageKey, prompt.images, destination,
    () => prompt.text === "" ? !storage.read(draftStorageKey(session.id)) : transferPromptDraft(storage.read, storage.write, session.id, prompt.text));
}

/** How continuing the conversation of a preview ended. */
export type PreviewContinuation =
  /** The session that continues it is shown; `sent` when the first message was given to it, `offered` when it held a draft of its own. */
  | Readonly<{ kind: "opened"; sessionId: string; existing: boolean; sent: boolean; offered: string | null }>
  /** It was not imported: the preview stays, with its message. */
  | Readonly<{ kind: "import_failed"; code: string }>
  /** It was imported, but the session is not in the list read after: the preview stays, with its message. */
  | Readonly<{ kind: "unopened"; sessionId: string }>;

/**
 * Continues the conversation of a preview in CodeAlta: it is imported (or the session that continues it already is
 * found), the list of sessions is read, the first message, if any, is given to that session, and the session is opened
 * in the place of the preview, with that message to send. When the import fails or the session cannot be found, nothing
 * of the message is touched: it stays under the preview.
 */
export async function continuePreview(steps: Readonly<{
  importConversation: () => Promise<ClaudeCodeImport>;
  /** The session in the list of sessions read again; null when it is not there. */
  findSession: (sessionId: string) => Promise<WorkspaceSession | null>;
  handOff: (session: WorkspaceSession, prompt: PreviewPrompt) => boolean;
  /** Shows the session in the place of the preview, with the message it is to send. */
  open: (session: WorkspaceSession, send: Readonly<{ text: string; images: number }> | null) => void;
}>, prompt: PreviewPrompt | null): Promise<PreviewContinuation> {
  const imported = await steps.importConversation();
  if (imported.kind === "error") return { kind: "import_failed", code: imported.code };
  const session = await steps.findSession(imported.sessionId);
  if (!session) return { kind: "unopened", sessionId: imported.sessionId };
  const sent = !!prompt && steps.handOff(session, prompt);
  steps.open(session, sent ? { text: prompt!.text, images: prompt!.images.length } : null);
  return { kind: "opened", sessionId: session.id, existing: imported.existing, sent, offered: prompt && !sent && prompt.text.trim() ? prompt.text : null };
}
