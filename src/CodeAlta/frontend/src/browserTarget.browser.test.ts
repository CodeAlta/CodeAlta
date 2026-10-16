import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { browserBaseArgs, browserExecutable, endBrowser, waitForBrowserPort } from "./browserTarget";

// The app follows the languages of the browser when none is chosen, and the tests look for its English labels:
// the browser they drive speaks English whatever the language of the system.
test("the browser of the tests speaks English whatever the language of the system", { skip: !browserExecutable, timeout: 60_000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), "codealta-browser-language-"));
  let browser: ReturnType<typeof spawn> | undefined, socket: WebSocket | undefined;
  try {
    browser = spawn(browserExecutable!, [...browserBaseArgs, `--user-data-dir=${join(root, "profile")}`, "--remote-debugging-port=0", "about:blank"],
      { stdio: ["ignore", "ignore", "pipe"], windowsHide: true });
    const port = await waitForBrowserPort(join(root, "profile"), browser);
    const pages = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json() as { type: string; webSocketDebuggerUrl: string }[];
    socket = new WebSocket(pages.find(tab => tab.type === "page")!.webSocketDebuggerUrl);
    await new Promise<void>((resolve, reject) => { socket!.addEventListener("open", () => resolve(), { once: true }); socket!.addEventListener("error", reject, { once: true }); });
    const reply = new Promise<Record<string, any>>(resolve => socket!.addEventListener("message", event => {
      const value = JSON.parse(String(event.data));
      if (value.id === 1) resolve(value);
    }));
    socket.send(JSON.stringify({ id: 1, method: "Runtime.evaluate", params: { expression: "[...navigator.languages]", returnByValue: true } }));
    const languages = (await reply).result.result.value as string[];
    assert.equal(languages[0], "en-US", `the languages of the page: ${JSON.stringify(languages)}`);
  } finally {
    if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ id: 2, method: "Browser.close" }));
    socket?.close();
    await endBrowser(browser);
    await rm(root, { recursive: true, force: true, maxRetries: 5 }).catch(() => { });
  }
});
