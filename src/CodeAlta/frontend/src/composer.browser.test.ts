import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { existsSync, readFileSync } from "node:fs";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { build } from "esbuild";
import { browserBaseArgs, browserExecutable, waitForBrowserPort } from "./browserTarget";
import { testLimit, testLimitRanOut } from "./testTime";

const edge = browserExecutable;

// Mounts the production OwnedSessionPanel with the production styles. The fixture plays the host: it takes or
// refuses a Send, starts and ends the run, and takes steering. The last scenario is the permission mode of a session
// whose model and effort are no longer a pair its provider lists: the host is asked for the mode alone. The session
// whose provider is no longer in its space sends nothing and opens its configuration. The session whose provider was
// removed from the configuration takes the default provider by itself. The model whose provider offers options (its
// context, a fast mode) shows them in the form of the Agent chip, and the host is asked for each change.
const scenarios = [
  { name: "composer sends, enqueues and explicitly steers on Windows", mac: false, permission: false, outside: false, removed: false, options: false },
  { name: "composer sends, enqueues and explicitly steers on macOS", mac: true, permission: false, outside: false, removed: false, options: false },
  { name: "composer sends the permission mode of a session whose saved selection is not valid", mac: false, permission: true, outside: false, removed: false, options: false },
  { name: "composer asks for another provider when the one of the session is not in its space", mac: false, permission: false, outside: true, removed: false, options: false },
  { name: "composer switches a session whose provider was removed to the default provider", mac: false, permission: false, outside: false, removed: true, options: false },
  { name: "composer sets the options of the model of the session", mac: false, permission: false, outside: false, removed: false, options: true },
  { name: "composer compacts only an attached idle session, says why not otherwise, and shows a failed compaction", mac: false, permission: false, outside: false, removed: false, options: false, compaction: true },
  { name: "a prompt the shell queued in a busy session is sent after its pane left and came back", mac: false, permission: false, outside: false, removed: false, options: false, remount: true },
  { name: "side questions bypass the running session and can be reopened", mac: false, permission: false, outside: false, removed: false, options: false, side: true },
  { name: "files and a folder that the host took from a drop are attached to the composer under the point", mac: false, permission: false, outside: false, removed: false, options: false, hostDrop: true },
  { name: "a prompt that starts with ! runs a shell command whose output goes with the next prompt", mac: false, permission: false, outside: false, removed: false, options: false, shell: true },
];
for (const { name, mac, permission, outside, removed, options, compaction = false, remount = false, hostDrop = false, side = false, shell = false } of scenarios) test(name, { skip: !edge, timeout: testLimit(90_000) }, async () => {
  const root = await mkdtemp(join(tmpdir(), "codealta-composer-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./composer.mount.tsx", import.meta.url))], outfile: join(root, "fixture.js"),
      bundle: true, platform: "browser", format: "iife", loader: { ".css": "empty", ".flf": "text", ".svg": "dataurl" },
      define: { "import.meta.env.VITE_DEMO_MODE": '"false"' }, plugins: [{ name: "isolated-composer", setup(bundle) {
        bundle.onResolve({ filter: /^#neoastra$/ }, () => ({ path: fileURLToPath(new URL("./composer.neoastra.mount.ts", import.meta.url)) }));
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
    await writeFile(join(root, "style.css"), readFileSync(new URL("../node_modules/@blueprintjs/core/lib/css/blueprint.css", import.meta.url), "utf8")
      + readFileSync(new URL("./style.css", import.meta.url), "utf8") + readFileSync(new URL("./sideQuestion/sideQuestion.css", import.meta.url), "utf8")
      + readFileSync(new URL("./timelineEmphasis/timelineEmphasis.css", import.meta.url), "utf8") + readFileSync(new URL("./shellCommands/shellCommands.css", import.meta.url), "utf8"));
    const page = join(root, "fixture.html");
    await writeFile(page, '<!doctype html><html><head><meta charset="utf-8"><link rel="stylesheet" href="style.css"></head><body><div id="app"></div><script src="fixture.js"></script></body></html>');
    browser = spawn(edge!, [...browserBaseArgs, "--allow-file-access-from-files",
      `--user-data-dir=${join(root, "profile")}`, "--remote-debugging-port=0", "about:blank"], { stdio: ["ignore", "ignore", "pipe"], windowsHide: true });
    const port = await waitForBrowserPort(join(root, "profile"), browser);
    const pages = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json() as { type?: string; webSocketDebuggerUrl?: string }[];
    const tab = pages.find(tab => tab.type === "page");
    assert.ok(tab?.webSocketDebuggerUrl);
    socket = new WebSocket(tab.webSocketDebuggerUrl);
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
      const timer = setTimeout(() => { socket!.removeEventListener("message", reply); reject(new Error(testLimitRanOut(testLimit(12_000), `the answer to ${method}`))); }, testLimit(12_000));
      socket!.addEventListener("message", reply); socket!.send(JSON.stringify({ id, method, params }));
    });
    const evaluate = async (expression: string) => {
      const value = await command("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true });
      assert.equal(value.exceptionDetails, undefined, JSON.stringify(value.exceptionDetails)); return value.result?.value;
    };
    // Resolves to true, or to what the page shows when the condition never held.
    const wait = (condition: string) => evaluate(`new Promise(resolve=>{const end=Date.now()+${testLimit(8000)};const tick=()=>{if(${condition})resolve(true);
      else if(Date.now()>end)resolve({rows:fixture.rows(),sends:fixture.sendCalls.map(call=>call.text),steers:fixture.steerCalls.map(call=>call.text),
        run:fixture.run(),status:document.querySelector('.composer-status-line')?.textContent,prompt:promptText()});else setTimeout(tick,20)};tick()})`);
    const key = async (key: string, code: string, virtual: number, ctrl = false) => {
      for (const type of ["keyDown", "keyUp"]) await command("Input.dispatchKeyEvent", { type, key, code, windowsVirtualKeyCode: virtual, modifiers: ctrl ? (mac ? 4 : 2) : 0 });
    };
    const enter = (ctrl = false) => key("Enter", "Enter", 13, ctrl);
    const write = async (text: string) => {
      await evaluate("document.querySelector('#session-prompt').focus()");
      assert.equal(await wait("!!document.activeElement?.closest('#session-prompt')"), true);
      await command("Input.insertText", { text });
      assert.equal(await wait(`promptText()===${JSON.stringify(text)}`), true);
    };
    const rows = async () => JSON.parse(await evaluate("JSON.stringify(fixture.rows())")) as string[];
    const sends = async () => JSON.parse(await evaluate("JSON.stringify(fixture.sendCalls.map(call=>call.text))")) as string[];
    const steers = async () => JSON.parse(await evaluate("JSON.stringify(fixture.steerCalls.map(call=>call.text))")) as string[];
    const steer = () => evaluate("document.querySelector('.composer-toolbar [aria-label=\"Steer the running turn\"]').click()");
    const running = "!!document.querySelector('.composer-toolbar [aria-label=\"Cancel observed run\"]')";
    const idle = "!!document.querySelector('.composer-toolbar [aria-label=\"Send\"]')";
    const row = (text: string) => `[...document.querySelectorAll('.composer-queue-row')].find(row=>row.querySelector('.composer-queue-preview').textContent===${JSON.stringify(text)})`;

    await command("Page.enable");
    await command("Runtime.enable");
    await command("Page.addScriptToEvaluateOnNewDocument", { source: `Object.defineProperty(navigator,'platform',{get:()=>${JSON.stringify(mac ? "MacIntel" : "Win32")}});
      window.promptText=()=>(document.querySelector('#session-prompt .view-lines')?.textContent??'').replaceAll('\\u00a0',' ');
      ${side ? "localStorage.setItem('codealta.desktop.timeline.emphasis.v1',JSON.stringify({enabled:true,types:{prompt:{dim:true,opacity:50,hoverOpacity:50},answer:{dim:true,opacity:60,hoverOpacity:60}}}));" : ""}
      ${permission ? "window.fixturePermission={effort:'max',mode:null,status:'ok',calls:[]};" : ""}
      ${outside ? "window.fixtureOutsideSpace=true;" : ""}
      ${removed ? "window.fixtureRemoved={replacement:'fixture-provider',switched:[]};" : ""}
      ${options ? "window.fixtureModelOptions={context:'300k',fast:'false',calls:[]};window.fixtureSessionOptions={options:{subSessions:true},calls:[]};" : ""}
      ${shell ? "window.fixtureReferences=true;" : ""}` });
    await command("Emulation.setFocusEmulationEnabled", { enabled: true });
    await command("Emulation.setDeviceMetricsOverride", { width: 1120, height: 800, deviceScaleFactor: 1, mobile: false });
    await command("Page.navigate", { url: pathToFileURL(page).href });
    assert.equal(await wait(`!!document.querySelector('#session-prompt') && ${idle}`), true, exceptions.join("\n"));

    if (shell) {
      const shellMode = "document.querySelector('#session-prompt')?.hasAttribute('data-shell')";
      const bang = () => key("!", "Digit1", 49);
      // A `!` that starts an empty prompt turns it into a shell command and is not written; Backspace in the empty prompt gives it back.
      await evaluate("document.querySelector('#session-prompt').focus()");
      await bang();
      assert.equal(await wait(`${shellMode} && promptText()==='' && document.querySelector('.composer-status-line')?.textContent.startsWith('Shell command')`), true);
      await key("Backspace", "Backspace", 8);
      assert.equal(await wait(`!${shellMode} && promptText()===''`), true);
      // Enter runs the command: nothing goes to the session, the panel shows its output, and the output is attached to the prompt.
      await bang();
      assert.equal(await wait(shellMode), true);
      await command("Input.insertText", { text: "git status" });
      assert.equal(await wait("promptText()==='git status'"), true);
      await enter();
      assert.equal(await wait("document.querySelector('.shell-command-output')?.textContent==='ran git status'"), true);
      assert.equal(await wait(`!${shellMode} && promptText()==='' && document.querySelector('.prompt-image-attachments')?.textContent.includes('$ git status')`), true);
      assert.equal(await wait("document.querySelector('.composer-shell-count')?.textContent==='1'"), true);
      assert.deepEqual(await sends(), []);
      // A `!` in a prompt that holds text is text.
      await write("Explain!");
      // The prompt carries the output to the session, and gives the timeline back its place.
      await enter();
      assert.equal(await wait("fixture.sendCalls.length===1 && !document.querySelector('.shell-command')"), true);
      assert.deepEqual(JSON.parse(await evaluate("JSON.stringify({ text: fixture.sendCalls[0].text, files: fixture.sendCalls[0].images?.map(file => [file.title, file.mediaType]) })")),
        { text: "Explain!", files: [["$ git status", "text/x-shell-output"]] });
      // In shell mode, Alt+Up brings back the commands run before.
      await bang();
      for (const type of ["keyDown", "keyUp"]) await command("Input.dispatchKeyEvent", { type, key: "ArrowUp", code: "ArrowUp", windowsVirtualKeyCode: 38, modifiers: 1 });
      assert.equal(await wait(`${shellMode} && promptText()==='git status'`), true);
      // A text that starts with `!` and is pasted in an empty prompt is a command too, without its `!`.
      await evaluate("fixture.shellCommands.setMode('fixture-session',false)");
      await key("a", "KeyA", 65, true);
      await key("Backspace", "Backspace", 8);
      assert.equal(await wait(`!${shellMode} && promptText()===''`), true);
      await evaluate(`(() => { const data = new DataTransfer(); data.setData("text/plain", "!dir /b");
        document.querySelector('#session-prompt .native-edit-context, #session-prompt textarea').dispatchEvent(new ClipboardEvent("paste", { clipboardData: data, bubbles: true, cancelable: true })); })()`);
      assert.equal(await wait(`${shellMode} && promptText()==='dir /b'`), true);
      // So is a `!` typed while the keyboard was elsewhere (in the timeline), which the window gives the prompt to type.
      await evaluate("fixture.shellCommands.setMode('fixture-session',false)");
      await key("a", "KeyA", 65, true);
      await key("Backspace", "Backspace", 8);
      assert.equal(await wait(`!${shellMode} && promptText()===''`), true);
      await evaluate("document.activeElement.blur(); document.querySelector('#session-prompt').dispatchEvent(new CustomEvent('codealta:prompt-type', { detail: '!', cancelable: true }))");
      assert.equal(await wait(`${shellMode} && promptText()===''`), true);
      // In shell mode, `@` and `#` are characters of the command: no picker opens.
      for (const [typed, shown] of [["@", "@"], ["(1,2) #", "@(1,2) #"], [" a comment", "@(1,2) # a comment"]]) {
        await command("Input.insertText", { text: typed });
        assert.equal(await wait(`promptText()===${JSON.stringify(shown)}`), true);
        await new Promise(resolve => setTimeout(resolve, 300));
        assert.equal(await evaluate("!!document.querySelector('.reference-palette')"), false, `a picker opened after ${shown}`);
      }
      // A command that ends while a prompt is being sent: its output waits, and goes with the prompt after it.
      await key("a", "KeyA", 65, true);
      await command("Input.insertText", { text: "slow" });
      assert.equal(await wait("promptText()==='slow'"), true);
      await enter();
      assert.equal(await wait("document.querySelector('.shell-command-output')?.textContent==='ran slow'"), true);
      await evaluate("fixture.finishRun(); fixture.sendMode('hold')");
      assert.equal(await wait(idle), true);
      await write("While it runs");
      await enter();
      assert.equal(await wait("fixture.sendCalls.length===2"), true);
      await evaluate("fixture.endSlowCommand()");
      assert.equal(await wait("fixture.shellCommands.view('fixture-session').entries.at(-1).state==='done'"), true);
      await evaluate("fixture.releaseSend()");
      assert.equal(await wait("document.querySelector('.prompt-image-attachments')?.textContent.includes('$ slow')"), true);
      assert.equal(await evaluate("fixture.sendCalls[1].images?.length ?? 0"), 0, "the output was not part of the prompt being sent");
      assert.deepEqual(exceptions, []);
      return;
    }
    if (side) {
      // The fixture has no outer workspace layout; give the timeline real room for the resize check.
      await evaluate("document.querySelector('.session-workspace').style.height='700px'");
      await evaluate("fixture.startRun()");
      assert.equal(await wait(running), true);
      await evaluate("window.dispatchEvent(new CustomEvent('codealta:composer',{detail:'sideQuestion'}))");
      assert.equal(await wait("promptText()==='/ask '"), true);
      await command("Input.insertText", { text: "why?" });
      await enter();
      assert.equal(await wait("document.querySelector('.side-question-body')?.textContent.includes('Answer to why?')"), true);
      await evaluate("fixture.showTimelineReference()");
      assert.equal(await wait("!!document.querySelector('#timeline-reference .message-assistant .markdown-content pre')"), true);
      const compareTimeline = async () => { for (const role of ["user", "assistant"]) {
        const compared = await evaluate(`(()=>{
          const panel=document.querySelector('.side-question .message-${role}');
          const reference=document.querySelector('#timeline-reference .message-${role}');
          if(!panel || !reference) return null;
          const style=(element,selector,properties)=>{const s=getComputedStyle(element.querySelector(selector));
            return properties.map(name=>s.getPropertyValue(name));};
          return {panelHtml:panel.querySelector('.markdown-content').innerHTML, referenceHtml:reference.querySelector('.markdown-content').innerHTML,
            panelOpacity:getComputedStyle(panel.querySelector('.message-body')).opacity, referenceOpacity:getComputedStyle(reference.querySelector('.message-body')).opacity,
            panelStyle:style(panel,'.markdown-content',['color','line-height']), referenceStyle:style(reference,'.markdown-content',['color','line-height']),
            panelList:${role === "assistant"}?style(panel,'ul',['padding-left','margin-top']):null,
            referenceList:${role === "assistant"}?style(reference,'ul',['padding-left','margin-top']):null,
            panelHeading:${role === "assistant"}?style(panel,'h3',['font-size','margin-top']):null,
            referenceHeading:${role === "assistant"}?style(reference,'h3',['font-size','margin-top']):null,
            panelCode:${role === "assistant"}?style(panel,'pre',['white-space','padding','font-size','border-radius']):null,
            referenceCode:${role === "assistant"}?style(reference,'pre',['white-space','padding','font-size','border-radius']):null}; })()`);
        assert.ok(compared, `missing ${role} timeline card`);
        assert.equal(compared.panelHtml, compared.referenceHtml);
        assert.equal(compared.panelOpacity, compared.referenceOpacity);
        assert.deepEqual(compared.panelStyle, compared.referenceStyle);
        assert.deepEqual(compared.panelList, compared.referenceList);
        assert.deepEqual(compared.panelHeading, compared.referenceHeading);
        assert.deepEqual(compared.panelCode, compared.referenceCode);
        assert.equal(compared.panelOpacity, role === "user" ? "0.5" : "0.6");
      } };
      await compareTimeline();
      const geometry = () => evaluate(`(()=>{const panel=document.querySelector('.side-question').getBoundingClientRect();
        const area=document.querySelector('.session-timeline-area').getBoundingClientRect();
        return {height:panel.height, area:area.height, bottom:panel.bottom, areaBottom:area.bottom}})()`);
      const initial = await geometry();
      // Until its edge is dragged, the panel is as tall as its answer, up to half the timeline.
      assert.ok(initial.area > 200 && initial.height > 120 && initial.height <= initial.area / 2 + 2, JSON.stringify(initial));
      assert.ok(Math.abs(initial.bottom - initial.areaBottom) < 10, JSON.stringify(initial));
      const dragGrip = async (delta: number) => {
        const grip = await evaluate(`(()=>{const r=document.querySelector('[aria-label="Resize side question"]').getBoundingClientRect();
          return {x:r.x+r.width/2,y:r.y+r.height/2}})()`);
        // A drag as a hand makes it: the press, a frame for the grip to take the pointer, moves in steps that stay in the
        // window, then the release; a single jump right after the press can reach the page before the capture.
        const height = await evaluate("innerHeight");
        const to = Math.max(1, Math.min(height - 1, grip.y + delta));
        const frame = () => evaluate("new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(() => resolve(true))))");
        await command("Input.dispatchMouseEvent", { type: "mousePressed", x: grip.x, y: grip.y, button: "left", buttons: 1, clickCount: 1 });
        await frame();
        for (let step = 1; step <= 4; step++)
          await command("Input.dispatchMouseEvent", { type: "mouseMoved", x: grip.x, y: grip.y + (to - grip.y) * step / 4, button: "left", buttons: 1 });
        await command("Input.dispatchMouseEvent", { type: "mouseReleased", x: grip.x, y: to, button: "left", buttons: 0, clickCount: 1 });
        await frame();
      };
      await dragGrip(-80);
      const taller = await geometry();
      assert.ok(taller.height > initial.height + 60 && Math.abs(taller.bottom - initial.bottom) < 2, JSON.stringify(taller));
      await dragGrip(80);
      const restored = await geometry();
      assert.ok(Math.abs(restored.height - initial.height) < 2, JSON.stringify({ initial, taller, restored }));
      await dragGrip(1000);
      const minimum = await geometry();
      assert.ok(minimum.height >= 119 && minimum.height <= 121, JSON.stringify(minimum));
      await dragGrip(-1000);
      const maximum = await geometry();
      assert.ok(maximum.height <= maximum.area - 7 && maximum.height >= maximum.area - 9, JSON.stringify(maximum));
      await key("ArrowDown", "ArrowDown", 40);
      const shorter = await geometry();
      assert.ok(shorter.height < maximum.height - 10, JSON.stringify(shorter));
      await key("Home", "Home", 36);
      assert.ok(Math.abs((await geometry()).height - initial.height) < 2);
      assert.deepEqual(await sends(), []);
      assert.deepEqual(await rows(), []);
      assert.deepEqual(await steers(), []);
      await key("Escape", "Escape", 27);
      assert.equal(await wait("!document.querySelector('.side-question')"), true);
      await write("/ask");
      await enter();
      assert.equal(await wait("!!document.querySelector('.side-question-body li button')"), true);
      await evaluate("document.querySelector('.side-question-body li button').click()");
      assert.equal(await wait("document.querySelector('.side-question-body')?.textContent.includes('Answer to why?')"), true);
      assert.equal(await evaluate("fixture.sideQuestions.view('fixture-session').entries[0].state"), "stopped");
      await compareTimeline();
      // The list says when each question was asked; the button of the composer counts them.
      await key("Escape", "Escape", 27);
      await write("/ask");
      await enter();
      assert.equal(await wait("!!document.querySelector('.side-question-list li time')?.textContent"), true);
      assert.equal(await evaluate("document.querySelector('.composer-side-count')?.textContent"), "1");
      await key("Escape", "Escape", 27);
      await write("/ask queued?");
      await evaluate("document.querySelector('.composer-actions-menu').open=true; document.querySelector('[aria-label=\"Enqueue for the next turn\"]').click()");
      assert.equal(await wait("document.querySelector('.side-question-body')?.textContent.includes('Answer to queued?')"), true);
      assert.deepEqual(await rows(), []);
      assert.deepEqual(await sends(), []);
      // Another question from the foot of the panel: its answer takes the place of the one shown, and nothing is sent.
      await evaluate(`(() => { const input = document.querySelector('.side-question-ask input'); const set = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set;
        set.call(input, 'follow?'); input.dispatchEvent(new Event('input', { bubbles: true })); })()`);
      await evaluate("document.querySelector('.side-question-ask button[type=submit]').click()");
      assert.equal(await wait("document.querySelector('.side-question-body')?.textContent.includes('Answer to follow?')"), true);
      assert.equal(await evaluate("document.querySelector('.composer-side-count')?.textContent"), "3");
      assert.deepEqual(await sends(), []);
      // The button of the composer hides the panel, and shows the list again.
      await evaluate("document.querySelector('.composer-side-questions').click()");
      assert.equal(await wait("!document.querySelector('.side-question')"), true);
      await evaluate("document.querySelector('.composer-side-questions').click()");
      assert.equal(await wait("document.querySelectorAll('.side-question-list li').length === 3"), true);
      // A failed answer says what failed in words, not the status of the host.
      await key("Escape", "Escape", 27);
      await write("/ask unsupported?");
      await enter();
      assert.equal(await wait("document.querySelector('.side-question-failure')?.textContent === 'The provider of this session runs an agent of its own and cannot answer side questions.'"), true);
      return;
    }

    if (hostDrop) {
      // Where NeoAstra takes a drop of the file manager away from the page (Linux, Windows), the host tells the page:
      // the folder becomes a link as with Add folder…, the file is read once and attached as from a drop of the page.
      const tiles = "JSON.stringify([...document.querySelectorAll('.prompt-image-thumbnail')].map(tile=>tile.getAttribute('aria-label')))";
      // A change of the choices of the session ends the attachments in flight, for a drop of the page as for this one:
      // the drop comes once the composer has read them (twice as it opens) and settled.
      assert.equal(await wait("window.fixtureChoiceReads>=2"), true);
      await evaluate("new Promise(resolve=>setTimeout(()=>resolve(true),500))");
      assert.deepEqual(JSON.parse(await evaluate(`(async()=>{const box=document.querySelector('.prompt-surface').getBoundingClientRect();
        return JSON.stringify(await fixture.hostDrop(box.left+box.width/2, box.top+box.height/2));})()`)), { delivered: true, problems: [] });
      assert.equal(await wait(`${tiles}==='["docs","notes.txt"]'`), true, await evaluate(tiles));
      assert.equal(await evaluate("document.querySelector('.prompt-image-thumbnail[aria-label=\"docs\"]').title"), "[docs](</home/me/docs>)");
      // Outside the window there is nothing under the point.
      assert.deepEqual(JSON.parse(await evaluate("(async()=>JSON.stringify(await fixture.hostDrop(-50,-50)))()")), { delivered: false, problems: [] });
      assert.deepEqual(exceptions, []);
      return;
    }

    if (outside) {
      // The status line says why nothing is sent, with the name of the provider; the form to choose another provider
      // opens once when the session is shown, and a Send keeps the prompt and opens it again.
      const problem = "Fixture Business is not available in the space of this session: choose another provider to send.";
      assert.equal(await wait(`document.querySelector('.composer-status-line')?.textContent===${JSON.stringify(problem)}`), true);
      assert.equal(await wait("!!document.querySelector('.composer-selection-popover .composer-selection-form')"), true);
      await key("Escape", "Escape", 27);
      assert.equal(await wait("!document.querySelector('.composer-selection-popover')"), true);
      await write("not in this space"); await enter();
      assert.equal(await wait("!!document.querySelector('.composer-selection-popover .composer-selection-form')"), true);
      // The open form covers the status line: it says why under the provider.
      assert.equal(await evaluate("document.querySelector('.composer-selection-popover .composer-selection-notice')?.textContent"), problem);
      assert.deepEqual(await sends(), []);
      assert.equal(await evaluate("promptText()"), "not in this space");
      assert.deepEqual(exceptions, []);
      return;
    }

    if (removed) {
      // The session takes the default provider once it is shown, once, says so, and sends with it.
      assert.equal(await wait("window.fixtureRemoved.switched.length===1"), true);
      assert.deepEqual(await evaluate("window.fixtureRemoved.switched"), ["fixture-provider"]);
      const notice = "fixture-removed is no longer configured: this session now uses Fixture Default.";
      assert.equal(await wait(`[...document.querySelectorAll('.bp6-toast')].some(toast=>toast.textContent.includes(${JSON.stringify(notice)}))`), true);
      assert.equal(await wait("document.querySelector('.composer-status-line')?.textContent==='Prompt ready'"), true);
      assert.equal(await evaluate("!!document.querySelector('.composer-selection-popover')"), false, "Nothing is asked of the user.");
      await write("after the switch"); await enter();
      assert.equal(await wait("fixture.sendCalls.length===1"), true);
      assert.equal(await evaluate("fixture.sendCalls[0].selection?.providerKey"), "fixture-provider");
      assert.deepEqual(await evaluate("window.fixtureRemoved.switched"), ["fixture-provider"]);
      assert.deepEqual(exceptions, []);
      return;
    }

    if (remount) {
      // A work item started in this session while it works: its prompt waits above the composer, and the start
      // waits for the session to take it.
      await evaluate("fixture.startRun()");
      assert.equal(await wait(running), true);
      await evaluate("window.asked=fixture.ask('enqueue','Carry out the task.'),true");
      assert.deepEqual(JSON.parse(await evaluate("JSON.stringify(window.asked)")), { handled: true, result: true });
      assert.deepEqual(await rows(), ["Queue:waiting:1:Carry out the task."]);
      // The window shows another space: the pane of the session leaves. The prompt stays queued, and the start is not refused.
      await evaluate("fixture.unmount()");
      assert.equal(await wait("!document.querySelector('#session-prompt')"), true);
      await evaluate("new Promise(resolve=>setTimeout(()=>resolve(true),200))");
      assert.deepEqual(await rows(), ["Queue:waiting:1:Carry out the task."]);
      assert.equal(await evaluate("window.asked.outcome===undefined"), true, "a pane that leaves does not refuse the prompt it queued");
      // The pane is back out of sight, as the one that sends the queued prompts of a session of another space: the
      // turn ends and the prompt is sent with the selection it was queued with, which is the session taking it.
      await evaluate("fixture.mount(true)");
      assert.equal(await wait("document.querySelector('.composer-status-line')?.textContent.startsWith('Thinking')"), true);
      await evaluate("fixture.finishRun()");
      assert.equal(await wait("fixture.sendCalls.length===1 && window.asked.outcome===true"), true,
        JSON.stringify(await evaluate("JSON.stringify(window.asked)")));
      assert.deepEqual(await sends(), ["Carry out the task."]);
      assert.equal(await evaluate("fixture.sendCalls[0].selection?.providerKey"), "fixture-provider");
      assert.equal(await wait("fixture.rows().length===0"), true);
      assert.deepEqual(exceptions, []);
      return;
    }

    if (compaction) {
      // The control the Compact command presses, in the menu of the composer: it says why it does nothing, and does nothing.
      const compact = "document.querySelector('[data-composer-command=\"compact\"]')";
      const status = "document.querySelector('.composer-foreground-text')?.textContent";
      const unattached = "Available once the session has started: send it a prompt first.";
      await evaluate("fixture.attach(false)");
      assert.equal(await wait(`${compact}?.disabled && ${compact}.dataset.unavailable===${JSON.stringify(unattached)}`), true);
      assert.equal(await evaluate(`${compact}.title.endsWith(${JSON.stringify(unattached)})`), true, "the control tells why in its tooltip");
      await evaluate(`${compact}.click()`);
      assert.equal(await evaluate("fixture.compactCalls.length"), 0);
      assert.equal(await evaluate("fixture.sendCalls.length"), 0, "a compaction is never turned into a Send");
      // Attached, it compacts while idle only.
      await evaluate("fixture.attach(true)");
      assert.equal(await wait(`!${compact}.disabled && !${compact}.dataset.unavailable`), true);
      await evaluate("fixture.startRun()");
      assert.equal(await wait(`${compact}.disabled && ${compact}.dataset.unavailable==='Available once the session is idle.'`), true);
      await evaluate("fixture.finishRun()");
      assert.equal(await wait(`!${compact}.disabled`), true);
      // A compaction the host takes and then fails ends with a visible failure, without anything else to recover.
      await evaluate(`${compact}.click()`);
      assert.equal(await wait(`fixture.compactCalls.length===1 && ${status}==='Compacting…'`), true);
      assert.equal(await wait(`${compact}.disabled && ${compact}.dataset.unavailable==='The conversation is being compacted.'`), true);
      await evaluate("fixture.settleCompaction('Failed','compact_failed')");
      assert.equal(await wait(`${status}==='The compaction failed. The timeline says why.'`), true);
      assert.equal(await evaluate("document.querySelector('.composer-foreground-status').dataset.tone"), "danger");
      assert.equal(await wait(`!${compact}.disabled`), true, "it can be asked again");
      assert.equal(await evaluate("document.querySelector('.retained-send-recovery')"), null);
      // The next prompt replaces the failure in the status line.
      await write("after the failure"); await enter();
      assert.equal(await wait(`fixture.sendCalls.length===1 && ${status}!=='The compaction failed. The timeline says why.'`), true);
      assert.deepEqual(await sends(), ["after the failure"]);
      assert.equal(await evaluate("fixture.compactCalls.length"), 1);
      assert.deepEqual(exceptions, []);
      return;
    }

    if (options) {
      // The options are under the reasoning: a list for the context, a switch for the fast mode.
      const chip = "document.querySelector('.composer-selection')";
      const context = "document.querySelector('#composer-option-fixture-session-context')";
      const fast = "document.querySelector('#composer-option-fixture-session-fast')";
      await evaluate(`${chip}.click()`);
      assert.equal(await wait(`!!${context} && !!${fast}`), true, "the form of the Agent chip has the options of the model");
      assert.equal(await evaluate(`${context}.value`), "300k");
      assert.equal(await evaluate(`${fast}.checked`), false);
      await evaluate(`(() => { const select = ${context}; Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype, 'value').set.call(select, '1m');
        select.dispatchEvent(new Event('change', { bubbles: true })); })()`);
      assert.equal(await wait(`fixtureModelOptions.calls.length===1 && ${context}.value==='1m'`), true);
      await evaluate(`${fast}.click()`);
      assert.equal(await wait(`fixtureModelOptions.calls.length===2 && ${fast}.checked`), true);
      assert.deepEqual(JSON.parse(await evaluate("JSON.stringify(fixtureModelOptions.calls)")), ["fixture-model:context=1m", "fixture-model:fast=true"]);

      // The fast mode of the session is under More options, after Private. On, a colored lightning comes right before the
      // name of the model on the chip, and its tooltip says it; off, it is gone.
      const more = "document.querySelector('.composer-selection-more-button')";
      const fastMode = "document.querySelector('#composer-fast-mode-fixture-session')";
      const name = "document.querySelector('.composer-selection .composer-selection-model-name')";
      const lightning = `${name}?.querySelector('.composer-selection-fast')`;
      assert.equal(await wait(`!!${more}`), true, "the options of the session are known");
      await evaluate(`${more}.click()`);
      assert.equal(await wait(`!!${fastMode}`), true, "the model of the session has a fast mode");
      assert.equal(await evaluate(`!!(document.querySelector('#composer-private-fixture-session').compareDocumentPosition(${fastMode}) & Node.DOCUMENT_POSITION_FOLLOWING)`), true);
      assert.equal(await evaluate(`${fastMode}.checked`), false);
      assert.equal(await evaluate(`!!${lightning}`), false);
      // Until the session chooses, its switch and chip follow the speed reported by the provider.
      await evaluate("window.fixtureReportedFastMode = true");
      assert.equal(await wait(`${fastMode}.checked && !!${lightning}`), true);
      assert.equal(await evaluate("fixtureSessionOptions.calls.length"), 0, "observing fast mode does not choose it");
      await evaluate("window.fixtureReportedFastMode = false");
      assert.equal(await wait(`!${fastMode}.checked && !${lightning}`), true);
      await evaluate(`${fastMode}.click()`);
      assert.equal(await wait(`fixtureSessionOptions.calls.length===1 && ${fastMode}.checked && !!${lightning}`), true);
      assert.equal(await evaluate("JSON.stringify(fixtureSessionOptions.calls[0])"), JSON.stringify({ subSessions: true, fastMode: true }));
      assert.equal(await evaluate(`${name}.firstChild === ${lightning} && ${name}.textContent`), "⚡️Fixture model");
      assert.equal(await evaluate("document.querySelector('.composer-selection').title.split('\\n').includes('Fast mode: On')"), true);
      assert.equal(await evaluate(`${lightning}.hasAttribute('data-standard')`), false, "a new choice clears the previous reported speed");

      await evaluate("window.fixtureReportedFastMode = false");
      assert.equal(await wait(`${lightning}?.hasAttribute('data-standard') && ${chip}.title.includes('The last turn ran at standard speed.')`), true);
      const dimmedOpacity = await evaluate(`Number(getComputedStyle(${lightning}).opacity)`);
      assert.ok(dimmedOpacity < 1, "the standard-speed fallback visibly dims the lightning");
      // Production CSS keeps the switch and lightning visible in both themes, at presentation and narrow sizes.
      for (const theme of ["dark", "light"]) for (const [width, height] of [[1600, 1000], [900, 650]]) {
        await command("Emulation.setDeviceMetricsOverride", { width, height, deviceScaleFactor: 1, mobile: false });
        await evaluate(`document.documentElement.dataset.theme = '${theme}'; document.documentElement.classList.toggle('bp6-dark', '${theme}' === 'dark');
          ${fastMode}.scrollIntoView({ block: 'nearest' })`);
        assert.equal(await wait(`(() => {
          const form = document.querySelector('.composer-selection-form'), toggle = ${fastMode}.closest('label').getBoundingClientRect();
          const bolt = ${lightning}.getBoundingClientRect(), label = ${name}.getBoundingClientRect();
          return form.scrollWidth <= form.clientWidth && toggle.left >= 0 && toggle.right <= innerWidth
            && toggle.top >= 0 && toggle.bottom <= innerHeight && bolt.width > 0 && bolt.right <= label.right + 1;
        })()`), true, `${theme} at ${width}×${height} has no horizontal overflow or clipped control`);
        assert.equal(await evaluate(`Number(getComputedStyle(${lightning}).opacity)`), dimmedOpacity);
      }
      await command("Emulation.setDeviceMetricsOverride", { width: 1600, height: 1000, deviceScaleFactor: 1, mobile: false });
      await evaluate("document.documentElement.dataset.theme = 'dark'; document.documentElement.classList.add('bp6-dark')");
      await evaluate(`${fastMode}.click()`);
      assert.equal(await wait(`fixtureSessionOptions.calls.length===2 && !${fastMode}.checked && !${lightning}`), true);
      assert.equal(await evaluate("JSON.stringify(fixtureSessionOptions.calls[1])"), JSON.stringify({ subSessions: true, fastMode: false }), "off is a choice too");

      // The options are the model's, kept by the host: a Send names none of them, nor any other choice.
      await evaluate(`${chip}.click()`);
      assert.equal(await wait("!document.querySelector('.composer-selection-popover')"), true);
      await write("with the options"); await enter();
      assert.equal(await wait(`fixture.sendCalls.length===1 && ${running}`), true);
      assert.equal(await evaluate("JSON.stringify(fixture.sendCalls[0].selection ?? null)"), "null");
      assert.equal(await evaluate("document.querySelectorAll('.bp6-toast').length"), 0);
      assert.deepEqual(exceptions, []);
      return;
    }

    if (permission) {
      // The session holds the effort "max", which its model does not list: nothing else of its selection can be
      // changed, but the mode is the session's and goes to the host by itself. The mode is chosen in the form of the
      // Agent chip, a select like its other rows; the chip shows it as a shield and names it in its tooltip.
      const select = "document.querySelector('#composer-permission-fixture-session')";
      const chip = "document.querySelector('.composer-selection')";
      const shows = (mode: string) => `${chip}?.title.split('\\n').includes(${JSON.stringify(`Permissions: ${mode}`)})`;
      const choose = async (mode: string) => {
        if (!await evaluate(`!!${select}`)) await evaluate(`${chip}.click()`);
        assert.equal(await wait(`!!${select}`), true, "the form of the Agent chip has the permission mode");
        const option = `[...${select}.options].find(option=>option.textContent.split(' · ')[0]===${JSON.stringify(mode)})`;
        assert.equal(await evaluate(`!!${option}`), true, `the list of modes offers ${mode}`);
        await evaluate(`(() => { const value = ${option}.value; Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype, 'value').set.call(${select}, value);
          ${select}.dispatchEvent(new Event('change', { bubbles: true })); })()`);
      };
      const calls = async () => JSON.parse(await evaluate("JSON.stringify(fixturePermission.calls)")) as string[];
      assert.equal(await wait(shows("Bypass permissions")), true, "the mode of the provider, which the session runs in");

      await choose("Ask first");
      assert.equal(await wait(`fixturePermission.calls.length===1 && ${shows("Ask first")}`), true);
      assert.deepEqual(await calls(), ["default"], "the host is asked for the mode alone");
      await choose("Accept edits");
      assert.equal(await wait(`fixturePermission.calls.length===2 && ${shows("Accept edits")}`), true);
      assert.deepEqual(await calls(), ["default", "acceptEdits"]);
      // Back to the mode of the provider, by its name.
      await choose("Bypass permissions");
      assert.equal(await wait(`fixturePermission.calls.length===3 && ${shows("Bypass permissions")}`), true);
      assert.deepEqual(await calls(), ["default", "acceptEdits", "provider"]);
      assert.equal(await evaluate("document.querySelectorAll('.bp6-toast').length"), 0);

      // A mode the host refuses is said, and the button shows the mode in force again.
      await evaluate("fixturePermission.status='busy'");
      await choose("Ask first");
      assert.equal(await wait(`fixturePermission.calls.length===4 && !!document.querySelector('.bp6-toast') && ${shows("Bypass permissions")}`), true);
      assert.equal(await evaluate("document.querySelector('.bp6-toast').textContent.includes('The permission mode was not changed (busy).')"), true);

      // Another choice of the selection is refused while the session holds an effort its model lists others than,
      // and that is said: nothing else shows it.
      if (!await evaluate("!!document.querySelector('.composer-selection-popover')")) await evaluate("document.querySelector('.composer-selection').click()");
      const agent = "document.querySelector('#composer-agent-fixture-session')";
      assert.equal(await wait(`!!${agent}`), true, "the agent prompts of the session configuration");
      await evaluate(`(() => { const select = ${agent}; Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype, 'value').set.call(select, 'plan');
        select.dispatchEvent(new Event('change', { bubbles: true })); })()`);
      assert.equal(await wait("[...document.querySelectorAll('.bp6-toast')].some(toast=>toast.textContent.includes('The choice was not changed: choose an available model and agent prompt first.'))"), true);
      assert.equal(await evaluate(`${agent}.value`), "default", "the prompt of the session is still the one shown");
      await evaluate("document.querySelector('.composer-selection').click()");
      assert.equal(await wait("!document.querySelector('.composer-selection-popover')"), true);
      // The rest of the selection is as it was: a Send names none of it.
      await write("after the modes"); await enter();
      assert.equal(await wait(`fixture.sendCalls.length===1 && ${running}`), true);
      assert.equal(await evaluate("JSON.stringify(fixture.sendCalls[0].selection ?? null)"), "null");
      assert.deepEqual(exceptions, []);
      return;
    }

    // Background work is visible even when no foreground turn is thinking.
    const backgroundStatus = "document.querySelector('.background-tasks-status')";
    assert.equal(await evaluate(`!!${backgroundStatus}`), false);
    await evaluate("fixture.background(['running'])");
    assert.equal(await wait(`${backgroundStatus}?.getAttribute('aria-label')==='1 background task running' && !!document.querySelector('.background-tasks-spinner')`), true);
    assert.equal(await evaluate("document.querySelector('.composer-status-line').dataset.busy"), "false");

    // An idle session takes the prompt at once; Stop and Send now (steer) then remain separate actions.
    // Enter still sends immediately when the split button's chosen default is Enqueue.
    await evaluate("document.querySelector('[aria-label=\"Send options\"]').click()");
    assert.equal(await wait("!![...document.querySelectorAll('[role=option]')].find(item=>item.textContent.includes('Enqueue until idle'))"), true);
    assert.equal(await evaluate("[...document.querySelectorAll('[role=option]')].find(item=>item.textContent.includes('Enqueue until idle')).textContent.includes('Enter')"), true);
    assert.equal(await evaluate("[...document.querySelectorAll('[role=option]')].find(item=>item.textContent.includes('Send now')).textContent.includes(" + JSON.stringify(mac ? "Cmd+Enter" : "Ctrl+Enter") + ")"), true);
    await evaluate("[...document.querySelectorAll('[role=option]')].find(item=>item.textContent.includes('Enqueue until idle')).querySelector('a').click()");
    assert.equal(await wait("!document.querySelector('[role=option]')"), true);
    await write("first"); await enter();
    assert.equal(await wait(`fixture.sendCalls.length===1 && ${running} && promptText()===''`), true);
    assert.equal(await evaluate("!!document.querySelector('.composer-queue-strip')"), false);
    await evaluate("document.querySelector('[aria-label=\"Send options\"]').click()");
    assert.equal(await wait("!![...document.querySelectorAll('[role=option]')].find(item=>item.textContent.includes('Send now'))"), true);
    await evaluate("[...document.querySelectorAll('[role=option]')].find(item=>item.textContent.includes('Send now')).querySelector('a').click()");
    assert.equal(await wait("!document.querySelector('[role=option]')"), true);

    // Recent results never inflate the running count, which remains separate from Thinking.
    await evaluate("fixture.background(['running','running','completed','failed','stopped'])");
    assert.equal(await wait(`${backgroundStatus}?.getAttribute('aria-label')==='2 background tasks running'`), true);
    assert.equal(await evaluate("document.querySelectorAll('.composer-status-line .activity-spinner').length"), 1);
    assert.equal(await evaluate("document.querySelectorAll('.composer-status-line .background-tasks-spinner').length"), 1);
    assert.equal(await evaluate("document.querySelector('.background-tasks-spinner').getAttribute('aria-hidden')"), "true");
    assert.equal(await evaluate("getComputedStyle(document.querySelector('.background-tasks-spinner')).animationName"), "background-tasks-spin");
    await command("Emulation.setEmulatedMedia", { features: [{ name: "prefers-reduced-motion", value: "reduce" }] });
    assert.equal(await evaluate("getComputedStyle(document.querySelector('.background-tasks-spinner')).animationName"), "none");
    assert.equal(await evaluate(`${backgroundStatus}.getAttribute('aria-label')`), "2 background tasks running");
    await command("Emulation.setEmulatedMedia", { features: [] });

    for (const width of [390, 1120]) {
      await command("Emulation.setDeviceMetricsOverride", { width, height: 800, deviceScaleFactor: 1, mobile: false });
      for (const theme of ["dark", "light"]) {
        await evaluate(`document.documentElement.dataset.theme='${theme}'`);
        const layout = await evaluate(`(()=>{const status=document.querySelector('.composer-status-line').getBoundingClientRect();
          const tasks=${backgroundStatus}.getBoundingClientRect();const thinking=document.querySelector('.composer-foreground-status').getBoundingClientRect();
          return {fits:tasks.width>0&&tasks.left>=status.left&&tasks.right<=status.right+1,
            separate:tasks.left>=thinking.right||tasks.top>=thinking.bottom,
            page:document.documentElement.scrollWidth,view:document.documentElement.clientWidth}})()`);
        assert.ok(layout.fits && layout.separate && layout.page <= layout.view + 1, `${width}px ${theme} background status fits: ${JSON.stringify(layout)}`);
      }
    }

    // Keyboard users can inspect the same list, including finished jobs and only two Stop controls.
    await evaluate(`${backgroundStatus}.focus()`); await enter();
    assert.equal(await wait("document.querySelectorAll('.background-tasks li').length===5"), true);
    assert.equal(await evaluate("document.querySelectorAll('.background-tasks [aria-label=\"Stop this background task\"]').length"), 2);
    await key("Escape", "Escape", 27);
    await evaluate("fixture.background(['completed','running','failed','stopped'])");
    assert.equal(await wait(`${backgroundStatus}?.getAttribute('aria-label')==='1 background task running'`), true);
    await evaluate("fixture.background(['completed','failed','stopped'])");
    assert.equal(await wait(`${backgroundStatus}?.getAttribute('aria-label')==='No background tasks running' && !document.querySelector('.background-tasks-spinner')`), true);
    assert.equal(await evaluate("document.querySelectorAll('.composer-status-line .activity-spinner').length"), 1, "Thinking continues independently of background completion");
    await evaluate("fixture.background([])");
    assert.equal(await wait(`!${backgroundStatus}`), true);

    // Enqueue while the session works: the prompt waits above the composer. Nothing is sent, refused or announced.
    await write("second"); await enter();
    assert.equal(await wait("fixture.rows().length===1 && promptText()===''"), true);
    await write("third"); await enter();
    assert.deepEqual(await rows(), ["Queue:waiting:1:second", "Queue:waiting:1:third"]);
    assert.deepEqual(await sends(), ["first"]);
    assert.equal(await evaluate("document.querySelectorAll('.bp6-toast').length"), 0, "a prompt is never answered with a toast");
    assert.deepEqual(JSON.parse(await evaluate("JSON.stringify(fixture.echoes())")), ["accepted:first"]);
    assert.deepEqual(JSON.parse(await evaluate("JSON.stringify([...document.querySelectorAll('.composer-queue-position')].map(mark=>mark.textContent))")), ["1", "2"]);
    assert.equal(await evaluate(`!!document.querySelector('.composer-toolbar [aria-label="Steer the running turn"]') && !!document.querySelector('.composer-toolbar [aria-label="Enqueue for the next turn"]')`), true);

    // Images explain a disabled Steer now even while text-only prompts can steer the same running turn.
    for (const text of ["look at this image", ""]) {
      await evaluate(`fixture.enqueueWithImage(${JSON.stringify(text)})`);
      const imageRow = row(text || "Prompt");
      const imageSteer = `${imageRow}.querySelector('[aria-label="Steer now"]')`;
      assert.equal(await wait(`!!${imageRow} && ${imageSteer}.disabled`), true);
      assert.equal(await evaluate(`${imageSteer}.title`), "Prompts with images wait for the next turn; steering accepts text only");
      assert.equal(await evaluate(`${imageRow}.querySelector('.composer-queue-images').textContent`), "1");
      await evaluate(`${imageSteer}.click()`);
      assert.deepEqual(await steers(), [], "a disabled action must not send text without its image");
      assert.deepEqual(await sends(), ["first"]);
      await evaluate(`${imageRow}.querySelector('.composer-queue-delete').click()`);
      assert.equal(await wait("fixture.rows().length===2"), true);
    }
    const textSteer = `${row("second")}.querySelector('[aria-label="Steer now"]')`;
    assert.equal(await evaluate(`${textSteer}.disabled`), false);
    assert.equal(await evaluate(`${textSteer}.title`), "Send now to the running turn as steering");

    // The repeat count, by its stepper and by a typed number.
    await evaluate(`${row("second")}.querySelector('[aria-label="Increase repeat count"]').click()`);
    assert.equal(await wait(`fixture.rows()[0]==='Queue:waiting:2:second'`), true);
    assert.equal(await evaluate(`${row("second")}.querySelector('[aria-label="Repeat count"]').value`), "×2");
    assert.match(await evaluate("document.querySelector('.composer-queue-summary').textContent"), /2 queued · 3 sends/);
    await evaluate(`${row("third")}.querySelector('[aria-label="Repeat count"]').focus()`);
    assert.equal(await wait("document.activeElement.value==='1' && document.activeElement.selectionEnd-document.activeElement.selectionStart===1"), true,
      "the field shows the bare number, selected for typing over");
    await command("Input.insertText", { text: "4" });
    await key("Enter", "Enter", 13);
    assert.equal(await wait(`fixture.rows()[1]==='Queue:waiting:4:third'`), true);
    await evaluate(`${row("third")}.querySelector('[aria-label="Decrease repeat count"]').click()`);
    await evaluate(`${row("third")}.querySelector('[aria-label="Decrease repeat count"]').click()`);
    await evaluate(`${row("third")}.querySelector('[aria-label="Decrease repeat count"]').click()`);
    assert.equal(await wait(`fixture.rows()[1]==='Queue:waiting:1:third'`), true);
    assert.equal(await evaluate(`${row("third")}.querySelector('[aria-label="Decrease repeat count"]').disabled`), true);

    // The rows stay one line each and inside the window, in both themes and in a narrow window.
    for (const width of [390, 1120]) {
      await command("Emulation.setDeviceMetricsOverride", { width, height: 800, deviceScaleFactor: 1, mobile: false });
      for (const theme of ["dark", "light"]) {
        await evaluate(`document.documentElement.dataset.theme='${theme}'`);
        const layout = JSON.parse(await evaluate(`JSON.stringify((()=>{const strip=document.querySelector('.composer-queue-strip').getBoundingClientRect();
          return {rows:[...document.querySelectorAll('.composer-queue-row')].map(row=>Math.round(row.getBoundingClientRect().height)),
            trash:[...document.querySelectorAll('.composer-queue-delete')].every(button=>{const box=button.getBoundingClientRect();return box.width>0&&box.right<=strip.right+1}),
            page:document.documentElement.scrollWidth,view:document.documentElement.clientWidth,right:Math.round(strip.right)}})())`));
        assert.ok(layout.rows.every((height: number) => height >= 28 && height <= 34), `${width}px ${theme} rows are one line: ${JSON.stringify(layout)}`);
        assert.ok(layout.trash && layout.page <= layout.view + 1 && layout.right <= layout.view, `${width}px ${theme} rows fit the window: ${JSON.stringify(layout)}`);
      }
    }

    // The queue leaves one prompt at a time, when the session is idle, each as a normal Send.
    await evaluate("fixture.finishRun()");
    assert.equal(await wait("fixture.sendCalls.length===2 && fixture.rows()[0]==='Queue:waiting:1:second'"), true);
    assert.deepEqual(await rows(), ["Queue:waiting:1:second", "Queue:waiting:1:third"], "a repeated prompt keeps its place for its next send");
    await evaluate("fixture.finishRun()");
    assert.equal(await wait("fixture.sendCalls.length===3 && fixture.rows().length===1"), true);
    await evaluate("fixture.finishRun()");
    assert.equal(await wait("fixture.sendCalls.length===4 && fixture.rows().length===0"), true);
    assert.deepEqual(await sends(), ["first", "second", "second", "third"]);
    assert.equal(await evaluate("fixture.sendCalls.every(call=>call.selection===null||call.selection.providerKey==='fixture-provider')"), true);
    assert.equal(await evaluate("!!document.querySelector('.composer-queue-strip')"), false);
    await evaluate("fixture.finishRun()");
    assert.equal(await wait(idle), true);

    // The host says the session is busy although it looked idle: the prompt joins the queue and leaves later.
    await evaluate("fixture.sendMode('busy')");
    await write("refused"); await enter();
    assert.equal(await wait("fixture.rows()[0]==='Queue:waiting:1:refused' && promptText()===''"), true);
    assert.equal(await evaluate("fixture.echoes().some(echo=>echo.startsWith('failed'))||document.querySelectorAll('.bp6-toast').length>0"), false);
    await evaluate("fixture.sendMode('accept')");
    assert.equal(await wait("fixture.rows().length===0 && fixture.sendCalls.at(-1).text==='refused' && fixture.echoes().includes('accepted:refused')"), true);
    await evaluate("fixture.finishRun()");
    assert.equal(await wait(idle), true);

    // A run the host starts from its own queue (the answer of a child session) drains for as long as it runs:
    // it is stopped like a run of a prompt sent here.
    const stop = "document.querySelector('.composer-toolbar [aria-label=\"Cancel observed run\"]')";
    await evaluate("fixture.startDrainedRun()");
    assert.equal(await wait(`${running} && !${stop}.disabled`), true);
    await evaluate(`${stop}.click()`);
    assert.equal(await wait("fixture.abortRunCalls.length===1"), true);
    assert.equal(await evaluate("fixture.abortRunCalls[0].expectedRunId===fixture.run() && fixture.abortRunCalls[0].expectedAttachmentGeneration==='12'"), true);
    await evaluate("fixture.finishRun()");
    assert.equal(await wait(idle), true);

    // Another refusal keeps the prompt in the composer and says why in the status line, without a toast.
    await evaluate("fixture.sendMode('refuse')");
    await write("not taken"); await enter();
    assert.equal(await wait("document.querySelector('.composer-status-line').textContent.includes('was not accepted')"), true);
    assert.equal(await evaluate("promptText()==='not taken' && fixture.rows().length===0 && document.querySelectorAll('.bp6-toast').length===0"), true);
    await evaluate("fixture.sendMode('accept')");
    await command("Input.insertText", { text: " yet" });
    assert.equal(await wait("!document.querySelector('.composer-status-line').textContent.includes('was not accepted')"), true, "editing clears the notice");

    // Explicit Enqueue on an idle session queues and drains as a normal Send, never steering.
    const before = (await sends()).length;
    await evaluate("document.querySelector('[aria-label=\"Send options\"]').click()");
    assert.equal(await wait("!![...document.querySelectorAll('[role=option]')].find(item=>item.textContent.includes('Enqueue until idle'))"), true);
    await evaluate("[...document.querySelectorAll('[role=option]')].find(item=>item.textContent.includes('Enqueue until idle')).querySelector('a').click()");
    assert.equal(await wait("!document.querySelector('[role=option]')"), true);
    await evaluate("document.querySelector('[aria-label=\"Enqueue\"]').click()");
    assert.equal(await wait(`fixture.sendCalls.length===${before + 1} && ${running} && promptText()===''`), true);
    assert.deepEqual([(await sends()).at(-1), await steers()], ["not taken yet", []]);
    await evaluate("document.querySelector('[aria-label=\"Send options\"]').click()");
    assert.equal(await wait("!![...document.querySelectorAll('[role=option]')].find(item=>item.textContent.includes('Send now (steer)'))"), true);
    await evaluate("[...document.querySelectorAll('[role=option]')].find(item=>item.textContent.includes('Send now (steer)')).querySelector('a').click()");
    assert.equal(await wait("!document.querySelector('[role=option]')"), true);

    // Enter during a running turn enqueues rather than steering, regardless of the selected Send now action.
    await write("shortcut queued"); await enter();
    assert.equal(await wait("fixture.rows()[0]==='Queue:waiting:1:shortcut queued' && promptText()===''"), true);
    assert.deepEqual(await steers(), []);
    assert.equal((await sends()).length, before + 1);
    await evaluate(`${row("shortcut queued")}.querySelector('.composer-queue-delete').click()`);
    assert.equal(await wait("fixture.rows().length===0"), true);

    // Cmd/Ctrl+Enter uses Send now (steer) while busy. The row stays until the agent takes the prompt up.
    assert.equal(await evaluate("document.querySelector('[aria-label=\"Send now (steer)\"]').title"), "Send now (steer)");
    await write("steer live"); await enter(true);
    assert.equal(await wait("fixture.steerCalls.length===1 && fixture.rows()[0]==='Steer:delivering:1:steer live' && promptText()===''"), true);
    assert.equal(await evaluate("fixture.steerCalls[0].expectedRunId===fixture.run() && fixture.steerCalls[0].expectedAttachmentGeneration==='12'"), true);
    assert.equal(await evaluate(`${row("steer live")}.querySelector('.composer-queue-status').textContent`), "Steer pending");
    assert.equal(await evaluate(`!${row("steer live")}.querySelector('[aria-label="Repeat count"]') && !${row("steer live")}.querySelector('[aria-label="Edit queued prompt"]')`), true);

    // Steering is listed first. The Steer button with an empty draft uses the first queued prompt.
    await write("queued"); await enter();
    assert.equal(await wait("fixture.rows().length===2"), true);
    assert.deepEqual(await rows(), ["Steer:delivering:1:steer live", "Queue:waiting:1:queued"]);
    await evaluate("document.querySelector('#session-prompt').focus()");
    await enter();
    assert.deepEqual(await rows(), ["Steer:delivering:1:steer live", "Queue:waiting:1:queued"], "empty Enter must not steer queued work");
    assert.deepEqual(await steers(), ["steer live"]);
    await steer();
    assert.equal(await wait("fixture.steerCalls.length===2 && fixture.rows().every(row=>row.startsWith('Steer:delivering'))"), true);
    assert.deepEqual(await steers(), ["steer live", "queued"]);

    // The Steer now button of a queued row does the same for that row.
    await write("later"); await enter();
    assert.equal(await wait(`!!${row("later")}`), true);
    await evaluate(`${row("later")}.querySelector('[aria-label="Steer now"]').click()`);
    assert.equal(await wait("fixture.steerCalls.length===3 && fixture.rows().length===3 && fixture.rows().every(row=>row.startsWith('Steer:delivering'))"), true);

    // Clicking Send now (steer) uses the same live action; an unsupported steer becomes queued work.
    await evaluate("fixture.steerMode('refuse')");
    await write("cannot steer");
    await evaluate("document.querySelector('[aria-label=\"Send now (steer)\"]').click()");
    assert.equal(await wait("fixture.steerCalls.length===4 && fixture.rows().at(-1)==='Queue:waiting:1:cannot steer'"), true);
    // Steering the host ran earlier, and keeps no receipt of, is not sent a second time as the next prompt.
    await evaluate("fixture.steerMode('expired')");
    await write("already delivered"); await steer();
    assert.equal(await wait("fixture.steerCalls.length===5 && fixture.rows().includes('Steer:failed:1:already delivered')"), true);
    assert.equal(await evaluate("fixture.rows().filter(row=>row.endsWith(':already delivered')).length"), 1);
    await evaluate(`${row("already delivered")}.querySelector('.composer-queue-delete').click()`);
    assert.equal(await wait("fixture.rows().length===4 && fixture.rows().at(-1)==='Queue:waiting:1:cannot steer'"), true);
    await evaluate("fixture.steerMode('accept')");

    // A sent steering prompt can be taken off the list; the others leave with their turn, and the queue goes on.
    await evaluate(`${row("steer live")}.querySelector('.composer-queue-delete').click()`);
    assert.equal(await wait("fixture.rows().length===3"), true);
    const sent = (await sends()).length;
    await evaluate("fixture.finishRun()");
    assert.equal(await wait(`fixture.sendCalls.length===${sent + 1} && fixture.rows().length===0`), true);
    assert.equal((await sends()).at(-1), "cannot steer");

    // Deleting a queued prompt, and clearing the queue.
    assert.equal(await wait(running), true);
    for (const text of ["one", "two", "three"]) { await write(text); await enter(); assert.equal(await wait(`!!${row(text)}`), true); }
    await evaluate(`${row("two")}.querySelector('.composer-queue-delete').click()`);
    assert.equal(await wait("fixture.rows().length===2"), true);
    assert.deepEqual(await rows(), ["Queue:waiting:1:one", "Queue:waiting:1:three"]);
    await evaluate("document.querySelector('.composer-queue-clear').click()");
    assert.equal(await wait("fixture.rows().length===0 && !document.querySelector('.composer-queue-strip')"), true);
    const total = (await sends()).length;
    await evaluate("fixture.finishRun()");
    assert.equal(await wait(idle), true);
    assert.equal((await sends()).length, total, "a cleared queue sends nothing");

    // Send now's modified shortcut runs normally when there is no active turn.
    await write("modified idle"); await enter(true);
    assert.equal(await wait(`fixture.sendCalls.length===${total + 1} && ${running} && promptText()===''`), true);
    assert.equal((await sends()).at(-1), "modified idle");
    await evaluate("fixture.finishRun()");
    assert.equal(await wait(idle), true);

    assert.equal(await evaluate("document.querySelectorAll('.bp6-toast').length"), 0);
    assert.deepEqual(exceptions, []);
  } finally {
    // Edge's launcher can exit while the browser it started goes on: the browser itself is asked to close.
    if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ id: 9999, method: "Browser.close" }));
    socket?.close(); browser?.kill(); await rm(root, { recursive: true, force: true, maxRetries: 8, retryDelay: 100 });
  }
});
