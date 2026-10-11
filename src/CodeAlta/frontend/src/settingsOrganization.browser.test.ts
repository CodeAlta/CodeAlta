import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { build } from "esbuild";
import { browserBaseArgs, browserExecutable, waitForBrowserPort } from "./browserTarget";
import { colorSchemeIds, colorSchemeStorageKey } from "./colorSchemes";
import { changesPreferencesKey } from "./changes/projectChanges";
import { projectSortStorageKey } from "./explorer/projectRail";
import { projectRailVisibilityKey } from "./explorer/projectRailVisibility";
import { landingAnimationKey, landingStartupKey } from "./landing/landingPreferences";
import { languageStorageKey } from "./localization";
import { recentSessionCountKey, subAgentCountKey } from "./recentSessions";
import { confirmationStorageKey, darkerStorageKey, themeStorageKey } from "./windowPreferences";

test("Settings navigation preserves preferences, shell capabilities and manual confirmation synchronization", { skip: !browserExecutable, timeout: 90_000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), "codealta-settings-organization-"));
  let browser: ReturnType<typeof spawn> | undefined, socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./main.tsx", import.meta.url))], outfile: join(root, "fixture.js"),
      bundle: true, platform: "browser", format: "iife", loader: { ".css": "empty", ".flf": "text", ".svg": "dataurl" },
      define: { "import.meta.env.VITE_DEMO_MODE": '"false"' }, plugins: [{ name: "isolated-settings", setup(bundle) {
        bundle.onResolve({ filter: /^#neoastra$/ }, () => ({ path: fileURLToPath(new URL("./settingsOrganization.neoastra.mount.ts", import.meta.url)) }));
        bundle.onResolve({ filter: /^monaco-editor\/.*\?worker$/ }, args => ({ path: args.path, namespace: "fixture-worker" }));
        bundle.onLoad({ filter: /.*/, namespace: "fixture-worker" }, async () => {
          const worker = await build({ entryPoints: [fileURLToPath(new URL("../node_modules/monaco-editor/esm/vs/editor/editor.worker.js", import.meta.url))],
            bundle: true, platform: "browser", format: "iife", write: false });
          return { loader: "js", contents: `export default class extends Worker { constructor() {
            const url=URL.createObjectURL(new Blob([${JSON.stringify(worker.outputFiles[0].text)}],{type:"text/javascript"}));
            super(url); URL.revokeObjectURL(url);
          } }` };
        });
      } }] });
    await writeFile(join(root, "style.css"), (await Promise.all([
      "../node_modules/@blueprintjs/core/lib/css/blueprint.css", "../node_modules/flexlayout-react/style/light.css", "./style.css",
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
    const frames = () => evaluate("new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(()=>resolve(true))))");
    const open = async () => {
      await evaluate("document.querySelector('button.activity-settings').click()");
      await until("document.querySelector('.settings-dialog-content h1')?.textContent==='Appearance'");
    };
    const select = async (page: string) => {
      await evaluate(`[...document.querySelectorAll('.settings-dialog-navigation button')].find(button=>button.textContent===${JSON.stringify(page)}).click()`);
      await until(`document.querySelector('.settings-dialog-content h1')?.textContent===${JSON.stringify(page)}`); await frames();
    };
    const close = async () => { await evaluate("document.querySelector('button[aria-label=\"Close settings\"]').click()"); await until("!document.querySelector('.settings-dialog')"); };
    const change = async (id: string, value: string) => {
      await evaluate(`(()=>{const input=document.getElementById(${JSON.stringify(id)});input.value=${JSON.stringify(value)};input.dispatchEvent(new Event('change',{bubbles:true}));})()`); await frames();
    };
    const stored = (key: string) => evaluate(`localStorage.getItem(${JSON.stringify(key)})`);
    const seed = { [languageStorageKey]: "en", [themeStorageKey]: "light", [darkerStorageKey]: "on", [colorSchemeStorageKey]: colorSchemeIds[1],
      [projectSortStorageKey]: "recent", [recentSessionCountKey]: "17", [subAgentCountKey]: "9", [projectRailVisibilityKey]: "collapsed",
      [changesPreferencesKey]: JSON.stringify({ view: "all", sideBySide: false }), [landingStartupKey]: "off", [landingAnimationKey]: "off",
      [confirmationStorageKey("sessionDelete")]: "skip", [confirmationStorageKey("projectArchive")]: "skip" };
    await command("Page.enable"); await command("Emulation.setFocusEmulationEnabled", { enabled: true });
    await command("Emulation.setDeviceMetricsOverride", { width: 1280, height: 900, deviceScaleFactor: 1, mobile: false });
    await command("Page.navigate", { url: pathToFileURL(page).href });
    await until("!!window.settingsOrganization && !!document.querySelector('button.activity-settings')");
    for (const owned of [true, false]) {
      await evaluate(`localStorage.clear(); for(const [key,value] of Object.entries(${JSON.stringify(seed)})) localStorage.setItem(key,value);
        localStorage.setItem('settingsFixtureOwned',${JSON.stringify(String(owned))})`);
      await command("Page.reload"); await until("!!window.settingsOrganization && !!document.querySelector('button.activity-settings')");
      await evaluate(`window.preferenceWrites=[]; const keys=${JSON.stringify(Object.keys(seed))}; for(const method of ['setItem','removeItem']) {
        const original=Storage.prototype[method]; Storage.prototype[method]=function(key,...args){if(keys.includes(key))preferenceWrites.push(key);return original.call(this,key,...args)}; }`);
      await open();
      assert.deepEqual(await evaluate("['settings-project-sort','settings-recent-count','settings-sub-agent-count','settings-changes-view'].map(id=>document.getElementById(id).value)"), ["recent", "17", "9", "all"]);
      assert.equal(await evaluate("document.querySelector('#settings-darker').checked && document.querySelector('#settings-darker').disabled && document.querySelector('#settings-rail-collapsed').checked"), true);
      assert.equal(await evaluate("document.querySelector('.settings-session-width output').textContent"), owned ? "75%" : "100%");
      await select("General");
      assert.equal(await evaluate("document.querySelector('#settings-language').value"), "en");
      assert.deepEqual(await evaluate("['settings-landing-startup','settings-landing-animation'].map(id=>document.getElementById(id).checked)"), [false, false]);
      assert.equal(await evaluate("!!document.querySelector('#settings-reconnect-remote-control')"), owned);
      // A catalog-only start never reads shell preferences; it still has all local controls.
      assert.equal(await evaluate("document.querySelector('#settings-on-close')?.value ?? null"), owned ? "keep" : null);
      await select("Permissions");
      assert.deepEqual(await evaluate("['settings-permission-mode','settings-permission-created'].map(id=>document.getElementById(id).disabled)"), [!owned, !owned]);
      assert.deepEqual(await evaluate("['settings-confirm-session-delete','settings-confirm-project-archive'].map(id=>({checked:document.getElementById(id).checked,disabled:document.getElementById(id).disabled}))"), [{ checked: false, disabled: false }, { checked: false, disabled: false }]);
      await select("Appearance"); await select("General"); await select("Permissions");
      assert.deepEqual(await evaluate("preferenceWrites"), [], "navigation must not write a preference");
      assert.deepEqual(await evaluate("settingsOrganization.calls"), [], "navigation must not call shell setters");
      for (const [key, value] of Object.entries(seed)) assert.equal(await stored(key), value, key);
      await evaluate("document.querySelector('#settings-confirm-session-delete').click();document.querySelector('#settings-confirm-project-archive').click()"); await frames();
      assert.equal(await stored(confirmationStorageKey("sessionDelete")), "ask"); assert.equal(await stored(confirmationStorageKey("projectArchive")), "ask");
      await select("General"); await evaluate("document.querySelector('#settings-landing-startup').click();document.querySelector('#settings-landing-animation').click()");
      if (owned) {
        await evaluate("document.querySelector('#settings-reconnect-remote-control').click()");
        await change("settings-on-close", "exit");
      }
      assert.equal(await stored(landingStartupKey), "on"); assert.equal(await stored(landingAnimationKey), "on");
      assert.deepEqual(await evaluate("settingsOrganization.calls.map(call=>call.kind)"), owned ? ["remote", "close"] : []);
      await select("Appearance"); await select("General");
      assert.equal(await evaluate("document.querySelector('#settings-on-close')?.value ?? null"), owned ? "exit" : null);
      await close();
    }
    // Keeping the window running is a separate capability from Remote Control restoration.
    await evaluate("localStorage.setItem('settingsFixtureOwned','true');localStorage.setItem('organizationShell',JSON.stringify({canKeepRunning:false}))");
    await command("Page.reload"); await until("!!window.settingsOrganization && !!document.querySelector('button.activity-settings')"); await open(); await select("General");
    assert.equal(await evaluate("!!document.querySelector('#settings-on-close')"), false);
    assert.equal(await evaluate("!!document.querySelector('#settings-reconnect-remote-control')"), true);
    await close();
    // An owned host with a completely unavailable shell still leaves local confirmations usable.
    await evaluate("localStorage.setItem('organizationNoShell','true')"); await command("Page.reload");
    await until("!!window.settingsOrganization && !!document.querySelector('button.activity-settings')"); await open(); await select("General");
    assert.equal(await evaluate("!!document.querySelector('#settings-on-close') || !!document.querySelector('#settings-reconnect-remote-control')"), false);
    await select("Permissions"); await evaluate("document.querySelector('#settings-confirm-session-delete').click()"); await frames();
    assert.equal(await stored(confirmationStorageKey("sessionDelete")), "skip");
    await close();

    // Both production Explorer questions write the same state the Permissions page reads, and can be re-enabled there.
    for (const kind of ["sessionDelete", "projectArchive"] as const) {
      await evaluate(`localStorage.setItem('settingsFixtureOwned','true');localStorage.removeItem('organizationNoShell');
        localStorage.setItem(${JSON.stringify(projectRailVisibilityKey)},'expanded');localStorage.setItem(${JSON.stringify(landingStartupKey)},'off');
        localStorage.setItem(${JSON.stringify(confirmationStorageKey(kind))},'ask')`);
      await command("Page.reload"); await until("!!document.querySelector('.session-row[data-session-id=two]')");
      if (kind === "sessionDelete") {
        await evaluate("document.querySelector('.session-row[data-session-id=two] .session-actions-trigger').click()");
        await until("!!document.querySelector('.session-tab-popup')");
        await evaluate("[...document.querySelectorAll('.session-tab-popup [role=menuitem]')].find(item=>item.textContent.trim()==='Delete…').click()");
      } else {
        await evaluate("document.querySelector('button[aria-label=\"Project actions\"]').click()");
        await until("!!document.querySelector('[aria-label=\"Project actions\"] .bp6-menu-item')");
        await evaluate("[...document.querySelectorAll('.bp6-menu-item')].find(item=>item.textContent==='Archive project…').click()");
      }
      await until("!!document.querySelector('.confirm-form input')");
      await evaluate("document.querySelector('.confirm-form input').click();document.querySelector('.confirm-form .rename-actions button:last-child').click()");
      await until(`localStorage.getItem(${JSON.stringify(confirmationStorageKey(kind))})==='skip'`);
      await open(); await select("Permissions");
      const id = kind === "sessionDelete" ? "settings-confirm-session-delete" : "settings-confirm-project-archive";
      assert.equal(await evaluate(`document.getElementById(${JSON.stringify(id)}).checked`), false);
      await evaluate(`document.getElementById(${JSON.stringify(id)}).click()`); await frames();
      assert.equal(await stored(confirmationStorageKey(kind)), "ask");
      await close();
    }
  } finally {
    if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ id: 9999, method: "Browser.close" }));
    socket?.close(); browser?.kill(); await rm(root, { recursive: true, force: true, maxRetries: 8, retryDelay: 100 });
  }
});
