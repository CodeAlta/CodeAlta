/** Records the tab just shown, retaining history only for tabs still open. */
export function rememberRecent(recent: readonly string[], id: string, open: readonly string[]): readonly string[] {
  const remaining = new Set(open);
  return [id, ...recent.filter(value => value !== id && remaining.has(value))];
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
export type TabSwitcherState<T> = Readonly<{ order: readonly T[]; index: number }>;

/**
 * Starts the switcher on the tab shown before the current one, or, going back, on the one shown longest ago.
 * Null when there is no other tab to go to.
 */
export function openTabSwitcher<T>(order: readonly T[], delta: 1 | -1): TabSwitcherState<T> | null {
  return order.length < 2 ? null : { order, index: delta > 0 ? 1 : order.length - 1 };
}

/** The switcher moved by `delta` places, around its tabs. */
export function stepTabSwitcher<T>(state: TabSwitcherState<T>, delta: 1 | -1): TabSwitcherState<T> {
  return { ...state, index: (state.index + delta + state.order.length) % state.order.length };
}
