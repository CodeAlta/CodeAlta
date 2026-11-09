import assert from "node:assert/strict";
import test from "node:test";
import { sessionLinkAddress, sessionLinkTarget } from "./sessionLinks";

test("a link to a session names the session, and a message of it by the offset of its record", () => {
  assert.deepEqual(sessionLinkTarget("session:01a1c209-e03f-7319-9b99-cfee4356f9c2"), { sessionId: "01a1c209-e03f-7319-9b99-cfee4356f9c2", offset: null });
  assert.deepEqual(sessionLinkTarget("session:01a1c209-e03f#12345"), { sessionId: "01a1c209-e03f", offset: "12345" });
  for (const value of ["", "session:", "session:#12", "session:a b", "session:abc#top", "session:abc#", "session://abc", "Session:abc", "sessions:abc",
    "https://example.invalid/session:abc", `session:${"a".repeat(129)}`, "session:-abc", null, undefined])
    assert.equal(sessionLinkTarget(value), null, JSON.stringify(value));
  assert.equal(sessionLinkAddress({ sessionId: "abc", offset: null }), "session:abc");
  assert.equal(sessionLinkAddress({ sessionId: "abc", offset: "42" }), "session:abc#42");
});
