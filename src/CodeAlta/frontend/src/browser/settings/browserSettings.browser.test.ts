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

test("the settings page of Browser shows the choice and the browsers found, changes the browser and the sites, opens and deletes profiles, and fits a narrow Settings", { skip: !edge, timeout: 180_000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), "codealta-browser-settings-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./browserSettings.mount.tsx", import.meta.url))], outfile: join(root, "fixture.js"), bundle: true, platform: "browser", format: "iife" });
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
    // Types in a field, and chooses in a list, as a person does: React hears the input and change events.
    const type = (id: string, value: string) => evaluate(`(() => { const input = document.getElementById(${JSON.stringify(id)});
      Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(input, ${JSON.stringify(value)}); input.dispatchEvent(new Event('input', { bubbles: true })); })()`);
    const choose = (id: string, value: string) => evaluate(`(() => { const select = document.getElementById(${JSON.stringify(id)});
      Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype, 'value').set.call(select, ${JSON.stringify(value)}); select.dispatchEvent(new Event('change', { bubbles: true })); })()`);
    const button = (text: string, scope = "document") => `[...${scope}.querySelectorAll('button')].find(element => element.textContent.trim() === ${JSON.stringify(text)})`;
    const calls = () => evaluate<string[]>("browserSettingsFixture.state.calls");
    await command("Page.navigate", { url: pathToFileURL(page).href });
    assert.equal(await wait("window.browserSettingsFixture"), true);

    await evaluate("browserSettingsFixture.render()");
    assert.equal(await wait("document.getElementById('browser-settings-browser')?.value === 'auto'"), true);
    assert.equal(await evaluate("document.querySelector('[data-browser-choice]').textContent"), "A session gets Microsoft Edge: the default browser, used just now.");
    assert.equal(await evaluate("[...document.getElementById('browser-settings-browser').options].map(option => option.textContent).join('|')"),
      "Automatic (Microsoft Edge)|Microsoft Edge|Firefox|A program…");
    assert.equal(await evaluate("document.querySelector('[data-browser=edge] .browser-settings-default')?.textContent"), "Default");
    assert.equal(await evaluate("document.querySelector('[data-browser=firefox] .browser-settings-used').textContent"), "No use found");
    assert.equal(await evaluate("document.querySelector('#browser-settings-profile option[value=user]').disabled"), true, "the user's browser is offered once it is turned on");
    // A narrow Settings: every card fits, nothing scrolls sideways.
    assert.equal(await evaluate("[...document.querySelectorAll('.browser-settings, .browser-settings .bp6-card')].every(element => element.scrollWidth <= element.clientWidth)"), true);
    assert.equal(await evaluate("document.documentElement.scrollWidth <= document.documentElement.clientWidth"), true);

    // The preferred browser is applied at once, and the page shows what the plugin answers.
    await choose("browser-settings-browser", "firefox");
    assert.equal(await wait("document.querySelector('[data-browser-choice]').textContent === 'A session gets Firefox: preferred in Settings.'"), true);
    assert.ok((await calls()).includes('set:{"browser":"firefox"}'));
    assert.equal(await wait("document.querySelector('.browser-settings-notice')?.textContent === 'Saved.'"), true);

    // The user's own browser: turned on, then a site is added to the blocked ones, and an allowed one removed.
    await evaluate("document.getElementById('browser-settings-user').click()");
    assert.equal(await wait("document.querySelector('#browser-settings-profile option[value=user]').disabled === false"), true);
    await type("browser-settings-blocked", "https://www.Bank.example/login");
    await evaluate(`${button("Add", "document.getElementById('browser-settings-blocked').closest('.browser-settings-sites')")}.click()`);
    assert.ok(await wait("browserSettingsFixture.state.calls.includes('set:{\"blockedSites\":[\"bank.example\"]}')"), JSON.stringify(await calls()));
    await type("browser-settings-allowed", "not a site");
    await evaluate(`${button("Add", "document.getElementById('browser-settings-allowed').closest('.browser-settings-sites')")}.click()`);
    assert.equal(await wait("document.querySelector('.browser-settings-notice')?.textContent === 'A site is a host name, such as github.com.'"), true);
    await evaluate("document.querySelector('.browser-settings-sites .bp6-tag-remove').click()");
    assert.ok(await wait("browserSettingsFixture.state.calls.includes('set:{\"allowedSites\":[]}')"), JSON.stringify(await calls()));

    // A persistent profile opens to sign in; deleting one asks first.
    await evaluate(`${button("Open to sign in", "document.querySelector('[data-browser-profile=work]')")}.click()`);
    assert.equal(await wait("document.querySelector('.browser-settings-notice')?.textContent === 'Microsoft Edge opened the profile work.'"), true);
    await evaluate("document.querySelector('[data-browser-profile=work] [aria-label=\"Delete work\"]').click()");
    assert.equal(await wait("document.querySelector('.confirm-form p')?.textContent === 'Its sign-ins and its data are deleted.'"), true);
    await evaluate("[...document.querySelectorAll('.confirm-form button')].find(element => element.textContent.trim() === 'Delete').click()");
    assert.equal(await wait("browserSettingsFixture.state.calls.includes('remove:work') && !document.querySelector('[data-browser-profile=work]')"), true);
    assert.equal(await evaluate("document.querySelector('[data-browser-profile=default] [aria-label]')"), null, "the default profile is not deleted from here");
  } finally {
    if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ id: 9999, method: "Browser.close" }));
    socket?.close();
    await endBrowser(browser);
    await rm(root, { recursive: true, force: true, maxRetries: 5 }).catch(() => { });
  }
});
