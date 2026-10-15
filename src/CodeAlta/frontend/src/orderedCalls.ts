/**
 * Sends the calls of one key to the host one at a time, in the order they were asked. The host runs its calls side by
 * side, so of two calls in flight the later one may end first: a call that sets a state (shown, visible, attached) waits
 * for the one before it of the same key. A call goes out at once when none of its key is waiting, and one that fails
 * does not stop the next.
 */
export function createOrderedCalls() {
  const waiting = new Map<string, Promise<void>>();
  return function inOrder<T>(key: string, call: () => Promise<T>): Promise<T> {
    const before = waiting.get(key);
    const result = before ? before.then(call) : call();
    const settled = result.then(() => undefined, () => undefined);
    waiting.set(key, settled);
    void settled.then(() => { if (waiting.get(key) === settled) waiting.delete(key); });
    return result;
  };
}
