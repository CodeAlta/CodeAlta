import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdtemp, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { build } from "esbuild";
import { browserBaseArgs, browserExecutable, endBrowser, waitForBrowserPort } from "../browserTarget";
import { testLimit, testLimitRanOut } from "../testTime";

const edge = browserExecutable;

test("the window of the sub-projects shows each entry, hides one, edits one, adds a folder and creates a label of the map", { skip: !edge, timeout: testLimit(60_000) }, async () => {
  const root = await mkdtemp(join(tmpdir(), "codealta-subprojects-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./subProjects.mount.tsx", import.meta.url))], outfile: join(root, "fixture.js"), bundle: true, platform: "browser", format: "iife", loader: { ".css": "empty" } });
    const page = join(root, "fixture.html");
    await writeFile(page, '<!doctype html><html><head><meta charset="utf-8"></head><body style="margin:0"><div id="root"></div><script src="fixture.js"></script></body></html>');
    browser = spawn(edge!, [...browserBaseArgs, `--user-data-dir=${join(root, "profile")}`, "--remote-debugging-port=0", "about:blank"], { stdio: ["ignore", "ignore", "pipe"], windowsHide: true });
    const port = await waitForBrowserPort(join(root, "profile"), browser);
    const pages: { type?: string; webSocketDebuggerUrl?: string }[] = await (await fetch(`http://127.0.0.1:${port}/json/list`, { signal: AbortSignal.timeout(testLimit(10_000)) })).json();
    socket = new WebSocket(pages.find(value => value.type === "page")!.webSocketDebuggerUrl!);
    await new Promise<void>((resolve, reject) => { socket!.addEventListener("open", () => resolve(), { once: true }); socket!.addEventListener("error", () => reject(Error("browser unavailable")), { once: true }); });
    let sequence = 0;
    const command = (method: string, params: object = {}) => new Promise<{ result?: { value?: unknown }; exceptionDetails?: unknown }>((resolve, reject) => {
      const id = ++sequence, timer = setTimeout(() => reject(Error(testLimitRanOut(testLimit(12_000), `the answer to ${method}`))), testLimit(12_000));
      const reply = (event: MessageEvent) => {
        const message = JSON.parse(String(event.data)); if (message.id !== id) return;
        socket!.removeEventListener("message", reply); clearTimeout(timer);
        if (message.error) reject(Error(`${method}: ${JSON.stringify(message.error)}`)); else resolve(message.result ?? {});
      };
      socket!.addEventListener("message", reply); socket!.send(JSON.stringify({ id, method, params }));
    });
    const evaluate = async (expression: string) => {
      const result = await command("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true });
      assert.equal(result.exceptionDetails, undefined, JSON.stringify(result.exceptionDetails));
      return result.result?.value;
    };
    const until = (condition: string) => evaluate(`new Promise(resolve => { const end = Date.now() + ${testLimit(7000)}; (function check() {
      if (${condition}) resolve(true); else if (Date.now() > end) resolve(false); else setTimeout(check, 20); })(); })`);
    const calls = async () => await evaluate("[...window.calls]") as string[];
    const click = (selector: string) => evaluate(`(() => { const element = document.querySelector(${JSON.stringify(selector)}); element?.click(); return !!element; })()`);
    // A value typed as React reads it: through the setter of the element, then the event of an input.
    const type = (selector: string, value: string) => evaluate(`(() => { const element = document.querySelector(${JSON.stringify(selector)});
      Object.getOwnPropertyDescriptor(Object.getPrototypeOf(element), "value").set.call(element, ${JSON.stringify(value)});
      element.dispatchEvent(new Event("input", { bubbles: true })); return true; })()`);
    await command("Page.enable");
    await command("Emulation.setDeviceMetricsOverride", { width: 1300, height: 900, deviceScaleFactor: 1, mobile: false });
    await command("Page.navigate", { url: pathToFileURL(page).href });
    assert.equal(await until(`document.querySelectorAll(".subprojects-row").length === 5`), true);

    // Each entry says what it is; a folder that is not there cannot be shown, and the map says what it leaves out.
    assert.deepEqual(await evaluate(`[...document.querySelectorAll(".subprojects-row")].map(row => row.dataset.state)`), ["shown", "hidden", "optional", "shown", "missing"]);
    assert.equal(await evaluate(`document.querySelector('.subprojects-row[data-state="missing"] input').disabled`), true);
    assert.match(await evaluate(`document.querySelector(".subprojects-problems").textContent`) as string, /1 problem/);
    assert.match(await evaluate(`document.querySelector(".subprojects-summary").textContent`) as string, /2 shown, 1 hidden, 1 optional, 1 unavailable/);

    // The switch of a hidden entry shows it again, for the user only.
    assert.equal(await click('.subprojects-row[data-state="hidden"] input'), true);
    assert.equal(await until(`window.calls.includes("show web true")`), true);
    assert.ok((await calls()).includes("changed"));

    // An entry selected shows its form: a name and its folder only for its sessions, saved together.
    assert.equal(await click(".subprojects-row-main"), true);
    assert.equal(await until(`document.querySelector(".subprojects-form input")?.value === "Billing"`), true);
    await type(".subprojects-form input", "Billing service");
    assert.equal(await evaluate(`(() => { const box = [...document.querySelectorAll(".subprojects-form label")].find(label => label.textContent.includes("write only")); box.querySelector("input").click(); return true; })()`), true);
    assert.equal(await until(`!document.querySelector(".subprojects-form button[type=submit]").disabled`), true);
    assert.equal(await click(".subprojects-form button[type=submit]"), true);
    assert.equal(await until(`window.calls.some(call => call.startsWith("update billing"))`), true);
    assert.equal((await calls()).find(call => call.startsWith("update billing")), "update billing name=Billing service labels=- write=folder");

    // The folders that could be added leave out those the map declares.
    assert.equal(await click(".subprojects-tab:nth-child(3)"), true);
    assert.equal(await until(`document.querySelectorAll(".subprojects-candidates li").length === 1`), true);
    assert.match(await evaluate(`document.querySelector(".subprojects-candidates li").textContent`) as string, /apps\/admin/);
    assert.equal(await click(".subprojects-candidates input"), true);
    assert.equal(await until(`[...document.querySelectorAll(".subprojects-add button")].some(button => button.textContent.trim() === "Add the folder")`), true,
      String(await evaluate(`[...document.querySelectorAll(".subprojects-add button")].map(button => button.textContent).join("|")`)));
    assert.equal(await evaluate(`(() => { const add = [...document.querySelectorAll(".subprojects-add button")].find(button => button.textContent.trim() === "Add the folder"); add.click(); return !!add; })()`), true);
    assert.equal(await until(`window.calls.includes("add apps/admin")`), true);

    // A new label of the map gets its key from its name.
    assert.equal(await click(".subprojects-tab:nth-child(2)"), true);
    assert.equal(await until(`!!document.querySelector(".subprojects-new-label")`), true);
    assert.equal(await click(".subprojects-new-label"), true);
    assert.equal(await until(`!!document.querySelector(".subprojects-form input")`), true);
    await type(".subprojects-form input", "Front end");
    assert.equal(await until(`[...document.querySelectorAll(".subprojects-form input")][1]?.value === "front-end"`), true);
    assert.equal(await click(".subprojects-form button[type=submit]"), true);
    assert.equal(await until(`window.calls.includes("label front-end Front end remove=false")`), true);
  } finally {
    // Edge's launcher can exit while the browser it started goes on: the browser itself is asked to close.
    if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ id: 9999, method: "Browser.close" }));
    socket?.close(); await endBrowser(browser); await rm(root, { recursive: true, force: true, maxRetries: 8, retryDelay: 100 });
  }
});
