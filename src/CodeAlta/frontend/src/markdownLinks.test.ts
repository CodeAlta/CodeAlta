import test from "node:test";
import assert from "node:assert/strict";
import { markdownLinkActivation, openMarkdownLink, safeMarkdownHref } from "./markdownLinks";
import { createMutationCapability } from "./sessionOperations";

const epoch = "3b0a7c1e-52c9-4f0b-8a55-6d0c2f9e1b77";
const address = "https://example.invalid/path?q=one%20two#section";

test("Markdown href policy accepts only bounded credential-free absolute HTTP(S)", () => {
  for (const value of [address, "http://localhost:8080/", "HTTPS://example.invalid/", "https://[::1]/"]) assert.equal(safeMarkdownHref(value), true, value);
  for (const value of ["", "/relative", "//example.invalid/", "mailto:a@b.invalid", "app://codealta/", "file:///tmp/a", "javascript:alert(1)",
    "https://user:pass@example.invalid/", "https://@example.invalid/", "https:///example.invalid", "https://", "https://example.invalid:bad/",
    " https://example.invalid/", "https://example.invalid/a b", "https://example.invalid/\n", "https://example.invalid/\u0085", "https://example.invalid/\u00a0",
    "https://example.invalid/\\path", "https://example.invalid/%", "https://example.invalid/%xx", "https://example.invalid/" + "x".repeat(2048)]) {
    assert.equal(safeMarkdownHref(value), false, JSON.stringify(value));
  }
});

test("intentional primary, modified primary, middle and Enter activation only", () => {
  const click = { type: "click", button: 0, isTrusted: true, defaultPrevented: false, altKey: false };
  for (const modifiers of [{}, { metaKey: true }, { ctrlKey: true }, { shiftKey: true }]) assert.equal(markdownLinkActivation({ ...click, ...modifiers }), true);
  assert.equal(markdownLinkActivation({ ...click, type: "auxclick", button: 1 }), true);
  const enter = { ...click, type: "keydown", key: "Enter" };
  assert.equal(markdownLinkActivation(enter), true);
  for (const change of [{ button: 1 }, { button: 2 }, { isTrusted: false }, { defaultPrevented: true }, { altKey: true }, { type: "contextmenu" }]) {
    assert.equal(markdownLinkActivation({ ...click, ...change }), false);
  }
  for (const change of [{ key: " " }, { repeat: true }, { isComposing: true }, { keyCode: 229 }, { isTrusted: false }]) assert.equal(markdownLinkActivation({ ...enter, ...change }), false);
  assert.equal(markdownLinkActivation({ ...click, type: "auxclick", button: 2 }), false);
});

test("no opener invocation for invalid links, missing epoch or a retired grant", async () => {
  let calls = 0;
  const open = async () => { calls++; return { status: "ok", hostEpoch: epoch }; };
  assert.equal(await openMarkdownLink(open, null, address, () => true), "unavailable");
  assert.equal(await openMarkdownLink(open, epoch, "file:///tmp/a", () => true), "invalid_request");
  assert.equal(await openMarkdownLink(open, epoch, address, () => false), "stale_epoch");
  assert.equal(calls, 0);
});

test("opener forwards exact epoch/address and handles stale replies and private failures", async () => {
  assert.equal(await openMarkdownLink(async request => { assert.deepEqual(request, { expectedHostEpoch: epoch, address }); return { status: "ok", hostEpoch: epoch }; }, epoch, address, () => true), "ok");
  assert.equal(await openMarkdownLink(async () => ({ status: "stale_epoch", hostEpoch: epoch }), epoch, address, () => true), "stale_epoch");
  assert.equal(await openMarkdownLink(async () => ({ status: "ok", hostEpoch: "another-host" }), epoch, address, () => true), "stale_epoch");
  let current = true;
  assert.equal(await openMarkdownLink(async () => { current = false; return { status: "ok", hostEpoch: epoch }; }, epoch, address, () => current), "stale_epoch");
  for (const status of ["failed", "invalid_request", "unavailable", "unexpected"]) assert.equal(await openMarkdownLink(async () => ({ status, hostEpoch: epoch }), epoch, address, () => true), "failed");
  assert.equal(await openMarkdownLink(async () => { throw new Error("private OS diagnostics"); }, epoch, address, () => true), "failed");
});

test("message link replies use the shared capability's epoch envelope", async () => {
  for (const status of ["ok", "failed", "invalid_request"]) {
    const capability = createMutationCapability(epoch);
    await openMarkdownLink(async () => ({ status, hostEpoch: epoch }), epoch, address, capability.canMutate, capability.observe);
    assert.equal(capability.canMutate(), true, "Current-host browser replies must not disable session actions");
  }
  for (const reply of [{ status: "stale_epoch", hostEpoch: epoch }, { status: "ok", hostEpoch: "another-host" }]) {
    const capability = createMutationCapability(epoch);
    assert.equal(await openMarkdownLink(async () => reply, epoch, address, capability.canMutate, capability.observe), "stale_epoch");
    assert.equal(capability.canMutate(), false);
  }
});
