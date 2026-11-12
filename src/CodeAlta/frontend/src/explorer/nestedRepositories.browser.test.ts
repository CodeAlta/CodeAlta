import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { build } from "esbuild";
import { browserBaseArgs, browserExecutable, endBrowser, waitForBrowserPort } from "../browserTarget";
import { testLimit, testLimitRanOut } from "../testTime";

const edge = browserExecutable;

test("a sub-project with a map of its own is a repository group nested in its repository, with Sub-projects… on its title", { skip: !edge, timeout: testLimit(60_000) }, async () => {
  const root = await mkdtemp(join(tmpdir(), "codealta-nested-repositories-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./nestedRepositories.mount.tsx", import.meta.url))], outfile: join(root, "fixture.js"), bundle: true, platform: "browser",
      format: "iife", loader: { ".css": "empty" } });
    await writeFile(join(root, "style.css"), await readFile(new URL("../../node_modules/@blueprintjs/core/lib/css/blueprint.css", import.meta.url), "utf8")
      + await readFile(new URL("../style.css", import.meta.url), "utf8") + await readFile(new URL("./explorer.css", import.meta.url), "utf8"));
    const page = join(root, "fixture.html");
    await writeFile(page, '<!doctype html><html><head><meta charset="utf-8"><link rel="stylesheet" href="style.css"></head><body><div id="root"></div><script src="fixture.js"></script></body></html>');
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
    // Whether an element is on the screen: none of the branches it is in is folded.
    const visible = (selector: string) => `(() => { const element = document.querySelector(${JSON.stringify(selector)});
      return !!element && !element.closest("[hidden]") && element.getBoundingClientRect().height > 0; })()`;
    await command("Page.enable");
    await command("Emulation.setDeviceMetricsOverride", { width: 900, height: 900, deviceScaleFactor: 1, mobile: false });
    await command("Page.navigate", { url: pathToFileURL(page).href });
    assert.equal(await until(`document.querySelectorAll(".project-repository-row").length === 2`), true);

    // platform-map › Repos › billing › Apps › invoices-api: each title in the branch of the one above it.
    assert.deepEqual(await evaluate(`[...document.querySelectorAll(".project-repository-row")].map(row => [row.dataset.repository, row.dataset.depth])`),
      [["platform", "0"], ["billing", "2"]]);
    assert.equal(await evaluate(`(() => {
      const outer = document.querySelector('[data-repository="platform"]').nextElementSibling;
      const repos = document.querySelector('[data-label-title="platform-map.repos"]');
      const billing = document.querySelector('[data-repository="billing"]');
      const apps = document.querySelector('[data-label-title="platform-map-billing.apps"]');
      const invoices = document.querySelector('button[data-scope="invoices"]');
      return outer.contains(repos) && repos.nextElementSibling.contains(billing) && billing.nextElementSibling.contains(apps)
        && apps.nextElementSibling.contains(invoices);
    })()`), true);
    assert.match(await evaluate(`document.querySelector('[data-repository="billing"] .project-label-count').textContent`) as string, /^3$/,
      "billing counts itself and its two sub-projects.");
    assert.match(await evaluate(`document.querySelector('[data-repository="platform"] .project-label-count').textContent`) as string, /^5$/,
      "platform-map counts the projects of the nested group too.");
    assert.equal(await evaluate(visible('button[data-scope="notes"]')), true, "A project of no repository stays after the groups.");

    // Sub-projects… of the nested title opens the map of billing, not the one of its repository.
    assert.equal(await evaluate(`(() => { const button = document.querySelector('[data-repository="billing"] .project-repository-subprojects'); button?.click(); return !!button; })()`), true);
    assert.equal(await until(`window.calls.includes("subprojects billing")`), true, String(await evaluate("window.calls.join('|')")));

    // Folding billing hides its sub-projects only; folding platform-map hides billing with them.
    assert.equal(await evaluate(`(() => { document.querySelector('[data-repository="billing"] .project-label-toggle').click(); return true; })()`), true);
    assert.equal(await until(`!${visible('button[data-scope="invoices"]')}`), true);
    assert.equal(await evaluate(visible('[data-repository="billing"]')), true);
    assert.equal(await evaluate(visible('button[data-scope="checkout"]')), true);
    assert.equal(await evaluate(`(() => { document.querySelector('[data-repository="platform"] .project-label-toggle').click(); return true; })()`), true);
    assert.equal(await until(`!${visible('[data-repository="billing"]')}`), true);
    assert.equal(await evaluate(visible('button[data-scope="notes"]')), true);
  } finally {
    // Edge's launcher can exit while the browser it started goes on: the browser itself is asked to close.
    if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ id: 9999, method: "Browser.close" }));
    socket?.close(); await endBrowser(browser); await rm(root, { recursive: true, force: true, maxRetries: 8, retryDelay: 100 });
  }
});
