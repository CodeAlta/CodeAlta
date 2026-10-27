import test from "node:test";
import assert from "node:assert/strict";
import type { WorkflowRunRequest, WorkflowRunResponse } from "#neoastra";
import { defaultSessionSettings, startRequest } from "../sessionSettings/sessionSettings";
import { createWorkflowsHub, type WorkflowsApi } from "./workflowsHub";

test("Run… sends what the form of the Agent chip chose, and the defaults as nulls", async () => {
  const requests: WorkflowRunRequest[] = [];
  const api = { run: async (request: WorkflowRunRequest): Promise<WorkflowRunResponse> => { requests.push(request); return { status: "ok", sessionId: "s1", message: null }; } } as unknown as WorkflowsApi;
  const hub = createWorkflowsHub(api, "epoch");

  assert.deepEqual(await hub.run("triage", "p1", "{\"days\":2}"), { ok: true, sessionId: "s1", message: null });
  const shown = { providerId: "codex", modelId: "gpt-6.1-sol", reasoningEffort: "low" };
  await hub.run("triage", "p1", null, startRequest({ ...defaultSessionSettings, providerId: "codex", modelId: "gpt-6.1-sol", permissionMode: "auto" }, shown));

  // The defaults leave every part to the host; a choice names its provider, model and effort.
  assert.deepEqual(requests[0], { expectedEpoch: "epoch", name: "triage", projectId: "p1", arguments: "{\"days\":2}", ...defaultSessionSettings });
  assert.deepEqual([requests[1].providerId, requests[1].modelId, requests[1].reasoningEffort, requests[1].permissionMode, requests[1].arguments],
    ["codex", "gpt-6.1-sol", "low", "auto", null]);
});

test("a run the host refuses says why, and a failed call is not a start", async () => {
  const refusing = createWorkflowsHub({ run: async () => ({ status: "refused", sessionId: null, message: "The provider 'codex' does not offer the model 'x'." }) } as unknown as WorkflowsApi, "epoch");
  assert.deepEqual(await refusing.run("triage", "p1", null), { ok: false, sessionId: null, message: "The provider 'codex' does not offer the model 'x'." });
  const failing = createWorkflowsHub({ run: async () => { throw new Error("closed"); } } as unknown as WorkflowsApi, "epoch");
  assert.deepEqual(await failing.run("triage", "p1", null), { ok: false });
});
