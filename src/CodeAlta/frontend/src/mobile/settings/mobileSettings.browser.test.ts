import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { build } from "esbuild";
import { browserBaseArgs, browserExecutable, endBrowser, waitForBrowserPort } from "../../browserTarget";

const edge = browserExecutable;

test("the settings page of Mobile shows its ports and its address, applies new ports, asks before it resets the access, and fits a narrow Settings", { skip: !edge, timeout: 180_000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), "codealta-mobile-settings-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./mobileSettings.mount.tsx", import.meta.url))], outfile: join(root, "fixture.js"), bundle: true, platform: "browser", format: "iife" });
    const read = (path: string) => readFile(new URL(path, import.meta.url), "utf8");
    await writeFile(join(root, "style.css"), await read("../../../node_modules/@blueprintjs/core/lib/css/blueprint.css") + await read("../../style.css") + await read("./settings.css"));
    const page = join(root, "fixture.html");
    await writeFile(page, '<!doctype html><html><head><meta charset="utf-8"><link rel="stylesheet" href="style.css"></head><body><div id="root" style="width:430px"></div><script src="fixture.js"></script></body></html>');
    browser = spawn(edge!, [...browserBaseArgs, `--user-data-dir=${join(root, "profile")}`, "--remote-debugging-port=0", "about:blank"], { stdio: ["ignore", "ignore", "pipe"], windowsHide: true });
    const port = await waitForBrowserPort(join(root, "profile"), browser);
    const pages: { type?: string; webSocketDebuggerUrl?: string }[] = await (await fetch(`http://127.0.0.1:${port}/json/list`, { signal: AbortSignal.timeout(10_000) })).json();
    socket = new WebSocket(pages.find(value => value.type === "page")!.webSocketDebuggerUrl!);
    await new Promise<void>((resolve, reject) => { socket!.addEventListener("open", () => resolve(), { once: true }); socket!.addEventListener("error", () => reject(Error("Browser unavailable")), { once: true }); });
    let sequence = 0;
    const command = (method: string, params: object = {}) => new Promise<any>((resolve, reject) => {
      const id = ++sequence;
      const timer = setTimeout(() => reject(Error(`${method} timed out`)), 30_000);
      const reply = (event: MessageEvent) => {
        const message = JSON.parse(String(event.data));
        if (message.id !== id) return;
        socket!.removeEventListener("message", reply); clearTimeout(timer);
        if (message.error) reject(Error(JSON.stringify(message.error))); else resolve(message.result);
      };
      socket!.addEventListener("message", reply); socket!.send(JSON.stringify({ id, method, params }));
    });
    const evaluate = async <T,>(expression: string): Promise<T> => {
      const result = await command("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true });
      assert.equal(result.exceptionDetails, undefined, JSON.stringify(result.exceptionDetails));
      return result.result?.value as T;
    };
    const wait = (expression: string, milliseconds = 8_000) => evaluate<boolean>(
      `new Promise(resolve=>{const end=Date.now()+${milliseconds};const tick=()=>{let ok=false;try{ok=!!(${expression})}catch{}ok?resolve(true):Date.now()>end?resolve(false):setTimeout(tick,25)};tick()})`);
    // Types in a field as a person does: React hears the input event.
    const type = (id: string, value: string) => evaluate(`(() => { const input = document.getElementById(${JSON.stringify(id)});
      Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(input, ${JSON.stringify(value)}); input.dispatchEvent(new Event('input', { bubbles: true })); })()`);
    const button = (text: string) => `[...document.querySelectorAll('button')].find(element => element.textContent.trim() === ${JSON.stringify(text)})`;
    await command("Page.navigate", { url: pathToFileURL(page).href });
    assert.equal(await wait("window.mobileSettingsFixture"), true);

    await evaluate("mobileSettingsFixture.render()");
    assert.equal(await wait("document.getElementById('mobile-settings-port')?.value === '2584'"), true);
    assert.equal(await evaluate("document.getElementById('mobile-settings-https-port').value"), "2584");
    assert.equal(await evaluate("document.querySelector('.mobile-settings-address code')?.textContent"), "https://desk.tail1234.ts.net:2584");
    assert.equal(await evaluate(`${button("Apply")}.disabled`), true, "nothing to apply while nothing changed");
    // A narrow Settings: every card fits, nothing scrolls sideways.
    assert.equal(await evaluate("[...document.querySelectorAll('.mobile-settings, .mobile-settings .bp6-card')].every(element => element.scrollWidth <= element.clientWidth)"), true);
    assert.equal(await evaluate("document.documentElement.scrollWidth <= document.documentElement.clientWidth"), true);

    // A port out of range says so and cannot be applied; a port in range is applied, and the page shows what the plugin answers.
    await type("mobile-settings-port", "80");
    assert.equal(await wait("document.getElementById('mobile-settings-port-problem')?.textContent === 'A port is a number from 1024 to 65535.'"), true);
    assert.equal(await evaluate(`${button("Apply")}.disabled`), true);
    await type("mobile-settings-port", "2590");
    assert.equal(await wait(`!${button("Apply")}.disabled`), true);
    await evaluate(`${button("Apply")}.click()`);
    assert.equal(await wait("mobileSettingsFixture.state.calls.includes('set:2590:2584')"), true);
    assert.equal(await wait("document.querySelector('.mobile-settings-notice')?.textContent === 'Saved.'"), true);
    assert.equal(await evaluate("document.querySelector('.mobile-settings-address').textContent"), "Not served on the tailnet");
    // The plugin says it serves the new port: the page follows it.
    await evaluate("mobileSettingsFixture.change({ address: 'https://desk.tail1234.ts.net:2584', tailscale: { state: 'serving', detail: 'Tailscale serves it' } })");
    assert.equal(await wait("document.querySelector('.mobile-settings-address code')?.textContent === 'https://desk.tail1234.ts.net:2584'"), true);
    // A refusal of the plugin is said with its words, and the field keeps what was typed.
    await evaluate("mobileSettingsFixture.state.refuse = 'Port 2591 is not allowed.'");
    await type("mobile-settings-https-port", "2591");
    await evaluate(`${button("Apply")}.click()`);
    assert.equal(await wait("document.querySelector('.mobile-settings-notice')?.textContent === 'Port 2591 is not allowed.'"), true);
    assert.equal(await evaluate("document.getElementById('mobile-settings-https-port').value"), "2591");
    await evaluate(`${button("Cancel")}.click()`);
    assert.equal(await wait("document.getElementById('mobile-settings-https-port').value === '2584'"), true);

    // Reset access asks first, in a popover beside its button; Cancel leaves the access as it is.
    await evaluate(`${button("Reset access")}.click()`);
    assert.equal(await wait("document.querySelector('.confirm-form p')?.textContent === 'Every phone will need to scan the new code.'"), true);
    assert.equal(await evaluate("document.querySelector('.confirm-form .bp6-checkbox')"), null, "the question is asked every time");
    await evaluate("[...document.querySelectorAll('.confirm-form button')].find(element => element.textContent.trim() === 'Cancel').click()");
    assert.equal(await wait("!document.querySelector('.confirm-form')"), true);
    assert.ok(!(await evaluate<string[]>("mobileSettingsFixture.state.calls")).includes("reset"));
    await evaluate(`${button("Reset access")}.click()`);
    assert.equal(await wait("document.querySelector('.confirm-form')"), true);
    await evaluate("[...document.querySelectorAll('.confirm-form button')].find(element => element.textContent.trim() === 'Reset').click()");
    assert.equal(await wait("mobileSettingsFixture.state.calls.includes('reset') && !document.querySelector('.confirm-form')"), true);
    assert.equal(await wait("document.querySelector('.mobile-settings-notice')?.textContent === 'The access was reset.'"), true);
  } finally {
    if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ id: 9999, method: "Browser.close" }));
    socket?.close();
    await endBrowser(browser);
    await rm(root, { recursive: true, force: true, maxRetries: 5 }).catch(() => { });
  }
});
