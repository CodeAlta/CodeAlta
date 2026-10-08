import test from "node:test";
import assert from "node:assert/strict";
import { fileMarkdownHref, markdownHrefKind, markdownLinkActivation, openMarkdownLink, safeMarkdownHref } from "./markdownLinks";
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

test("a target is a file of this computer when it is a path or a file: address without a host", () => {
  for (const value of ["src/Program.cs", "src/Program.cs#L42", "src/Program.cs:42", "src/Program.cs:42:7", "src/Program.cs:42-50", "README.md", "../other/a.cs", "/relative",
    "/home/me/a.cs", "C:/code/a.cs", "C:/code/a.cs:12", "c:%5Ccode%5Ca.cs", "/C:/code/a.cs#L3", "my%20notes/a%20b.md", "~/.alta/config.toml", "notes/a:b.md",
    "file:///C:/code/report.html", "file:///tmp/a", "file://localhost/tmp/a"]) {
    assert.equal(fileMarkdownHref(value), true, value);
    assert.equal(markdownHrefKind(value), "file", value);
  }
  for (const value of ["", "#L10", " src/a.cs", "src/a.cs\n", "//server/share/a.cs", "%5C%5Cserver%5Cshare%5Ca.cs", "file://server/share/a.cs", "file:////server/share/a.cs",
    "file:a.cs", "mailto:a@b.invalid", "javascript:alert(1)", "vscode://file/C:/code/a.cs", "app://codealta/index.html", "https://user:pass@example.invalid/",
    "C:/code/a.cs:stream", "x".repeat(2049)]) {
    assert.equal(fileMarkdownHref(value), false, JSON.stringify(value));
    assert.equal(markdownHrefKind(value), null, JSON.stringify(value));
  }
  assert.equal(markdownHrefKind(address), "web");
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
  for (const invalid of ["mailto:a@b.invalid", "//server/share/a.cs", "file://server/share/a.cs", "#L3"]) assert.equal(await openMarkdownLink(open, epoch, invalid, () => true), "invalid_request");
  assert.equal(await openMarkdownLink(open, epoch, address, () => false), "stale_epoch");
  assert.equal(calls, 0);
});

test("opener forwards exact epoch/address and handles stale replies and private failures", async () => {
  const none = { sessionId: null, projectId: null, directory: null };
  assert.equal(await openMarkdownLink(async request => { assert.deepEqual(request, { expectedHostEpoch: epoch, address, ...none }); return { status: "ok", hostEpoch: epoch }; }, epoch, address, () => true), "ok");
  // A link to a file goes to the same opener, with where its relative path starts from.
  assert.equal(await openMarkdownLink(async request => {
    assert.deepEqual(request, { expectedHostEpoch: epoch, address: "src/Program.cs#L3", ...none, sessionId: "session-1" }); return { status: "ok", hostEpoch: epoch };
  }, epoch, "src/Program.cs#L3", () => true, undefined, { sessionId: "session-1" }), "ok");
  assert.equal(await openMarkdownLink(async request => {
    assert.deepEqual(request, { expectedHostEpoch: epoch, address: "setup.md", sessionId: null, projectId: "project-1", directory: "doc" }); return { status: "not_found", hostEpoch: epoch };
  }, epoch, "setup.md", () => true, undefined, { projectId: "project-1", directory: "doc" }), "not_found");
  assert.equal(await openMarkdownLink(async () => ({ status: "binary", hostEpoch: epoch }), epoch, "tool.bin", () => true), "binary");
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
