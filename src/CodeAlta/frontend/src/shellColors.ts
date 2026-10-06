/** The color a custom property resolves to on a probe element, as #rrggbb; undefined when it is not an opaque color. */
export function shellColor(probe: HTMLElement, property: string): string | undefined {
  probe.style.color = `var(${property})`;
  const channels = /^rgb\((\d+), (\d+), (\d+)\)$/.exec(getComputedStyle(probe).color);
  return channels ? `#${channels.slice(1).map(channel => Number(channel).toString(16).padStart(2, "0")).join("")}` : undefined;
}

/** Mixes two #rrggbb colors: `amount` of `over` on `base`. */
export function mixColors(base: string, over: string, amount: number): string {
  const channel = (color: string, index: number) => parseInt(color.slice(1 + 2 * index, 3 + 2 * index), 16);
  return `#${[0, 1, 2].map(index => Math.round(channel(base, index) * (1 - amount) + channel(over, index) * amount)
    .toString(16).padStart(2, "0")).join("")}`;
}

/** What identifies the window's appearance: the theme and the palette set on the root element. */
export function appearanceKey(): string {
  const root = document.documentElement;
  return `${root.dataset.theme ?? ""}|${root.dataset.palette ?? ""}`;
}

/** Calls `listener` when the theme or the palette of the window changes. */
export function subscribeAppearance(listener: () => void): () => void {
  const observer = new MutationObserver(listener);
  observer.observe(document.documentElement, { attributes: true, attributeFilter: ["data-theme", "data-palette"] });
  return () => observer.disconnect();
}
