import assert from "node:assert/strict";
import test from "node:test";
import { fittingRailItems } from "./ActivityRail";

test("the buttons of the rail that have no room fold, and the menu that holds them takes the room of one", () => {
  // The Explorer and Settings (54px with their gap) and six buttons of 28px: 222px holds them all.
  assert.equal(fittingRailItems(222, 54, 28, 6), 6);
  // 2px less: the menu takes a place, so two fold.
  assert.equal(fittingRailItems(220, 54, 28, 6), 4);
  // The narrowest Explorer leaves 174px: three show, three are in the menu.
  assert.equal(fittingRailItems(174, 54, 28, 6), 3);
  // No room at all: everything folds, never fewer than none.
  assert.equal(fittingRailItems(40, 54, 28, 6), 0);
  // Nothing measured yet: everything shows.
  assert.equal(fittingRailItems(0, 0, 0, 6), 6);
});
