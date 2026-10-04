/** The git facts shown beside the composer: the branch (or a short commit when detached) and the changes against HEAD. */
export type ProjectGitView = Readonly<{ branch: string; detached: boolean;
  changes: Readonly<{ insertions: number; deletions: number; files: number }> | null }>;

const count = (value: unknown): value is number => Number.isInteger(value) && (value as number) >= 0;

/** Accepts a well-formed `projectGit.status` answer for the asked project; anything else shows nothing. */
export function projectGitStatus(reply: unknown, projectId: string): ProjectGitView | null {
  if (!reply || typeof reply !== "object") return null;
  const value = reply as Record<string, unknown>;
  if (value.status !== "ok" || value.projectId !== projectId || typeof value.branch !== "string" || !value.branch
    || value.branch.length > 256 || typeof value.detached !== "boolean") return null;
  const changes = count(value.insertions) && count(value.deletions) && count(value.changedFiles)
    ? { insertions: value.insertions, deletions: value.deletions, files: value.changedFiles } : null;
  return { branch: value.branch, detached: value.detached, changes };
}
