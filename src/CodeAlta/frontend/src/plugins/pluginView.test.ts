import assert from "node:assert/strict";
import test from "node:test";
import { hasPluginFilters, matchesPluginFilters, matchesPluginQuery, noPluginFilters, persistPluginView, pluginAccent, pluginSource, pluginSourceCounts, restorePluginView,
  type PluginFacts } from "./pluginView";

const plugin = (id: string, change: Partial<PluginFacts> = {}): PluginFacts => ({ id, name: id, description: null, enabled: true, builtIn: false, project: false, ...change });
const all = [
  plugin("mcp", { name: "MCP", description: "Connects Model Context Protocol servers and exposes their tools.", builtIn: true }),
  plugin("mobile", { name: "Mobile", description: "The sessions of this computer on a phone.", builtIn: true, enabled: false }),
  plugin("commits", { name: "Commits", description: "Shows the commits of a project in a canvas.", words: ["Ada", "git", "History"] }),
  plugin("lint", { name: "Lint", project: true, enabled: false }),
];

test("the pictures of the cards are on until the user turns them off, and what is kept is read back", () => {
  assert.deepEqual(restorePluginView(() => null), { images: true });
  assert.deepEqual(restorePluginView(() => "not json"), { images: true });
  assert.deepEqual(restorePluginView(() => JSON.stringify({ images: "no" })), { images: true }, "a value of another kind is not a choice");
  assert.deepEqual(restorePluginView(() => { throw new Error("no storage"); }), { images: true });
  let kept = "";
  persistPluginView(value => { kept = value; }, { images: false });
  assert.deepEqual(restorePluginView(() => kept), { images: false });
  assert.doesNotThrow(() => persistPluginView(() => { throw new Error("full"); }, { images: false }));
});

test("a search finds a plugin by every word, in its name, its id, its description and what else names it", () => {
  const found = (query: string) => all.filter(value => matchesPluginQuery(value, query)).map(value => value.id);
  assert.deepEqual(found(""), ["mcp", "mobile", "commits", "lint"]);
  assert.deepEqual(found("  "), ["mcp", "mobile", "commits", "lint"]);
  assert.deepEqual(found("PROTOCOL"), ["mcp"], "the case does not matter");
  assert.deepEqual(found("project canvas"), ["commits"], "every word, wherever it is");
  assert.deepEqual(found("ada"), ["commits"], "the author");
  assert.deepEqual(found("history"), ["commits"], "the title of a canvas");
  assert.deepEqual(found("lint"), ["lint"], "the id of a plugin that says nothing else");
  assert.deepEqual(found("phone tools"), [], "words of two plugins find none");
});

test("the filters keep the plugins of one source and of one state, and count what each source has", () => {
  assert.deepEqual(all.map(pluginSource), ["builtIn", "builtIn", "user", "project"]);
  const kept = (filters: Partial<typeof noPluginFilters>) => all.filter(value => matchesPluginFilters(value, { ...noPluginFilters, ...filters })).map(value => value.id);
  assert.deepEqual(kept({}), ["mcp", "mobile", "commits", "lint"]);
  assert.deepEqual(kept({ source: "builtIn" }), ["mcp", "mobile"]);
  assert.deepEqual(kept({ source: "user" }), ["commits"]);
  assert.deepEqual(kept({ source: "project" }), ["lint"]);
  assert.deepEqual(kept({ state: "on" }), ["mcp", "commits"]);
  assert.deepEqual(kept({ state: "off", source: "builtIn" }), ["mobile"]);
  assert.deepEqual(kept({ state: "on", query: "commits" }), ["commits"]);
  // The counts follow the search and the state, not the source that is chosen: they say what each choice would show.
  assert.deepEqual(pluginSourceCounts(all, noPluginFilters), { all: 4, builtIn: 2, user: 1, project: 1 });
  assert.deepEqual(pluginSourceCounts(all, { ...noPluginFilters, state: "off", source: "user" }), { all: 2, builtIn: 1, user: 0, project: 1 });
  assert.deepEqual(pluginSourceCounts(all, { ...noPluginFilters, query: "sessions" }), { all: 1, builtIn: 1, user: 0, project: 0 });
  assert.equal(hasPluginFilters(noPluginFilters), false);
  assert.equal(hasPluginFilters({ ...noPluginFilters, query: " " }), false);
  assert.ok(hasPluginFilters({ ...noPluginFilters, query: "x" }) && hasPluginFilters({ ...noPluginFilters, source: "user" }) && hasPluginFilters({ ...noPluginFilters, state: "off" }));
});

test("a plugin has the color it gives, or one of its own that it keeps", () => {
  assert.equal(pluginAccent("notes", "#ff8800"), "#ff8800");
  assert.equal(pluginAccent("notes", "#F80"), "#F80");
  for (const refused of ["red", "#12", "#12345g", "", null, undefined]) assert.match(pluginAccent("notes", refused), /^#[0-9a-f]{6}$/u, String(refused));
  assert.equal(pluginAccent("notes"), pluginAccent("Notes"), "the same plugin, the same color");
  assert.ok(new Set(["notes", "commits", "lint", "board", "deploy", "timer"].map(id => pluginAccent(id))).size > 1);
});
