import { attachmentLimits } from "./promptImages";

/**
 * Files and folders dropped on the window from the file manager, as the host tells it with a `files-dropped` notice.
 * On Linux and Windows NeoAstra takes such a drop away from the page, which never gets its `drop` event: the host gives
 * the point in CSS pixels, the folders by their path and the files by an id the page reads their content with, once.
 * On macOS the page gets the drop itself and the host tells nothing.
 */
export type HostDrop = Readonly<{ x: number; y: number; folders: readonly string[]; files: readonly HostDroppedFile[] }>;
export type HostDroppedFile = Readonly<{ id: string; name: string; size: number }>;

/** What the composer under the point gets, as the detail of a `hostDropEvent` on the element of its surface. */
export type HostDropDetail = Readonly<{ files: File[]; folders: string[] }>;

/** The event the composer surface under a host drop receives. */
export const hostDropEvent = "codealta-host-drop";

/** A dropped file that was not attached: larger than an attachment, or that the host could not read. */
export type HostDropProblem = Readonly<{ name: string; reason: "too_large" | "unreadable" }>;

type ReadDroppedFile = (request: { id: string }) => Promise<unknown>;

const idPattern = /^[0-9a-f]{32}$/u;

/** The drop of a `files-dropped` notice, or null for one that is not understood. */
export function readHostDrop(value: unknown): HostDrop | null {
  if (!value || typeof value !== "object") return null;
  const { x, y, folders, files } = value as Record<string, unknown>;
  if (typeof x !== "number" || typeof y !== "number" || !Number.isFinite(x) || !Number.isFinite(y)) return null;
  if (!Array.isArray(folders) || !Array.isArray(files) || folders.length + files.length === 0 || folders.length + files.length > 256) return null;
  if (!folders.every(path => typeof path === "string" && path.trim() !== "" && path.length <= 4096 && !path.includes("\0"))) return null;
  const read: HostDroppedFile[] = [];
  for (const file of files as unknown[]) {
    const { id, name, size } = (file ?? {}) as Record<string, unknown>;
    if (typeof id !== "string" || !idPattern.test(id) || typeof name !== "string" || !name.trim() || name.length > 1024
      || typeof size !== "number" || !Number.isInteger(size) || size < 0) return null;
    read.push(Object.freeze({ id, name, size }));
  }
  return Object.freeze({ x, y, folders: Object.freeze([...folders as string[]]), files: Object.freeze(read) });
}

/**
 * The composer surface a drop at a point goes to: the one under the point, else the one of the pane under the point, the
 * nearest region around the point that holds exactly one shown surface. Null over a dialog, or where the region holds
 * several (between two panes) or none.
 */
export function dropSurface(root: Document, x: number, y: number): HTMLElement | null {
  const hit = root.elementFromPoint(x, y);
  if (!hit) return null;
  const shown = (element: Element) => element.getClientRects().length > 0 && !element.closest("[inert]");
  const own = hit.closest<HTMLElement>(".prompt-surface");
  if (own) return shown(own) ? own : null;
  for (let node: Element | null = hit; node; node = node.parentElement) {
    if (node.matches("dialog, [role='dialog'], [role='alertdialog'], [aria-modal='true']")) return null;
    const inside = Array.from(node.querySelectorAll<HTMLElement>(".prompt-surface")).filter(shown);
    if (inside.length === 1) return inside[0];
    if (inside.length > 1) return null;
  }
  return null;
}

/**
 * Gives a host drop to the composer under its point: reads the content of each file from the host, then sends the
 * files and the folders to that composer's surface, which attaches them as it attaches a drop of the page and a folder
 * of Add folder…. A file larger than an attachment is not read.
 * @returns Whether a composer was under the point, and the files that could not be attached.
 */
export async function deliverHostDrop(drop: HostDrop, read: ReadDroppedFile, root: Document = document): Promise<{ delivered: boolean; problems: HostDropProblem[] }> {
  const target = dropSurface(root, drop.x, drop.y);
  if (!target) return { delivered: false, problems: [] };
  const files: File[] = [];
  const problems: HostDropProblem[] = [];
  for (const file of drop.files) {
    if (file.size > attachmentLimits.fileBytes) { problems.push({ name: file.name, reason: "too_large" }); continue; }
    const reply = await readReply(read, file.id);
    if (reply.status === "too_large") { problems.push({ name: file.name, reason: "too_large" }); continue; }
    const bytes = reply.status === "ok" && typeof reply.base64 === "string" ? decode(reply.base64) : null;
    if (!bytes) { problems.push({ name: file.name, reason: "unreadable" }); continue; }
    const type = typeof reply.mediaType === "string" && reply.mediaType.length <= 128 ? reply.mediaType : "";
    files.push(new File([bytes], file.name, { type }));
  }
  if (!target.isConnected) return { delivered: false, problems };
  if (files.length || drop.folders.length)
    target.dispatchEvent(new CustomEvent<HostDropDetail>(hostDropEvent, { detail: Object.freeze({ files, folders: [...drop.folders] }) }));
  return { delivered: true, problems };
}

// The answer of `desktopShell.readDroppedFile`; a call that fails is a file that could not be read.
async function readReply(read: ReadDroppedFile, id: string): Promise<{ status?: unknown; mediaType?: unknown; base64?: unknown }> {
  try {
    const reply = await read({ id });
    return reply && typeof reply === "object" ? reply as { status?: unknown; mediaType?: unknown; base64?: unknown } : {};
  } catch { return {}; }
}

function decode(base64: string): Uint8Array<ArrayBuffer> | null {
  try { return Uint8Array.from(atob(base64), value => value.charCodeAt(0)); } catch { return null; }
}
