import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdir, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import { build } from "esbuild";
import { browserBaseArgs, browserExecutable, waitForBrowserPort } from "../browserTarget";
import { workletFile } from "./workletFile";

// Mounts the production microphone button, prompt editor, download notice and Speech settings page in headless
// Edge, under StrictMode and the page's production content security policy, over a host and a microphone the
// test plays. The last test runs the real capture on the microphone Edge makes up for tests.

const edge = browserExecutable;
const origin = "https://speech.invalid", pageUrl = `${origin}/index.html`;
/** Where the screenshots go to be looked at: the git-ignored tmp folder of the repository. */
const pictures = fileURLToPath(new URL("../../../../../tmp/speech/", import.meta.url));
const read = (path: string) => readFile(new URL(path, import.meta.url), "utf8");

type Page = Readonly<{
  evaluate: <T = unknown>(expression: string) => Promise<T>;
  command: (method: string, params?: object) => Promise<Record<string, any>>;
  until: (expression: string, what: string) => Promise<void>;
  click: (selector: string) => Promise<void>;
  /** Presses and releases a key; `modifiers` is 2 for Ctrl. */
  key: (name: string, virtual: number, options?: { code?: string; modifiers?: number }) => Promise<void>;
  shot: (name: string) => Promise<void>;
  problems: string[];
}>;

async function withPage(entry: string, fixture: string, body: (page: Page) => Promise<void>, flags: readonly string[] = []) {
  const directory = await mkdtemp(join(tmpdir(), "codealta-speech-"));
  let browser: ReturnType<typeof spawn> | undefined, socket: WebSocket | undefined;
  try {
    const config = JSON.parse(await read("../../../neoastra.json")) as { assets: { csp: string } };
    const worker = await build({ entryPoints: [fileURLToPath(new URL("../../node_modules/monaco-editor/esm/vs/editor/editor.worker.js", import.meta.url))],
      bundle: true, platform: "browser", format: "iife", write: false });
    // The stylesheets the editor imports are kept (the pictures show the prompt as the application draws it); its icon font is not needed.
    const bundle = await build({ entryPoints: { fixture: fileURLToPath(new URL(entry, import.meta.url)) }, bundle: true, platform: "browser", format: "iife", write: false,
      outdir: join(directory, "out"), loader: { ".css": "css", ".flf": "text", ".svg": "dataurl" }, external: ["*.ttf"],
      define: { "process.env.NODE_ENV": '"development"', "import.meta.env.VITE_DEMO_MODE": '"false"' },
      plugins: [{ name: "isolated-speech", setup(setup) {
        setup.onResolve({ filter: /^#neoastra$/ }, () => ({ path: fileURLToPath(new URL("../demo-api.ts", import.meta.url)) }));
        // The worker of the editor is a file of the origin, as the build emits it: the policy allows no blob.
        setup.onResolve({ filter: /^monaco-editor\/.*\?worker$/ }, args => ({ path: args.path, namespace: "fixture-worker" }));
        setup.onLoad({ filter: /.*/, namespace: "fixture-worker" }, () => ({ loader: "js", contents: `export default class extends Worker { constructor() { super("/assets/editor.worker.js"); } }` }));
      } }] });
    const output = (extension: string) => bundle.outputFiles.find(file => file.path.endsWith(extension))?.text ?? "";
    const style = (await read("../../node_modules/@blueprintjs/core/lib/css/blueprint.css")) + output(".css") + await read("../style.css") + await read("./speech.css")
      + "html, body { margin: 0; } body { background: var(--bg); color: var(--text); font-size: 14px; } "
      // As in the window, the composers are low in the page: the notices of the top right corner never cover them.
      + "#root { padding: 300px 12px 12px; } .composer-region { max-width: 720px; margin-bottom: 10px; }";
    const assets = new Map<string, { type: string; body: string }>([
      [pageUrl, { type: "text/html", body: '<!doctype html><html class="bp6-dark"><head><meta charset="utf-8"><link rel="icon" href="data:,"><link rel="stylesheet" href="/style.css"></head><body class="bp6-dark"><div id="root"></div><script src="/assets/fixture.js"></script></body></html>' }],
      [`${origin}/style.css`, { type: "text/css", body: style }],
      [`${origin}/assets/fixture.js`, { type: "text/javascript", body: output(".js") }],
      [`${origin}/assets/editor.worker.js`, { type: "text/javascript", body: worker.outputFiles[0].text }],
      // The worklet is beside the entry document, where the build of the application puts it.
      [`${origin}/${workletFile}`, { type: "text/javascript", body: await read("./pcm-worklet.js") }]]);
    browser = spawn(edge!, [...browserBaseArgs, ...flags, "--host-resolver-rules=MAP * ~NOTFOUND", `--user-data-dir=${join(directory, "profile")}`, "--remote-debugging-port=0", "about:blank"],
      { stdio: ["ignore", "ignore", "pipe"], windowsHide: true });
    const port = await waitForBrowserPort(join(directory, "profile"), browser);
    const tabs = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json() as { type: string; url: string; webSocketDebuggerUrl: string }[];
    socket = new WebSocket(tabs.find(tab => tab.type === "page" && tab.url === "about:blank")!.webSocketDebuggerUrl);
    await new Promise<void>((resolve, reject) => { socket!.addEventListener("open", () => resolve(), { once: true }); socket!.addEventListener("error", reject, { once: true }); });
    let sequence = 0;
    const waiting = new Map<number, (value: any) => void>();
    const problems: string[] = [], requested: string[] = [];
    const command = (method: string, params: object = {}) => new Promise<Record<string, any>>((resolve, reject) => {
      const id = ++sequence;
      const timer = setTimeout(() => { waiting.delete(id); reject(new Error(`${method} timed out`)); }, 30_000);
      waiting.set(id, reply => { clearTimeout(timer); reply.error ? reject(new Error(JSON.stringify(reply.error))) : resolve(reply.result ?? {}); });
      socket!.send(JSON.stringify({ id, method, params }));
    });
    socket.addEventListener("message", event => {
      const message = JSON.parse(String(event.data));
      if (waiting.has(message.id)) { waiting.get(message.id)!(message); waiting.delete(message.id); return; }
      if (message.method === "Fetch.requestPaused") {
        const { requestId, request } = message.params, asset = assets.get(request.url);
        requested.push(request.url);
        void (asset ? command("Fetch.fulfillRequest", { requestId, responseCode: 200, responseHeaders: [{ name: "Content-Type", value: asset.type }, { name: "Content-Security-Policy", value: config.assets.csp }],
          body: Buffer.from(asset.body).toString("base64") }) : command("Fetch.failRequest", { requestId, errorReason: "BlockedByClient" })).catch(() => { });
        if (!asset && !request.url.startsWith("data:") && !request.url.endsWith(".ttf")) problems.push(`request: ${request.url}`);
      }
      if (message.method === "Runtime.exceptionThrown") problems.push(`exception: ${JSON.stringify(message.params.exceptionDetails).slice(0, 600)}`);
      if (message.method === "Runtime.consoleAPICalled" && ["error", "warning"].includes(message.params.type)) problems.push(`console ${message.params.type}: ${message.params.args.map((a: any) => a.value ?? a.description).join(" ").slice(0, 400)}`);
      if (message.method === "Log.entryAdded" && message.params.entry.source === "security") problems.push(`security: ${message.params.entry.text}`);
    });
    const evaluate = async <T>(expression: string): Promise<T> => {
      const reply = await command("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true });
      assert.equal(reply.exceptionDetails, undefined, JSON.stringify(reply.exceptionDetails)); return reply.result?.value as T;
    };
    const until = async (expression: string, what: string) => {
      const found = await evaluate<boolean>(`new Promise(resolve => { const end = Date.now() + 10000; (function check() { let ok = false; try { ok = !!(${expression}); } catch {} if (ok) resolve(true); else if (Date.now() > end) resolve(false); else setTimeout(check, 25); })(); })`);
      if (!found) assert.fail(`waited for ${what}; ${problems.join(" | ")}\n${await evaluate<string>("document.body.innerText.slice(0, 900)").catch(() => "")}`);
    };
    // The pointer goes onto the element and presses it there, as a user does: what covers it gets the click instead.
    const click = async (selector: string) => {
      await until(`document.querySelector(${JSON.stringify(selector)})`, selector);
      const point = await evaluate<{ x: number; y: number }>(`(() => { const e = document.querySelector(${JSON.stringify(selector)}); e.scrollIntoView({ block: 'nearest' }); const r = e.getBoundingClientRect(); return { x: r.x + r.width / 2, y: r.y + r.height / 2 }; })()`);
      await command("Input.dispatchMouseEvent", { type: "mouseMoved", x: point.x, y: point.y });
      await command("Input.dispatchMouseEvent", { type: "mousePressed", x: point.x, y: point.y, button: "left", clickCount: 1 });
      await command("Input.dispatchMouseEvent", { type: "mouseReleased", x: point.x, y: point.y, button: "left", clickCount: 1 });
    };
    const key = async (name: string, virtual: number, { code = name, modifiers = 0 } = {}) => {
      await command("Input.dispatchKeyEvent", { type: "keyDown", key: name, code, windowsVirtualKeyCode: virtual, modifiers });
      await command("Input.dispatchKeyEvent", { type: "keyUp", key: name, code, windowsVirtualKeyCode: virtual, modifiers });
    };
    const shot = async (name: string) => {
      await mkdir(pictures, { recursive: true });
      // The settings page is below the composers of the fixture.
      await evaluate("document.querySelector('.speech-settings')?.scrollIntoView({ block: 'start' })");
      const picture = await command("Page.captureScreenshot", { format: "png", captureBeyondViewport: false });
      await writeFile(join(pictures, `${name}.png`), Buffer.from(picture.data, "base64"));
    };
    await command("Page.enable"); await command("Runtime.enable"); await command("Log.enable");
    await command("Emulation.setFocusEmulationEnabled", { enabled: true });
    await command("Emulation.setDeviceMetricsOverride", { width: 1000, height: 900, deviceScaleFactor: 1, mobile: false });
    await command("Fetch.enable", { patterns: [{ urlPattern: "*", requestStage: "Request" }] });
    await command("Page.navigate", { url: pageUrl });
    await until(`window.${fixture}`, "the fixture");
    await body({ evaluate, command, until, click, key, shot, problems });
    assert.deepEqual(problems, [], "no exception, console error or policy violation");
    return requested;
  } finally {
    socket?.close(); browser?.kill();
    await new Promise(resolve => setTimeout(resolve, 300));
    await rm(directory, { recursive: true, force: true, maxRetries: 5 }).catch(() => { });
  }
}

const f = "window.speechFixture";
const mic = (name = "a") => `document.querySelector('[data-composer="${name}"] .speech-button')`;
const view = (name = "a") => `${mic(name)}?.dataset.speech`;
// What the editor shows: the text of the prompt, with what is being said greyed inside it.
const shown = (name = "a") => `(document.querySelector('[data-composer="${name}"] .view-lines')?.textContent ?? '').replaceAll('\\u00a0', ' ')`;
const interim = (name = "a") => `[...document.querySelectorAll('[data-composer="${name}"] .view-lines .speech-interim')].map(e => e.textContent.replaceAll('\\u00a0', ' ')).join('')`;
const text = (name = "a") => `${f}.composers.${name}.text`;
const toast = "[...document.querySelectorAll('.bp6-toast')].map(e => e.querySelector('.bp6-toast-message').textContent)";
const dictation = `${f}.speech.getState().dictation`;
const model = "parakeet-tdt-0.6b-v3";

test("the microphone of a composer dictates into its prompt, and stops with a click, F9's command, Escape, a send and a hidden pane", { skip: !edge, timeout: 120_000 }, async () => {
  await withPage("./speech.mount.tsx", "speechFixture", async ({ evaluate, until, click, key, shot }) => {
    await until(`${view()} === 'idle' && ${view("b")} === 'idle'`, "the two microphones");
    assert.deepEqual(await evaluate(`[${mic()}.id, ${mic("b")}.id, ${mic()}.title, ${mic()}.getAttribute('aria-pressed')]`), ["dictate-prompt", "dictate-prompt-b", "Dictate the prompt (F9)", "false"]);
    // Just before Send, in the bar of the composer.
    assert.equal(await evaluate(`${mic()}.closest('.composer-actions') !== null && (${mic()}.closest('.bp6-popover-target') ?? ${mic()}).nextElementSibling.matches('[data-send]')`), true,
      await evaluate<string>(`${mic()}.closest('.composer-toolbar').outerHTML.slice(0, 1500)`));
    const renders = await evaluate<number>(`${f}.renders`);

    // The first click asks: nothing is in the cache, and nothing is downloaded before the answer.
    await click('[data-composer="a"] .speech-button');
    await until("document.querySelector('.speech-ask')", "the question of the first use");
    assert.deepEqual(await evaluate(`[document.querySelector('.speech-ask strong').textContent, document.querySelector('.speech-ask .confirm-subject').textContent, document.querySelector('.speech-ask p').textContent, ${f}.host.state.installs.length, ${f}.microphone.state.captures.length]`),
      ["Download the speech model?", "Parakeet TDT 0.6b v3", "670 MiB · 25 languages", 0, 0]);
    await until("document.activeElement?.matches('[data-speech-download]')", "the focus on Download");
    // Beside the button, not over it, and whole in the window (it grows into place for a moment).
    await until(`(() => { const ask = document.querySelector('.speech-ask').getBoundingClientRect(), button = ${mic()}.getBoundingClientRect();
      return (ask.bottom <= button.top + 1 || ask.top >= button.bottom - 1) && ask.right <= innerWidth && ask.left >= 0 && ask.top >= 0 && ask.width > 200; })()`, "the question beside the button");
    await shot("ask-dark");
    // Escape and Cancel leave everything as it is.
    await key("Escape", 27);
    await until("!document.querySelector('.speech-ask')", "the question closed by Escape");
    await click('[data-composer="a"] .speech-button');
    await until("document.querySelector('.speech-ask')", "the question again");
    await evaluate("[...document.querySelectorAll('.speech-ask button')].find(e => e.textContent === 'Cancel').click()");
    await until("!document.querySelector('.speech-ask')", "the question closed by Cancel");
    assert.equal(await evaluate(`${f}.host.state.installs.length`), 0);
    // The link goes to the Speech page of Settings.
    await click('[data-composer="a"] .speech-button');
    await until("document.querySelector('.speech-ask-other')", "the link to the settings");
    await evaluate("document.querySelector('.speech-ask-other').click()");
    await until(`${f}.opened.length === 1 && !document.querySelector('.speech-ask')`, "the Speech settings asked for");
    assert.deepEqual(await evaluate(`${f}.opened`), ["speech"]);

    // Download: the host owns it, a notice follows it, and the button says so.
    await click('[data-composer="a"] .speech-button');
    await until("document.querySelector('[data-speech-download]')", "Download");
    await evaluate("document.querySelector('[data-speech-download]').click()");
    await until(`${view()} === 'installing' && ${toast}.length === 1`, "the notice of the download");
    assert.deepEqual(await evaluate(`[${f}.host.state.installs, ${toast}, ${view("b")}, ${mic()}.title]`), [[null], ["Downloading Parakeet TDT 0.6b v3…"], "installing", "The speech model is being downloaded…"]);
    assert.equal(await evaluate("document.querySelector('.bp6-toast .bp6-progress-bar .bp6-progress-meter').style.width"), "", "no size yet: the bar does not measure");
    await evaluate(`${f}.host.tell({ receivedBytes: '222298112', totalBytes: '713031680' })`);
    await until(`${toast}[0] === 'Downloading Parakeet TDT 0.6b v3 — 212 MiB of 680 MiB'`, "the progress of the download");
    assert.match(await evaluate<string>("document.querySelector('.bp6-toast .bp6-progress-meter').style.width"), /^31\.\d+%$/);
    await shot("download-dark");
    // A click meanwhile dictates nothing; the notice a user closed comes back with it.
    await evaluate(`${f}.host.state.startStatus = 'installing'`);
    await evaluate("(document.querySelector('.bp6-toast button[aria-label=\"Close\"]') ?? [...document.querySelectorAll('.bp6-toast button')].at(-1)).click()");
    await until(`${toast}.length === 0`, "the notice closed");
    await evaluate(`${f}.host.tell({ receivedBytes: '322298112' })`);
    await new Promise(resolve => setTimeout(resolve, 400));
    assert.equal(await evaluate(`${toast}.length`), 0, "a closed notice stays closed while the download goes on");
    await click('[data-composer="a"] .speech-button');
    await until(`${toast}.length === 1`, "the notice asked for again");
    assert.equal(await evaluate(`${f}.microphone.state.captures.length`), 0);
    await evaluate(`${f}.host.tell({ state: 'installing' })`);
    await until(`${toast}[0] === 'Installing Parakeet TDT 0.6b v3…'`, "the installation");
    await evaluate(`${f}.host.state.startStatus = null; ${f}.host.installed('${model}')`);
    await until(`${toast}[0] === 'Speech to prompt is ready' && ${view()} === 'idle'`, "the model ready");
    assert.equal(await evaluate("!!document.querySelector('.bp6-toast.bp6-intent-success')"), true);

    // A dictation. The words go where the caret is, after what is typed.
    await evaluate("document.querySelector('#session-prompt').focus()");
    await until("document.activeElement?.closest('#session-prompt')", "the focus in the prompt");
    await evaluate(`${f}.composers.a.changes.length = 0`);
    await click('[data-composer="a"] .speech-button');
    await until(`${view()} === 'recording'`, "the recording");
    assert.deepEqual(await evaluate(`[${mic()}.title, ${mic()}.getAttribute('aria-pressed'), ${f}.microphone.open(), !!document.activeElement?.closest('#session-prompt')]`), ["Stop dictation (F9)", "true", true, true]);
    assert.equal(await evaluate(`getComputedStyle(${mic()}).color === getComputedStyle(document.documentElement).getPropertyValue('--red').trim() || getComputedStyle(${mic()}).color !== getComputedStyle(${mic("b")}).color`), true, "the button that records is not the color of the others");
    // The ring follows the voice, without a render.
    await evaluate(`${f}.microphone.speak({ level: .8 })`);
    await until(`${mic()}.style.getPropertyValue('--speech-level') === '0.80'`, "the level on the button");
    assert.deepEqual(await evaluate(`${f}.host.state.appends.map(request => [request.id, request.sequence])`), [["dictation-1", 0]]);
    // What is being said is greyed in the editor and is no text of the prompt.
    await evaluate(`${f}.host.hear('', 'hello wor')`);
    await until(`${interim()} === 'hello wor'`, "the greyed text");
    assert.deepEqual(await evaluate(`[${text()}, ${f}.composers.a.changes.length, document.querySelector('#session-prompt').hasAttribute('data-interim')]`), ["", 0, true]);
    assert.equal(await evaluate("getComputedStyle(document.querySelector('#session-prompt'), '::after').content"), "none", "the placeholder gives its place");
    await shot("recording-dark");
    await evaluate(`${f}.host.hear('Hello world.', '')`);
    await until(`${text()} === 'Hello world.' && ${interim()} === ''`, "the first sentence written");
    assert.deepEqual(await evaluate(`[${shown()}, ${f}.composers.a.changes]`), ["Hello world.", ["Hello world."]]);
    // The next piece follows the caret, with the space that separates two sentences, and the greyed text too.
    await evaluate(`${f}.host.hear('Hello world.', 'how are')`);
    await until(`${interim()} === ' how are'`, "the greyed text after the sentence");
    await evaluate(`${f}.host.hear('Hello world. How are you?', '')`);
    await until(`${text()} === 'Hello world. How are you?'`, "the second sentence written");
    // The user types meanwhile: the next piece goes where the caret is.
    await key("Home", 36);
    await evaluate(`${f}.host.hear('Hello world. How are you? Fine.', '')`);
    await until(`${text()} === 'Fine.Hello world. How are you?'`, "a piece at the caret");
    // A piece is one step of undo, and what was dictated before it stays.
    await key("z", 90, { code: "KeyZ", modifiers: 2 });
    await until(`${text()} === 'Hello world. How are you?'`, "the last piece undone");
    assert.equal(await evaluate(`${f}.renders`), renders, "a level, a word and a percentage render nothing of the owner");

    // A click stops, and the host gives its last words once.
    await evaluate(`${f}.host.state.dictations[0].finalText = 'Hello world. How are you? Fine. Bye.'`);
    await click('[data-composer="a"] .speech-button');
    await until(`${view()} === 'idle'`, "the dictation stopped");
    assert.deepEqual(await evaluate(`[${text()}, ${f}.microphone.open(), ${f}.host.state.stops.map(request => [request.id, request.cancel]), !!document.activeElement?.closest('#session-prompt')]`),
      ["Bye.Hello world. How are you?", false, [["dictation-1", false]], true]);
    // The editor draws on its next frame: what it shows is then the prompt, with nothing greyed.
    await until(`${interim()} === '' && ${shown()} === 'Bye.Hello world. How are you?'`, "the prompt shown without greyed text");
    assert.equal(await evaluate(`${f}.composers.a.changes.filter(value => value.includes('Bye.')).length`), 1, "the last words are written once");

    // Escape stops the dictation of the composer the keyboard is in, and only that.
    await click('[data-composer="a"] .speech-button');
    await until(`${view()} === 'recording'`, "the recording");
    await key("Escape", 27);
    await until(`${view()} === 'idle'`, "the dictation stopped by Escape");
    assert.equal(await evaluate(`${f}.host.state.stops.at(-1).cancel`), false);
    assert.equal(await evaluate("!!document.activeElement?.closest('#session-prompt')"), true, "Escape was the dictation's: the prompt keeps the keyboard");
    // The command of F9 presses the button of the composer in front.
    await evaluate("document.querySelector('#dictate-prompt').click()");
    await until(`${view()} === 'recording'`, "the recording by the command");
    // Another composer starts: one dictation for the window.
    await evaluate(`${f}.host.hear('Kept.', 'and')`);
    await until(`${text()}.includes('Kept.')`, "a sentence in the first prompt");
    await click('[data-composer="b"] .speech-button');
    await until(`${view("b")} === 'recording' && ${view()} === 'idle'`, "the second composer recording");
    // The first prompt keeps what it heard, and no longer shows what was being said (the editor draws on its next frame).
    await until(`${interim()} === ''`, "the greyed text of the first prompt gone");
    assert.equal(await evaluate(`${dictation}.owner`), "b");
    await evaluate(`${f}.host.hear('For the second.', 'more')`);
    await until(`${text("b")} === 'For the second.' && ${interim("b")} === ' more'`, "the words in the second prompt");
    assert.equal(await evaluate(`${text()}.includes('second')`), false);
    await shot("two-composers-dark");

    // Sending ends the dictation: what was being said is not written into the prompt that left.
    await evaluate(`${f}.host.state.dictations.at(-1).finalText = 'For the second. More that came late.'`);
    await evaluate("document.querySelector('[data-send=\"b\"]').click()");
    await until(`${view("b")} === 'idle'`, "the dictation ended by the send");
    assert.deepEqual(await evaluate(`[${f}.composers.b.sent, ${text("b")}, ${f}.host.state.stops.at(-1).cancel, ${f}.microphone.open()]`), [["For the second."], "", true, false]);
    await until(`${interim("b")} === '' && ${shown("b")} === ''`, "nothing left in the prompt that was sent");
    // A prompt that takes no text, and a pane that is hidden.
    await click('[data-composer="b"] .speech-button');
    await until(`${view("b")} === 'recording'`, "the recording");
    await evaluate(`${f}.composers.b.setDisabled(true)`);
    await until(`${view("b")} === 'idle' && ${mic("b")}.disabled`, "the dictation ended by a read-only prompt");
    assert.equal(await evaluate(`${f}.host.state.stops.at(-1).cancel`), true);
    await evaluate(`${f}.composers.b.setDisabled(false)`);
    await until(`!${mic("b")}.disabled`, "the prompt editable again");
    await click('[data-composer="b"] .speech-button');
    await until(`${view("b")} === 'recording'`, "the recording");
    await evaluate(`${f}.composers.b.setVisible(false)`);
    await until(`${dictation}.status === 'idle'`, "the dictation ended by a hidden pane");
    await evaluate(`${f}.composers.b.setVisible(true)`);
    // A composer that goes away.
    await click('[data-composer="a"] .speech-button');
    await until(`${view()} === 'recording'`, "the recording");
    const stops = await evaluate<number>(`${f}.host.state.stops.length`);
    await evaluate(`${f}.setComposers(false)`);
    await until(`${dictation}.status === 'idle' && ${f}.host.state.stops.length === ${stops + 1}`, "the dictation ended with its composer");
    assert.deepEqual(await evaluate(`[${f}.microphone.open(), ${f}.host.state.stops.at(-1).cancel]`), [false, true]);
    await evaluate(`${f}.setComposers(true)`);
    await until(`${view()} === 'idle'`, "the composers back");

    // The microphone the system refuses: one message, and nothing left open.
    await evaluate(`${f}.microphone.state.opening = 'microphone'`);
    await click('[data-composer="a"] .speech-button');
    await until(`${toast}.includes('The microphone is not available. Check the privacy settings of the system.')`, "the message of the microphone");
    await until(`${view()} === 'idle'`, "the button back");
    assert.deepEqual(await evaluate(`[${f}.microphone.open(), ${f}.host.state.stops.at(-1).cancel]`), [false, true]);
    await evaluate(`${f}.microphone.state.opening = 'ready'`);

    // The light theme, and another language.
    await evaluate("document.documentElement.dataset.theme = 'light'; document.documentElement.classList.remove('bp6-dark'); document.body.classList.remove('bp6-dark')");
    await click('[data-composer="a"] .speech-button');
    await until(`${view()} === 'recording'`, "the recording");
    await evaluate(`${f}.microphone.speak({ level: .6 }); ${f}.host.hear('', 'in the light theme')`);
    await until(`${interim()}.includes('in the light theme')`, "the greyed text");
    await shot("recording-light");
    await evaluate(`${f}.setLocale('fr')`);
    await until(`${mic()}.title === 'Arrêter la dictée (F9)'`, "the button in French");
    assert.equal(await evaluate(`${view()}`), "recording", "a change of language does not end the dictation");
    await evaluate(`${f}.speech.stop('a')`);
    await until(`${view()} === 'idle'`, "the dictation stopped");

    // Turned off, or a page with no microphone: no button.
    await evaluate(`${f}.host.state.enabled = false; ${f}.speech.refresh()`);
    await until(`!${mic()} && !${mic("b")}`, "no button when it is turned off");
    await evaluate(`${f}.host.state.enabled = true; ${f}.speech.refresh()`);
    await until(`${mic()}`, "the button back");
    await evaluate(`Object.defineProperty(navigator, 'mediaDevices', { value: undefined, configurable: true }); ${f}.setComposers(false)`);
    await until("!document.querySelector('[data-composer]')", "the composers gone");
    await evaluate(`${f}.setComposers(true)`);
    await until("document.querySelector('[data-composer=\"a\"] #session-prompt')", "the composers back");
    await new Promise(resolve => setTimeout(resolve, 200));
    assert.equal(await evaluate(`!!document.querySelector('.speech-button')`), false, "no button where the page has no microphone");
  });
});

test("the Speech settings page turns it on and off, chooses and downloads a model, and removes one", { skip: !edge, timeout: 120_000 }, async () => {
  await withPage("./speech.mount.tsx", "speechFixture", async ({ evaluate, until, click, shot }) => {
    const state = `${f}.host.state`, page = "document.querySelector('.speech-settings')";
    const enabled = "document.querySelector('#settings-speech-enabled')", select = "document.querySelector('#settings-speech-model')";
    const status = "document.querySelector('.speech-model-state')";
    const choose = (value: string) => evaluate(`(() => { const e = ${select}; e.value = ${JSON.stringify(value)}; e.dispatchEvent(new Event('change', { bubbles: true })); })()`);
    const button = (label: string, scope = "document") => `[...${scope}.querySelectorAll('button')].find(e => e.textContent.trim() === ${JSON.stringify(label)})`;
    // No horizontal overflow: of the page in its window, and of anything inside it.
    const fits = `(() => { const box = document.querySelector('.settings-dialog-content'); const edge = box.getBoundingClientRect().right + 1;
      return box.scrollWidth <= box.clientWidth && [...box.querySelectorAll('.speech-settings *')].every(e => e.getBoundingClientRect().right <= edge); })()`;
    await evaluate(`${f}.setSettings(true)`);
    await until(`${select} && ${status}`, "the page");
    assert.deepEqual(await evaluate(`[${page}.querySelector('h1').textContent, ${page}.querySelector('.eyebrow').textContent, ${enabled}.checked, ${select}.value, ${status}.dataset.speechModelState, ${status}.querySelector('span').textContent]`),
      ["Speech", "Personalization", true, model, "missing", "Not downloaded · 670 MiB"]);
    assert.deepEqual(await evaluate(`[...${select}.options].map(option => option.textContent)`), ["Parakeet TDT 0.6b v3 — 25 languages — 670 MiB", "Parakeet TDT 0.6b v2 — English — 661 MiB",
      "Whisper small — Multilingual — 376 MiB", "Moonshine base — English — 141 MiB", "Moonshine tiny — English — 44 MiB", "Custom…"]);
    assert.deepEqual(await evaluate(`[${page}.querySelector('.key-gesture').textContent, ${page}.querySelector('.speech-models .speech-hint').textContent, ${page}.querySelector('.settings-file-location code').textContent]`),
      ["F9", "No model is downloaded.", "C:\\Users\\alexandre.mutel.contoso\\.alta\\cache\\speech"]);
    assert.equal(await evaluate(fits), true);

    // Off and on again: saved at once, and the microphones of the composers follow without a read of their own.
    const reads = await evaluate<number>(`${state}.reads`);
    await evaluate(`${enabled}.click()`);
    await until(`${state}.saves.length === 1 && !${enabled}.checked && !document.querySelector('.speech-button')`, "speech to prompt turned off");
    assert.deepEqual(await evaluate(`${state}.saves[0]`), { enabled: false, model });
    await evaluate(`${enabled}.click()`);
    await until(`${state}.saves.length === 2 && ${enabled}.checked && document.querySelector('.speech-button')`, "speech to prompt turned on");
    assert.ok(await evaluate<number>(`${state}.reads`) <= reads + 2, "one read of the page after each save");

    // Another preset.
    await choose("moonshine-tiny-en");
    await until(`${state}.saves.length === 3 && ${select}.value === 'moonshine-tiny-en'`, "the model chosen");
    assert.deepEqual(await evaluate(`[${state}.saves[2], ${status}.querySelector('span').textContent]`), [{ enabled: true, model: "moonshine-tiny-en" }, "Not downloaded · 44 MiB"]);

    // Download from the page: no question, the button is the request. The page shows how far it is.
    await evaluate(`${button("Download", status)}.click()`);
    await until(`${status}.dataset.speechModelState === 'downloading'`, "the download");
    assert.deepEqual(await evaluate(`[${state}.installs, ${status}.querySelector('span').textContent, !!${button("Cancel", status)}]`), [[null], "Downloading…", true]);
    await evaluate(`${f}.host.tell({ receivedBytes: '14302577', totalBytes: '46137344' })`);
    await until(`${status}.querySelector('span').textContent === 'Downloading… 31%'`, "the percentage");
    await shot("settings-downloading-dark");
    // Cancel stops it; what it wrote stays with the host.
    await evaluate(`${button("Cancel", status)}.click()`);
    await until(`${state}.cancels === 1 && ${status}.dataset.speechModelState === 'missing'`, "the download canceled");
    // Again, to its end: the model is in the cache and in the table.
    await evaluate(`${button("Download", status)}.click()`);
    await until(`${status}.dataset.speechModelState === 'downloading'`, "the download again");
    await evaluate(`${f}.host.tell({ state: 'installing' })`);
    await until(`${status}.querySelector('span').textContent === 'Installing…'`, "the installation");
    await evaluate(`${f}.host.installed('moonshine-tiny-en')`);
    await until(`${status}.dataset.speechModelState === 'installed'`, "the model downloaded");
    assert.deepEqual(await evaluate(`[${status}.querySelector('span').textContent, [...${page}.querySelectorAll('.speech-table tbody tr')].map(row => [...row.cells].slice(0, 3).map(cell => cell.textContent))]`),
      ["Downloaded · 44 MiB", [["Moonshine tiny", "Useful Sensors · MIT", "44 MiB"]]]);

    // A custom address: what the host refuses is said beside the field, which keeps what was typed.
    await choose(":custom");
    await until("document.querySelector('#settings-speech-address')", "the field of the address");
    assert.equal(await evaluate(`${state}.saves.length`), 4 - 1, "choosing Custom saves nothing");
    const type = (value: string) => evaluate(`(() => { const e = document.querySelector('#settings-speech-address'); e.focus();
      Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(e, ${JSON.stringify(value)}); e.dispatchEvent(new Event('input', { bubbles: true })); })()`);
    assert.equal(await evaluate(`${button("Apply", page)}.disabled`), true);
    await type("not an address");
    await evaluate(`${button("Apply", page)}.click()`);
    await until("document.querySelector('#settings-speech-address-error')", "the refusal of the address");
    assert.deepEqual(await evaluate(`[document.querySelector('#settings-speech-address-error').textContent, document.querySelector('#settings-speech-address').value, document.querySelector('#settings-speech-address').getAttribute('aria-invalid'), ${state}.model]`),
      ["Enter the address of a Hugging Face repository, such as https://huggingface.co/owner/model.", "not an address", "true", "moonshine-tiny-en"]);
    await type("https://huggingface.co/csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8");
    assert.equal(await evaluate("!document.querySelector('#settings-speech-address-error')"), true, "typing takes the refusal away");
    await evaluate(`${button("Apply", page)}.click()`);
    await until(`!document.querySelector('#settings-speech-address') && ${select}.value.startsWith('https://huggingface.co/')`, "the address chosen");
    assert.deepEqual(await evaluate(`[${state}.model, ${select}.selectedOptions[0].textContent, ${status}.querySelector('span').textContent]`),
      ["https://huggingface.co/csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8", "csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8", "Not downloaded"]);

    // The narrow window of Settings: about 430 px of content, and nothing wider than it.
    await evaluate(`${f}.setSize({ width: 430, height: 560 })`);
    await until("document.querySelector('.settings-dialog-content').clientWidth <= 430", "the narrow page");
    assert.equal(await evaluate(fits), true, "no horizontal overflow at 430 px");
    await shot("settings-narrow-dark");
    await choose(":custom");
    await until("document.querySelector('#settings-speech-address')", "the field of the address");
    await type("x");
    await evaluate(`${button("Apply", page)}.click()`);
    await until("document.querySelector('#settings-speech-address-error')", "the refusal of the address");
    assert.equal(await evaluate(fits), true, "the field and its refusal fit at 430 px");
    await choose("moonshine-tiny-en");
    await until(`${select}.value === 'moonshine-tiny-en' && ${status}.dataset.speechModelState === 'installed'`, "the downloaded model chosen again");
    assert.equal(await evaluate(fits), true);
    await evaluate(`${f}.setSize({ width: 760, height: 620 })`);

    // Delete asks in a small popover beside the button, then removes the model from the cache.
    await click(".speech-table .speech-actions button");
    await until("document.querySelector('.provider-settings-confirm')", "the question of the removal");
    assert.deepEqual(await evaluate(`[document.querySelector('.provider-settings-confirm p').textContent, ${state}.deletes.length]`), ["Delete Moonshine tiny?", 0]);
    await shot("settings-delete-dark");
    await evaluate("document.querySelector('.provider-settings-confirm button').click()");
    await until(`${state}.deletes.length === 1 && ${status}.dataset.speechModelState === 'missing'`, "the model removed");
    assert.deepEqual(await evaluate(`[${state}.deletes, ${page}.querySelector('.speech-models .speech-hint').textContent]`), [["moonshine-tiny-en"], "No model is downloaded."]);

    // The light theme and another language.
    await evaluate(`${f}.host.state.models = ${f}.host.state.models.map(value => value.id === 'moonshine-tiny-en' ? { ...value, installed: true } : value)`);
    await evaluate(`[...${page}.querySelectorAll('.settings-editor-actions button')][0].click()`);
    await evaluate("document.documentElement.dataset.theme = 'light'; document.documentElement.classList.remove('bp6-dark'); document.body.classList.remove('bp6-dark')");
    await until(`${status}.dataset.speechModelState === 'installed'`, "the model back");
    await shot("settings-light");
    await evaluate(`${f}.setLocale('de')`);
    await until(`${page}.querySelector('h1').textContent === 'Sprache'`, "the page in German");
    assert.equal(await evaluate(fits), true);

    // A system with no engine: the page says so and offers nothing.
    await evaluate(`${state}.supported = false`);
    await evaluate(`[...${page}.querySelectorAll('.settings-editor-actions button')][0].click()`);
    await until(`!${select} && ${page}.querySelector('.bp6-non-ideal-state')`, "the page of a system without speech to prompt");
    assert.equal(await evaluate(`!!document.querySelector('.speech-button')`), false);
  });
});

test("the capture opens the microphone under the policy of the page, gives the chunks of the host, and closes what it opened", { skip: !edge, timeout: 90_000 }, async () => {
  const requested = await withPage("./capture.mount.ts", "captureFixture", async ({ evaluate, until }) => {
    const c = "window.captureFixture";
    assert.equal(await evaluate(`${c}.available`), true);
    await evaluate(`${c}.start()`);
    await until(`${c}.ready === 1 || ${c}.errors.length`, "the microphone open");
    assert.deepEqual(await evaluate(`${c}.errors`), []);
    await until(`${c}.chunks.length >= 5`, "half a second of audio");
    // A tenth of a second of 16 kHz 16-bit audio, whatever the rate of the system.
    assert.deepEqual(await evaluate(`[...new Set(${c}.chunks.map(chunk => chunk.bytes))]`), [3200]);
    assert.equal(await evaluate(`${c}.chunks.every(chunk => chunk.level >= 0 && chunk.level <= 1)`), true);
    assert.deepEqual(await evaluate(`${c}.constraints`), [{ audio: { channelCount: 1, noiseSuppression: true, autoGainControl: true, echoCancellation: false } }]);
    assert.deepEqual(await evaluate(`[${c}.held().tracks, ${c}.held().contexts]`), [1, 1]);

    // Stopped: the track ends, the context closes, and nothing more comes. Stopping twice changes nothing.
    await evaluate(`${c}.stop(); ${c}.stop()`);
    const count = await evaluate<number>(`${c}.chunks.length`);
    await until(`${c}.held().tracks === 0 && ${c}.held().contexts === 0`, "the microphone closed");
    await new Promise(resolve => setTimeout(resolve, 400));
    assert.equal(await evaluate(`${c}.chunks.length`), count, "no audio after the stop");

    // Stopped before the system answered: the microphone is released as soon as it is had.
    await evaluate(`${c}.start(); ${c}.stop()`);
    await new Promise(resolve => setTimeout(resolve, 600));
    assert.deepEqual(await evaluate(`[${c}.held().tracks, ${c}.held().contexts, ${c}.ready, ${c}.errors]`), [0, 0, 1, []]);

    // A page that goes away closes it too.
    await evaluate(`${c}.start()`);
    await until(`${c}.ready === 2`, "the microphone open again");
    await evaluate("window.dispatchEvent(new Event('pagehide'))");
    await until(`${c}.held().tracks === 0 && ${c}.held().contexts === 0`, "the microphone closed with the page");

    // No microphone at all: one failure, and nothing left open.
    await evaluate(`navigator.mediaDevices.getUserMedia = async () => { throw new DOMException('refused', 'NotAllowedError'); }; ${c}.start()`);
    await until(`${c}.errors.length === 1`, "the refusal");
    assert.deepEqual(await evaluate(`[${c}.errors, ${c}.held().tracks, ${c}.held().contexts]`), [["microphone"], 0, 0]);
  }, ["--use-fake-device-for-media-stream", "--use-fake-ui-for-media-stream", "--autoplay-policy=no-user-gesture-required"]);
  // The worklet is a file of the origin, asked for beside the entry document.
  assert.ok(requested.includes(`${origin}/${workletFile}`), requested.join(", "));
});
