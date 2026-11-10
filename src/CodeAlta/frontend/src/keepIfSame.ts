/**
 * The state a host read gives back when it read the same thing again: `previous` itself, so that React does not render
 * what the page shows again for an answer that says nothing new. Used in a `setState(previous => keepIfSame(previous, next))`
 * for plain data, what comes from the host as JSON. A value that JSON would flatten (a Map, a Set, a function) or cannot
 * write (a cycle) is never kept: `next` replaces the state, as it would without this function.
 */
export function keepIfSame<T>(previous: T, next: T): T {
  if (previous === next) return previous;
  try { return JSON.stringify(previous, plainOnly) === JSON.stringify(next, plainOnly) ? previous : next; }
  catch { return next; }
}

function plainOnly(_key: string, value: unknown): unknown {
  if (typeof value === "function" || value instanceof Map || value instanceof Set) throw new TypeError("Not plain data.");
  return value;
}
