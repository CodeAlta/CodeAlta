export type ComposerBounds = Readonly<{ min: number; max: number }>;

// Available is the space below the timeline's top, excluding header, notices and handle.
export function composerBounds(available: number): ComposerBounds {
  const space = Math.max(0, Math.floor(available));
  const max = Math.max(0, space - Math.min(96, Math.max(28, Math.floor(space * .3))));
  return { min: Math.min(120, Math.max(60, Math.floor(space * .45)), max), max };
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
