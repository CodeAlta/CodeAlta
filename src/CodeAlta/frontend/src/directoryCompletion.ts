import type { WorkspaceDirectoryCompletionRequest, WorkspaceDirectoryCompletionResponse } from "#neoastra";

export function folderCompletionRequest(draft: string, epoch: string): WorkspaceDirectoryCompletionRequest | string {
  // Do not trim, expand or resolve the user's draft. The host validates canonical/local paths.
  const windows = /^[A-Za-z]:\\/.test(draft);
  if (!windows && (!draft.startsWith("/") || draft.startsWith("//")))
    return "Suggestions require an absolute local folder path. Home, relative, UNC and device paths are unsupported.";
  if (draft.length > 1153 || /[\u0000-\u001f\u007f]/.test(draft) || (windows && draft.includes("/")) || (!windows && draft.includes("\\")))
    return "This path cannot be suggested without changing its literal spelling. Use a canonical local folder path.";
  const separator = windows ? "\\" : "/";
  const last = draft.lastIndexOf(separator);
  const directoryPath = draft.endsWith(separator) ? draft : draft.slice(0, last + 1);
  const prefix = draft.endsWith(separator) ? "" : draft.slice(last + 1);
  const root = windows ? draft.slice(0, 3) : "/";
  if (!directoryPath || directoryPath === root)
    return "Folder suggestions do not scan a filesystem root. Type an existing directory below the root first.";
  if (directoryPath.length > 1024 || prefix.length > 128 || /(^|[\\/])\.\.?([\\/]|$)/.test(directoryPath)
    || (windows ? directoryPath.includes("\\\\") : directoryPath.includes("//")) || prefix === "." || prefix === "..")
    return "The folder or prefix is noncanonical or exceeds the suggestion limits; edit the path and request suggestions again.";
  // Invalid Unicode must not be silently replaced by the JSON encoder.
  try { encodeURIComponent(draft); } catch { return "The path contains invalid Unicode; edit it before requesting suggestions."; }
  return { expectedHostEpoch: epoch, directoryPath, prefix };
}

export function projectFolderCompletion(reply: WorkspaceDirectoryCompletionResponse,
  request: WorkspaceDirectoryCompletionRequest): { status: string; directories: string[]; entriesVisited: number; omittedUnsafeEntries: boolean } | undefined {
  if (!reply || reply.hostEpoch !== request.expectedHostEpoch || reply.directoryPath !== request.directoryPath
    || reply.prefix !== request.prefix || !Number.isInteger(reply.entriesVisited) || reply.entriesVisited < 0
    || reply.entriesVisited > 129 || typeof reply.omittedUnsafeEntries !== "boolean"
    || !Array.isArray(reply.directories) || reply.directories.length > 16) return undefined;
  const statuses = ["complete", "incomplete", "invalid_request", "missing", "not_directory", "denied", "read_error", "unconfigured", "stale_epoch", "closed", "busy"];
  if (!statuses.includes(reply.status)) return undefined;
  if (reply.status !== "complete" && reply.status !== "incomplete" && reply.directories.length) return undefined;
  if (reply.directories.length > reply.entriesVisited || (reply.status === "complete" && reply.omittedUnsafeEntries)) return undefined;
  const parent = request.directoryPath.replace(/[\\/]$/, "");
  const separator = request.directoryPath.includes("\\") ? "\\" : "/";
  let length = 0;
  const seen = new Set<string>();
  for (const path of reply.directories) {
    if (typeof path !== "string" || !path || path.length > 1024 || /[\u0000-\u001f\u007f]/.test(path)
      || (separator === "\\" && path.includes("/"))
      || path.slice(0, path.lastIndexOf(separator)) !== parent || seen.has(path)) return undefined;
    const name = path.slice(path.lastIndexOf(separator) + 1);
    if (!name || name === "." || name === ".." || !name.toLowerCase().startsWith(request.prefix.toLowerCase())) return undefined;
    try { encodeURIComponent(path); } catch { return undefined; }
    seen.add(path);
    length += path.length;
    if (length > 16384) return undefined;
  }
  return { status: reply.status, directories: reply.directories, entriesVisited: reply.entriesVisited,
    omittedUnsafeEntries: reply.omittedUnsafeEntries };
}
