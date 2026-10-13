import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { build } from "esbuild";
import { browserBaseArgs, browserExecutable, waitForBrowserPort } from "./browserTarget";

// The window itself (main.tsx) over an isolated host, driven with the pointer: a session that is looked at from the
// Explorer takes the one transient tab, a double click keeps it, and the row of a project selects it without opening
// or closing it and shows the prompt of a new session of it.
test("sessions are looked at in one transient tab, kept by a double click, and a project row shows its new-session prompt without folding", { skip: !browserExecutable, timeout: 120_000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), "codealta-preview-tabs-"));
  let browser: ReturnType<typeof spawn> | undefined, socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./main.tsx", import.meta.url))], outfile: join(root, "fixture.js"),
      bundle: true, platform: "browser", format: "iife", loader: { ".css": "empty", ".flf": "text", ".svg": "dataurl" },
      define: { "import.meta.env.VITE_DEMO_MODE": '"false"' }, plugins: [{ name: "isolated-preview-tabs", setup(bundle) {
        bundle.onResolve({ filter: /^#neoastra$/ }, () => ({ path: fileURLToPath(new URL("./selectedSessionMenu.neoastra.mount.ts", import.meta.url)) }));
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
      "../node_modules/@blueprintjs/core/lib/css/blueprint.css", "../node_modules/flexlayout-react/style/light.css", "./style.css", "./explorer/explorer.css",
    ].map(path => readFile(new URL(path, import.meta.url), "utf8")))).join("\n"));
    const page = join(root, "fixture.html");
    await writeFile(page, '<!doctype html><html><head><link rel="stylesheet" href="style.css"></head><body><div id="root"></div><script src="fixture.js"></script></body></html>');
    browser = spawn(browserExecutable!, [...browserBaseArgs, "--allow-file-access-from-files",
      `--user-data-dir=${join(root, "profile")}`, "--remote-debugging-port=0", "about:blank"], { stdio: ["ignore", "ignore", "pipe"], windowsHide: true });
    const port = await waitForBrowserPort(join(root, "profile"), browser);
    const pages = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json() as { type: string; webSocketDebuggerUrl: string }[];
    socket = new WebSocket(pages.find(tab => tab.type === "page")!.webSocketDebuggerUrl);
    await new Promise<void>((resolve, reject) => { socket!.addEventListener("open", () => resolve(), { once: true }); socket!.addEventListener("error", reject, { once: true }); });
    const exceptions: string[] = [];
    socket.addEventListener("message", event => {
      const value = JSON.parse(String(event.data));
      if (value.method === "Runtime.exceptionThrown") exceptions.push(value.params.exceptionDetails.exception?.description ?? value.params.exceptionDetails.text);
      if (value.method === "Runtime.consoleAPICalled" && value.params.type === "error")
        exceptions.push(value.params.args.map((arg: { description?: string; value?: unknown }) => arg.description ?? arg.value).join(" "));
    });
    let sequence = 0;
    const command = (method: string, params: object = {}) => new Promise<Record<string, any>>((resolve, reject) => {
      const id = ++sequence;
      const reply = (event: MessageEvent) => {
        const value = JSON.parse(String(event.data));
        if (value.id !== id) return;
        socket!.removeEventListener("message", reply); clearTimeout(timer);
        value.error ? reject(new Error(JSON.stringify(value.error))) : resolve(value.result);
      };
      const timer = setTimeout(() => { socket!.removeEventListener("message", reply); reject(new Error(`${method} timed out`)); }, 12_000);
      socket!.addEventListener("message", reply); socket!.send(JSON.stringify({ id, method, params }));
    });
    const evaluate = async (expression: string) => {
      const value = await command("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true });
      assert.equal(value.exceptionDetails, undefined, JSON.stringify(value.exceptionDetails)); return value.result?.value;
    };
    // The tabs of the dock in the order they are shown: a transient one starts with "~", the one in front ends with "*".
    const tabs = `[...document.querySelectorAll('.session-dock .flexlayout__tab_button')].map(button => {
      const node = button.querySelector('[data-session-node]');
      return (node?.dataset.transient ? '~' : '') + (node?.textContent ?? '').trim() + (button.classList.contains('flexlayout__tab_button--selected') ? '*' : ''); })`;
    const until = async (condition: string, what: string) => {
      const found = await evaluate(`new Promise(resolve=>{const end=Date.now()+7000;const tick=()=>{let ok=false;try{ok=!!(${condition})}catch{}if(ok)resolve(true);else if(Date.now()>end)resolve(false);else setTimeout(tick,20)};tick()})`);
      assert.equal(found, true, `${what}; tabs: ${JSON.stringify(await evaluate(tabs))}; ${exceptions.join("\n")}`);
    };
    const shown = async (expected: string[], what: string) => {
      await until(`JSON.stringify(${tabs})===${JSON.stringify(JSON.stringify(expected))}`, what);
    };
    // The pointer on an element, pressed once or twice as a person does: a double click is two clicks, then the double click.
    const press = async (selector: string, times: 1 | 2 = 1) => {
      const point = await evaluate(`(() => { const e = ${selector}; if (!e) return null; e.scrollIntoView({ block: 'nearest' }); const r = e.getBoundingClientRect(); return { x: r.x + r.width / 2, y: r.y + r.height / 2 }; })()`);
      assert.ok(point, `nothing matches ${selector}`);
      await command("Input.dispatchMouseEvent", { type: "mouseMoved", x: point.x, y: point.y });
      for (let count = 1; count <= times; count++) {
        await command("Input.dispatchMouseEvent", { type: "mousePressed", x: point.x, y: point.y, button: "left", clickCount: count });
        await command("Input.dispatchMouseEvent", { type: "mouseReleased", x: point.x, y: point.y, button: "left", clickCount: count });
      }
    };
    const session = (id: string) => `(document.querySelector('.session-row[data-session-id="${id}"] > button:first-child') ?? [...document.querySelectorAll('.session-row > button:first-child')].find(row => row.querySelector('.session-title')?.textContent === ${JSON.stringify(id)}))`;
    const scope = (id: string) => `document.querySelector('.project-action-row > button[data-scope="${id}"]')`;
    const title = (text: string) => `[...document.querySelectorAll('.session-dock .flexlayout__tab_button [data-session-node]')].find(node => node.textContent.trim() === ${JSON.stringify(text)})`;
    const state = (id: string) => evaluate(`({ open: ${scope(id)}.getAttribute('aria-expanded'), selected: ${scope(id)}.getAttribute('aria-pressed') })`);

    await command("Page.enable"); await command("Runtime.enable");
    await command("Emulation.setFocusEmulationEnabled", { enabled: true });
    await command("Emulation.setDeviceMetricsOverride", { width: 1500, height: 900, deviceScaleFactor: 1, mobile: false });
    await command("Page.addScriptToEvaluateOnNewDocument", { source: "localStorage.setItem('settingsFixtureOwned','true'); localStorage.setItem('settingsFixtureSecondProject','true')" });
    await command("Page.navigate", { url: pathToFileURL(page).href });
    await until(`!!${session("two")} && ${session("one")}.getAttribute('aria-pressed')==='true'`, "the selected project with session one open");
    // The session the window starts on has a tab of its own.
    await shown(["one - Project*"], "the tab of the first session");

    // A session is looked at: it is in the transient tab, whose title is in italics. The next one takes its place.
    await press(session("two"));
    await shown(["one - Project", "~two - Project*"], "the session looked at, in the transient tab");
    assert.equal(await evaluate(`getComputedStyle(${title("two - Project")}).fontStyle`), "italic");
    assert.equal(await evaluate(`getComputedStyle(${title("one - Project")}).fontStyle`), "normal");
    await press(`document.querySelector('.project-action-row > button[data-scope=""] .tree-twist')`);
    await until(`!!${session("chat-1")}`, "the chats, opened by their chevron");
    assert.deepEqual(await state(""), { open: "true", selected: "false" }, "the chevron opens a scope without selecting it");
    await shown(["one - Project", "~two - Project*"], "nothing changed in the tabs");
    await press(session("chat-1"));
    await shown(["one - Project", "~chat-1 - Chat*"], "another session looked at, in the same tab");
    // Showing a tab that is kept, then the transient one again, changes neither.
    await press(title("one - Project"));
    await shown(["one - Project*", "~chat-1 - Chat"], "the kept tab in front");
    await press(title("chat-1 - Chat"));
    await shown(["one - Project", "~chat-1 - Chat*"], "the transient tab in front again");

    // A double click on a row keeps its session: the tab is its own, and the next session looked at gets another.
    await press(session("chat-2"), 2);
    await shown(["one - Project", "chat-2 - Chat*"], "the session of the row that was double-clicked, kept");
    await press(session("chat-1"));
    await shown(["one - Project", "chat-2 - Chat", "~chat-1 - Chat*"], "a transient tab beside the kept ones");
    // A double click on the title of the transient tab keeps it too.
    await press(title("chat-1 - Chat"), 2);
    await shown(["one - Project", "chat-2 - Chat", "chat-1 - Chat*"], "the transient tab, kept by its title");
    assert.equal(await evaluate(`getComputedStyle(${title("chat-1 - Chat")}).fontStyle`), "normal");
    await press(session("two"));
    await shown(["one - Project", "chat-2 - Chat", "chat-1 - Chat", "~two - Project*"], "the session looked at after that");

    // The row of a project selects it and shows the prompt of a new session of it in the transient tab, in the place of
    // the session that was looked at. It is not opened: its chevron does that.
    assert.deepEqual(await state("other"), { open: "false", selected: "false" });
    await press(scope("other"));
    await shown(["one - Project", "chat-2 - Chat", "chat-1 - Chat", "~New session — Other project*"], "the prompt of the project, in the transient tab");
    assert.deepEqual(await state("other"), { open: "false", selected: "true" }, "a closed project stays closed");
    assert.equal(await evaluate(`document.querySelector('.session-row[data-session-id="other-session"]')?.getBoundingClientRect().height ?? 0`), 0, "its sessions are not shown");
    // An open project stays open, pressed once or again.
    assert.deepEqual(await state("project"), { open: "true", selected: "false" });
    await press(scope("project"));
    await shown(["one - Project", "chat-2 - Chat", "chat-1 - Chat", "~New session — Project*"], "the prompt of the other project, in the same tab");
    assert.deepEqual(await state("project"), { open: "true", selected: "true" });
    await press(scope("project"));
    assert.deepEqual(await state("project"), { open: "true", selected: "true" }, "the row of the selected project does not close it");
    await press(`${scope("project")}.querySelector('.tree-twist')`);
    await until(`${scope("project")}.getAttribute('aria-expanded')==='false'`, "the project closed by its chevron");
    await press(`${scope("project")}.querySelector('.tree-twist')`);
    await until(`${scope("project")}.getAttribute('aria-expanded')==='true'`, "and opened again");

    // A double click on the row of a project gives its prompt a tab of its own; so does one on the title of the
    // transient prompt. Two projects and the chats each keep theirs.
    await press(scope("other"), 2);
    await shown(["one - Project", "chat-2 - Chat", "chat-1 - Chat", "New session — Other project*"], "the prompt of a project, kept");
    assert.deepEqual(await state("other"), { open: "false", selected: "true" });
    await press(scope("project"));
    await shown(["one - Project", "chat-2 - Chat", "chat-1 - Chat", "New session — Other project", "~New session — Project*"], "the prompt of another project, beside it");
    await press(title("New session — Project"), 2);
    await shown(["one - Project", "chat-2 - Chat", "chat-1 - Chat", "New session — Other project", "New session — Project*"], "kept by its title");
    // The prompt that is written in is the one of the tab in front; showing the other tab selects its project.
    assert.equal(await evaluate(`[...document.querySelectorAll('#catalog-prompt')].filter(prompt => prompt.getBoundingClientRect().width > 0).length`), 1);
    await press(title("New session — Other project"));
    await shown(["one - Project", "chat-2 - Chat", "chat-1 - Chat", "New session — Other project*", "New session — Project"], "the tab of the first prompt in front");
    await until(`${scope("other")}.getAttribute('aria-pressed')==='true'`, "its project selected");
    assert.equal(await evaluate(`[...document.querySelectorAll('#catalog-prompt')].filter(prompt => prompt.getBoundingClientRect().width > 0).length`), 1);
    // A session looked at leaves the kept prompts where they are.
    await press(session("two"));
    await shown(["one - Project", "chat-2 - Chat", "chat-1 - Chat", "New session — Other project", "New session — Project", "~two - Project*"], "a session looked at, beside the prompts");

    // The tabs come back as they were at the next start: the kept ones, the prompts, and the transient one.
    await command("Page.reload");
    await until(`!!${session("two")} && ${tabs}.length === 6`, "the window loaded again");
    // The dock is laid out anew at a start: the tabs are the same ones, in an order of its own.
    assert.deepEqual((await evaluate(tabs) as string[]).sort(),
      ["one - Project", "chat-2 - Chat", "chat-1 - Chat", "~two - Project*", "New session — Other project", "New session — Project"].sort());
    assert.deepEqual(exceptions, []);
  } finally {
    socket?.close(); browser?.kill();
    await new Promise(resolve => setTimeout(resolve, 300));
    await rm(root, { recursive: true, force: true, maxRetries: 5 }).catch(() => { });
  }
});
