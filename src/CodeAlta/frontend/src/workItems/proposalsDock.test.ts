import assert from "node:assert/strict";
import test from "node:test";
import { dockTargetAt, dockTargets, loadDockSide, notesKeepOut, placeProposals, saveDockSide } from "./proposalsDock";

const pane = { width: 1000, height: 600 };
const card = { width: 380, height: 250 };
const chip = { width: 110, height: 26 };

test("proposals sit on the chosen side of Notes, at its height or its right edge", () => {
  const notes = { left: 626, top: 44, width: 360, height: 300 };
  assert.deepEqual(placeProposals(pane, notes, card, false, "left"), { side: "left", left: 238, top: 44, width: 380, maxHeight: 548 });
  assert.deepEqual(placeProposals(pane, { ...notes, left: 200 }, card, false, "right"), { side: "right", left: 568, top: 44, width: 380, maxHeight: 548 });
  assert.deepEqual(placeProposals(pane, notes, card, false, "below"), { side: "below", left: 606, top: 352, width: 380, maxHeight: 240 });
  const low = { left: 626, top: 400, width: 360, height: 180 };
  assert.deepEqual(placeProposals(pane, low, card, false, "above"), { side: "above", left: 606, top: 142, width: 380, maxHeight: 384 });
});

test("beside Notes low in the pane, the cards rise just enough to stay inside it", () => {
  const placed = placeProposals(pane, { left: 626, top: 560, width: 110, height: 26 }, card, false, "left");
  assert.ok(placed.side === "left" && placed.top === 600 - 8 - 250, JSON.stringify(placed));
});

test("a side without room gives the opposite side first, then the others, then a strip", () => {
  // Notes at the left edge: no room on the left.
  assert.equal(placeProposals(pane, { left: 14, top: 44, width: 360, height: 300 }, card, false, "left").side, "right");
  // Notes at the bottom: no room below.
  assert.equal(placeProposals(pane, { left: 626, top: 400, width: 360, height: 192 }, card, false, "below").side, "above");
  // The chip of Notes at the top edge: no room above its tab, even for the chip of the cards.
  assert.deepEqual(placeProposals(pane, { left: 876, top: 0, width: 110, height: 26 }, chip, true, "above"),
    { side: "below", left: 876, top: 34, width: 110, maxHeight: 558 });
  // A pane barely larger than Notes: no side has room.
  assert.deepEqual(placeProposals({ width: 320, height: 300 }, { left: 10, top: 8, width: 300, height: 284 }, card, false, "left"),
    { side: "strip", left: 14, width: 292 });
});

test("the chosen side is kept in the storage of the window, automatic by default", () => {
  const values = new Map<string, string>();
  const storage = { getItem: (key: string) => values.get(key) ?? null, setItem: (key: string, value: string) => { values.set(key, value); },
    removeItem: (key: string) => { values.delete(key); } };
  assert.equal(loadDockSide(storage), null);
  saveDockSide("below", storage);
  assert.equal(loadDockSide(storage), "below");
  saveDockSide("left", storage);
  assert.equal(loadDockSide(storage), "left", "Left chosen is not automatic");
  saveDockSide(null, storage);
  assert.equal(values.size, 0, "Automatic is not stored");
  values.set("codealta.desktop.proposals-dock.v1", "diagonal");
  assert.equal(loadDockSide(storage), null, "An unknown value reads as automatic");
  assert.equal(loadDockSide({ getItem: () => { throw Error("denied"); } }), null);
  assert.doesNotThrow(() => saveDockSide("above", { setItem: () => { throw Error("full"); }, removeItem: () => {} }));
});

test("automatic tries the left of Notes first, then the other sides", () => {
  const notes = { left: 626, top: 44, width: 360, height: 300 };
  assert.equal(placeProposals(pane, notes, card, false, null).side, "left");
  assert.equal(placeProposals(pane, { ...notes, left: 14 }, card, false, null).side, "right");
});

test("a chosen side has Notes keep clear of a band along that edge, sized for the cards, not for where Notes are", () => {
  const notesWindow = { width: 240, height: 140 };
  assert.equal(notesKeepOut(pane, card, false, null, notesWindow), null, "Automatic moves nothing");
  assert.deepEqual(notesKeepOut(pane, card, false, "above", notesWindow), { side: "above", size: 258 });
  assert.deepEqual(notesKeepOut(pane, card, false, "below", notesWindow), { side: "below", size: 258 });
  assert.deepEqual(notesKeepOut(pane, card, false, "left", notesWindow), { side: "left", size: 294 });
  assert.deepEqual(notesKeepOut(pane, card, false, "right", notesWindow), { side: "right", size: 288 });
  assert.deepEqual(notesKeepOut(pane, chip, true, "left", chip), { side: "left", size: 124 }, "The chip of the cards needs only its own width");
  assert.deepEqual(notesKeepOut(pane, { width: 380, height: 900 }, false, "above", notesWindow), { side: "above", size: 308 }, "A tall card keeps at most half the pane");
  assert.equal(notesKeepOut({ width: 480, height: 600 }, card, false, "left", notesWindow), null, "Too narrow for the band and Notes");
  // Kept clear of the band, Notes at the top leave the card its whole height above them.
  const moved = { left: 626, top: 8 + 258, width: 360, height: 300 };
  assert.deepEqual(placeProposals(pane, moved, card, false, "above"), { side: "above", left: 606, top: 8, width: 380, maxHeight: 250 });
});

test("the targets form a cross around the middle of Notes, inside the pane", () => {
  const targets = dockTargets(pane, { left: 600, top: 200, width: 200, height: 100 });
  assert.deepEqual(targets.left, { left: 644, top: 234, width: 32, height: 32 });
  assert.deepEqual(targets.above, { left: 684, top: 194, width: 32, height: 32 });
  assert.equal(dockTargetAt(targets, 700, 290), "below");
  assert.equal(dockTargetAt(targets, 700, 250), null, "The middle is no target");
  // The chip of Notes in the top right corner: the cross moves in so that every target is in the pane.
  const corner = dockTargets(pane, { left: 900, top: 0, width: 100, height: 26 });
  for (const target of Object.values(corner))
    assert.ok(target.left >= 0 && target.top >= 0 && target.left + target.width <= pane.width && target.top + target.height <= pane.height, JSON.stringify(corner));
});
