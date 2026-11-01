import assert from "node:assert/strict";
import test from "node:test";
import { builtInSettings, type SettingEntry } from "./settingsCatalog";
import { settingProject, settingValueSource, writeExpired } from "./settingValues";

const plugin = (scopes: readonly string[], project: string | null): SettingEntry => Object.freeze({
  ...builtInSettings[0], id: "plugins.builds.branch", owner: "plugin", kind: "text", scopes, project, plugin: "builds", builtIn: false,
});

test("a setting a project can have is read for the selected project, a plugin of a project for its own", () => {
  assert.equal(settingProject(plugin(["user"], null), undefined, "selected"), null, "a setting of the user");
  assert.equal(settingProject(plugin(["project"], null), undefined, "selected"), "selected");
  assert.equal(settingProject(plugin(["project"], null), "named", "selected"), "named", "the project named, as Undo names the one changed");
  assert.equal(settingProject(plugin(["project"], "own"), "named", "selected"), "own", "a plugin of a project has no value for another");
});

test("the source passes the project to the host", async () => {
  const asked: (string | null)[] = [];
  const hub = {
    get: async (_id: string, projectId: string | null) => { asked.push(projectId); return Object.freeze({ status: "ok", value: "main", source: "default", message: null, choices: null }); },
    set: async (_id: string, _value: unknown, projectId: string | null) => { asked.push(projectId); return Object.freeze({ status: "ok", value: "main", previous: null, message: null }); },
  };
  const source = settingValueSource(hub, new Map(), "selected");
  await source.get(plugin(["project"], null));
  await source.set(plugin(["project"], null), "release", "changed");
  assert.deepEqual(asked, ["selected", "changed"]);
});

test("a change the host asked for is not made once the host stopped waiting for it", () => {
  assert.equal(writeExpired(null, 5), false);
  assert.equal(writeExpired("1000", 999), false);
  assert.equal(writeExpired("1000", 1001), true);
  assert.equal(writeExpired("soon", 1001), false, "a deadline that is not a number is not one");
});
