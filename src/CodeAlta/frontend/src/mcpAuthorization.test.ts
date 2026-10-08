import assert from "node:assert/strict";
import test from "node:test";
import { authorizationAddress, authorizationBlocked, authorizationFailure } from "./mcpAuthorization";

test("an authorization prompt shows only an http or https address", () => {
  assert.equal(authorizationAddress({ url: "https://auth.example.test/authorize?state=1" }), "https://auth.example.test/authorize?state=1");
  assert.equal(authorizationAddress({ url: "http://127.0.0.1:8123/authorize" }), "http://127.0.0.1:8123/authorize", "a local server may authorize over http");
  assert.equal(authorizationAddress({ url: "https://a b" }), null);
  assert.equal(authorizationAddress({ url: "file:///etc/passwd" }), null);
  assert.equal(authorizationAddress({ url: "https://auth.example.test/" + "a".repeat(8192) }), null);
  assert.equal(authorizationAddress({ url: null }), null);
});

test("an authorization is offered only for the saved, enabled definition in effect", () => {
  assert.equal(authorizationBlocked({ enabled: true, shadowed: false }, false), null);
  assert.equal(authorizationBlocked({ enabled: true, shadowed: false }, true), "Save before authorizing.");
  assert.equal(authorizationBlocked({ enabled: false, shadowed: false }, false), "Enable the server before authorizing.");
  assert.equal(authorizationBlocked({ enabled: true, shadowed: true }, false), "Another definition of this server is the one in use; authorize that one.");
  assert.equal(authorizationBlocked({ enabled: false, shadowed: true }, true), "Save before authorizing.", "unsaved changes come first");
});

test("a failed authorization explains itself, with the host's detail when it has one", () => {
  assert.deepEqual(authorizationFailure("canceled", null), { key: "Authorization canceled.", intent: "warning" });
  assert.equal(authorizationFailure("timeout", "did not finish within 30000 ms").key, "The authorization timed out. Start it again.");
  assert.equal(authorizationFailure("busy", null).intent, "warning");
  assert.equal(authorizationFailure("stale_epoch", null).key, authorizationFailure("unavailable", null).key);
  assert.deepEqual(authorizationFailure("login_failed", "HTTP 401"), { key: "The authorization did not complete: {detail}", intent: "danger", detail: "HTTP 401" });
  assert.deepEqual(authorizationFailure("login_failed", null), { key: "The authorization did not complete.", intent: "danger" });
  assert.equal(authorizationFailure(null, null).intent, "danger");
});
