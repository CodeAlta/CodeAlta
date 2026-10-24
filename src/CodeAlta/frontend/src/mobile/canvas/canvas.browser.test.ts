import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdir, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { build } from "esbuild";
import { blueprintPaletteVariables } from "../../blueprintPalette";
import { browserBaseArgs, browserExecutable, endBrowser, waitForBrowserPort } from "../../browserTarget";
import type { MobileStatus } from "./status";

const edge = browserExecutable;
// Where the pictures of this test go, when a person wants to look at them.
const shots = process.env.CODEALTA_SHOTS ?? "";

// A QR code of 25 modules, as the plugin sends it: a pattern is enough to draw.
const rows = Array.from({ length: 25 }, (_, y) => Array.from({ length: 25 }, (_, x) => ((x * 7 + y * 3) % 5 < 2) || (x < 7 && y < 7) ? "1" : "0").join(""));
const status = (overrides: Partial<MobileStatus> = {}, tailscale: Partial<MobileStatus["tailscale"]> = {}): MobileStatus => ({
  server: "listening", port: 51234, serverDetail: null,
  tailscale: { state: "serving", url: "https://workstation.tail1234.ts.net:8443", detail: "Published on the tailnet.", fixUrl: null, ...tailscale },
  connectUrl: "https://workstation.tail1234.ts.net:8443/#k=secret", address: "https://workstation.tail1234.ts.net:8443", qr: rows, ...overrides,
});
const unreachable = { connectUrl: null, address: null, qr: null };

test("the Mobile tab shows the code of the phone page, and what keeps a phone out with what fixes it", { skip: !edge, timeout: 120_000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), "codealta-mobile-tab-"));
  let browser: ReturnType<typeof spawn> | undefined, socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./canvas.mount.tsx", import.meta.url))], outfile: join(root, "fixture.js"), bundle: true, platform: "browser", format: "iife",
      loader: { ".css": "empty" }, define: { "process.env.NODE_ENV": '"development"' }, logLevel: "silent",
      // The tab reaches nothing of the host's client: what imports it is left empty.
      plugins: [{ name: "no-host", setup(bundle) {
        bundle.onResolve({ filter: /^#neoastra$/ }, () => ({ path: "neoastra", namespace: "no-host" }));
        bundle.onLoad({ filter: /.*/, namespace: "no-host" }, () => ({ contents: "export {};", loader: "js" }));
      } }] });
    await writeFile(join(root, "style.css"), blueprintPaletteVariables(await readFile(new URL("../../../node_modules/@blueprintjs/core/lib/css/blueprint.css", import.meta.url), "utf8"))
      + await readFile(new URL("../../style.css", import.meta.url), "utf8") + await readFile(new URL("./canvas.css", import.meta.url), "utf8"));
    const page = join(root, "fixture.html");
    await writeFile(page, '<!doctype html><html><head><meta charset="utf-8"><link rel="stylesheet" href="style.css"></head><body><div id="root" style="height:100vh"></div><script src="fixture.js"></script></body></html>');
    browser = spawn(edge!, [...browserBaseArgs, `--user-data-dir=${join(root, "profile")}`, "--remote-debugging-port=0", "about:blank"], { stdio: ["ignore", "ignore", "pipe"], windowsHide: true });
    const port = await waitForBrowserPort(join(root, "profile"), browser);
    const pages: { type?: string; webSocketDebuggerUrl?: string }[] = await (await fetch(`http://127.0.0.1:${port}/json/list`, { signal: AbortSignal.timeout(10_000) })).json();
    socket = new WebSocket(pages.find(value => value.type === "page")!.webSocketDebuggerUrl!);
    await new Promise<void>((resolve, reject) => { socket!.addEventListener("open", () => resolve(), { once: true }); socket!.addEventListener("error", () => reject(Error("Browser unavailable")), { once: true }); });
    let sequence = 0;
    const problems: string[] = [];
    const pending = new Map<number, (message: any) => void>();
    socket.addEventListener("message", event => {
      const message = JSON.parse(String(event.data));
      pending.get(message.id)?.(message);
      if (message.method === "Runtime.exceptionThrown") problems.push(JSON.stringify(message.params.exceptionDetails).slice(0, 600));
      if (message.method === "Runtime.consoleAPICalled" && message.params.type === "error") problems.push(message.params.args.map((value: any) => value.value ?? value.description).join(" ").slice(0, 400));
    });
    const command = (method: string, params: object = {}) => new Promise<any>((resolve, reject) => {
      const id = ++sequence;
      const timer = setTimeout(() => { pending.delete(id); reject(Error(`${method} timed out`)); }, 30_000);
      pending.set(id, message => { pending.delete(id); clearTimeout(timer); if (message.error) reject(Error(JSON.stringify(message.error))); else resolve(message.result); });
      socket!.send(JSON.stringify({ id, method, params }));
    });
    const evaluate = async <T,>(expression: string): Promise<T> => {
      const result = await command("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true });
      assert.equal(result.exceptionDetails, undefined, JSON.stringify(result.exceptionDetails));
      return result.result?.value as T;
    };
    const wait = async (expression: string, what: string, milliseconds = 8_000) => assert.equal(await evaluate<boolean>(
      `new Promise(resolve=>{const end=Date.now()+${milliseconds};const tick=()=>{let ok=false;try{ok=!!(${expression})}catch{}ok?resolve(true):Date.now()>end?resolve(false):setTimeout(tick,25)};tick()})`), true, what);
    const text = (selector: string) => evaluate<string[]>(`Array.from(document.querySelectorAll(${JSON.stringify(selector)})).map(e=>e.textContent)`);
    const press = (selector: string, label: string) => evaluate<boolean>(
      `(()=>{const e=Array.from(document.querySelectorAll(${JSON.stringify(selector)})).find(e=>e.textContent.trim()===${JSON.stringify(label)});if(!e)return false;e.click();return true})()`);
    const shot = async (name: string) => {
      if (!shots) return;
      await mkdir(shots, { recursive: true });
      await writeFile(join(shots, name), Buffer.from((await command("Page.captureScreenshot", { format: "png" })).data, "base64"));
    };
    const fits = () => evaluate<boolean>(`(()=>{const c=document.querySelector('.mobile-canvas');return c.scrollWidth<=c.clientWidth&&[...c.querySelectorAll('*')].every(e=>e.getBoundingClientRect().right<=c.getBoundingClientRect().right+0.5)})()`);
    const render = (value: MobileStatus | null) => evaluate(`mobileFixture.render(${JSON.stringify(value)})`);
    const publish = (value: MobileStatus) => evaluate(`mobileFixture.publish(${JSON.stringify(value)})`);

    await command("Runtime.enable");
    await command("Emulation.setDeviceMetricsOverride", { width: 900, height: 760, deviceScaleFactor: 1, mobile: false });
    await command("Page.navigate", { url: pathToFileURL(page).href });
    await wait("window.mobileFixture", "the fixture");

    // Served: the code, dark on white with its quiet zone, the address and its copy button, one line; nothing wrong is said.
    await render(status());
    await wait("document.querySelector('.mobile-qr path')", "the QR code");
    assert.deepEqual(await evaluate("mobileFixture.state.calls"), ["mobile.status"], "listens first, then reads the status once (under StrictMode too)");
    assert.equal(await evaluate("mobileFixture.listeners()"), 1);
    assert.deepEqual(await evaluate("(()=>{const s=document.querySelector('.mobile-qr');const r=s.getBoundingClientRect();return [s.getAttribute('viewBox'),Math.round(r.width),Math.round(r.height),s.querySelector('rect').getAttribute('fill'),s.querySelector('path').getAttribute('fill'),s.getAttribute('shape-rendering')]})()"),
      ["0 0 33 33", 280, 280, "#fff", "#000", "crispEdges"]);
    assert.deepEqual(await text(".mobile-address code"), ["https://workstation.tail1234.ts.net:8443"]);
    assert.deepEqual(await text(".mobile-instruction"), ["Scan it with the camera of the phone. The phone must be signed in to the same tailnet."]);
    assert.equal(await evaluate("document.querySelectorAll('.mobile-problem').length"), 0);
    assert.equal(await evaluate("/reset/i.test(document.body.textContent)"), false, "no Reset access in the tab: it is on the plugin's settings page");
    await evaluate("document.querySelector('.mobile-address button').click()");
    await wait("mobileFixture.state.copies.length === 1", "the address copied");
    assert.deepEqual(await evaluate("mobileFixture.state.copies"), ["https://workstation.tail1234.ts.net:8443"]);
    assert.equal(await evaluate("document.querySelector('.mobile-address button').getAttribute('aria-label')"), "Copied");
    await shot("mobile-tab-dark.png");
    for (const dark of [false, true]) {
      await evaluate(`mobileFixture.setTheme(${dark})`);
      assert.equal(await evaluate("getComputedStyle(document.querySelector('.mobile-qr rect')).fill"), "rgb(255, 255, 255)", "white in both themes");
      if (!dark) await shot("mobile-tab-light.png");
    }

    // Tailscale is not installed: the page that fixes it opens in the browser; no Try again for a state that does not change by itself.
    await publish(status(unreachable, { state: "not-installed", detail: "tailscale was not found on this computer.", fixUrl: "https://tailscale.com/download" }));
    await wait("document.querySelector('.mobile-problem[data-problem=tailscale]')", "the Tailscale problem");
    assert.equal(await evaluate("document.querySelectorAll('.mobile-qr').length"), 0);
    assert.deepEqual(await text(".mobile-problem .bp6-heading"), ["Tailscale is not installed on this computer."]);
    assert.deepEqual(await text(".mobile-problem p"), ["tailscale was not found on this computer."]);
    assert.deepEqual(await text(".mobile-problem-actions button"), ["Download Tailscale"]);
    assert.equal(await press(".mobile-problem-actions button", "Download Tailscale"), true);
    assert.deepEqual(await evaluate("mobileFixture.state.opened"), ["https://tailscale.com/download"]);

    // Serve not enabled: the admin console, and Try again, which asks Tailscale again and shows the code once it serves.
    await publish(status(unreachable, { state: "serve-disabled", detail: "Serve is not enabled on your tailnet.", fixUrl: "https://login.tailscale.com/f/serve" }));
    await wait("document.querySelectorAll('.mobile-problem-actions button').length === 2", "fix and Try again");
    assert.deepEqual(await text(".mobile-problem-actions button"), ["Enable it in the admin console", "Try again"]);
    await shot("mobile-tab-serve-disabled.png");
    await evaluate(`mobileFixture.state.afterRetry = ${JSON.stringify(status())}`);
    assert.equal(await press(".mobile-problem-actions button", "Try again"), true);
    await wait("document.querySelector('.mobile-qr')", "the code after Try again");
    assert.equal(await evaluate("mobileFixture.state.calls.filter(c => c === 'mobile.retry').length"), 1);

    // The port is taken by another program: which, and the likely reason when the plugin says none.
    await publish(status({ ...unreachable, server: "port-in-use" }, { state: "stopped", detail: "" }));
    await wait("document.querySelector('.mobile-problem[data-problem=server]')", "the server problem");
    assert.deepEqual(await text(".mobile-problem[data-problem=server] .bp6-heading"), ["The port 51234 is in use by another program."]);
    assert.deepEqual(await text(".mobile-problem[data-problem=server] p"), ["Another CodeAlta with the Mobile plugin on may be using it."]);
    assert.equal(await evaluate("document.querySelectorAll('.mobile-problem[data-problem=server] button').length"), 0);

    // While it starts, a quiet line, no problem.
    await publish(status({ ...unreachable, server: "starting" }, { state: "pending", detail: "" }));
    await wait("document.querySelector('.mobile-waiting')", "the starting line");
    assert.deepEqual(await text(".mobile-waiting span"), ["The phone page is starting."]);
    assert.equal(await evaluate("document.querySelectorAll('.mobile-problem').length"), 0);

    // A narrow tab: the code shrinks with it and nothing goes past its edge, in both themes.
    await evaluate("document.getElementById('canvas-host').style.width = '300px'");
    for (const [value, name] of [[status({}, { state: "serving" }), "code"], [status(unreachable, { state: "serve-disabled", detail: "Serve is not enabled on your tailnet; ask the admin of the tailnet to turn on HTTPS certificates and Serve.", fixUrl: "https://login.tailscale.com/f/serve" }), "problem"]] as const) {
      await publish(value);
      await new Promise(resolve => setTimeout(resolve, 100));
      for (const dark of [true, false]) {
        await evaluate(`mobileFixture.setTheme(${dark})`);
        assert.ok(await fits(), `the narrow tab holds the ${name} (${dark ? "dark" : "light"})`);
      }
      if (name === "code") assert.ok(await evaluate<number>("document.querySelector('.mobile-qr').getBoundingClientRect().width") <= 300 - 32, "the code shrinks with the tab");
      await shot(`mobile-tab-narrow-${name}.png`);
    }
    await evaluate("document.getElementById('canvas-host').style.width = '100%'");

    // A plugin that does not answer says so, and Try again asks it again.
    await evaluate("mobileFixture.state.down = true");
    await render(null);
    await wait("document.querySelector('.mobile-problem')", "the plugin that does not answer");
    assert.deepEqual(await text(".mobile-problem .bp6-heading"), ["The Mobile plugin does not answer."]);
    await evaluate(`mobileFixture.state.down = false; mobileFixture.state.status = ${JSON.stringify(status())}`);
    assert.equal(await press(".mobile-problem-actions button", "Try again"), true);
    await wait("document.querySelector('.mobile-qr')", "the code once the plugin answers");

    assert.deepEqual(problems, []);
  } finally {
    try { socket?.close(); } catch { /* already closed */ }
    await endBrowser(browser);
    await rm(root, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 }).catch(() => { });
  }
});
