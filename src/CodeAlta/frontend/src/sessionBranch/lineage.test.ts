import assert from "node:assert/strict";
import test from "node:test";
import { lineageSummary, lineageTitle, sessionLineage } from "./lineage";

const plain = { forkedFromSessionId: null, forkedFromTitle: null, forkKind: null, forkTurnCount: 0 };

test("a session says where it comes from when it is a fork or what a rewind kept", () => {
  assert.equal(sessionLineage(plain), null);
  assert.deepEqual(sessionLineage({ forkedFromSessionId: "source", forkedFromTitle: "Review the parser", forkKind: "fork", forkTurnCount: 4 }),
    { kind: "fork", sourceId: "source", title: "Review the parser", turnCount: 4 });
  assert.deepEqual(sessionLineage({ forkedFromSessionId: "source", forkedFromTitle: "Review the parser", forkKind: "rewind", forkTurnCount: 7 }),
    { kind: "rewind", sourceId: "source", title: "Review the parser", turnCount: 7 });
  // A source without a recorded title is named by its identifier; an unknown kind is a fork.
  assert.deepEqual(sessionLineage({ forkedFromSessionId: "source", forkedFromTitle: " ", forkKind: null, forkTurnCount: -1 }),
    { kind: "fork", sourceId: "source", title: "source", turnCount: 0 });
});

test("the source is named by the title it has now while it is listed, and by the one it had once it is gone", () => {
  const lineage = sessionLineage({ forkedFromSessionId: "source", forkedFromTitle: "Old title", forkKind: "fork", forkTurnCount: 2 })!;
  assert.deepEqual(lineageTitle(lineage, [{ id: "other", title: "Other" }, { id: "source", title: "New title" }]), { title: "New title", exists: true });
  assert.deepEqual(lineageTitle(lineage, [{ id: "other", title: "Other" }]), { title: "Old title", exists: false });
});

test("the copied details of a session name its origin in English", () => {
  assert.equal(lineageSummary({ kind: "fork", sourceId: "source", title: "Review", turnCount: 1 }), "Forked from Review (started with 1 turn); session source");
  assert.equal(lineageSummary({ kind: "fork", sourceId: "source", title: "Review", turnCount: 0 }), "Forked from Review (started with 0 turns); session source");
  // The session a rewind kept holds every turn its source had: the count says where the source was rewound to.
  assert.equal(lineageSummary({ kind: "rewind", sourceId: "source", title: "Review", turnCount: 5 }), "Kept from a rewind of Review (rewound to the end of turn 5); session source");
  assert.equal(lineageSummary({ kind: "rewind", sourceId: "source", title: "Review", turnCount: 0 }), "Kept from a rewind of Review (rewound to its start); session source");
});
