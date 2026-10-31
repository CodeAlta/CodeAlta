// A cache of values loaded on demand: the gets of a key share one load, and a value is kept for ttlMs.
export function createCache(load, { ttlMs = 60000, now = () => Date.now() } = {}) {
  const entries = new Map();

  return {
    // The value of a key: the one kept, or the one of a new load.
    get(key) {
      const entry = entries.get(key);
      if (entry && entry.expires > now()) return entry.promise;
      const promise = Promise.resolve().then(() => load(key));
      entries.set(key, { promise, expires: now() + ttlMs });
      // Once loaded, the value is kept from the time it arrived.
      promise.then((value) => entries.set(key, { promise: Promise.resolve(value), expires: now() + ttlMs }));
      return promise;
    },

    // Forgets the value of a key: the next get loads it again.
    invalidate(key) {
      entries.delete(key);
    },

    // The number of keys kept.
    size() {
      return entries.size;
    },
  };
}
