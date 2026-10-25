import assert from "node:assert/strict";
import { test } from "node:test";
import { sessionTabsKey, type SessionTab } from "../sessionTabs";
import { createOpenSessionsPublisher, elsewhereFollowView, elsewhereKind, elsewhereLabel, elsewhereRefreshView, openSessionIds } from "./sessionLeases";

const tab = (sessionId: string): SessionTab => ({ sessionId, projectId: null, path: null });
const kept = (...ids: string[]) => JSON.stringify({ version: 1, open: ids.map(tab), active: null, closed: [] });

function storage(values: Record<string, string>): Pick<Storage, "length" | "key" | "getItem"> {
  const keys = Object.keys(values);
  return { length: keys.length, key: index => keys[index] ?? null, getItem: key => values[key] ?? null };
}

test("a holder the host names is said by its kind, and any other is another process", () => {
  assert.equal(elsewhereKind(null), null);
  assert.equal(elsewhereKind(""), null);
  assert.equal(elsewhereKind("altacli"), "altacli");
  assert.equal(elsewhereKind("tui"), "tui");
  assert.equal(elsewhereKind("something"), "other");
  assert.equal(elsewhereLabel("altacli"), "Running in altacli");
  assert.equal(elsewhereLabel("desktop"), "Open in another CodeAlta Desktop");
  assert.equal(elsewhereLabel("other"), "In use by another process");
});

test("the timeline of a session held elsewhere is read again when its journal grows, not otherwise", () => {
  assert.equal(elsewhereRefreshView("s", "10").revision, elsewhereRefreshView("s", "10").revision);
  assert.notEqual(elsewhereRefreshView("s", "10").revision, elsewhereRefreshView("s", "20").revision);
  assert.equal(elsewhereRefreshView("s", null).sessionId, "s");
});

test("the timeline of a session held elsewhere is read once more when it is released, then not again", () => {
  // A session no other process held while it was shown has no such view.
  assert.equal(elsewhereFollowView("s", null, null), null);
  const held = elsewhereFollowView("s", "10", null)!;
  assert.equal(held.revision, elsewhereRefreshView("s", "10").revision);
  // The same length keeps the view: nothing is read again.
  assert.equal(elsewhereFollowView("s", "10", held), held);
  const grown = elsewhereFollowView("s", "20", held)!;
  assert.notEqual(grown.revision, held.revision);
  // Released: one more revision, which stays as long as the session is not held again.
  const released = elsewhereFollowView("s", null, grown)!;
  assert.equal(released.sessionId, "s");
  assert.notEqual(released.revision, grown.revision);
  assert.equal(elsewhereFollowView("s", null, released), released);
  // Held again, it follows the journal again, and is read once more at its next release.
  const again = elsewhereFollowView("s", "20", released)!;
  assert.notEqual(again.revision, released.revision);
  assert.notEqual(elsewhereFollowView("s", null, again)!.revision, again.revision);
  // The view of another session is not carried over.
  assert.equal(elsewhereFollowView("t", null, released), null);
  assert.equal(elsewhereFollowView("t", "20", grown)!.sessionId, "t");
});

test("the open sessions are the tabs of the shown space from memory and those kept for the other spaces", () => {
  const values = {
    [sessionTabsKey]: kept("default-old"),
    [`${sessionTabsKey}.work`]: kept("w1", "shared"),
    "codealta.desktop.other": kept("not-tabs"),
    [`${sessionTabsKey}.broken`]: "{",
  };
  // The default space is shown: its kept tabs are older than those in memory.
  assert.deepEqual(openSessionIds([tab("d1"), tab("shared")], storage(values), sessionTabsKey), ["d1", "shared", "w1"]);
  // A secondary window says its own tabs alone.
  assert.deepEqual(openSessionIds([tab("only")], null, null), ["only"]);
  // A storage that cannot be read gives the shown space.
  const failing = { length: 1, key: () => { throw new Error("denied"); }, getItem: () => null };
  assert.deepEqual(openSessionIds([tab("d1")], failing, sessionTabsKey), ["d1"]);
});

test("the publisher tells each set once, the newest next, and again after a failure or when told to forget", async () => {
  const sent: string[] = [];
  let fail = false;
  let release: (() => void) | null = null;
  const publisher = createOpenSessionsPublisher(async (epoch, ids) => {
    sent.push(`${epoch}:${ids.join(",")}`);
    if (release === null) await new Promise<void>(resolve => { release = resolve; });
    return !fail;
  });
  const first = publisher.publish("e", ["a"]);
  // While the first call runs, two sets come: only the newest is told after it.
  void publisher.publish("e", ["a", "b"]);
  void publisher.publish("e", ["a", "b", "c"]);
  release!();
  await first;
  assert.deepEqual(sent, ["e:a", "e:a,b,c"]);

  await publisher.publish("e", ["a", "b", "c"]);
  assert.equal(sent.length, 2, "A set already told is not told again.");
  await publisher.publish("other", ["a", "b", "c"]);
  assert.deepEqual(sent.at(-1), "other:a,b,c", "Another host is told.");

  fail = true;
  await publisher.publish("other", ["z"]);
  fail = false;
  await publisher.publish("other", ["z"]);
  assert.deepEqual(sent.slice(-2), ["other:z", "other:z"], "A set the host refused is told again.");
  publisher.forget();
  await publisher.publish("other", ["z"]);
  assert.equal(sent.at(-1), "other:z");
  assert.equal(sent.length, 6);
});
