import assert from "node:assert/strict";
import test from "node:test";
import type { MachineMobileInvokeRequest, MachineMobileInvokeView } from "#neoastra";
import { AltaError } from "../pluginScript/alta";
import { createMobileApi } from "../mobile/canvas/api";
import { createMachineMobileRpc, mobileProblem, mobileRetryable, type MachineMobileApi } from "./machineMobileRpc";

const status = { server: "listening", port: 2584, serverDetail: null, provider: { id: "tailscale", state: "serving", url: "https://n.ts.net:2584", detail: "", fixUrl: null, working: false, name: "Tailscale", actions: [] },
  connectUrl: null, address: null, qr: null, served: "https://n.ts.net:2584", phones: 0, pairsOnce: true, pairingSeconds: null };
const target = { epoch: "e1", machineId: "m1" };
const describe = (status: string) => mobileProblem(status);

function fake(answer: (method: string, payload: string | null) => MachineMobileInvokeView | Promise<MachineMobileInvokeView>) {
  const requests: { method: string; payload: string | null; machineId: string; expectedEpoch: string }[] = [];
  const api = {
    mobileInvoke: async (request: MachineMobileInvokeRequest) => { requests.push({ method: request.method, payload: request.payload ?? null, machineId: request.machineId, expectedEpoch: request.expectedEpoch }); return answer(request.method, request.payload ?? null); },
  } as unknown as MachineMobileApi;
  return { api, requests };
}
const ok = (json: unknown): MachineMobileInvokeView => ({ status: "ok", message: null, json: JSON.stringify(json) });
const wait = (milliseconds: number) => new Promise(resolve => setTimeout(resolve, milliseconds));

test("a call of the tab goes to the machine named, with its payload only when it has one, and its JSON is the answer", async () => {
  const { api, requests } = fake(() => ok(status));
  const rpc = createMachineMobileRpc(api, target, describe);
  assert.deepEqual(await rpc.invoke("mobile.status", {}), status);
  assert.deepEqual(await rpc.invoke("mobile.act", { action: "signin-github" }), status);
  assert.deepEqual(requests, [
    { method: "mobile.status", payload: null, machineId: "m1", expectedEpoch: "e1" },
    { method: "mobile.act", payload: '{"action":"signin-github"}', machineId: "m1", expectedEpoch: "e1" },
  ]);
});

test("a machine that refuses says so in its own words, or in the page's; only an unreachable machine may answer later", async () => {
  const answers: Record<string, MachineMobileInvokeView> = {
    "mobile.status": { status: "unavailable", message: null, json: null },
    "mobile.pair": { status: "policy.refused", message: "This machine lets other people in.", json: null },
    "mobile.retry": { status: "offline", message: null, json: null },
    "mobile.act": { status: "ok", message: null, json: null },
  };
  const rpc = createMachineMobileRpc(fake(method => answers[method]!).api, target, describe);
  const failure = async (name: string) => { try { await rpc.invoke(name); } catch (error) { return error as AltaError; } throw new Error("did not fail"); };
  const off = await failure("mobile.status");
  assert.equal(off.code, "unavailable");
  assert.equal(off.message, "The page of the phones is off on this machine.");
  assert.equal(off.retryable, false);
  assert.equal((await failure("mobile.pair")).message, "This machine lets other people in.", "what the machine said is kept");
  const offline = await failure("mobile.retry");
  assert.equal(offline.retryable, true);
  assert.equal(offline.message, "The machine does not answer: it is offline.");
  assert.equal((await failure("mobile.act")).code, "invalid_response", "an answer without its JSON is no answer");
  assert.equal(mobileRetryable("offline"), true);
  assert.equal(mobileRetryable("policy.refused"), false);
  assert.equal(mobileProblem("mobile.unsupported"), "This machine does not serve the page of the phones: update CodeAlta Node there.");
  assert.equal(mobileProblem("ok"), null);
});

test("a connection that breaks is a failure to try again, never an unhandled rejection", async () => {
  const api = { mobileInvoke: async () => { throw new Error("closed"); } } as unknown as MachineMobileApi;
  const rpc = createMachineMobileRpc(api, target, describe);
  await assert.rejects(rpc.invoke("mobile.status"), (error: AltaError) => error.retryable && error.message === "closed");
});

test("the status is asked for at its interval and sent when it changed, until the listening stops", async () => {
  let phones = 0;
  const { api, requests } = fake(() => ok({ ...status, phones }));
  const rpc = createMachineMobileRpc(api, target, describe, 10);
  const heard: number[] = [];
  const stop = await rpc.subscribe("mobile.changed", value => heard.push((value as { phones: number }).phones));
  await wait(60);
  assert.deepEqual(heard, [0], "the same status is not sent twice");
  phones = 1;
  await wait(60);
  assert.deepEqual(heard, [0, 1]);
  stop();
  const asked = requests.length;
  await wait(50);
  assert.equal(requests.length, asked, "nothing is asked once it stops");
  assert.ok(requests.every(request => request.method === "mobile.status"));
});

test("the tab over a machine follows its status, and asks for a code that connects one phone", async () => {
  let code = false;
  const { api, requests } = fake(method => {
    if (method === "mobile.pair") code = true;
    return ok({ ...status, connectUrl: code ? "https://n.ts.net:2584/#k=s" : null, qr: code ? ["101", "010", "101"] : null, pairingSeconds: code ? 300 : null });
  });
  const mobile = createMobileApi(createMachineMobileRpc(api, target, describe, 10));
  const events: (string | null)[] = [];
  const stop = mobile.watch(event => events.push(event.kind === "status" ? event.status.connectUrl : event.message));
  await wait(40);
  assert.deepEqual(events.filter(value => value !== null), []);
  assert.equal((await mobile.pair()).connectUrl, "https://n.ts.net:2584/#k=s");
  await wait(40);
  stop();
  assert.ok(events.includes("https://n.ts.net:2584/#k=s"), "the code is heard at the next read");
  assert.ok(requests.some(request => request.method === "mobile.pair"));
});
