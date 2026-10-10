// The accent of the landing page: a small field of pixels in the colors of the Alta logo, which drifts slowly. It is computed here,
// without the DOM, so that what it draws can be checked; `LandingAccent.tsx` paints it on a canvas of one pixel for each cell.

/** The colors of the Alta logo, as red, green and blue. */
export const altaColors: readonly (readonly [number, number, number])[] = Object.freeze([
  Object.freeze([0x37, 0xb4, 0xed] as const),
  Object.freeze([0xc4, 0x6a, 0xf3] as const),
  Object.freeze([0xff, 0xb3, 0x4c] as const),
]);

/** The size of a cell of the field, in CSS pixels. */
export const pixelCell = 9;
/** The most cells the field has on a side: a very wide window draws larger cells, never more of them. */
export const pixelLimit = Object.freeze({ columns: 96, rows: 40 });
/** The time between two frames of the field, in milliseconds: it drifts, it does not run. */
export const pixelFrameMilliseconds = 110;
/** The moment of the field that is drawn when it does not move. */
export const pixelStillTime = 7.5;

/** The number of cells for an area, within the limit. An area without size has none. */
export function pixelGrid(width: number, height: number): Readonly<{ columns: number; rows: number }> {
  if (!(width > 0) || !(height > 0)) return { columns: 0, rows: 0 };
  return { columns: Math.min(pixelLimit.columns, Math.max(1, Math.ceil(width / pixelCell))), rows: Math.min(pixelLimit.rows, Math.max(1, Math.ceil(height / pixelCell))) };
}

// A fixed number for a cell, between 0 and 1: the grain of the field, the same at every frame.
function grain(column: number, row: number): number {
  const value = Math.sin(column * 127.1 + row * 311.7) * 43758.5453;
  return value - Math.floor(value);
}

/**
 * How lit a cell is at a time, in four steps from 0 (nothing) to 1: a few slow waves that cross, with the grain of the cell, cut in steps so
 * that the field keeps the look of pixels. The field is denser at the right, where the page has no text, and fades out toward the left.
 */
export function pixelLevel(column: number, row: number, columns: number, rows: number, time: number): number {
  if (columns <= 0 || rows <= 0) return 0;
  const wave = Math.sin(column * 0.31 + time * 0.55) + Math.sin(row * 0.47 - time * 0.38) + Math.sin((column + row) * 0.19 + time * 0.27) + Math.sin((column - row) * 0.13 - time * 0.21);
  const across = columns === 1 ? 1 : column / (columns - 1);
  // From nothing at the left edge to the whole field at the right.
  const reach = Math.max(0, Math.min(1, (across - 0.15) / 0.6));
  const value = (wave / 4 + 1) / 2 * 0.62 + grain(column, row) * 0.38;
  const lit = (value - 0.56) / 0.44 * reach;
  return lit <= 0 ? 0 : Math.min(3, Math.ceil(lit * 3)) / 3;
}

/** The color of a cell at a time: the colors of the logo, which follow one another slowly along the field. */
export function pixelColor(column: number, row: number, time: number): readonly [number, number, number] {
  const place = (column * 0.045 + row * 0.02 + time * 0.035) % altaColors.length;
  const from = Math.floor(place < 0 ? place + altaColors.length : place) % altaColors.length;
  const to = (from + 1) % altaColors.length;
  const mix = place - Math.floor(place);
  return [0, 1, 2].map(channel => Math.round(altaColors[from][channel] + (altaColors[to][channel] - altaColors[from][channel]) * mix)) as unknown as readonly [number, number, number];
}

/**
 * Writes one frame of the field as pixels of red, green, blue and alpha, one for each cell.
 *
 * @param strength How visible the field is at its most, from 0 to 1: lower on a light theme.
 */
export function paintPixelField(pixels: Uint8ClampedArray, columns: number, rows: number, time: number, strength: number): void {
  for (let row = 0; row < rows; row++) {
    for (let column = 0; column < columns; column++) {
      const at = (row * columns + column) * 4;
      const level = pixelLevel(column, row, columns, rows, time);
      const [red, green, blue] = pixelColor(column, row, time);
      pixels[at] = red;
      pixels[at + 1] = green;
      pixels[at + 2] = blue;
      pixels[at + 3] = Math.round(255 * level * strength);
    }
  }
}

/**
 * Whether the field moves: the user wants it to, did not ask the system for less motion, and the page is on the screen (its tab is in
 * front, the window is not hidden, the field is not scrolled away).
 */
export function pixelFieldMoves(state: Readonly<{ animate: boolean; reducedMotion: boolean; visible: boolean; documentHidden: boolean; onScreen: boolean }>): boolean {
  return state.animate && !state.reducedMotion && state.visible && !state.documentHidden && state.onScreen;
}
