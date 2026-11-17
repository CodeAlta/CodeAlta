import assert from "node:assert/strict";
import test from "node:test";
import type { PseudonymizerModelsResponse } from "#neoastra";
import {
  canSaveProjectSecurity, defaultSecurityLanguage, makeProjectNormal, pseudonymizerPluginId, readProjectSecurity, saveProjectSecurity, secureProject, securityLines,
  type ProjectSecurityApi,
} from "./projectSecurity";

const epoch = "11111111-1111-4111-8111-111111111111";
const models = (state = "idle", language: string | null = null): PseudonymizerModelsResponse => ({
  status: "ok", languages: [{ id: "french", model: "CamemBERT-NER", sizeMegabytes: 420, installed: false }],
  download: { state, language, percent: 0, error: null },
});

function host(fail: Partial<Record<"plugin" | "setting" | "secure" | "read", string>> & { ignored?: boolean } = {}) {
  const calls: string[] = [];
  const values = new Map<string, string>();
  const api: ProjectSecurityApi = {
    setEnabled: async request => { calls.push(`plugin ${request.id} ${request.scope} ${request.enabled}`); return { status: fail.plugin ?? "ok", message: null, applied: true }; },
    setSetting: async request => {
      calls.push(`set ${request.id} ${request.value}`);
      if (!fail.setting) values.set(`${request.projectId}/${request.id}`, request.value ?? "null");
      return { status: fail.setting ?? "ok", message: null, previous: null, value: request.value };
    },
    getSetting: async request => ({ status: fail.read ?? "ok", value: values.get(`${request.projectId}/${request.id}`) ?? null, message: null, source: null, choices: null }),
    setProjectSecure: async request => { calls.push(`secure ${request.projectId} ${request.secure}`); return { status: fail.secure ?? "ok", secure: !fail.secure && !fail.ignored && request.secure }; },
    models: async () => models(),
    install: async request => { calls.push(`install ${request.language}`); return request.language === "identifiers" ? models() : models("downloading", request.language); },
  };
  return { api, calls };
}

const choice = { language: "french", alwaysHide: ["Sylvie Durand"], neverHide: [] };

test("a project is made secure in order: the plugin on, its choices saved, the project secure, then its model downloaded", async () => {
  const { api, calls } = host();
  assert.deepEqual(await secureProject(api, epoch, "p1", choice), { status: "ok", downloading: true });
  assert.deepEqual(calls, [
    `plugin ${pseudonymizerPluginId} Global true`,
    'set plugins.pseudonymizer.language "french"', 'set plugins.pseudonymizer.always_hide ["Sylvie Durand"]', "set plugins.pseudonymizer.never_hide []",
    "secure p1 true", "install french"]);
  assert.deepEqual(await readProjectSecurity(api, epoch, "p1"), choice, "what was saved is read back");
});

test("a step that fails leaves the project normal", async () => {
  for (const [fail, status] of [[{ plugin: "write_failed" }, "plugin_failed"], [{ setting: "invalid" }, "settings_failed"]] as const) {
    const { api, calls } = host(fail);
    assert.deepEqual(await secureProject(api, epoch, "p1", choice), { status });
    assert.ok(!calls.some(call => call.startsWith("secure")), `${status}: the project is not made secure`);
  }
});

test("a host that answers ok without making the project secure is a failure", async () => {
  const { api, calls } = host({ ignored: true });
  assert.deepEqual(await secureProject(api, epoch, "p1", choice), { status: "not_secured" });
  assert.ok(!calls.some(call => call.startsWith("install")), "no model is downloaded for a project that is not secure");
});

test("the choices of a secure project are not saved before they are read, nor when they cannot be", async () => {
  const shown = { language: "english", alwaysHide: [], neverHide: [] };
  assert.equal(await readProjectSecurity(host({ read: "unavailable" }).api, epoch, "p1"), null);
  assert.equal(canSaveProjectSecurity(null, shown, false), false, "what shows before the read is not what the project saved");
  assert.equal(canSaveProjectSecurity(null, shown, true), false, "not even to download a model");
  assert.equal(canSaveProjectSecurity(choice, choice, false), false);
  assert.equal(canSaveProjectSecurity(choice, choice, true), true);
  assert.equal(canSaveProjectSecurity(choice, { ...choice, alwaysHide: [] }, false), true);
});

test("identifiers only downloads nothing, and a project no longer pseudonymized keeps the plugin", async () => {
  const { api, calls } = host();
  assert.deepEqual(await saveProjectSecurity(api, epoch, "p1", { ...choice, language: "identifiers" }), { status: "ok", downloading: false });
  assert.deepEqual(await makeProjectNormal(api, epoch, "p1"), { status: "ok", downloading: false });
  assert.ok(!calls.some(call => call.startsWith("plugin")));
  assert.equal(calls.at(-1), "secure p1 false");
});

test("the language offered first follows the language of CodeAlta, and the lists drop empty lines and repeats", () => {
  assert.deepEqual([defaultSecurityLanguage("fr"), defaultSecurityLanguage("en"), defaultSecurityLanguage("de")], ["french", "english", "multilingual"]);
  assert.deepEqual(securityLines(" Sylvie Durand \n\nMe Martin\r\nSylvie Durand"), ["Sylvie Durand", "Me Martin"]);
});
