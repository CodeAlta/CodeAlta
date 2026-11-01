import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { build } from "esbuild";
import { browserBaseArgs, browserExecutable, waitForBrowserPort } from "../browserTarget";
import { darkerStorageKey, themeStorageKey } from "../windowPreferences";
import { builtInSettings } from "./settingsCatalog";

// The real window, with the Settings shell of the organization test: no host, so the settings the page keeps are the ones
// a search changes in place, and the pages show what they show without a host.
test("Settings search shows editable results, opens a setting with a glow, and Ctrl+P finds settings", { skip: !browserExecutable, timeout: 120_000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), "codealta-settings-search-"));
  let browser: ReturnType<typeof spawn> | undefined, socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("../main.tsx", import.meta.url))], outfile: join(root, "fixture.js"),
      bundle: true, platform: "browser", format: "iife", loader: { ".css": "empty", ".flf": "text", ".svg": "dataurl" },
      define: { "import.meta.env.VITE_DEMO_MODE": '"false"' }, plugins: [{ name: "isolated-settings", setup(bundle) {
        bundle.onResolve({ filter: /^#neoastra$/ }, () => ({ path: fileURLToPath(new URL("../settingsOrganization.neoastra.mount.ts", import.meta.url)) }));
        bundle.onResolve({ filter: /^monaco-editor\/.*\?worker$/ }, args => ({ path: args.path, namespace: "fixture-worker" }));
        bundle.onLoad({ filter: /.*/, namespace: "fixture-worker" }, () => ({ loader: "js", contents: "export default class extends Worker { constructor() { super(URL.createObjectURL(new Blob([''],{type:'text/javascript'}))); } }" }));
      } }] });
    await writeFile(join(root, "style.css"), (await Promise.all([
      "../../node_modules/@blueprintjs/core/lib/css/blueprint.css", "../../node_modules/flexlayout-react/style/light.css", "../style.css", "./settings.css",
    ].map(path => readFile(new URL(path, import.meta.url), "utf8")))).join("\n"));
    const page = join(root, "fixture.html");
    await writeFile(page, '<!doctype html><html><head><meta charset="utf-8"><link rel="stylesheet" href="style.css"></head><body><div id="root"></div><script src="fixture.js"></script></body></html>');
    browser = spawn(browserExecutable!, [...browserBaseArgs, "--allow-file-access-from-files", `--user-data-dir=${join(root, "profile")}`,
      "--remote-debugging-port=0", "about:blank"], { stdio: ["ignore", "ignore", "pipe"], windowsHide: true });
    const port = await waitForBrowserPort(join(root, "profile"), browser);
    const pages = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json() as { type: string; webSocketDebuggerUrl: string }[];
    socket = new WebSocket(pages.find(tab => tab.type === "page")!.webSocketDebuggerUrl);
    await new Promise<void>((resolve, reject) => { socket!.addEventListener("open", () => resolve(), { once: true }); socket!.addEventListener("error", reject, { once: true }); });
    let sequence = 0;
    const command = (method: string, params: object = {}) => new Promise<Record<string, any>>((resolve, reject) => {
      const id = ++sequence, timer = setTimeout(() => reject(Error(`${method} timed out`)), 12_000);
      const reply = (event: MessageEvent) => {
        const message = JSON.parse(String(event.data)); if (message.id !== id) return;
        socket!.removeEventListener("message", reply); clearTimeout(timer);
        if (message.error) reject(Error(JSON.stringify(message.error))); else resolve(message.result ?? {});
      };
      socket!.addEventListener("message", reply); socket!.send(JSON.stringify({ id, method, params }));
    });
    const evaluate = async (expression: string) => {
      const value = await command("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true });
      assert.equal(value.exceptionDetails, undefined, JSON.stringify(value.exceptionDetails)); return value.result?.value;
    };
    const until = async (condition: string) => assert.equal(await evaluate(`new Promise(resolve=>{const end=Date.now()+7000;const tick=()=>{
      if(${condition})resolve(true);else if(Date.now()>end)resolve(false);else setTimeout(tick,20)};tick()})`), true, condition);
    const type = async (text: string) => { for (const character of text) await command("Input.dispatchKeyEvent", { type: "char", text: character }); };
    const key = (name: string, code: string, keyCode: number, modifiers = 0) => command("Input.dispatchKeyEvent", { type: "rawKeyDown", key: name, code, windowsVirtualKeyCode: keyCode, modifiers })
      .then(() => command("Input.dispatchKeyEvent", { type: "keyUp", key: name, code, windowsVirtualKeyCode: keyCode, modifiers }));

    await command("Page.enable"); await command("Emulation.setFocusEmulationEnabled", { enabled: true });
    await command("Emulation.setDeviceMetricsOverride", { width: 1400, height: 950, deviceScaleFactor: 1, mobile: false });
    await command("Page.navigate", { url: pathToFileURL(page).href });
    await until("!!window.settingsOrganization && !!document.querySelector('button.activity-settings')");
    await evaluate(`localStorage.clear(); localStorage.setItem(${JSON.stringify(themeStorageKey)},'light'); localStorage.setItem(${JSON.stringify(darkerStorageKey)},'off');
      localStorage.setItem('settingsFixtureOwned','false')`);
    await command("Page.reload"); await until("!!window.settingsOrganization && !!document.querySelector('button.activity-settings')");

    // Settings opened by the user starts in its search.
    await evaluate("document.querySelector('button.activity-settings').click()");
    await until("document.activeElement?.closest('.settings-search-field') !== null && document.activeElement?.closest('.settings-search-field') !== undefined");

    // Every row with a control on these pages names a setting the catalog knows.
    const known = JSON.stringify(builtInSettings.map(entry => entry.id));
    for (const pageName of ["Appearance", "General", "Permissions", "Sessions"]) {
      await evaluate(`[...document.querySelectorAll('.settings-dialog-navigation button')].find(button=>button.textContent===${JSON.stringify(pageName)}).click()`);
      await until(`document.querySelector('.settings-dialog-content h1')?.textContent===${JSON.stringify(pageName)}`);
      const untagged = await evaluate(`(()=>{const known=new Set(${known});return [...document.querySelectorAll('.settings-dialog-content .settings-field')]
        .filter(field=>field.querySelector('input,select,button'))
        .filter(field=>{const ids=(field.closest('[data-setting]')?.getAttribute('data-setting')??'').split(' ').filter(Boolean);return !ids.length||ids.some(id=>!known.has(id));})
        .map(field=>field.textContent.slice(0,60));})()`);
      assert.deepEqual(untagged, [], `fields of ${pageName} without a known setting`);
    }

    // Typing shows the matching settings of every page, editable where they are shown.
    await evaluate("document.querySelector('.settings-search-field input').focus()");
    await type("darker");
    await until("!!document.querySelector('.settings-search-results [data-setting-result=\"appearance.darker\"] .bp6-switch input')");
    assert.match(await evaluate("document.querySelector('.settings-search-results h1').textContent"), /results for/);
    assert.equal(await evaluate("[...document.querySelectorAll('.settings-dialog-navigation button[data-settings-section=\"appearance\"] .settings-nav-count')].length"), 1);
    assert.equal(await evaluate("document.querySelector('.settings-dialog-navigation button[data-settings-section=\"providers\"]').hasAttribute('data-dimmed')"), true);
    await evaluate("document.querySelector('.settings-search-results [data-setting-result=\"appearance.darker\"] .bp6-switch input').click()");
    await until(`localStorage.getItem(${JSON.stringify(darkerStorageKey)})==='on'`);

    // Esc clears the search; Enter opens the first result on its page, where it glows.
    await evaluate("document.querySelector('.settings-search-field input').focus()");
    await key("Escape", "Escape", 27);
    await until("!document.querySelector('.settings-search-results') && !!document.querySelector('.settings-dialog')");
    await type("welcome page");
    await until("!!document.querySelector('.settings-search-results')");
    await key("Enter", "Enter", 13);
    await until("document.querySelector('.settings-dialog-content h1')?.textContent==='General'");
    await until("!!document.querySelector('.settings-dialog-content [data-glow]')");

    // Ctrl+P finds a setting, and choosing it opens Settings on it.
    await evaluate("document.querySelector('button[aria-label=\"Close settings\"]').click()");
    await until("!document.querySelector('.settings-dialog')");
    await key("p", "KeyP", 80, 2);
    await until("!!document.getElementById('search-input')");
    await type("darker dark");
    await until("!!document.querySelector('.global-search-row[data-kind=\"setting\"]')");
    await evaluate("document.querySelector('.global-search-row[data-kind=\"setting\"]').click()");
    await until("document.querySelector('.settings-dialog-content h1')?.textContent==='Appearance'");
    await until("document.querySelector('.settings-dialog-content [data-glow]')?.getAttribute('data-setting')==='appearance.darker'");
  } finally {
    if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ id: 9999, method: "Browser.close" }));
    socket?.close(); browser?.kill(); await rm(root, { recursive: true, force: true, maxRetries: 8, retryDelay: 100 });
  }
});
