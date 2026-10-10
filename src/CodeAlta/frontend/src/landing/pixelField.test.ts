import assert from "node:assert/strict";
import test from "node:test";
import { altaColors, paintPixelField, pixelCell, pixelColor, pixelFieldMoves, pixelGrid, pixelLevel, pixelLimit, pixelStillTime } from "./pixelField";

const times = [0, 1.3, pixelStillTime, 20, 61.7, 240];
const frame = (columns: number, rows: number, time: number, strength = 0.5) => { const pixels = new Uint8ClampedArray(columns * rows * 4); paintPixelField(pixels, columns, rows, time, strength); return pixels; };

test("the field has a cell for each nine pixels, never more than its limit, and none for an area without size", () => {
  assert.deepEqual(pixelGrid(0, 0), { columns: 0, rows: 0 });
  assert.deepEqual(pixelGrid(0, 200), { columns: 0, rows: 0 });
  assert.deepEqual(pixelGrid(300, 0), { columns: 0, rows: 0 });
  assert.deepEqual(pixelGrid(-40, 100), { columns: 0, rows: 0 });
  assert.deepEqual(pixelGrid(Number.NaN, 100), { columns: 0, rows: 0 });
  assert.deepEqual(pixelGrid(1, 1), { columns: 1, rows: 1 });
  assert.deepEqual(pixelGrid(pixelCell * 10, pixelCell * 4), { columns: 10, rows: 4 });
  assert.deepEqual(pixelGrid(pixelCell * 10 + 1, pixelCell * 4 + 1), { columns: 11, rows: 5 });
  assert.deepEqual(pixelGrid(500, 172), { columns: 56, rows: 20 });
  assert.deepEqual(pixelGrid(100_000, 100_000), { columns: pixelLimit.columns, rows: pixelLimit.rows });
  assert.deepEqual(pixelLimit, { columns: 96, rows: 40 });
});

test("a cell is lit in four steps, and a field without cells lights nothing", () => {
  const steps = [0, 1 / 3, 2 / 3, 1];
  const seen = new Set<number>();
  for (const time of times) {
    for (let row = 0; row < 20; row++) {
      for (let column = 0; column < 56; column++) {
        const level = pixelLevel(column, row, 56, 20, time);
        assert.ok(steps.includes(level), `${level} at ${column},${row} and ${time}`);
        seen.add(level);
      }
    }
  }
  assert.ok(seen.has(0) && seen.size >= 3, `the field has dark cells and cells of several strengths: ${[...seen]}`);
  assert.equal(pixelLevel(0, 0, 0, 0, 1), 0);
  assert.equal(pixelLevel(3, 3, 0, 10, 1), 0);
  assert.equal(pixelLevel(3, 3, 10, 0, 1), 0);
});

test("the accent covers a part of the hero: nothing at the left where the text is, more toward the right", () => {
  for (const [columns, rows] of [[56, 20], [96, 40], [30, 12]]) {
    const edge = Math.floor((columns - 1) * 0.15);
    let left = 0, right = 0, outer = 0, leftLight = 0, rightLight = 0;
    for (const time of times) {
      for (let row = 0; row < rows; row++) {
        for (let column = 0; column < columns; column++) {
          const level = pixelLevel(column, row, columns, rows, time), lit = level > 0;
          if (column <= edge) assert.equal(lit, false, `column ${column} of ${columns} at ${time}`);
          if (!lit) continue;
          if (column < columns / 2) { left++; leftLight += level; } else { right++; rightLight += level; }
          if (column >= columns * 0.75) outer++;
        }
      }
    }
    assert.ok(right > 0 && outer > 0, `${columns}x${rows}: the right of the field is lit`);
    assert.ok(right > left, `${columns}x${rows}: ${right} cells lit at the right, ${left} at the left`);
    assert.ok(rightLight > leftLight * 1.2, `${columns}x${rows}: a light of ${rightLight} at the right, ${leftLight} at the left`);
    // It is an accent, not a filled area: most cells stay dark.
    assert.ok(left + right < columns * rows * times.length * 0.5, `${columns}x${rows}: ${left + right} cells lit`);
  }
});

test("a frame is the same for the same time, and another one later", () => {
  assert.deepEqual(frame(56, 20, pixelStillTime), frame(56, 20, pixelStillTime));
  assert.deepEqual(frame(56, 20, 123.4), frame(56, 20, 123.4));
  assert.notDeepEqual(frame(56, 20, pixelStillTime), frame(56, 20, pixelStillTime + 2));
  // It drifts: two frames one after the other differ by a part of the field only.
  const now = frame(56, 20, 10), next = frame(56, 20, 10.11);
  let changed = 0;
  for (let cell = 0; cell < 56 * 20; cell++) if (now[cell * 4 + 3] !== next[cell * 4 + 3]) changed++;
  assert.ok(changed < 56 * 20 * 0.2, `${changed} cells changed in one frame`);
});

test("a frame is painted in the colors of the Alta logo, never stronger than asked", () => {
  const low = [0, 1, 2].map(channel => Math.min(...altaColors.map(color => color[channel]))), high = [0, 1, 2].map(channel => Math.max(...altaColors.map(color => color[channel])));
  for (const strength of [0.5, 0.34, 1, 0]) {
    for (const time of times) {
      const pixels = frame(56, 20, time, strength);
      const limit = Math.round(255 * strength), allowed = [0, 1, 2, 3].map(step => Math.round(255 * step / 3 * strength));
      let lit = 0;
      for (let at = 0; at < pixels.length; at += 4) {
        for (const channel of [0, 1, 2]) assert.ok(pixels[at + channel] >= low[channel] && pixels[at + channel] <= high[channel], `channel ${channel} is ${pixels[at + channel]}`);
        assert.ok(pixels[at + 3] <= limit && allowed.includes(pixels[at + 3]), `alpha ${pixels[at + 3]} for a strength of ${strength}`);
        if (pixels[at + 3] > 0) lit++;
      }
      assert.equal(lit > 0, strength > 0, `strength ${strength} at ${time}`);
    }
  }
  // The colors follow one another: each of the three is met exactly somewhere along the time.
  assert.deepEqual(pixelColor(0, 0, 0), altaColors[0]);
  for (const [column, row, time] of [[0, 0, 0], [40, 10, 3], [95, 39, 500], [7, 2, 86.2]]) {
    const color = pixelColor(column, row, time);
    assert.deepEqual(color, pixelColor(column, row, time));
    color.forEach((value, channel) => assert.ok(Number.isInteger(value) && value >= low[channel] && value <= high[channel]));
  }
  // A buffer of a field without cells is left as it is.
  const untouched = new Uint8ClampedArray([9, 9, 9, 9]);
  paintPixelField(untouched, 0, 0, 1, 1);
  assert.deepEqual([...untouched], [9, 9, 9, 9]);
});

test("the field moves only when the user wants it, the system allows it and the page is on the screen", () => {
  const moving = { animate: true, reducedMotion: false, visible: true, documentHidden: false, onScreen: true };
  assert.equal(pixelFieldMoves(moving), true);
  assert.equal(pixelFieldMoves({ ...moving, animate: false }), false, "the user turned the animation off");
  assert.equal(pixelFieldMoves({ ...moving, reducedMotion: true }), false, "the system asks for less motion");
  assert.equal(pixelFieldMoves({ ...moving, visible: false }), false, "the tab is behind another one");
  assert.equal(pixelFieldMoves({ ...moving, documentHidden: true }), false, "the window is hidden");
  assert.equal(pixelFieldMoves({ ...moving, onScreen: false }), false, "the hero is scrolled away");
  assert.equal(pixelFieldMoves({ animate: false, reducedMotion: true, visible: false, documentHidden: true, onScreen: false }), false);
});
