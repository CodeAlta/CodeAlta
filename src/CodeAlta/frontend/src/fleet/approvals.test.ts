import assert from "node:assert/strict";
import test from "node:test";
import type { FleetBudgetHoldView, FleetServerView } from "#neoastra";
import { continueHeldRun, heldRunToastKey } from "./approvals";

const server = (id: string, controller: boolean): FleetServerView => ({ id, url: `https://${id}.example`, organization: id, link: "approved", connection: "online",
  problem: null, controller, spaces: [], recoveryKey: true, shareUrl: null, stop: null }) as FleetServerView;
const hold = (id: string, runId: string, status = "waiting"): FleetBudgetHoldView => ({ id, budgetId: "b1", budgetName: "Home", machine: "Mac mini", runId, title: null,
  status, requestedAt: "2026-11-04T09:00:00Z" });

function api(holds: Record<string, FleetBudgetHoldView[]>, continued = "ok") {
  const calls: string[] = [];
  return {
    calls,
    api: {
      status: async () => ({ status: "ok", servers: [server("joined", false), server("home", true)], deviceId: "desktop", words: "" }),
      budgets: async (request: { linkId: string }) => {
        calls.push(`budgets:${request.linkId}`);
        return { status: "ok", message: null, timeZone: "UTC", budgets: [], holds: holds[request.linkId] ?? [], notifications: [], machines: [], spaces: [], projects: [] };
      },
      continueRun: async (request: { linkId: string; id: string }) => { calls.push(`continue:${request.linkId}:${request.id}`); return { status: continued, message: null }; },
    } as never,
  };
}

test("Continue anyway for a session started on a machine finds its hold by the session on the servers this computer owns", async () => {
  const fake = api({ home: [hold("h0", "s1", "continued"), hold("h1", "s2"), hold("h2", "s1")] });
  assert.equal(await continueHeldRun(fake.api, "epoch", "s1"), "continued");
  // Only the server this computer owns is asked, and only the hold that still waits is continued.
  assert.deepEqual(fake.calls, ["budgets:home", "continue:home:h2"]);
});

test("A hold the server does not list yet, or a Continue anyway refused, is said as such", async () => {
  assert.equal(await continueHeldRun(api({ home: [hold("h1", "other")] }).api, "epoch", "s1"), "not_found");
  assert.equal(await continueHeldRun(api({ home: [hold("h1", "s1")] }, "refused").api, "epoch", "s1"), "failed");
  assert.equal(heldRunToastKey("s1"), "fleet-held-run:s1");
});