// Records of the disk usage as the host gives them, for the tests of the model and the page of the tab.
import type { DiskUsageCheckout, DiskUsageGitState, DiskUsageMeasurement, DiskUsageProject, DiskUsageSession } from "#neoastra";

const gib = 1024 ** 3, mib = 1024 ** 2;
export const size = { gib, mib };

export const git = (change: Partial<DiskUsageGitState> = {}): DiskUsageGitState => ({ hasRemote: true, changedFiles: 0, untrackedFiles: 0, branch: "main", detached: false,
  unpushedBranches: [], unpushedBranchesTruncated: false, stashes: 0, detachedCommits: 0, submodulesWithLocalWork: [], hiddenFiles: 0, tagOnlyCommits: 0,
  unexaminedSubmoduleRepositories: 0, operationInProgress: null, hasLocalWork: false, otherWorktreeDetachedCommits: 0, unexaminedBranches: 0, otherRefs: 0, otherRefNames: [],
  ...change });

export function checkout(path: string, bytes: number, change: Partial<DiskUsageCheckout> = {}, build = 0): DiskUsageCheckout {
  return {
    path, name: path.split("/").pop()!, branch: "main", main: false, project: false, locked: false, missing: false, inUse: false, protection: null, projectIds: [],
    runningApplication: false, measure: { totalBytes: String(bytes), totalFiles: "100", gitBytes: "0", buildOutputBytes: String(build), otherIgnoredBytes: "0", otherIgnoredCount: "0",
      skippedEntries: "0", gitKnown: true },
    git: git(), verdict: "nothing_local", lastCommit: "2026-11-01T10:00:00Z", sessionCount: 0, lastUsedAt: "2026-11-01T10:00:00Z",
    planned: build > 0 ? [{ path: "bin", bytes: String(Math.round(build * 0.75)), files: "40", lastWrite: "2026-11-10T08:00:00Z" },
      { path: "obj", bytes: String(build - Math.round(build * 0.75)), files: "20", lastWrite: "2026-11-09T08:00:00Z" }] : [],
    plannedTruncated: false, problem: null, sameFolderAs: null, measureId: `m-${path}`, heldCheckouts: [], worseHeldCheckouts: [], ...change,
  };
}

export const session = (id: string, title: string, bytes: number, change: Partial<DiskUsageSession> = {}): DiskUsageSession =>
  ({ id, title, bytes: String(bytes), updatedAt: "2026-11-12T09:00:00Z", running: false, checkout: null, ...change });

export function project(id: string, name: string, change: Partial<DiskUsageProject> = {}): DiskUsageProject {
  return { id, name, folder: `/code/${id}`, spaces: ["work"], archived: false, status: "ok", problem: null, repository: true, repositoryKey: `/code/${id}/.git`,
    measuredWith: null, sharedWith: [], checkouts: [], checkoutsTruncated: false,
    sessions: { count: 0, bytes: "0", bytesInCheckouts: "0", bytesKnown: true, largest: [], truncated: false }, ...change };
}

/** Two spaces; Alpha with its folder, a large worktree and sessions, in both; Beta a sub-project of Alpha's repository; Gamma that is gone. */
export function measurement(change: Partial<DiskUsageMeasurement> = {}): DiskUsageMeasurement {
  const alpha = project("alpha", "Alpha", { spaces: ["work", "home"], sharedWith: ["beta"],
    checkouts: [
      checkout("/code/alpha", 2 * gib, { project: true, main: true, protection: "main", projectIds: ["alpha"], sessionCount: 4, lastUsedAt: "2026-11-13T09:00:00Z" }, gib),
      checkout("/trees/alpha/quiet-heron", 6 * gib, { branch: "alta/quiet-heron", sessionCount: 1, verdict: "has_local_work",
        git: git({ branch: "alta/quiet-heron", changedFiles: 3, unpushedBranches: [{ name: "alta/quiet-heron", commits: 2 }], hasLocalWork: true }) }, 5 * gib),
      checkout("/trees/alpha/busy-finch", gib, { inUse: true, protection: "in_use", sessionCount: 1, lastUsedAt: "2026-11-14T08:00:00Z" }, 512 * mib),
      checkout("/trees/alpha/lost-otter", 0, { missing: true, measure: null, git: null, verdict: "unknown", measureId: null }),
    ],
    sessions: { count: 3, bytes: String(300 * mib), bytesInCheckouts: "0", bytesKnown: true, truncated: false,
      largest: [session("s1", "Fix the parser", 200 * mib, { checkout: "/trees/alpha/quiet-heron" }), session("s2", "Port the tests", 80 * mib, { running: true, checkout: "/trees/alpha/busy-finch" }),
        session("s3", "Review the release", 20 * mib, { checkout: "/code/alpha" })] } });
  const beta = project("beta", "Beta", { folder: "/code/alpha/beta", measuredWith: "alpha", repositoryKey: "/code/alpha/.git" });
  const gamma = project("gamma", "Gamma", { spaces: ["home"], status: "missing", repository: false, repositoryKey: null });
  // The projects only: their checkouts, and their sessions that no checkout holds.
  const total = 2 * gib + 6 * gib + gib + 300 * mib;
  return { startedAt: "2026-11-14T10:00:00Z", completedAt: "2026-11-14T10:02:00Z", projects: [alpha, beta, gamma], sessionsKnown: true,
    totalBytes: String(total), totalPartial: false, complete: true, unreadable: [], ...change };
}

export const spaces = [{ id: "work", name: "Work" }, { id: "home", name: "Home" }];
