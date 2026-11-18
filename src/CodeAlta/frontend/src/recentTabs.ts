/** The tabs remembered as shown: the most recent ones are kept. */
const recentLimit = 128;

/** `recent` after `id` was shown: it comes first. */
export function rememberRecent(recent: readonly string[], id: string): readonly string[] {
  return recent[0] === id ? recent : [id, ...recent.filter(value => value !== id)].slice(0, recentLimit);
}

/**
 * The tabs by how recently they were shown: `current` first, then the others as `recent` remembers them, then the
 * ones it never saw in the order they have in `all`. A remembered tab that is no longer in `all` is dropped.
 */
export function recentOrder(recent: readonly string[], all: readonly string[], current: string): string[] {
  const open = new Set(all);
  const seen = new Set<string>();
  const order: string[] = [];
  for (const id of [current, ...recent, ...all]) if (open.has(id) && !seen.has(id)) { seen.add(id); order.push(id); }
  return order;
}

/** The tabs of the switcher, the one shown first, and the one the keyboard is on. */
export type TabSwitcherState = Readonly<{ order: readonly string[]; index: number }>;

/**
 * Starts the switcher on the tab shown before the current one, or, going back, on the one shown longest ago.
 * Null when there is no other tab to go to.
 */
export function openTabSwitcher(order: readonly string[], delta: 1 | -1): TabSwitcherState | null {
  return order.length < 2 ? null : { order, index: delta > 0 ? 1 : order.length - 1 };
}

/** The switcher moved by `delta` places, around its tabs. */
export function stepTabSwitcher(state: TabSwitcherState, delta: 1 | -1): TabSwitcherState {
  return { ...state, index: (state.index + delta + state.order.length) % state.order.length };
}
