/** What `keepMiddleButtonFromTab` reads of a mouse press or release. */
export type TabMouseButton = { button: number; target: EventTarget | null; preventDefault(): void };

/**
 * Keeps the default actions of the middle button from a tab, which a middle click closes: its press would start
 * scrolling the page, and on Linux its release pastes the primary selection where the keyboard is. WebKitGTK pastes
 * once the click that closed the tab has run, into the prompt that kept or took the keyboard, unless the release
 * (`mouseup`) is canceled: canceling the click does not stop it.
 * @param event The `mousedown` or `mouseup` event.
 * @param tab The selector of the tabs.
 */
export function keepMiddleButtonFromTab(event: TabMouseButton, tab: string) {
  if (event.button === 1 && (event.target as Element | null)?.closest?.(tab)) event.preventDefault();
}
