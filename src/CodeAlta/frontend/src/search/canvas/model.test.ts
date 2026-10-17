import assert from "node:assert/strict";
import test from "node:test";
import { exploreView, extraOf, formatSize, historyData, historyRange, hitTarget, indexState, isSearchKind, keptOutCount, kindCounts, moveActive, passProgress,
  projectCells, projectTreemapOption, rankParts, searchKinds, shownDate, snippetParts, sourceLabel, totalItems } from "./model";

const hit = (over: Record<string, unknown>) => ({ kind: "doc", ref: "doc:x", sessionId: null, projectId: null, extra: null, ...over }) as Parameters<typeof hitTarget>[0];
const projects = [{ id: "p1", name: "Alpha", path: "/code/alpha" }, { id: "P2", name: "Beta", path: "C:\\code\\beta\\" }, { id: "p3", name: "Gamma", path: "/code/gamma" }];

test("the kinds are counted the most first, without the empty ones, and summed", () => {
  assert.deepEqual(kindCounts({ message: 30, doc: 30, skill: 4, tool: 0, other: 9 }), [{ kind: "message", count: 30 }, { kind: "doc", count: 30 }, { kind: "skill", count: 4 }],
    "a tie keeps the order of the canvas; a kind the canvas does not know is not listed");
  assert.equal(totalItems({ message: 30, doc: 30, skill: 4, other: 9 }), 73);
  assert.ok(searchKinds.every(isSearchKind));
  assert.equal(isSearchKind("files"), false);
});

test("a size reads in the unit that fits it", () => {
  assert.equal(formatSize(512, "en"), "512 kB");
  assert.equal(formatSize(3.4 * 1024, "en"), "3.4 MB");
  assert.equal(formatSize(52 * 1024, "en"), "52 MB");
  assert.equal(formatSize(1.25 * 1024 * 1024, "en"), "1.3 GB");
});

test("the history is a year of local days, and keeps the days of the range that have turns", () => {
  const range = historyRange(new Date(2026, 9, 17, 22, 30));
  assert.deepEqual(range, { from: "2025-10-18", to: "2026-10-17" });
  assert.deepEqual(historyData([{ day: "2026-10-17", turns: 3 }, { day: "2026-10-16", turns: 0 }, { day: "2024-01-01", turns: 9 }], range), [{ date: "2026-10-17", value: 3 }]);
});

test("the projects of the treemap: sized by their items, and a project kept out gets a small hatched cell of its own", () => {
  const cells = projectCells(projects, { p1: 300, p2: 100, other: 50 }, ["P3"]);
  assert.deepEqual(cells.map(cell => [cell.id, cell.items, cell.value, cell.keptOut]), [["p1", 300, 300, false], ["P2", 100, 100, false], ["p3", 0, 20, true]],
    "ids match whatever their case; a project of another space is not shown; the kept out one comes last");
  assert.deepEqual(projectCells(projects, {}, []), [], "a project with no item that is not kept out is left out");
  assert.equal(projectCells(projects, {}, ["p1"])[0].value, 1, "a kept out project is never an empty cell");
  assert.equal(keptOutCount(projects, ["P1", "p3", "elsewhere"]), 2);

  const { option, table } = projectTreemapOption(cells, ["#111", "#222"], "#999", { name: "Items", keptOut: "kept out", format: value => `${value} items` });
  const data = (option.series[0] as { data: { name: string; itemStyle: Record<string, unknown> }[] }).data;
  assert.deepEqual(data.map(item => item.name), ["Alpha", "Beta", "Gamma · kept out"]);
  assert.equal(data[0].itemStyle.color, "#111");
  assert.equal(data[2].itemStyle.color, "transparent", "without a pattern, the cell of a project kept out is only outlined");
  const hatch = { image: "pattern", repeat: "repeat" };
  const hatched = (projectTreemapOption(cells, ["#111"], "#999", { name: "Items", keptOut: "kept out", format: String }, hatch).option.series[0] as { data: { itemStyle: Record<string, unknown> }[] }).data;
  assert.equal(hatched[2].itemStyle.color, hatch, "the pattern of the page hatches it");
  assert.deepEqual(table.rows, [["Alpha", 300], ["Beta", 100], ["Gamma", "kept out"]]);
  const tooltip = (option.tooltip as { formatter: (params: unknown) => string }).formatter;
  assert.equal(tooltip({ data: { id: "p1" } }), "Alpha<br>300 items");
  assert.equal(tooltip({ data: { id: "p3" } }), "Gamma<br>kept out");
});

test("a hit explains its rank by words and by meaning, and its snippet is cut at its marks", () => {
  assert.deepEqual(rankParts({ wordsRank: 3, meaningRank: null }), [{ key: "words", rank: 3 }]);
  assert.deepEqual(rankParts({ wordsRank: 3, meaningRank: 1 }), [{ key: "words", rank: 3 }, { key: "meaning", rank: 1 }]);
  assert.deepEqual(rankParts({ wordsRank: null, meaningRank: null }), []);

  assert.deepEqual(snippetParts("the retry test waits for the port", [{ start: 29, length: 4 }, { start: 4, length: 5 }]),
    [{ text: "the ", mark: false }, { text: "retry", mark: true }, { text: " test waits for the ", mark: false }, { text: "port", mark: true }]);
  assert.deepEqual(snippetParts("abc", [{ start: 1, length: 1 }, { start: 1, length: 2 }, { start: 9, length: 2 }]),
    [{ text: "a", mark: false }, { text: "b", mark: true }, { text: "c", mark: true }], "an overlap keeps what is left of it, a mark past the end is dropped");
  assert.deepEqual(snippetParts("", []), []);
});

test("a hit leads where the search window would open it", () => {
  assert.deepEqual(hitTarget(hit({ kind: "message", ref: "msg:s1/c1", sessionId: "s1" }), projects), { kind: "message", ref: "msg:s1/c1", sessionId: "s1" });
  assert.deepEqual(hitTarget(hit({ kind: "note", sessionId: "s1" }), projects), { kind: "session", sessionId: "s1" });
  assert.deepEqual(hitTarget(hit({ kind: "plan", projectId: "p1", extra: '{"id":"2026-10-17-pelican"}' }), projects), { kind: "file", path: "/code/alpha/.alta/plans/2026-10-17-pelican.md" });
  assert.deepEqual(hitTarget(hit({ kind: "task", projectId: "p2", extra: '{"id":"fix-it"}' }), projects), { kind: "file", path: "C:\\code\\beta\\.alta\\tasks\\fix-it.md" },
    "the folder of the project, with its own separators");
  assert.equal(hitTarget(hit({ kind: "plan", projectId: "p1", extra: '{"id":"../secret"}' }), projects), null, "an id is a file name, never a path");
  assert.equal(hitTarget(hit({ kind: "plan", projectId: "gone", extra: '{"id":"a"}' }), projects), null);
  assert.deepEqual(hitTarget(hit({ kind: "doc", extra: '{"page":"worktrees.md","anchor":"cleanup"}' }), projects), { kind: "documentation", page: "worktrees.md", anchor: "cleanup" });
  assert.deepEqual(hitTarget(hit({ kind: "doc", extra: '{"api":"PluginBase"}' }), projects), { kind: "documentation", page: null, anchor: null });
  assert.deepEqual(hitTarget(hit({ kind: "skill", extra: '{"name":"pdf","path":"/skills/pdf/SKILL.md"}' }), projects), { kind: "file", path: "/skills/pdf/SKILL.md" });
  assert.deepEqual(hitTarget(hit({ kind: "command", extra: '{"id":"palette"}' }), projects), { kind: "command", id: "palette" });
  for (const kind of ["tool", "mcp", "plugin"]) assert.equal(hitTarget(hit({ kind }), projects), null, kind);
  assert.deepEqual(extraOf('{"a":"x","b":2,"c":null}'), { a: "x" });
  assert.deepEqual(extraOf("not json"), {});
  assert.deepEqual(extraOf("[1]"), {});
});

test("the state of the index, its progress, its sources and its dates", () => {
  const status = (over: Record<string, unknown>) => ({ busy: false, ready: true, settings: { enabled: true }, ...over });
  assert.equal(indexState(status({ settings: { enabled: false } })), "off");
  assert.equal(indexState(status({ ready: false })), "starting");
  assert.equal(indexState(status({ ready: false, busy: true })), "building");
  assert.equal(indexState(status({ busy: true })), "updating");
  assert.equal(indexState(status({})), "ready");
  assert.equal(passProgress({ journalsDone: 5, journalsTotal: 20 }), 0.25);
  assert.equal(passProgress({ journalsDone: 0, journalsTotal: 0 }), null);
  assert.equal(passProgress({ journalsDone: 30, journalsTotal: 20 }), 1);
  assert.equal(sourceLabel("journal"), "Session journals");
  assert.equal(sourceLabel("something"), null);
  assert.equal(shownDate("1970-01-01T00:00:00+00:00"), null, "the catalog has no date");
  assert.equal(shownDate("not a date"), null);
  assert.equal(shownDate("2026-10-17T09:00:00+00:00")?.toISOString(), "2026-10-17T09:00:00.000Z");
});

test("Explore shows the hits of a query, the items of a kind, or the kinds; the keyboard wraps around the hits", () => {
  assert.equal(exploreView("  retry ", null), "query");
  assert.equal(exploreView(" ", "skill"), "browse");
  assert.equal(exploreView("", null), "kinds");
  assert.equal(moveActive(-1, 3, 1), 0);
  assert.equal(moveActive(-1, 3, -1), 2);
  assert.equal(moveActive(2, 3, 1), 0);
  assert.equal(moveActive(0, 3, -1), 2);
  assert.equal(moveActive(0, 0, 1), -1);
});
