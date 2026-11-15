import assert from "node:assert/strict";
import test from "node:test";
import { locales, translate, type MessageKey } from "../localization";
import { checkoutsByPath, cleanBlock, cleanBlockText, cleanStatusText, levelDonut, measurementView, nodePath, outcomeText, sizeText, sortNodes, sumSizes, usageTree, verdictLabel, verdictReasons,
  withCheckout, withProject, type UsageNode } from "./diskUsage";
import { checkout, git, measurement, project, size, spaces } from "./diskUsageFixtures";
import { diskUsageMessages } from "./messages";

const t = (key: MessageKey, parameters?: Readonly<Record<string, string | number>>) => translate("en", key, parameters);
const { gib, mib } = size;
const child = (node: UsageNode, id: string) => {
  const found = node.children.find(value => value.id === id);
  assert.ok(found, `${node.id} has ${id}: ${node.children.map(value => value.id).join(", ")}`);
  return found;
};

test("the tree goes space, project, then its folder, its worktrees and its sessions", () => {
  const root = usageTree(measurementView(measurement()), spaces);
  assert.deepEqual(root.children.map(node => [node.kind, node.name]), [["space", "Work"], ["space", "Home"]]);
  const work = child(root, "space:work");
  assert.deepEqual(work.children.map(node => node.name), ["Alpha", "Beta"]);
  const alpha = child(work, "space:work/project:alpha");
  assert.deepEqual(alpha.children.map(node => node.kind), ["checkout", "worktrees", "sessions"]);
  const folder = alpha.children[0];
  assert.equal(folder.name, "Alpha", "the folder of the project carries its name");
  assert.deepEqual([folder.size, folder.buildOutput], [{ bytes: 2 * gib, state: "exact" }, { bytes: gib, state: "exact" }]);
  const worktrees = child(alpha, "space:work/project:alpha/worktrees");
  assert.deepEqual(worktrees.children.map(node => node.name), ["quiet-heron", "busy-finch", "lost-otter"]);
  assert.deepEqual(worktrees.size, { bytes: 7 * gib, state: "exact" }, "a folder that is gone holds nothing");
  assert.equal(worktrees.verdict, null, "a row of several checkouts has no verdict: its rows say theirs");
  const sessions = child(alpha, "space:work/project:alpha/sessions");
  assert.deepEqual([sessions.size.bytes, sessions.lastUsed], [300 * mib, "2026-11-12T09:00:00Z"]);
  assert.deepEqual(sessions.children.map(node => [node.kind, node.name, node.session?.checkout]),
    [["session", "Fix the parser", "/trees/alpha/quiet-heron"], ["session", "Port the tests", "/trees/alpha/busy-finch"], ["session", "Review the release", "/code/alpha"]]);
  assert.deepEqual(alpha.size, { bytes: 9 * gib + 300 * mib, state: "exact" });
  assert.equal(alpha.lastUsed, "2026-11-14T08:00:00Z", "last used is the latest of its checkouts and sessions");
  assert.deepEqual(alpha.sharedWith, ["Beta"], "the sub-projects are named on the project that carries the repository");
  // The breadcrumb follows ids that stay while records arrive.
  assert.deepEqual(nodePath(root, ["space:work", "space:work/project:alpha", "gone"]).map(node => node.id), ["root", "space:work", "space:work/project:alpha"]);
});

test("a project in two spaces is in each, and the total counts it once: the one of the measurement", () => {
  const value = measurement();
  const root = usageTree(measurementView(value), spaces);
  const inWork = child(child(root, "space:work"), "space:work/project:alpha"), inHome = child(child(root, "space:home"), "space:home/project:alpha");
  assert.deepEqual(inWork.size, inHome.size);
  assert.deepEqual(root.size, { bytes: Number(value.totalBytes), state: "exact" });
  assert.ok(root.children.reduce((sum, node) => sum + node.counted, 0) > Number(value.totalBytes), "the spaces show Alpha twice");
  // While records arrive the total is what arrived, each project once, and a lower bound.
  const arriving = usageTree({ projects: value.projects, done: null }, spaces);
  assert.deepEqual(arriving.size, { bytes: 9 * gib + 300 * mib, state: "atLeast" });
  assert.equal(usageTree({ ...measurementView(value), done: { ...value, totalPartial: true } }, spaces).size.state, "atLeast");
});

test("the default space holds every project, including projects without explicit memberships", () => {
  const value = measurement();
  const projects = value.projects.map((project, index) => index === 0 ? { ...project, spaces: [] } : project);
  const root = usageTree(measurementView({ ...value, projects }), [{ id: "default", name: "All projects" }, ...spaces]);
  const all = child(root, "space:default");
  assert.deepEqual(all.children.map(node => node.project?.id), value.projects.map(project => project.id));
  assert.equal(root.children.some(node => node.id === "space:"), false, "every project belongs to Default");
  assert.equal(child(root, "space:work").children.some(node => node.project?.id === value.projects[0].id), false,
    "other spaces still require explicit membership");
  assert.deepEqual(root.size, { bytes: Number(value.totalBytes), state: "exact" }, "the total still counts projects once");
});

test("a project that shares a repository names the one measured with it and adds only its sessions", () => {
  const root = usageTree(measurementView(measurement()), spaces);
  const beta = child(child(root, "space:work"), "space:work/project:beta");
  assert.equal(beta.measuredWith, "Alpha");
  assert.deepEqual(beta.children.map(node => node.kind), ["sessions"]);
  assert.deepEqual([beta.size, beta.counted], [{ bytes: 0, state: "exact" }, 0]);
});

test("what is not measured is unknown, never 0, and makes its sums a lower bound", () => {
  const broken = checkout("/trees/alpha/unread", 0, { measure: null, git: null, verdict: "unknown", measureId: null, problem: "Access denied." });
  const record = project("delta", "Delta", { checkouts: [checkout("/code/delta", 3 * mib, { project: true }), broken] });
  const failed = project("epsilon", "Epsilon", { status: "failed", problem: "The folder could not be listed.", checkouts: [] });
  const noSessions = project("zeta", "Zeta", { checkouts: [checkout("/code/zeta", mib, { project: true })], sessions: { count: 2, bytes: "0", bytesInCheckouts: "0", bytesKnown: false, largest: [], truncated: false } });
  const root = usageTree({ projects: [record, failed, noSessions], done: null }, [{ id: "work", name: "Work" }]);
  const work = child(root, "space:work");
  const delta = child(work, "space:work/project:delta");
  const unread = child(child(delta, "space:work/project:delta/worktrees"), "space:work/project:delta/worktrees/checkout:/trees/alpha/unread");
  assert.deepEqual([unread.size.state, unread.buildOutput.state, unread.counted], ["unknown", "unknown", 0]);
  assert.deepEqual(delta.size, { bytes: 3 * mib, state: "atLeast" });
  assert.equal(child(work, "space:work/project:epsilon").size.state, "unknown");
  assert.equal(child(child(work, "space:work/project:zeta"), "space:work/project:zeta/sessions").size.state, "unknown", "sessions whose sizes could not be read");
  assert.equal(sizeText(unread.size, "en", t), "Unknown");
  assert.equal(sizeText(delta.size, "en", t), "at least 3 MiB");
  assert.equal(sizeText({ bytes: 0, state: "none" }, "en", t), "—");
  assert.deepEqual(sumSizes([]), { bytes: 0, state: "none" });
});

test("the measurement is of the projects: the top level holds the spaces alone", () => {
  const value = measurement();
  const root = usageTree(measurementView(value), spaces);
  assert.ok(root.children.every(node => node.kind === "space"));
  assert.deepEqual(root.size, { bytes: 9 * gib + 300 * mib, state: "exact" });
  assert.equal(usageTree({ projects: [], done: null }, spaces).children.length, 0, "no project, no level");
});

test("a level sorts by any column, what is not known last in both directions", () => {
  const root = usageTree(measurementView(measurement()), spaces);
  const worktrees = child(child(child(root, "space:work"), "space:work/project:alpha"), "space:work/project:alpha/worktrees");
  const unknown = { ...worktrees.children[0], id: "x", name: "unknown", size: { bytes: 0, state: "unknown" as const }, lastUsed: null, verdict: null };
  const nodes = [...worktrees.children, unknown];
  const names = (column: Parameters<typeof sortNodes>[1]["column"], descending: boolean) => sortNodes(nodes, { column, descending }).map(node => node.name);
  assert.deepEqual(names("size", true), ["quiet-heron", "busy-finch", "lost-otter", "unknown"]);
  assert.deepEqual(names("size", false), ["lost-otter", "busy-finch", "quiet-heron", "unknown"]);
  assert.deepEqual(names("name", false), ["busy-finch", "lost-otter", "quiet-heron", "unknown"]);
  assert.deepEqual(names("lastUsed", true), ["busy-finch", "lost-otter", "quiet-heron", "unknown"], "equal times keep the order of the names");
  assert.deepEqual(names("verdict", true).slice(0, 2), ["quiet-heron", "busy-finch"]);
});

test("the verdict of a checkout says why, in sentences", () => {
  const value = measurement();
  const heron = value.projects[0].checkouts[1];
  assert.equal(verdictLabel("has_local_work", t), "Has local work");
  assert.deepEqual(verdictReasons(heron, t, "en"), ["3 changed files", "Branches with commits on no remote: alta/quiet-heron (2)"]);
  const every = checkout("/x", mib, { verdict: "has_local_work", measure: { ...heron.measure!, otherIgnoredBytes: String(2 * mib), otherIgnoredCount: "2", skippedEntries: "4" },
    git: git({ hasRemote: false, untrackedFiles: 1, stashes: 2, detached: true, detachedCommits: 5, submodulesWithLocalWork: ["lib/a"], hiddenFiles: 1, tagOnlyCommits: 3,
      unexaminedSubmoduleRepositories: 1, operationInProgress: "rebase", unpushedBranches: [{ name: "x", commits: 1 }], unpushedBranchesTruncated: true }) });
  assert.deepEqual(verdictReasons(every, t, "en"), ["The repository has no remote.", "1 untracked file", "Branches with commits on no remote: x (1), and more", "2 stashes",
    "A detached HEAD on 5 commits of no branch", "Submodules with local work: lib/a", "1 files git is told not to look at", "3 commits only a tag holds",
    "1 submodule repositories that are not checked out", "A rebase is in progress.",
    "2 MiB ignored outside build folders, in 2 entries: a clone does not bring it back.", "4 entries could not be read: the sizes are a lower bound."]);
  assert.deepEqual(verdictReasons(checkout("/y", 0, { measure: null, git: null, problem: "Denied." }), t, "en"), ["It could not be measured: Denied."]);
  assert.deepEqual(verdictReasons(value.projects[0].checkouts[3], t, "en"), ["Git still lists this worktree, and its folder is gone."]);
});

test("every verdict but Nothing local says why: the new reasons of git, and the checkouts a folder holds", () => {
  const heron = measurement().projects[0].checkouts[1];
  const refs = checkout("/r", mib, { verdict: "has_local_work", git: git({ hasLocalWork: true, otherWorktreeDetachedCommits: 4, unexaminedBranches: 120, otherRefs: 7,
    otherRefNames: ["refs/notes/commits", "refs/pull/1/head"] }) });
  assert.deepEqual(verdictReasons(refs, t, "en"), ["4 commits only the detached HEADs of other worktrees hold", "120 branches were not examined: there are more than 100.",
    "7 other references, among them refs/notes/commits, refs/pull/1/head"]);
  assert.deepEqual(verdictReasons(checkout("/s", mib, { verdict: "has_local_work", git: git({ hasLocalWork: true, otherRefs: 1, otherRefNames: ["refs/notes/commits"] }) }), t, "en"),
    ["Other references: refs/notes/commits"]);
  // A project folder that holds worktrees takes their worse verdict, and names them.
  const held = new Map([[heron.path, heron], ["/trees/alpha/odd", checkout("/trees/alpha/odd", 0, { verdict: "ignored_files_only" })],
    ["/trees/alpha/dark", checkout("/trees/alpha/dark", 0, { verdict: "unknown" })]]);
  const folder = checkout("/code/alpha", gib, { project: true, verdict: "has_local_work", heldCheckouts: [...held.keys()], worseHeldCheckouts: [...held.keys(), "/elsewhere/gone"] });
  assert.deepEqual(verdictReasons(folder, t, "en", path => held.get(path)), ["It holds the checkout quiet-heron, which has local work.",
    "It holds the checkout odd, which holds ignored files outside build folders.", "It holds the checkout dark, whose git state is unknown.",
    "It holds the checkout gone, whose git state is unknown."]);
  // A verdict that is not Nothing local is never without a reason.
  for (const verdict of ["has_local_work", "ignored_files_only", "unknown"])
    assert.ok(verdictReasons(checkout("/q", mib, { verdict }), t, "en").length > 0, verdict);
  assert.deepEqual(verdictReasons(checkout("/n", mib), t, "en"), []);
  // The checkouts of every project, by their path, for the names of the ones a folder holds.
  assert.equal(checkoutsByPath(measurementView(measurement())).get("/trees/alpha/quiet-heron")?.name, "quiet-heron");
});

test("a checkout can be chosen for a clean only when measured in this run, with build output, and nothing protects it", () => {
  const [folder, heron, busy, lost] = measurement().projects[0].checkouts;
  assert.equal(cleanBlock(heron, true), null);
  assert.equal(cleanBlock(folder, true), null, "the folder of a project can lose its build output");
  assert.equal(cleanBlock(heron, false), "not_measured");
  assert.equal(cleanBlock(busy, true), "in_use");
  assert.equal(cleanBlock(lost, true), "missing");
  assert.equal(cleanBlock({ ...heron, runningApplication: true }, true), "running_application");
  assert.equal(cleanBlock({ ...heron, sameFolderAs: "/x" }, true), "same_folder");
  assert.equal(cleanBlock({ ...heron, planned: [] }, true), "no_build_output");
  assert.equal(cleanBlock({ ...heron, measureId: null }, true), "not_measured");
  for (const block of ["missing", "same_folder", "in_use", "running_application", "not_measured", "no_build_output"] as const) assert.ok(cleanBlockText(block, t).length > 0);
});

test("every refusal and outcome of a clean has a text of its own", () => {
  assert.equal(cleanStatusText("ok", null, t), null);
  assert.equal(cleanStatusText("failed", "fatal: bad", t), "Git failed: fatal: bad");
  const statuses = ["canceled", "checkout_changed", "not_repository", "git_too_old", "busy", "running_application", "unavailable", "stale_epoch", "invalid_request", "not_found",
    "not_measured", "stale_measure", "scanning", "cleaning", "unconfirmed"];
  const texts = statuses.map(status => cleanStatusText(status, null, t));
  assert.equal(new Set(texts).size, statuses.length, "each says something else");
  assert.ok(!texts.includes(cleanStatusText("something_new", null, t)));
  const outcomes = ["removed", "partly_removed", "kept_holds_repository", "kept_below_link", "kept_not_build_output", "not_attempted", "unknown"];
  assert.equal(new Set(outcomes.map(outcome => outcomeText(outcome, t))).size, outcomes.length);
});

test("records that arrive and a checkout cleaned replace what the view held", () => {
  const value = measurement();
  const view = withProject(withProject({ projects: [], done: null }, value.projects[0]), value.projects[1]);
  assert.deepEqual(view.projects.map(record => record.id), ["alpha", "beta"]);
  assert.equal(withProject(view, { ...value.projects[0], name: "Renamed" }).projects[0].name, "Renamed");
  const cleaned = { ...value.projects[0].checkouts[1], measure: { ...value.projects[0].checkouts[1].measure!, totalBytes: String(gib), buildOutputBytes: "0" }, planned: [] };
  const after = withCheckout(view, "alpha", cleaned);
  assert.equal(after.projects[0].checkouts[1], cleaned);
  assert.equal(after.projects[1], view.projects[1]);
});

test("the donut of a level gives the nine largest rows their own color and the others one segment, without what adds nothing", () => {
  const root = usageTree(measurementView(measurement()), spaces);
  const alpha = child(child(root, "space:work"), "space:work/project:alpha");
  const worktrees = child(alpha, "space:work/project:alpha/worktrees");
  const donut = levelDonut(worktrees.children);
  assert.equal(donut.slices.reduce((total, slice) => total + slice.value, 0), 7 * gib);
  assert.deepEqual(donut.slices.map(slice => [slice.id, slice.value, slice.color]), [
    ["space:work/project:alpha/worktrees/checkout:/trees/alpha/quiet-heron", 6 * gib, 0], ["space:work/project:alpha/worktrees/checkout:/trees/alpha/busy-finch", gib, 1]]);
  assert.equal(donut.colors.get("space:work/project:alpha/worktrees/checkout:/trees/alpha/lost-otter"), undefined, "a folder that holds nothing has no segment");
  // Twelve rows: nine of their own, three in Other with its color.
  const many = Array.from({ length: 12 }, (_, index): UsageNode => ({ ...worktrees.children[0], id: `n${index}`, name: `n${index}`, counted: (12 - index) * mib,
    size: { bytes: (12 - index) * mib, state: "exact" } }));
  const unknown: UsageNode = { ...many[0], id: "unknown", counted: 0, size: { bytes: 0, state: "unknown" } };
  const split = levelDonut([unknown, ...[...many].reverse()]);
  assert.deepEqual(split.slices.map(slice => [slice.id, slice.color, slice.nodes.length]),
    [...many.slice(0, 9).map((node, index) => [node.id, index, 1]), [null, 9, 3]]);
  assert.equal(split.slices.at(-1)!.value, (3 + 2 + 1) * mib);
  assert.deepEqual(["n9", "n10", "n11"].map(id => split.colors.get(id)), [9, 9, 9], "the rows in Other carry its color");
  assert.equal(split.colors.get("unknown"), undefined);
  // Ten rows fit the ten colors: none is put in Other.
  assert.deepEqual(levelDonut(many.slice(0, 10)).slices.map(slice => slice.id), many.slice(0, 10).map(node => node.id));
});

test("the words of the disk usage are in every language", () => {
  for (const [key, row] of Object.entries(diskUsageMessages)) {
    assert.equal(row.length, locales.length - 1, key);
    const parameters = [...key.matchAll(/\{(\w+)\}/g)].map(match => match[1]).sort();
    for (const text of row) assert.deepEqual([...text.matchAll(/\{(\w+)\}/g)].map(match => match[1]).sort(), parameters, `${key} → ${text}`);
  }
});
