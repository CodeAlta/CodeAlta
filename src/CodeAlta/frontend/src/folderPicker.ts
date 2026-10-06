/** How asking the operating system for a folder ended. */
export type FolderPick = Readonly<{ status: "ok"; path: string } | { status: "canceled" | "busy" | "unavailable" | "failed" }>;

type Invoke = (request: { title: string | null; initialDirectory: string | null }, options: { timeoutMilliseconds: number }) => Promise<unknown>;

/** The folder dialog may stay open as long as one invocation may last. */
export const folderPickTimeoutMilliseconds = 600_000;

const statuses = ["canceled", "busy", "unavailable", "failed"] as const;

/**
 * Shows the operating system's folder dialog through `desktopShell.pickFolder` and returns the chosen folder.
 * Choosing a folder opens nothing: the caller decides what the path is for. A reply that is not understood,
 * and a call that fails, is a failed pick.
 */
export async function pickFolder(invoke: Invoke, title: string, initialDirectory: string | null): Promise<FolderPick> {
  try {
    const reply = await invoke({ title, initialDirectory: initialDirectory?.trim() || null }, { timeoutMilliseconds: folderPickTimeoutMilliseconds }) as
      { status?: unknown; path?: unknown } | null;
    if (reply?.status === "ok") {
      return typeof reply.path === "string" && reply.path.trim() && reply.path.length <= 4096 && !reply.path.includes("\0")
        ? { status: "ok", path: reply.path } : { status: "failed" };
    }
    const status = statuses.find(value => value === reply?.status);
    return { status: status ?? "failed" };
  } catch {
    return { status: "failed" };
  }
}

/** Tells whether two paths name the same folder: a trailing separator and the letter case do not count. */
export function sameFolder(first: string, second: string): boolean {
  const name = (path: string) => path.trim().replace(/[\\/]+$/u, "").toLowerCase();
  return name(first) !== "" && name(first) === name(second);
}
