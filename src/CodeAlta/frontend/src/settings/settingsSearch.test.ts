import assert from "node:assert/strict";
import test from "node:test";
import { builtInSettings } from "./settingsCatalog";
import { foldText, groupResults, matchCounts, searchSettings, settingResults } from "./settingsSearch";

const ids = (query: string, locale = "en") => searchSettings(builtInSettings, query, locale).settings.map(match => match.entry.id);

test("a word of the label finds a setting before a word of its help or of its page", () => {
  const found = ids("title");
  assert.equal(found[0], "sessions.title", "the label first");
  assert.ok(found.includes("sessions.title"));
  assert.ok(ids("keep awake").includes("general.keep_awake"));
  assert.deepEqual(ids("zzzz"), []);
});

test("every word must be found, in any text of the setting", () => {
  assert.deepEqual(ids("suggest prompt"), ["sessions.prompt_suggestions"]);
  assert.ok(!ids("suggest worktree").length);
});

test("the shown language and English both find a setting, accents aside", () => {
  assert.ok(ids("thème", "fr").includes("appearance.theme"), "in French");
  assert.ok(ids("theme", "fr").includes("appearance.theme"), "without the accent");
  assert.ok(ids("theme", "ja").includes("appearance.theme"), "in English whatever the language shown");
  assert.equal(foldText("Paramètres"), "parametres");
});

test("keywords find what its label does not say", () => {
  assert.ok(ids("dark mode").includes("appearance.theme"));
  assert.ok(ids("tailscale").includes("mcp_host.tailnet"));
});

test("pages are found by their name and their words; the results group by page in the order of the navigation", () => {
  const result = searchSettings(builtInSettings, "providers", "en");
  assert.equal(result.pages[0]?.page.id, "providers");
  const fallback = searchSettings(builtInSettings, "fallback", "en");
  const groups = groupResults(fallback, builtInSettings);
  assert.equal(groups[0].page, "models");
  assert.deepEqual(groups[0].settings.map(entry => entry.id).slice(0, 2), ["models.fallback.enabled", "models.fallback.on_quota"], "in the order of the page");
  const counts = matchCounts(fallback);
  assert.ok((counts.get("models") ?? 0) >= 5);
});

test("a setting that is not here is not found", () => {
  const entries = builtInSettings.map(entry => entry.id === "general.keep_awake" ? { ...entry, available: false } : entry);
  assert.ok(!searchSettings(entries, "keep awake", "en").settings.some(match => match.entry.id === "general.keep_awake"));
});

test("the search of the window gets the settings and the pages, and opens them", () => {
  const opened: string[] = [];
  const results = settingResults(builtInSettings, "keep awake", "en", (page, setting) => opened.push(`${page}/${setting ?? ""}`));
  assert.equal(results[0].label, "Keep computer awake when CodeAlta is running");
  assert.equal(results[0].path, "Personalization › General");
  results[0].run();
  assert.deepEqual(opened, ["general/general.keep_awake"]);
});
