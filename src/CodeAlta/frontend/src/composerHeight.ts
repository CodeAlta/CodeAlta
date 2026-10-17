export type ComposerBounds = Readonly<{ min: number; max: number }>;

// Measure from the containing viewport, not current timeline/splitter coordinates:
// FlexLayout can reparent a pane and its outer scroller can retain a scroll offset.
export function composerAvailableHeight(viewportHeight: number, padding: number, chromeHeight: number): number {
  return Math.max(0, Math.floor(viewportHeight - padding - chromeHeight));
}

/** The least height of the editor of a prompt, two lines: it scrolls its text when it is given less than what it holds. */
export const promptEditorMinimum = 64;

// Available is the space below the timeline's top, excluding header, notices and handle.
// Minimum is the smallest layout of the composer (`composerMinimum`): the timeline keeps its reserve only after it,
// and the composer cannot be made smaller than it, so that the toolbar of the prompt never leaves the window.
export function composerBounds(available: number, minimum = 0): ComposerBounds {
  const space = Math.max(0, Math.floor(available));
  const least = Math.min(space, Math.max(0, Math.ceil(minimum)));
  const max = Math.max(least, space - Math.min(96, Math.max(28, Math.floor(space * .3))));
  return { min: Math.min(Math.max(Math.min(120, Math.max(60, Math.floor(space * .45))), least), max), max };
}

/**
 * The smallest layout of a composer whose prompt editor alone gives room back.
 * @param content The height of all that the region holds, its padding included (its `scrollHeight`).
 * @param surface The height of the prompt surface, the editor included.
 * @param editor The height of the editor.
 * @returns The least height of the prompt surface, and of the whole region.
 */
export function composerMinimum(content: number, surface: number, editor: number): Readonly<{ surface: number; region: number }> {
  const least = Math.max(0, surface - editor) + promptEditorMinimum;
  return { surface: least, region: Math.max(0, content - surface) + least };
}

export function resizeComposerHeight(base: number, delta: number, bounds: ComposerBounds): number {
  return Math.min(bounds.max, Math.max(bounds.min, Math.min(720, Math.round(base + delta))));
}

export function composerSizeKey(epoch: string | null, projectId: string | null, sessionId: string): string {
  return JSON.stringify([epoch, projectId, sessionId]);
}

export function rememberComposerHeight(sizes: ReadonlyMap<string, number>, key: string, height: number | undefined): Map<string, number> {
  const next = new Map(sizes);
  next.delete(key);
  if (height !== undefined) next.set(key, height);
  if (next.size > 64) next.delete(next.keys().next().value!);
  return next;
}
