import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { existsSync } from "node:fs";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { build } from "esbuild";
import { translate } from "./localization";
import { browserBaseArgs, browserExecutable } from "./browserTarget";

const edge = browserExecutable;
const fixture = "window.settingsFilesFixture";
const calls = (method: string) => `${fixture}.state.calls.filter(call => call.method === ${JSON.stringify(method)}).map(call => call.request)`;
// The rows of the files of the page, above its list.
const rows = "[...document.querySelectorAll('.settings-file-locations > .settings-file-location')]";
const button = (scope: string, title: string) => `${scope}.querySelector('button[title=${JSON.stringify(title)}]')`;

test("a settings page says where its files are, and opens, copies and shows them", { skip: !edge, timeout: 120_000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), "codealta-settings-files-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./settingsFiles.mount.tsx", import.meta.url))], outfile: join(root, "fixture.js"), bundle: true, platform: "browser", format: "iife",
      define: { "import.meta.env.VITE_DEMO_MODE": '"false"' }, plugins: [{ name: "isolated-settings-files", setup(bundle) {
        bundle.onResolve({ filter: /^#neoastra$/ }, () => ({ path: fileURLToPath(new URL("./settingsFiles.neoastra.mount.ts", import.meta.url)) }));
      } }] });
    await writeFile(join(root, "style.css"), await readFile(new URL("../node_modules/@blueprintjs/core/lib/css/blueprint.css", import.meta.url), "utf8")
      + await readFile(new URL("./style.css", import.meta.url), "utf8"));
    const page = join(root, "fixture.html");
    await writeFile(page, '<!doctype html><html><head><meta charset="utf-8"><link rel="stylesheet" href="style.css"></head><body><div id="root"></div><script src="fixture.js"></script></body></html>');
    browser = spawn(edge!, [...browserBaseArgs, `--user-data-dir=${join(root, "profile")}`, "--remote-debugging-port=0", "about:blank"], { stdio: "ignore", windowsHide: true });
    let port = "";
    for (let attempt = 0; attempt < 100 && !port; attempt++) {
      try { port = (await readFile(join(root, "profile", "DevToolsActivePort"), "utf8")).split(/\r?\n/)[0]; }
      catch { await new Promise(resolve => setTimeout(resolve, 50)); }
    }
    assert.match(port, /^\d+$/);
    const pages: { type?: string; webSocketDebuggerUrl?: string }[] = await (await fetch(`http://127.0.0.1:${port}/json/list`, { signal: AbortSignal.timeout(10_000) })).json();
    socket = new WebSocket(pages.find(value => value.type === "page")!.webSocketDebuggerUrl!);
    await new Promise<void>((resolve, reject) => { socket!.addEventListener("open", () => resolve(), { once: true }); socket!.addEventListener("error", () => reject(Error("browser unavailable")), { once: true }); });
    let sequence = 0;
    const command = (method: string, params: object = {}) => new Promise<{ result?: { value?: unknown }; exceptionDetails?: unknown }>((resolve, reject) => {
      const id = ++sequence, timer = setTimeout(() => reject(Error(`${method} timed out`)), 12_000);
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
    // An action, then the answers of the host and what React shows of them.
    const act = (script: string) => evaluate(`(async () => { ${script}; for (let turn = 0; turn < 4; turn++) await new Promise(resolve => requestAnimationFrame(() => setTimeout(resolve, 0))); return true; })()`);
    const until = async (expression: string, label = expression) => assert.equal(await evaluate(`new Promise(resolve => { const end = Date.now() + 7000; (function check() {
      if (${expression}) resolve(true); else if (Date.now() > end) resolve(false); else setTimeout(check, 20); })(); })`), true, label);
    await command("Page.enable");
    await command("Emulation.setDeviceMetricsOverride", { width: 1280, height: 900, deviceScaleFactor: 1, mobile: false });
    await command("Page.navigate", { url: pathToFileURL(page).href });
    await until(`${rows}.length === 2`, "the folders of the plugins are listed above the list");

    // Each row has its scope, its whole path, and its buttons; a folder CodeAlta creates is opened before it exists.
    const home = "C:\\Users\\someone-with-a-long-name\\.alta\\plugins", work = "D:\\work\\a-project-with-a-long-name\\.alta\\plugins";
    assert.deepEqual(await evaluate(`${rows}.map(row => [row.querySelector('.settings-file-label').textContent, row.querySelector('code').textContent, row.querySelector('code').title,
      [...row.querySelectorAll('button')].map(value => value.title)])`), [
      ["Global", home, home, ["Edit in the code editor", "Copy path", "Reveal in File Explorer"]],
      ["Project", work, work, ["Edit in the code editor", "Copy path"]]]);

    // In a narrow window a path is cut at its start: its end, which says what it is, stays in view.
    await act(`${fixture}.setWidth(430)`);
    assert.deepEqual(await evaluate(`${rows}.map(row => { const code = row.querySelector('code').getBoundingClientRect(), text = row.querySelector('bdi').getBoundingClientRect(), frame = row.getBoundingClientRect();
      return [text.left < code.left - 1, Math.abs(text.right - code.right) <= 1, code.height < 24, [...row.querySelectorAll('button')].every(value => value.getBoundingClientRect().right <= frame.right + 1)]; })`),
      [[true, true, true, true], [true, true, true, true]], "start cut, end shown, one line, buttons in the row");
    // Nothing of the page is wider than the page: what would make it scroll sideways is named.
    assert.deepEqual(await evaluate(`(() => { const page = document.querySelector('main'), edge = page.getBoundingClientRect().right - parseFloat(getComputedStyle(page).paddingRight);
      return [...page.querySelectorAll('*')].filter(value => value.getBoundingClientRect().right > edge + 1 && value.getBoundingClientRect().width > 0)
        .map(value => value.tagName.toLowerCase() + '.' + value.className).slice(0, 6); })()`), [], "the page does not scroll sideways");
    await act(`${fixture}.setWidth(900)`);

    // Open: the page names the folder by kind and scope, never by its path, and leaves Settings once it is shown.
    await act(`${button(`${rows}[0]`, "Edit in the code editor")}.click()`);
    assert.deepEqual(await evaluate(calls("files.open")), [{ expectedEpoch: "epoch", projectId: "p", kind: "plugins", scope: "Global", id: null, part: null }]);
    assert.equal(await evaluate(`${fixture}.state.opened`), 1);
    // A folder that cannot be shown says so, and the page stays.
    await act(`${fixture}.state.openStatus = "failed"; ${button(`${rows}[1]`, "Edit in the code editor")}.click()`);
    assert.equal(await evaluate(`${fixture}.state.opened`), 1);
    assert.equal(await evaluate("document.querySelector('[role=alert].bp6-callout')?.textContent"), translate("en", "The file could not be opened."));
    await act(`${fixture}.state.openStatus = "ok"`);

    // Copy and show.
    await act(`${button(`${rows}[1]`, "Copy path")}.click()`);
    assert.deepEqual(await evaluate(`${fixture}.state.copied`), [work]);
    await act(`${button(`${rows}[0]`, "Reveal in File Explorer")}.click()`);
    assert.deepEqual(await evaluate(calls("files.reveal")), [{ expectedEpoch: "epoch", projectId: "p", kind: "plugins", scope: "Global", id: null, part: null }]);

    // The row of a source plugin has the path of its folder, shown in the file manager by the id of that folder.
    const card = (name: string) => `[...document.querySelectorAll('.settings-editor-rows .bp6-card')].find(value => value.querySelector('strong').textContent === ${JSON.stringify(name)})`;
    assert.equal(await evaluate(`${card("commits")}.querySelector('.settings-file-location code').title`), `${home}\\commits`);
    assert.equal(await evaluate(`${card("MCP")}.querySelector('.settings-file-location')`), null, "A plugin that ships with CodeAlta has no folder.");
    await act(`${button(card("commits"), "Reveal in File Explorer")}.click()`);
    assert.deepEqual(await evaluate(calls("reveal")), [{ expectedEpoch: "epoch", projectId: "plugin:global:commits", path: "" }]);

    // A configuration file that does not parse is named in red above the list, and is opened from there.
    const problem = "document.querySelector('.plugin-problems .plugin-failure')";
    assert.equal(await evaluate(`[...${problem}.childNodes].filter(node => node.nodeType === Node.TEXT_NODE).map(node => node.textContent).join("")`),
      translate("en", "The configuration file {path} could not be read.", { path: "D:\\work\\a-project-with-a-long-name\\.alta\\config.toml" }) + " (2,1): expected ]");
    assert.equal(await evaluate(`getComputedStyle(${problem}).color !== getComputedStyle(${card("commits")}.querySelector('strong')).color`), true, "what could not be read is not in the color of the text");
    await act(`${button(problem, "Edit in the code editor")}.click()`);
    assert.deepEqual(await evaluate(`${calls("files.open")}.at(-1)`), { expectedEpoch: "epoch", projectId: "p", kind: "config", scope: "Project", id: null, part: null });
    assert.equal(await evaluate(`${fixture}.state.opened`), 2);

    // Remove: the red button of a source plugin asks first, with the name of what goes; a built-in plugin has none.
    const trash = (name: string) => `${card(name)}.querySelector('button.bp6-intent-danger')`;
    const confirm = "document.querySelector('.provider-settings-confirm')";
    assert.equal(await evaluate(`${trash("MCP")}`), null);
    assert.equal(await evaluate(`${trash("commits")}.getAttribute('aria-label')`), "Remove commits");
    assert.equal(await evaluate(`getComputedStyle(${trash("commits")}).color !== getComputedStyle(${button(card("commits"), "Edit in the code editor")}).color`), true, "the button is red");
    await act(`${trash("commits")}.click()`);
    await until(`${confirm} !== null`, "the confirmation is shown");
    assert.equal(await evaluate(`${confirm}.querySelector('p').textContent`), translate("en", "Remove {name}?", { name: "commits" }));
    assert.deepEqual(await evaluate(calls("delete")), [], "Nothing is removed before it is confirmed.");
    // The folder could not be moved: the page says so, and the plugin stays listed.
    await act(`${fixture}.state.deleteStatus = "trash_failed"; ${confirm}.querySelector('button').click()`);
    assert.deepEqual(await evaluate(calls("delete")), [{ expectedEpoch: "epoch", projectId: "p", scope: "Global", id: "commits" }]);
    await until(`document.querySelector('[role=alert].bp6-callout')?.textContent === ${JSON.stringify(translate("en", "It could not be moved to the Trash."))}`, "the failure is said");
    assert.equal(await evaluate(`${card("commits")} !== undefined`), true);
    // Confirmed again, it is removed: the list is read again without it, and the other plugin stays.
    await until(`${confirm} === null`, "the confirmation closed");
    await act(`${fixture}.state.deleteStatus = "ok"; ${trash("commits")}.click()`);
    await until(`${confirm} !== null`);
    await act(`${confirm}.querySelector('button').click()`);
    await until(`${card("commits")} === undefined`, "the removed plugin is no longer listed");
    assert.equal(await evaluate("document.querySelector('[role=status].bp6-callout')?.textContent"), translate("en", "Removed."));
    assert.equal(await evaluate(`${card("local")} !== undefined && ${calls("delete")}.length === 2`), true);

    // The Skills page, when the configuration file of the user does not parse: the skills are listed all the same,
    // and the file is named in red above the list, where it is opened in the code editor.
    await command("Page.navigate", { url: pathToFileURL(page).href + "?skills" });
    await until(`document.querySelectorAll('.skill-settings .settings-editor-rows .bp6-card').length === 2`, "the skills are listed although a configuration file was not read");
    assert.deepEqual(await evaluate(`[...document.querySelectorAll('.skill-settings .settings-editor-rows .bp6-card strong')].map(value => value.textContent)`), ["release-notes", "triage"]);
    assert.equal(await evaluate("document.querySelector('.skill-settings .bp6-non-ideal-state')"), null, "the page is not blank");
    assert.equal(await evaluate(`[...${problem}.childNodes].filter(node => node.nodeType === Node.TEXT_NODE).map(node => node.textContent).join("")`),
      translate("en", "The configuration file {path} could not be read.", { path: "C:\\Users\\someone\\.alta\\config.toml" }) + " (1,8): expected ]");
    assert.equal(await evaluate(`(${problem}.compareDocumentPosition(document.querySelector('.skill-settings .settings-editor-rows')) & Node.DOCUMENT_POSITION_FOLLOWING) !== 0`), true,
      "the file is named above the list");
    await act(`${button(problem, "Edit in the code editor")}.click()`);
    assert.deepEqual(await evaluate(`${calls("files.open")}.at(-1)`), { expectedEpoch: "epoch", projectId: "p", kind: "config", scope: "Global", id: null, part: null });
    assert.equal(await evaluate(`${fixture}.state.opened`), 1, "the window leaves Settings for the code editor");

    // Malformed metadata must not collapse rows, suppress their details or send an empty name to removal.
    await command("Page.navigate", { url: pathToFileURL(page).href + "?skills=invalid" });
    const skillRows = "[...document.querySelectorAll('.skill-settings .settings-editor-rows .bp6-card')]";
    const skillDetail = "document.querySelector('.skill-detail')";
    await until(`${skillRows}.length === 4`, "unnamed skills and duplicate names each have their own row");
    assert.deepEqual(await evaluate(`${skillRows}.map(row => row.querySelector('input').disabled)`), [true, true, false, false], "only usable names have enablement switches");
    assert.equal(await evaluate("document.querySelector('.settings-scope [aria-checked=true]')?.textContent"), "Global");
    await act(`${card("Broken second")}.querySelector('strong').click()`);
    await until(`${skillDetail}?.querySelector('.skill-detail-diagnostics')?.textContent.includes('Broken second: invalid YAML.')`, "the second unnamed skill has its own diagnostic");
    assert.equal(await evaluate(`${card("Broken second")}.getAttribute('aria-current')`), "true");
    assert.equal(await evaluate(`${card("Broken first")}.getAttribute('aria-current')`), null);
    assert.deepEqual(await evaluate(`${calls("skills.detail")}.at(-1)`), { expectedEpoch: "epoch", projectId: "p", id: "broken-second", name: null, source: "ProjectAlta" });
    await until(`${skillDetail}?.querySelector('.skill-detail-instructions')?.textContent.includes('Instructions for broken-second')`, "the right body is shown");
    assert.ok((await evaluate(`${skillDetail}.textContent`) as string).includes("C:\\skills\\broken-second\\SKILL.md"));

    // Bulk toggles still name skills, but omit empty names and deduplicate usable names.
    const bulk = (text: string) => `[...document.querySelectorAll('.settings-editor-toolbar button')].find(value => value.textContent === ${JSON.stringify(text)})`;
    await act(`${bulk("Disable all")}.click()`);
    await until(`${calls("skills.setAllEnabled")}.length === 1`);
    assert.deepEqual(await evaluate(calls("skills.setAllEnabled")), [{ expectedEpoch: "epoch", projectId: "p", scope: "Global", names: ["duplicate"], enabled: false }]);
    assert.deepEqual(await evaluate(calls("skills.setEnabled")), []);
    const filter = (text: string) => `(() => { const input = document.querySelector('input[type=search]'); Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(input, ${JSON.stringify(text)}); input.dispatchEvent(new Event('input', { bubbles: true })); })()`;
    await act(filter("Broken"));
    assert.deepEqual(await evaluate(`[${bulk("Enable all")}.disabled, ${bulk("Disable all")}.disabled]`), [true, true], "a list with only unnamed skills has no bulk toggle action");
    await act(filter(""));
    await until(`${skillDetail}?.querySelector('.skill-detail-diagnostics')?.textContent.includes('Broken second: invalid YAML.')`);

    // Cached detail is fenced by project and epoch as well as row id, even when the fake host takes time to answer.
    for (const [change, context] of [[`${fixture}.setProjectId('other')`, "epoch/other"], [`${fixture}.setEpoch('next')`, "next/other"]]) {
      await act(`${fixture}.state.holdDetails = true; ${change}`);
      await until(`${fixture}.state.pendingDetails.length > 0`);
      assert.equal(await evaluate(`${skillDetail}.querySelector('.skill-detail-instructions')`), null, "the old scope's detail is hidden while the new one loads");
      await act(`${fixture}.state.holdDetails = false; ${fixture}.state.pendingDetails.splice(0).forEach(value => value.resolve())`);
      await until(`${skillDetail}?.querySelector('.skill-detail-instructions')?.textContent.includes(${JSON.stringify(context)})`);
    }
    await act(`${fixture}.setEpoch('epoch'); ${fixture}.setProjectId('p')`);
    await until(`${skillDetail}?.querySelector('.skill-detail-instructions')?.textContent.includes('epoch/p')`);

    // Remove from the details while Global is selected: identity and project, not the write scope, select the file.
    await act(`${skillDetail}.querySelector('button.bp6-intent-danger').click()`);
    await until(`${confirm} !== null`);
    assert.equal(await evaluate(`${confirm}.querySelector('p').textContent`), "Remove Broken second?");
    await act(`${confirm}.querySelector('button').click()`);
    await until(`${card("Broken second")} === undefined`);
    assert.deepEqual(await evaluate(calls("skills.delete")), [{ expectedEpoch: "epoch", projectId: "p", id: "broken-second", name: null, source: "ProjectAlta" }]);
    assert.equal(await evaluate(`${card("Broken first")} !== undefined`), true, "the first unnamed skill stays");
    await until(`${confirm} === null`, "the first confirmation has closed");

    // The same is true for two files with the same nonempty name and source.
    await act(`${card("Duplicate second")}.querySelector('strong').click()`);
    await until(`${skillDetail}?.querySelector('.skill-detail-instructions')?.textContent.includes('Instructions for duplicate-second')`);
    assert.equal(await evaluate(`${card("Duplicate second")}.getAttribute('aria-current')`), "true");
    assert.deepEqual(await evaluate(`${calls("skills.detail")}.at(-1)`), { expectedEpoch: "epoch", projectId: "p", id: "duplicate-second", name: null, source: "ProjectAlta" });
    await act(`${trash("Duplicate second")}.click()`);
    await until(`${confirm} !== null`);
    assert.equal(await evaluate(`${confirm}.querySelector('p').textContent`), "Remove Duplicate second?");
    await act(`${confirm}.querySelector('button').click()`);
    await until(`${card("Duplicate second")} === undefined`);
    assert.deepEqual(await evaluate(`${calls("skills.delete")}.at(-1)`), { expectedEpoch: "epoch", projectId: "p", id: "duplicate-second", name: null, source: "ProjectAlta" });
    assert.deepEqual(await evaluate(`${skillRows}.map(row => row.querySelector('strong').textContent)`), ["Broken first", "Duplicate first"]);
  } finally {
    // Edge's launcher can exit while the browser it started goes on: the browser itself is asked to close.
    if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ id: 9999, method: "Browser.close" }));
    socket?.close();
    if (browser && browser.exitCode === null) { const closed = new Promise<void>(resolve => browser!.once("exit", () => resolve())); browser.kill(); await Promise.race([closed, new Promise(resolve => setTimeout(resolve, 5000))]); }
    await rm(root, { recursive: true, force: true, maxRetries: 10, retryDelay: 100 });
  }
});
