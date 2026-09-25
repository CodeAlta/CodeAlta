import assert from "node:assert/strict";
import test from "node:test";
import { visibleConfigurationSections } from "./configurationSections";

test("configuration navigation scopes sections without changing backend state", () => {
  assert.deepEqual(visibleConfigurationSections("general", ""), ["appearance", "logs", "providers", "about"]);
  assert.deepEqual(visibleConfigurationSections("agent", ""), ["prompts", "skills"]);
  assert.deepEqual(visibleConfigurationSections("extensions", ""), ["plugins"]);
});

test("configuration search matches all terms within the selected scope", () => {
  assert.deepEqual(visibleConfigurationSections("all", "default model"), ["providers"]);
  assert.deepEqual(visibleConfigurationSections("extensions", "MCP tool"), ["plugins"]);
  assert.deepEqual(visibleConfigurationSections("agent", "theme"), []);
  assert.deepEqual(visibleConfigurationSections("all", "  SYSTEM   instructions "), ["prompts"]);
  assert.deepEqual(visibleConfigurationSections("general", "application logs"), ["logs"]);
});
