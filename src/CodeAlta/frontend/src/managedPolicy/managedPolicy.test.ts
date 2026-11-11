import assert from "node:assert/strict";
import test from "node:test";
import type { ManagedPolicyReading, ManagedPolicyResponse } from "#neoastra";
import { locales, translate } from "../localization";
import { managedPolicyMessages } from "./messages";
import { downloadSize, managedPolicyLines, readingUnderWay } from "./managedPolicy";

const policy = (change: Partial<ManagedPolicyResponse> = {}): ManagedPolicyResponse => ({
  status: "ok", managed: true, source: "GitHub Copilot", account: "copilot", bypassPermissionsDisabled: false, assistedPermissionsDisabled: false,
  defaultPermissionMode: null, sandboxRequired: false, runOutsideSandboxDisallowed: false, freshPolicyMissing: false, supportUrl: null,
  managedKeys: [], readAt: null, copilot: null, ...change,
});
const reading = (change: Partial<ManagedPolicyReading>): ManagedPolicyReading => ({ state: "ready", message: null, receivedKilobytes: null, totalKilobytes: null, readAt: null, ...change });

test("the policy of the organization says what it enforces, one sentence each, the blocking one first", () => {
  assert.deepEqual(managedPolicyLines(null), []);
  assert.deepEqual(managedPolicyLines(policy({ managed: false, bypassPermissionsDisabled: true })), [], "a policy that narrows nothing says nothing");
  assert.deepEqual(managedPolicyLines(policy({
    bypassPermissionsDisabled: true, assistedPermissionsDisabled: true, defaultPermissionMode: "default", sandboxRequired: true,
    runOutsideSandboxDisallowed: true, freshPolicyMissing: true,
  })), [
    { key: "No session runs until the policy is read again." },
    { key: "Bypass permissions is turned off." },
    { key: "Auto is turned off." },
    { key: "New sessions start in {mode}.", mode: "default" },
    { key: "Every session runs in the sandbox." },
    { key: "No command runs outside the sandbox." },
  ]);
});

test("the reading of the Copilot accounts shows the download of its runtime in megabytes", () => {
  assert.equal(readingUnderWay(null), false);
  assert.equal(readingUnderWay(reading({ state: "installing" })), true);
  assert.equal(readingUnderWay(reading({ state: "reading" })), true);
  assert.equal(readingUnderWay(reading({ state: "failed" })), false);
  assert.deepEqual(downloadSize(reading({ state: "installing", receivedKilobytes: 51_200, totalKilobytes: 94_360 })), { received: "50", total: "92" });
  assert.deepEqual(downloadSize(reading({ state: "installing", receivedKilobytes: 200_000, totalKilobytes: 94_360 })), { received: "92", total: "92" }, "never more than the whole");
  assert.equal(downloadSize(reading({ state: "installing", receivedKilobytes: 10 })), null, "no size, no figure");
  assert.equal(downloadSize(reading({ state: "reading", receivedKilobytes: 10, totalKilobytes: 20 })), null);
});

test("every sentence of the policy is translated, and keeps its parameters", () => {
  for (const key of Object.keys(managedPolicyMessages) as (keyof typeof managedPolicyMessages)[]) {
    const parameters = [...key.matchAll(/\{(\w+)\}/g)].map(match => match[1]);
    for (const locale of locales) {
      const text = translate(locale, key, Object.fromEntries(parameters.map(name => [name, `<${name}>`])));
      assert.ok(text.length > 0, `${key} in ${locale}`);
      for (const name of parameters) assert.ok(text.includes(`<${name}>`), `${key} in ${locale} keeps {${name}}`);
    }
  }
});
