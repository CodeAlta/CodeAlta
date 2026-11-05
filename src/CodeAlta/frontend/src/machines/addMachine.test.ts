import assert from "node:assert/strict";
import test from "node:test";
import { hostKeyOf, setupProblem, setupSteps, sshTargetOf, stepTitle } from "./addMachine";

test("The SSH key of an unknown computer is shown before the person trusts it", () => {
  assert.equal(hostKeyOf("host_key_unknown", "SHA256:Qm9vLWtleS1maW5nZXJwcmludA (ED25519)"), "SHA256:Qm9vLWtleS1maW5nZXJwcmludA (ED25519)");
  assert.equal(hostKeyOf("host_key_unknown", "Host key verification failed."), null, "an ssh that gave no key: the words of ssh stay");
  assert.equal(hostKeyOf("auth_failed", "SHA256:Qm9vLWtleS1maW5nZXJwcmludA (ED25519)"), null);
  assert.equal(hostKeyOf("host_key_unknown", null), null);
});

test("A computer is typed as ssh takes it", () => {
  assert.equal(sshTargetOf(" xoofx@pc-ai.home.arpa "), "xoofx@pc-ai.home.arpa");
  assert.equal(sshTargetOf("ssh me@mac-mini"), "me@mac-mini", "a copied ssh command is fine");
  assert.equal(sshTargetOf("ssh://me@box:2222/"), "me@box:2222");
  assert.equal(sshTargetOf("build-box"), "build-box", "a host of the SSH configuration");
  assert.equal(sshTargetOf("me@[fd00::1]:22"), "me@[fd00::1]:22");
  assert.equal(sshTargetOf("-oProxyCommand=evil"), "", "never an option of ssh");
  assert.equal(sshTargetOf("me@box; rm -rf /"), "");
  assert.equal(sshTargetOf(""), "");
});

test("The checklist has five steps, each with its title, and each failure says what to do", () => {
  assert.deepEqual([...setupSteps], ["connect", "check", "install", "service", "pair"]);
  assert.equal(stepTitle("service"), "Start it as a service");
  assert.match(setupProblem("auth_failed"), /ssh-add/u);
  assert.match(setupProblem("host_key_unknown"), /trust it/u);
  assert.match(setupProblem("dotnet_missing"), /\.NET 10/u);
  assert.match(setupProblem("local_network_blocked"), /Privacy & Security > Local Network/u, "macOS keeps the app from the network: not a firewall of the machine");
  assert.equal(setupProblem("something new"), "The setup did not go through.");
});
