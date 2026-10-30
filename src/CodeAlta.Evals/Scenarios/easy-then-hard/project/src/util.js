// Remembers the results of a pure function by its arguments: a conversion asked twice is computed once.
export function memoize(fn) {
  const cache = new Map();
  return function memoized(...args) {
    const key = args.join("|");
    if (!cache.has(key)) cache.set(key, fn(...args));
    return cache.get(key);
  };
}
