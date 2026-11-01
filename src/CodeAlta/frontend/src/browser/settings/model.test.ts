import assert from "node:assert/strict";
import test from "node:test";
import { isProfileName, persistentName, profileKind, readBrowserSettings, readSite, withSite } from "./model";

const edge = { id: "edge", name: "Microsoft Edge", version: "156.0.3485.54", engine: "chromium", path: "C:/Edge/msedge.exe", isDefault: true,
  lastUsed: "2026-11-01T10:05:00Z", measurable: true, supportsUserProfile: true };
const answer = {
  browsers: [edge], browser: "auto", engine: null, profile: "isolated", headless: false, evaluateScript: true,
  user: { enabled: false, allowedSites: ["github.com"], blockedSites: [] },
  choice: { id: "edge", name: "Microsoft Edge", source: "auto", reason: "the default browser" }, automatic: { id: "edge", name: "Microsoft Edge", source: "auto", reason: "the default browser" },
  refusal: null, persistentProfiles: ["work"], error: null, notice: null,
};

test("the settings of the plugin are read for what they say, and anything else is no settings", () => {
  assert.deepEqual(readBrowserSettings(answer), answer);
  assert.equal(readBrowserSettings({ ...answer, choice: null })?.choice, null, "no browser fits");
  assert.equal(readBrowserSettings({ ...answer, browsers: [{ ...edge, lastUsed: "yesterday" }] })?.browsers[0].lastUsed, null, "a date that is no date is no evidence");
  assert.equal(readBrowserSettings({ ...answer, error: "" })?.error, null, "an empty text is no text");
  for (const bad of [null, "settings", { ...answer, browsers: null }, { ...answer, browsers: [{ ...edge, engine: "webkit" }] }, { ...answer, headless: "no" },
    { ...answer, user: { enabled: true, allowedSites: [3], blockedSites: [] } }, { ...answer, persistentProfiles: undefined }]) {
    assert.equal(readBrowserSettings(bad), null, JSON.stringify(bad));
  }
});

test("a profile setting is isolated, persistent with its name, or the user's own browser", () => {
  assert.deepEqual(["isolated", "persistent", "persistent:work", "user", "anything"].map(profileKind), ["isolated", "persistent", "persistent", "user", "isolated"]);
  assert.deepEqual(["persistent", "persistent:work"].map(persistentName), ["default", "work"]);
  assert.deepEqual(["work", "Work_2", "a-b", "", "a b", "../x", "x".repeat(65)].map(isProfileName), [true, true, true, false, false, false, false]);
});

test("a site is a host without www, typed or taken from an address, and a list keeps each once", () => {
  assert.deepEqual(["GitHub.com", "https://www.github.com/login", " www.example.org/ ", "bücher.example", "ftp://x.com", "a b", "", "http://"].map(readSite),
    ["github.com", "github.com", "example.org", "xn--bcher-kva.example", null, null, null, null]);
  assert.deepEqual(withSite(["b.com", "a.com"], "a.com"), ["a.com", "b.com"]);
});
