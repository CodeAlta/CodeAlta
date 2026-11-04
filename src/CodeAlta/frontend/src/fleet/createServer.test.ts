import assert from "node:assert/strict";
import test from "node:test";
import type { FleetDeviceView, FleetServerView } from "#neoastra";
import { waitingApprovals } from "./approvals";
import { localServerSteps, organizationUrlOf, serverSetupProblem, serverStepTitle, sshServerSteps } from "./createServer";
import { joinSteps, organizationAddressOf } from "./joinOrganization";

const server = (id: string): FleetServerView => ({ id, url: `https://${id}.example`, organization: id, link: "approved", connection: "online", problem: null, controller: true, spaces: [], recoveryKey: null, shareUrl: null, stop: null });
const device = (id: string, status: string, createdAt: string): FleetDeviceView => ({ id, name: id, kind: "node", status, online: true, words: "a b c d e f", createdAt });

test("A server is set up over SSH in six checked steps, on this computer in four", () => {
  assert.deepEqual([...sshServerSteps], ["connect", "check", "install", "service", "reach", "claim"]);
  assert.deepEqual([...localServerSteps], ["find", "install", "start", "claim"]);
  assert.equal(serverStepTitle("reach"), "Check that it answers from here");
  assert.equal(serverStepTitle("claim"), "Make you its owner");
});

test("A server that does not answer from here says what to open, and where", () => {
  assert.match(serverSetupProblem("server_blocked_ufw"), /sudo ufw allow \{port\}\/tcp/u);
  assert.match(serverSetupProblem("server_proxy_unreachable"), /forwards to port \{port\} of \{host\}/u);
  assert.match(serverSetupProblem("auth_failed"), /ssh-add/u, "the failures of SSH say what they say for a machine");
});

test("An address is a server's with its scheme, https by default", () => {
  assert.equal(organizationAddressOf("fleet.contoso.com"), "https://fleet.contoso.com");
  assert.equal(organizationAddressOf("http://box:2586/"), "http://box:2586");
  assert.equal(organizationAddressOf("https://user:pw@fleet.example"), "");
  assert.equal(organizationAddressOf("not a url"), "");
  assert.equal(organizationUrlOf("fleet.example"), "https://fleet.example");
  assert.deepEqual([...joinSteps], ["address", "signin", "space", "approval"]);
});

test("The devices that wait for an approval come from every server this computer owns, the oldest first", () => {
  const waiting = waitingApprovals([
    { server: server("home"), devices: [device("approved", "approved", "2026-11-01"), device("b", "pending", "2026-11-03")] },
    { server: server("lab"), devices: [device("a", "pending", "2026-11-02")] },
  ]);
  assert.deepEqual(waiting.map(item => `${item.server.id}/${item.device.id}`), ["lab/a", "home/b"]);
});