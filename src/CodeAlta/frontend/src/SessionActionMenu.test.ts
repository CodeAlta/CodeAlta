import assert from "node:assert/strict";
import test from "node:test";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { SessionActionMenu } from "./SessionActionMenu";
import { spawn } from "node:child_process";
import { existsSync } from "node:fs";
import { mkdtemp, readFile, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import { build } from "esbuild";

test("menu exposes keyboard actions, and disables mutations without authority or typed confirmation", () => {
  const render = (enabled: boolean) => renderToStaticMarkup(createElement(SessionActionMenu, {
    id: "actions", label: "A session", rename: enabled, deleteAllowed: enabled, menuRef: null,
    onAction: () => assert.fail("Rendering must not invoke an action"), onDismiss: () => {},
  }));
  const readOnly = render(false);
  assert.match(readOnly, /role="menu"/);
  assert.match(readOnly, /Open session/);
  assert.match(readOnly, /Rename…<\/button>/);
  assert.match(readOnly, /Delete… \(confirmation required\)/);
  assert.equal((readOnly.match(/disabled=""/g) ?? []).length, 2);
  assert.equal((render(true).match(/disabled=""/g) ?? []).length, 0);
  assert.doesNotMatch(readOnly, /prompt|draft|Delete this session/);
});

const edge = ["C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe",
  "C:/Program Files/Microsoft/Edge/Application/msedge.exe"].find(existsSync);

test("menu click only requests an action; Delete cannot invoke a mutation or bypass the existing confirmation", { skip: !edge, timeout: 60_000 }, async t => {
  const root = await mkdtemp(join(tmpdir(), "codealta-session-action-menu-"));
  t.diagnostic(`Mounted menu fixture retained at ${root}`);
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./SessionActionMenu.mount.tsx", import.meta.url))],
      outfile: join(root, "fixture.js"), bundle: true, platform: "browser", format: "iife" });
    const page = join(root, "fixture.html");
    await writeFile(page, '<!doctype html><html><body><div id="root"></div><script src="fixture.js"></script></body></html>');
    const profile = join(root, "profile");
    browser = spawn(edge!, ["--headless=new", "--disable-gpu", "--no-first-run", "--disable-background-networking", "--disable-extensions",
      `--user-data-dir=${profile}`, "--remote-debugging-port=0", "about:blank"], { stdio: "ignore", windowsHide: true });
    let port = "";
    for (let attempt = 0; attempt < 100 && !port; attempt++) {
      try { port = (await readFile(join(profile, "DevToolsActivePort"), "utf8")).split(/\r?\n/)[0]; }
      catch { await new Promise(resolve => setTimeout(resolve, 50)); }
    }
    assert.match(port, /^\d+$/);
    const pages: { type?: string; url?: string; webSocketDebuggerUrl?: string }[] =
      await (await fetch(`http://127.0.0.1:${port}/json/list`, { signal: AbortSignal.timeout(10_000) })).json();
    const tab = pages.find(value => value.type === "page" && value.url === "about:blank");
    assert.ok(tab?.webSocketDebuggerUrl);
    socket = new WebSocket(tab.webSocketDebuggerUrl);
    await new Promise<void>((resolve, reject) => {
      socket!.addEventListener("open", () => resolve(), { once: true });
      socket!.addEventListener("error", () => reject(new Error("test browser unavailable")), { once: true });
    });
    let sequence = 0;
    const command = (method: string, params: object = {}) => new Promise<{ result?: { value?: unknown }; exceptionDetails?: unknown }>((resolve, reject) => {
      const id = ++sequence;
      const timer = setTimeout(() => reject(new Error(`browser ${method} timed out`)), 12_000);
      const reply = (event: MessageEvent) => {
        const message = JSON.parse(String(event.data));
        if (message.id !== id) return;
        socket!.removeEventListener("message", reply); clearTimeout(timer);
        if (message.error) reject(new Error(JSON.stringify(message.error))); else resolve(message.result ?? {});
      };
      socket!.addEventListener("message", reply);
      socket!.send(JSON.stringify({ id, method, params }));
    });
    const evaluate = async (expression: string) => {
      const response = await command("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true });
      assert.equal(response.exceptionDetails, undefined);
      return response.result?.value;
    };
    await command("Page.enable");
    await command("Page.navigate", { url: pathToFileURL(page).href });
    assert.equal(await evaluate("new Promise(resolve=>{const end=Date.now()+7000;const tick=()=>{if(document.querySelectorAll('[role=menuitem]').length===6)resolve(true);else if(Date.now()>end)resolve(false);else setTimeout(tick,20)};tick()})"), true);
    assert.deepEqual(await evaluate("sessionMenuRequests"), []);
    for (const [index, action] of ["open", "rename", "delete"].entries()) {
      await evaluate(`document.querySelectorAll('#enabled button')[${index}].click()`);
      assert.deepEqual(await evaluate("sessionMenuRequests"), ["open", "rename", "delete"].slice(0, index + 1), action);
    }
    // This component dispatches requests, never a confirmed delete. The unchanged
    // actual-App fixture separately verifies typed confirmation before bridge writes.
    assert.equal(await evaluate("!document.querySelector('input,form,.session-delete') && !document.body.textContent.includes('Delete this session')"), true);
    assert.equal(await evaluate("[...document.querySelectorAll('#disabled button')].slice(1).every(b=>b.disabled)"), true);
    await evaluate("document.querySelectorAll('#disabled button')[1].click();document.querySelectorAll('#disabled button')[2].click()");
    assert.deepEqual(await evaluate("sessionMenuRequests"), ["open", "rename", "delete"]);
    await evaluate("document.querySelector('#disabled button').click()");
    assert.deepEqual(await evaluate("sessionMenuRequests"), ["open", "rename", "delete", "read-only:open"]);
  } finally { socket?.close(); browser?.kill(); }
});
