/**
 * Puts a text on the clipboard. A text still being read comes as a promise: Safari allows a write only during the tap, so
 * the write starts at once and waits for its text, where the browser can. A text that could not be read rejects with its own error.
 */
export async function copyText(window: Window & typeof globalThis, text: string | Promise<string>): Promise<void> {
  const clipboard = window.navigator.clipboard;
  if (!clipboard) throw new Error("this page cannot use the clipboard.");
  if (typeof text === "string") await clipboard.writeText(text);
  else if (typeof window.ClipboardItem === "function") {
    const blob = text.then(value => new window.Blob([value], { type: "text/plain" }));
    await Promise.all([text, clipboard.write([new window.ClipboardItem({ "text/plain": blob })])]);
  } else await clipboard.writeText(await text);
}
