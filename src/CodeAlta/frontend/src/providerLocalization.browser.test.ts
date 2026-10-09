import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { existsSync, readFileSync } from "node:fs";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { build } from "esbuild";
import { locales, translate } from "./localization";
import { browserBaseArgs, browserExecutable } from "./browserTarget";

const edge = browserExecutable;
test("provider presentation preserves literal decisions and input owners across languages and scope ABA", { skip: !edge, timeout: 60_000 }, async t => {
  const root = await mkdtemp(join(tmpdir(), "codealta-provider-language-"));
  let browser: ReturnType<typeof spawn> | undefined; let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./providerLocalization.mount.tsx", import.meta.url))], outfile: join(root, "fixture.js"), bundle: true, platform: "browser", format: "iife" });
    await writeFile(join(root, "style.css"), readFileSync(fileURLToPath(new URL("./style.css", import.meta.url))));
    const page = join(root, "fixture.html");
    await writeFile(page, '<!doctype html><html><head><link rel="stylesheet" href="style.css"></head><body><div id="app"></div><script src="fixture.js"></script></body></html>');
    browser = spawn(edge!, [...browserBaseArgs, `--user-data-dir=${join(root, "profile")}`, "--remote-debugging-port=0", "about:blank"], { stdio: "ignore", windowsHide: true });
    let port = "";
    for (let attempt = 0; attempt < 100 && !port; attempt++) {
      try { port = (await readFile(join(root, "profile", "DevToolsActivePort"), "utf8")).split(/\r?\n/)[0]; }
      catch { await new Promise(resolve => setTimeout(resolve, 50)); }
    }
    assert.match(port, /^\d+$/);
    const pages: { type?: string; webSocketDebuggerUrl?: string }[] = await (await fetch(`http://127.0.0.1:${port}/json/list`, { signal: AbortSignal.timeout(10_000) })).json();
    const tab = pages.find(value => value.type === "page"); assert.ok(tab?.webSocketDebuggerUrl);
    socket = new WebSocket(tab.webSocketDebuggerUrl);
    await new Promise<void>((resolve, reject) => { socket!.addEventListener("open", () => resolve(), { once: true }); socket!.addEventListener("error", () => reject(Error("browser unavailable")), { once: true }); });
    let sequence = 0;
    const command = (method: string, params: object = {}) => new Promise<{ result?: { value?: unknown }; exceptionDetails?: unknown }>((resolve, reject) => {
      const id = ++sequence; const timer = setTimeout(() => reject(Error(`browser ${method} timed out`)), 12_000);
      const reply = (event: MessageEvent) => {
        const message = JSON.parse(String(event.data)); if (message.id !== id) return;
        socket!.removeEventListener("message", reply); clearTimeout(timer);
        if (message.error || message.result?.exceptionDetails) reject(Error(`browser ${method}: ${JSON.stringify(message)}`)); else resolve(message.result ?? {});
      };
      socket!.addEventListener("message", reply); socket!.send(JSON.stringify({ id, method, params }));
    });
    const evaluate = async (expression: string) => {
      try { return (await command("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true })).result?.value; }
      catch (cause) { throw new Error(`Provider modal expression=${expression}; failure=${cause instanceof Error ? cause.message : String(cause)}`, { cause }); }
    };
    const wait = (condition: string) => evaluate(`new Promise(resolve=>{const end=Date.now()+4000;const check=()=>{if(${condition})resolve(true);else if(Date.now()>end)resolve(false);else setTimeout(check,20)};check()})`);
    const paint = () => evaluate("new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(()=>resolve(true))))");
    const click = (text: string) => evaluate(`[...document.querySelectorAll('button')].find(b=>b.textContent===${JSON.stringify(text)}).click()`);
    const armed = () => wait("!!document.querySelector('[data-permission-decision=allow_once]') && !document.querySelector('[data-permission-decision=allow_once]').disabled");
    const focused = (selector: string) => evaluate(`document.activeElement===document.querySelector(${JSON.stringify(selector)})`);
    const key = async (key: string, code = key, keyCode = 0, text?: string) => { await command("Input.dispatchKeyEvent", { type: "keyDown", key, code, windowsVirtualKeyCode: keyCode, ...(text ? { text } : {}) }); await command("Input.dispatchKeyEvent", { type: "keyUp", key, code, windowsVirtualKeyCode: keyCode }); };
    const openInput = async () => {
      await evaluate("document.querySelector('[data-input-review]').focus();document.querySelector('[data-input-review]').click()");
      assert.equal(await wait("document.querySelector('.provider-input-dialog')?.open && !document.querySelector('.provider-input-dialog fieldset').disabled"), true);
      assert.equal(await evaluate("document.activeElement===document.querySelector('.provider-input-dialog header button')"), true, "Input initially focuses safe Close");
    };
    const languages = async () => {
      await paint();
      await evaluate(`void(window.kept={nodes:[...document.querySelectorAll('input,textarea')].map(node=>({node,value:node.value,disabled:node.disabled})),focus:document.activeElement,
        calls:JSON.stringify([providerFixture.permissions.length,providerFixture.decisions.length,providerFixture.inputs.length,providerFixture.answers.length,providerFixture.cancels.length]),
        original:providerFixture.input.readOriginal()?.request,decision:providerFixture.permission.readOriginal()?.origin,capability:providerFixture.capability.canMutate()})`);
      for (const locale of locales) {
        await evaluate(`providerFixture.language('${locale}')`); await paint();
        assert.equal(await evaluate(`document.querySelector('main > section:last-child h3').textContent===${JSON.stringify(translate(locale, "Nonsecret provider input"))}`), true);
        assert.equal(await evaluate(`(()=>{const panel=document.querySelector('main > section.command-permission-panel');return !panel||panel.getAttribute('aria-label')===${JSON.stringify(translate(locale, "Pending command permissions"))}&&(!panel.querySelector('h3')||panel.querySelector('h3').textContent===${JSON.stringify(translate(locale, "Allow this command?"))})})()`), true);
        assert.equal(await evaluate(`kept.calls===JSON.stringify([providerFixture.permissions.length,providerFixture.decisions.length,providerFixture.inputs.length,providerFixture.answers.length,providerFixture.cancels.length]) && kept.original===providerFixture.input.readOriginal()?.request && kept.decision===providerFixture.permission.readOriginal()?.origin && kept.capability===providerFixture.capability.canMutate() && kept.focus===document.activeElement && kept.nodes.every(v=>v.node.isConnected&&v.value===v.node.value&&v.disabled===v.node.disabled)`), true, `${locale}: no reads, grants, owner replacement, reset or focus loss`);
        assert.equal(await evaluate(`(()=>{const d=document.querySelector('.command-permission-panel');return !d?.querySelector('h3')||d.querySelector('[data-permission-command]').textContent===providerFixture.permissionPage.entries[0].command&&d.querySelector('[data-permission-directory]').textContent===providerFixture.permissionPage.entries[0].workingDirectory&&d.querySelector('[data-permission-reason]').textContent===providerFixture.permissionPage.entries[0].reason&&d.querySelector('[data-permission-decision=deny]').textContent.endsWith(${JSON.stringify(translate(locale, "Deny"))})})()`), true);
        assert.equal(await evaluate(`(()=>{const option=document.querySelector('[aria-pressed]');return !option||option.textContent==='Settings — Complete'})()`), true);
        assert.equal(await evaluate(`(()=>{const d=document.querySelector('.provider-input-dialog');return !d||d.querySelector('h2').textContent===${JSON.stringify(translate(locale, "Review provider input"))}&&[...d.querySelectorAll('[data-input-question]')].every((p,i)=>p.textContent===providerFixture.inputPage.entries[0].prompts[i].question)})()`), true);
      }
      await evaluate("providerFixture.language('en')"); await paint();
    };
    await command("Page.enable"); await command("Emulation.setFocusEmulationEnabled", { enabled: true });
    await command("Page.navigate", { url: pathToFileURL(page).href });
    assert.equal(await wait("!!window.providerFixture && providerFixture.permissions.length===1"), true);
    assert.equal(await evaluate("!document.querySelector('.command-permission-panel')"), true, "Nothing shows while the first read goes on");
    await languages();
    await click("Refresh input"); await languages();
    assert.equal(await evaluate("providerFixture.inputs.length===1 && providerFixture.permissions.length===1"), true);
    await evaluate("providerFixture.inputs[0].resolve(providerFixture.inputPage);providerFixture.permissions[0].resolve(providerFixture.permissionPage)");
    assert.equal(await wait("!!document.querySelector('[data-input-review]') && !!document.querySelector('.command-permission-panel h3')"), true, "Manually observed input requires deliberate Review, not an inline answer form");
    assert.equal(await evaluate("!document.querySelector('.provider-input-dialog') && providerFixture.answers.length===0 && providerFixture.cancels.length===0"), true);
    await openInput();
    assert.equal(await evaluate("providerFixture.inputs.length===1 && document.querySelector('[data-input-submit]').disabled"), true, "Opening never reads or implicitly answers missing text");
    await command("Input.dispatchKeyEvent", { type: "keyDown", key: "Enter", code: "Enter", windowsVirtualKeyCode: 13, text: "\r" });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: "Enter", code: "Enter", windowsVirtualKeyCode: 13 });
    assert.equal(await wait("!document.querySelector('.provider-input-dialog') && document.activeElement===document.querySelector('[data-input-review]')"), true);
    assert.equal(await evaluate("providerFixture.inputs.length===1 && providerFixture.answers.length===0 && providerFixture.cancels.length===0"), true);
    await openInput();
    for (const extra of ["isComposing:true", "repeat:true", "keyCode:229"]) {
      await evaluate(`document.querySelector('.provider-input-dialog header button').dispatchEvent(new KeyboardEvent('keydown',{key:'Escape',bubbles:true,cancelable:true,${extra}}))`);
      assert.equal(await evaluate("document.querySelector('.provider-input-dialog').open"), true);
    }
    await evaluate("{const e=new KeyboardEvent('keydown',{key:'Escape',bubbles:true,cancelable:true});e.preventDefault();document.querySelector('.provider-input-dialog header button').dispatchEvent(e)}");
    assert.equal(await evaluate("document.querySelector('.provider-input-dialog').open"), true);
    await evaluate("document.querySelector('[aria-pressed]').click()");
    assert.equal(await evaluate("document.querySelector('[data-input-submit]').disabled"), true, "Missing freeform is not deliberate empty");
    await click("Deliberately answer with empty text");
    assert.equal(await evaluate("!document.querySelector('[data-input-submit]').disabled && document.querySelector('textarea').value===''"), true);
    await evaluate(`document.querySelector('[aria-pressed]').click();const field=document.querySelector('textarea');field.focus();Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype,'value').set.call(field,'  Settings 日本語  ');field.dispatchEvent(new Event('input',{bubbles:true}))`);
    await languages();
    assert.equal(await evaluate("document.querySelector('textarea').value"), "  Settings 日本語  ");
    await command("Input.dispatchKeyEvent", { type: "keyDown", key: "Escape", code: "Escape", windowsVirtualKeyCode: 27 });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: "Escape", code: "Escape", windowsVirtualKeyCode: 27 });
    assert.equal(await wait("!document.querySelector('.provider-input-dialog')"), true);
    await openInput();
    assert.equal(await evaluate("document.querySelector('textarea').value==='  Settings 日本語  ' && document.querySelector('[aria-pressed]').getAttribute('aria-pressed')==='true' && providerFixture.inputs.length===1 && providerFixture.answers.length===0 && providerFixture.cancels.length===0"), true, "Exact current local draft survives dismissal");
    await evaluate("document.querySelector('textarea').focus()");
    await evaluate("document.querySelector('textarea').dispatchEvent(new KeyboardEvent('keydown',{key:'Enter',ctrlKey:true,isComposing:true,bubbles:true,cancelable:true}))");
    assert.equal(await evaluate("providerFixture.answers.length"), 0);
    await command("Input.dispatchKeyEvent", { type: "keyDown", key: "Tab", code: "Tab", windowsVirtualKeyCode: 9 });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: "Tab", code: "Tab", windowsVirtualKeyCode: 9 });
    assert.equal(await evaluate("document.activeElement.textContent"), "Deliberately answer with empty text");
    for (const locale of ["de", "ja"] as const) for (const theme of ["light", "dark"]) {
      await command("Emulation.setDeviceMetricsOverride", { width: 390, height: 500, deviceScaleFactor: 1, mobile: false });
      await evaluate(`providerFixture.language('${locale}');document.documentElement.dataset.theme='${theme}'`); await paint();
      assert.equal(await evaluate("document.documentElement.scrollWidth<=innerWidth+2"), true, `${locale}/${theme}: provider controls fit`);
      assert.equal(await evaluate("(()=>{const d=document.querySelector('.provider-input-dialog'),r=d.getBoundingClientRect();return r.left>=0&&r.right<=innerWidth+1&&r.top>=0&&r.bottom<=innerHeight+1&&d.scrollWidth<=d.clientWidth+1})()"), true, `${locale}/${theme}: input modal fits`);
      await evaluate("document.querySelector('[data-input-cancel]').scrollIntoView({block:'center'})");
      assert.equal(await evaluate("document.querySelector('[data-input-cancel]').getBoundingClientRect().bottom<=innerHeight"), true);
    }
    await evaluate("providerFixture.language('en')"); await paint();
    await click("Submit literal answers");
    await evaluate("document.querySelector('.provider-input-dialog header button').click()");
    assert.equal(await evaluate("document.querySelectorAll('.command-permission-panel [data-permission-decision]').length"), 2, "The request is answered on its card, not in a dialog");
    assert.equal(await armed(), true, "Its choices arm a moment after it appears");
    assert.equal(await evaluate("providerFixture.permissions.length===1 && providerFixture.decisions.length===0"), true, "Showing it performs no read or decision");
    await evaluate("document.querySelector('[data-permission-decision=allow_once]').focus()");
    assert.equal(await evaluate("getComputedStyle(document.querySelector('[data-permission-decision=allow_once]')).color==='rgb(255, 255, 255)'"), true, "The focused choice stands out");
    await key("ArrowDown", "ArrowDown", 40); assert.equal(await focused("[data-permission-decision=deny]"), true, "Down moves to the next choice");
    await key("ArrowRight", "ArrowRight", 39); assert.equal(await focused("[data-permission-instead]"), true, "Right too; the text to deny with is the last choice");
    await key("ArrowLeft", "ArrowLeft", 37); assert.equal(await focused("[data-permission-instead]"), true, "Left and Right move the caret in the text");
    await key("ArrowDown", "ArrowDown", 40); assert.equal(await focused("[data-permission-decision=allow_once]"), true, "The list wraps");
    await key("ArrowUp", "ArrowUp", 38); await key("ArrowUp", "ArrowUp", 38); assert.equal(await focused("[data-permission-decision=deny]"), true, "Up moves back");
    await evaluate("document.querySelector('[data-permission-decision=deny]').dispatchEvent(new KeyboardEvent('keydown',{key:'1',bubbles:true,cancelable:true,repeat:true}))");
    assert.equal(await evaluate("providerFixture.decisions.length"), 0, "A held key answers nothing");
    await evaluate("document.querySelector('[data-permission-instead]').focus()"); await command("Input.insertText", { text: "not that" });
    await key("Escape", "Escape", 27);
    assert.equal(await evaluate("document.querySelector('[data-permission-instead]').value===''&&providerFixture.decisions.length===0"), true, "Escape in the text first takes the text back");
    await evaluate("document.querySelector('[data-permission-decision=allow_once]').dispatchEvent(new KeyboardEvent('keydown',{key:'Escape',bubbles:true,cancelable:true,isComposing:true}))");
    assert.equal(await evaluate("providerFixture.decisions.length"), 0, "Escape that ends a composition answers nothing");
    for (const theme of ["light", "dark"]) {
      await command("Emulation.setDeviceMetricsOverride", { width: 390, height: 600, deviceScaleFactor: 1, mobile: false });
      await evaluate(`document.documentElement.dataset.theme='${theme}'`); await paint();
      assert.equal(await evaluate("(()=>{const d=document.querySelector('.command-permission-panel'),r=d.getBoundingClientRect();return r.left>=0&&r.right<=innerWidth+1&&d.scrollWidth<=d.clientWidth+1})()"), true, `${theme}: the card fits a narrow window`);
    }
    await evaluate("document.querySelector('[data-permission-decision=allow_once]').focus()");
    await languages();
    await key("Enter", "Enter", 13, "\r"); await languages();
    assert.deepEqual(await evaluate("providerFixture.answers[0].request.answers"), [{ promptId: "choice", value: "Settings" }, { promptId: "free", value: "  Settings 日本語  " }]);
    assert.equal(await evaluate("providerFixture.decisions[0].request.decision"), "allow_once");
    await evaluate("providerFixture.answers[0].resolve({status:'rejected',hostEpoch:providerFixture.epoch,handle:providerFixture.handle});providerFixture.decisions[0].resolve({status:'rejected',hostEpoch:providerFixture.epoch,handle:providerFixture.handle})");
    assert.equal(await wait("providerFixture.input.readOriginal()?.kind==='terminal' && providerFixture.permission.readOriginal()?.state==='rejected'"), true);
    await languages();
    await click("Observe original locally (no RPC)"); await click("Acknowledge observed terminal original");
    assert.equal(await wait("providerFixture.permissions.length===2"), true, "The panel acknowledges the answer to a decision and reads again");
    await click("Refresh input");
    await evaluate("providerFixture.inputs[1].resolve(providerFixture.inputPage);providerFixture.permissions[1].resolve(providerFixture.permissionPage)");
    assert.equal(await wait("!!document.querySelector('[data-input-review]')"), true); assert.equal(await armed(), true);
    await openInput();
    await click("Cancel this attempt only"); await evaluate("document.querySelector('.provider-input-dialog header button').click();document.querySelector('[data-permission-decision=deny]').click()"); await languages();
    await evaluate("providerFixture.select('other')"); await paint(); await evaluate("providerFixture.select('Settings')"); await paint();
    await languages();
    await evaluate("providerFixture.cancels[0].reject(Error('literal uncertain input'));providerFixture.decisions[1].reject(Error('literal uncertain command'))");
    assert.equal(await wait("providerFixture.input.readOriginal()?.kind==='uncertain' && providerFixture.permission.readOriginal()?.state==='error'"), true);
    await languages();
    assert.equal(await evaluate("providerFixture.input.acknowledge()"), false);
    assert.equal(await evaluate("providerFixture.decisions.length===2 && providerFixture.answers.length===1 && providerFixture.cancels.length===1"), true);
    // Fresh owners: same-handle replacement and native modal ABA never authorize old controls.
    await command("Page.navigate", { url: pathToFileURL(page).href }); assert.equal(await wait("!!window.providerFixture && providerFixture.permissions.length===1"), true);
    await evaluate("providerFixture.permissions[0].resolve(providerFixture.permissionPage)");
    assert.equal(await armed(), true);
    await evaluate("window.oldDecision=document.querySelector('[data-permission-decision=allow_once]');providerFixture.select('other')"); await paint();
    await evaluate("providerFixture.select('Settings')");
    assert.equal(await wait("providerFixture.permissions.length===3"), true, "Each selection reads its own requests");
    await evaluate("providerFixture.permissions[2].resolve(structuredClone(providerFixture.permissionPage))");
    assert.equal(await wait("!!document.querySelector('[data-permission-decision=allow_once]')"), true);
    await evaluate("oldDecision.click()"); assert.equal(await evaluate("providerFixture.decisions.length"), 0, "A session selected again never reactivates old controls");
    assert.equal(await evaluate("document.querySelector('[data-permission-decision=allow_once]').disabled"), true, "A card shown again arms again");
    assert.equal(await armed(), true);
    await evaluate("document.querySelector('[data-permission-decision=allow_once]').focus()"); await key("Escape", "Escape", 27);
    assert.deepEqual(await evaluate("providerFixture.decisions[0].request"), { expectedHostEpoch: "11111111-1111-4111-8111-111111111111", handle: await evaluate("providerFixture.handle"), decision: "deny" }, "Escape denies");
    await evaluate("providerFixture.decisions[0].reject(Error('transport failure'))");
    assert.equal(await wait("providerFixture.permission.readOriginal()?.state==='error'"), true);
    assert.equal(await wait("!!document.querySelector('.command-permission-panel [role=alert]')"), true, "An uncertain answer stays shown");
    await evaluate("new Promise(resolve=>setTimeout(resolve,2000))");
    assert.equal(await evaluate("providerFixture.permissions.length===3 && providerFixture.decisions.length===1"), true, "Dismissal and error never unlock uncertainty: nothing is read or decided again");
    // Fresh disposable owners: late old-selection reads must still revoke, not regain authority through ABA/locale.
    await command("Page.navigate", { url: pathToFileURL(page).href }); assert.equal(await wait("!!window.providerFixture && providerFixture.permissions.length===1"), true);
    await click("Refresh input");
    await evaluate("providerFixture.select('other')"); await paint(); await evaluate("providerFixture.select('Settings')"); await paint();
    await evaluate("providerFixture.inputs[0].resolve({...providerFixture.inputPage,status:'stale_epoch'});providerFixture.permissions[0].resolve({...providerFixture.permissionPage,status:'stale_epoch'})");
    assert.equal(await wait("!providerFixture.capability.canMutate()"), true); await languages();
    assert.equal(await evaluate("providerFixture.inputs.length===1 && providerFixture.permissions.length===3 && providerFixture.decisions.length===0 && providerFixture.answers.length===0"), true);
    // Nothing shows while nothing waits. A running session is read again until a request shows, an idle one is not,
    // and the end of a run reads what is left of the requests shown.
    await command("Page.navigate", { url: pathToFileURL(page).href }); assert.equal(await wait("!!window.providerFixture && providerFixture.permissions.length===1"), true);
    await evaluate("providerFixture.permissions[0].resolve({...providerFixture.permissionPage,entries:[]})");
    assert.equal(await wait("providerFixture.permissions.length===2"), true, "A running session is read again");
    assert.equal(await evaluate("!document.querySelector('.command-permission-panel')"), true, "Nothing shows while nothing waits");
    await evaluate("providerFixture.run(false);providerFixture.permissions[1].resolve({...providerFixture.permissionPage,entries:[]})");
    await evaluate("new Promise(resolve=>setTimeout(resolve,2000))");
    assert.equal(await evaluate("providerFixture.permissions.length"), 2, "An idle session is not read");
    await evaluate("providerFixture.run(true)");
    assert.equal(await wait("providerFixture.permissions.length===3"), true);
    await evaluate("document.activeElement?.blur();providerFixture.focus(true);providerFixture.permissions[2].resolve(providerFixture.permissionPage)");
    assert.equal(await wait("document.querySelector('.command-permission-panel h3')?.textContent==='Allow this command?' && !document.querySelector('[data-permission-decision=allow_once]').disabled"), true, "A waiting request shows");
    assert.equal(await wait("document.activeElement===document.querySelector('[data-permission-decision=allow_once]')"), true, "With no draft, its first choice takes the focus: the keyboard alone answers");
    await evaluate("new Promise(resolve=>setTimeout(resolve,2000))");
    assert.equal(await evaluate("providerFixture.permissions.length"), 3, "A request that is shown is not read again while the run goes on");
    await evaluate("providerFixture.run(false)");
    assert.equal(await wait("providerFixture.permissions.length===4"), true, "The end of the run reads what is left");
    assert.equal(await evaluate("document.querySelector('[data-permission-decision=allow_once]').disabled"), true, "A request being read again cannot be answered");
    await evaluate("providerFixture.permissions[3].resolve({...providerFixture.permissionPage,entries:[]})");
    assert.equal(await wait("!document.querySelector('.command-permission-panel')"), true, "A request that no longer waits disappears");
    // Denying with what to do instead: the text follows a denial the agent took, and a draft keeps its focus.
    await command("Page.navigate", { url: pathToFileURL(page).href }); assert.equal(await wait("!!window.providerFixture && providerFixture.permissions.length===1"), true);
    await evaluate("providerFixture.focus(true);document.querySelector('[data-input-refresh], button')?.focus()");
    await evaluate("providerFixture.permissions[0].resolve(providerFixture.permissionPage)");
    assert.equal(await armed(), true); await paint();
    assert.equal(await evaluate("document.querySelector('.command-permission-panel').contains(document.activeElement)"), false, "A request never takes the focus from elsewhere in the window");
    await evaluate("document.querySelector('[data-permission-instead]').focus()");
    await command("Input.insertText", { text: "Use a dry run" });
    await key("Enter", "Enter", 13, "\r");
    assert.equal(await wait("providerFixture.decisions.length===1"), true);
    assert.equal(await evaluate("providerFixture.decisions[0].request.decision"), "deny");
    assert.equal(await evaluate("providerFixture.instructions.length"), 0, "Nothing is told before the denial is taken");
    await evaluate("providerFixture.decisions[0].resolve({status:'resolved',hostEpoch:providerFixture.epoch,handle:providerFixture.handle})");
    assert.equal(await wait("providerFixture.instructions.length===1"), true);
    assert.deepEqual(await evaluate("providerFixture.instructions[0]"), ["Steer", "Use a dry run"]);
    for (const mode of ["replacement", "native-aba", "selection", "capability", "empty", "late", "uncertain"]) {
      t.diagnostic(`Provider input scenario: ${mode}`);
      await command("Page.navigate", { url: pathToFileURL(page).href });
      assert.equal(await wait("!!window.providerFixture && providerFixture.permissions.length===1"), true);
      await click("Refresh input");
      await evaluate("providerFixture.inputs[0].resolve(providerFixture.inputPage)");
      assert.equal(await wait("!!document.querySelector('[data-input-review]')"), true);
      await openInput();
      await evaluate("document.querySelector('[aria-pressed]').click()"); await click("Deliberately answer with empty text");
      await evaluate("void(window.oldInputSubmit=document.querySelector('[data-input-submit]'));void(window.oldInputCancel=document.querySelector('[data-input-cancel]'))");
      if (mode === "replacement") {
        await evaluate("document.querySelector('[data-input-refresh]').click();oldInputSubmit.click();oldInputCancel.click()");
        assert.equal(await evaluate("providerFixture.answers.length===0 && providerFixture.cancels.length===0 && document.querySelector('.provider-input-dialog fieldset').disabled"), true, "Refresh start fences original controls before its reply");
        await evaluate("providerFixture.inputs[1].resolve(structuredClone(providerFixture.inputPage))"); await paint();
        await evaluate("oldInputSubmit.click();oldInputCancel.click();document.querySelector('.provider-input-dialog header button').click()");
        assert.equal(await evaluate("!!document.querySelector('[data-input-draft-lost]')"), true);
        await openInput();
        assert.equal(await evaluate("document.querySelector('[data-input-submit]').disabled && document.querySelector('[aria-pressed]').getAttribute('aria-pressed')==='false'"), true, "Same-handle page replacement never rebinds edits");
      } else if (mode === "native-aba") {
        await evaluate("const d=document.querySelector('.provider-input-dialog');d.close();d.showModal();oldInputSubmit.click();oldInputCancel.click()");
        await paint();
        if (await evaluate("!!document.querySelector('.provider-input-dialog')")) await evaluate("document.querySelector('.provider-input-dialog header button').click()");
        await openInput();
        assert.equal(await evaluate("document.querySelector('[data-input-submit]').disabled"), true, "Native ABA discards local answers");
      } else if (mode === "selection") {
        await evaluate("providerFixture.select('other')"); await paint(); await evaluate("providerFixture.select('Settings')"); await paint();
        await evaluate("oldInputSubmit.click();oldInputCancel.click()");
        assert.equal(await evaluate("!document.querySelector('[data-input-review]') && !document.querySelector('textarea')"), true);
      } else if (mode === "capability") {
        await evaluate("providerFixture.capability.observe({status:'stale_epoch',epoch:'22222222-2222-4222-8222-222222222222'});oldInputSubmit.click();oldInputCancel.click()");
        await languages();
      } else {
        await evaluate("oldInputSubmit.click();oldInputCancel.click()");
        assert.equal(await wait("providerFixture.answers.length===1"), true);
        assert.deepEqual(await evaluate("providerFixture.answers[0].request"), { expectedHostEpoch: "11111111-1111-4111-8111-111111111111", handle: await evaluate("providerFixture.handle"), answers: [{ promptId: "choice", value: "Settings" }, { promptId: "free", value: "" }] });
        await evaluate("document.querySelector('.provider-input-dialog header button').click()");
        assert.equal(await evaluate("providerFixture.input.acknowledge()"), false);
        if (mode === "late") { await evaluate("providerFixture.select('other')"); await paint(); }
        await evaluate(mode === "uncertain" ? "providerFixture.answers[0].reject(Error('literal transport failure'))" : "providerFixture.answers[0].resolve({status:'resolved',hostEpoch:providerFixture.epoch,handle:providerFixture.handle})");
        assert.equal(await wait(`providerFixture.input.readOriginal()?.kind==='${mode === "uncertain" ? "uncertain" : "terminal"}'`), true);
        assert.equal(await evaluate("providerFixture.input.acknowledge()"), false, "Displaying the result never observes it");
        await click("Observe original locally (no RPC)");
        assert.equal(await evaluate("providerFixture.input.acknowledge()"), mode !== "uncertain");
        if (mode === "uncertain") {
          await click("Refresh input"); await evaluate("providerFixture.inputs[1].resolve(providerFixture.inputPage)"); await paint();
          assert.equal(await evaluate("document.querySelector('[data-input-review]').disabled && providerFixture.input.blocked()"), true);
        }
      }
      assert.equal(await evaluate(`providerFixture.answers.length===${["empty", "late", "uncertain"].includes(mode) ? 1 : 0} && providerFixture.cancels.length===0`), true, mode);
    }
  } finally {
    // Edge's launcher can exit while the browser it started goes on: the browser itself is asked to close.
    if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ id: 9999, method: "Browser.close" }));
    socket?.close(); browser?.kill(); await rm(root, { recursive: true, force: true, maxRetries: 8, retryDelay: 100 });
  }
});
