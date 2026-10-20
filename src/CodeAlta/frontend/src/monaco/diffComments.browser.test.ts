import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { build } from "esbuild";
import { browserBaseArgs, browserExecutable, endBrowser, waitForBrowserPort } from "../browserTarget";

const edge = browserExecutable;

// A real Monaco diff in a real page, with view zones added as a comment layer would: where Monaco draws them,
// what it hides, which mouse targets each side gives, and what the opt-ins of DiffEditor report.
test("comment threads can live in the view zones of both sides of the diff editor", { skip: !edge, timeout: 180_000 }, async t => {
  const root = await mkdtemp(join(tmpdir(), "codealta-diff-comments-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./diffComments.mount.tsx", import.meta.url))], outfile: join(root, "fixture.js"),
      bundle: true, platform: "browser", format: "iife", loader: { ".ttf": "dataurl" }, plugins: [{ name: "inline-worker", setup(bundle) {
        bundle.onResolve({ filter: /^monaco-editor\/.*\?worker$/ }, args => ({ path: args.path, namespace: "fixture-worker" }));
        bundle.onLoad({ filter: /.*/, namespace: "fixture-worker" }, async () => {
          const worker = await build({ entryPoints: [fileURLToPath(new URL("../../node_modules/monaco-editor/esm/vs/editor/editor.worker.js", import.meta.url))],
            bundle: true, platform: "browser", format: "iife", write: false });
          return { loader: "js", contents: `export default class extends Worker { constructor() {
            const url=URL.createObjectURL(new Blob([${JSON.stringify(worker.outputFiles[0].text)}],{type:"text/javascript"}));
            super(url); URL.revokeObjectURL(url);
          } }` };
        });
      } }] });
    // The styles of the editor come out of the bundle beside it; those of the application are the ones it ships.
    await writeFile(join(root, "style.css"), await readFile(new URL("../../node_modules/@blueprintjs/core/lib/css/blueprint.css", import.meta.url), "utf8")
      + await readFile(new URL("../style.css", import.meta.url), "utf8"));
    const page = join(root, "fixture.html");
    await writeFile(page, '<!doctype html><html><head><meta charset="utf-8"><link rel="stylesheet" href="fixture.css"><link rel="stylesheet" href="style.css"></head>'
      + '<body style="margin:0"><div id="root"></div><script src="fixture.js"></script></body></html>');
    browser = spawn(edge!, [...browserBaseArgs, "--allow-file-access-from-files",
      `--user-data-dir=${join(root, "profile")}`, "--remote-debugging-port=0", "about:blank"], { stdio: ["ignore", "ignore", "pipe"], windowsHide: true });
    const port = await waitForBrowserPort(join(root, "profile"), browser);
    const pages = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json() as { type?: string; webSocketDebuggerUrl?: string }[];
    socket = new WebSocket(pages.find(tab => tab.type === "page")!.webSocketDebuggerUrl!);
    await new Promise<void>((resolve, reject) => { socket!.addEventListener("open", () => resolve(), { once: true }); socket!.addEventListener("error", reject, { once: true }); });
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
    // An expression of the page once it is true, or a failure that says what it was.
    const until = async (expression: string, timeout = 8000) => assert.equal(await evaluate(`new Promise(resolve => { const end = Date.now() + ${timeout}; (function check() {
      if (${expression}) resolve(true); else if (Date.now() > end) resolve(false); else setTimeout(check, 20); })(); })`), true, `waited for ${expression}`);
    // A few frames, for Monaco to render what it was told.
    const frames = () => evaluate("new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(() => setTimeout(resolve, 50))))");
    const click = async (point: { x: number; y: number }) => {
      await command("Input.dispatchMouseEvent", { type: "mouseMoved", x: point.x, y: point.y });
      await command("Input.dispatchMouseEvent", { type: "mousePressed", x: point.x, y: point.y, button: "left", clickCount: 1 });
      await command("Input.dispatchMouseEvent", { type: "mouseReleased", x: point.x, y: point.y, button: "left", clickCount: 1 });
    };
    const fixture = "diffCommentsFixture";

    await command("Page.enable");
    await command("Emulation.setDeviceMetricsOverride", { width: 1200, height: 900, deviceScaleFactor: 1, mobile: false });
    await command("Page.navigate", { url: pathToFileURL(page).href });
    await until(`window.${fixture} && ${fixture}.state.editors && ${fixture}.state.editors.diff.getLineChanges()?.length === 3`, 15_000);
    await frames();

    const f = fixture;
    const value = async <T>(expression: string) => JSON.parse(await evaluate(`JSON.stringify(${expression})`)) as T;
    type Box = { top: number; height: number } | null;
    type Mouse = { side: "old" | "new"; type: number; line: number | null; zone: number | null; removed: number | null };
    let documents = 0;
    // Another document with the fixture's texts (or the look given), once compared and drawn: no zone, no click recorded.
    const fresh = async (look: object = {}) => {
      await evaluate(`${f}.clearZones(); ${f}.state.set({ documentKey: "document ${++documents}", original: ${f}.original, modified: ${f}.modified, ...${JSON.stringify(look)} })`);
      await until(`${f}.state.editors.diff.getLineChanges()?.length === 3`);
      await frames();
      await evaluate(`void ${f}.state.mouse.splice(0)`);
    };
    // The page tops of lines of both sides, null for a line that is not drawn.
    const tops = (side: "old" | "new", ...lines: number[]) => value<(number | null)[]>(`[${lines.join(",")}].map(line => ${f}.lineTop("${side}", line))`);
    const heights = () => value<number[]>(`[${f}.state.editors.original.getContentHeight(), ${f}.state.editors.modified.getContentHeight()]`);
    // The fixture: old lines 50-52 changed, old 100-104 removed (after new 99), three lines added after old 300 (new 296-298).
    // Below old 104 a new line is five less than its old line, and below old 300 two less.

    await t.test("side by side, a zone on one side is matched by room on the other, and both content heights count it", async () => {
      await fresh();
      const [before] = await heights();
      assert.deepEqual(await tops("old", 54, 55), await tops("new", 54, 55));
      for (const [name, side, line] of [["after-unchanged-new", "new", 54], ["after-unchanged-old", "old", 54], ["in-change", "new", 51], ["after-removed", "old", 102]] as const) {
        await evaluate(`${f}.addZone("${name}", "${side}", ${line}, 60)`);
        await frames();
        const zone = await value<Box>(`${f}.zone("${name}")`);
        // The zone is under its line, and the lines after it are in line on both sides.
        assert.equal(zone?.top, (await tops(side, line))[0]! + 20, name);
        assert.equal(zone?.height, 60);
        assert.deepEqual(await tops("old", 55, 99, 105), await tops("new", 55, 99, 100), name);
        assert.deepEqual(await heights(), [before + 60, before + 60], name);
        await evaluate(`${f}.removeZone("${name}")`);
      }
    });

    await t.test("a zone under a line of a folded region is hidden, and reveal unfolds it with the line after it, both sides in line", async () => {
      await fresh();
      const [before] = await heights();
      await evaluate(`${f}.addZone("folded-new", "new", 20, 60); ${f}.addZone("folded-old", "old", 150, 60)`);
      await frames();
      assert.deepEqual([await value<Box>(`${f}.zone("folded-new")`), await value<Box>(`${f}.zone("folded-old")`)], [null, null]);
      assert.deepEqual(await heights(), [before, before]);
      // The caret alone on the line unfolds the region from its closer end, the top here: the line shows, the one
      // after it does not, nor does the zone between them.
      await evaluate(`${f}.state.editors.modified.setPosition({ lineNumber: 20, column: 1 })`);
      await frames();
      assert.deepEqual(await tops("new", 20, 21).then(lines => lines.map(line => line !== null)), [true, false]);
      assert.equal(await value<Box>(`${f}.zone("folded-new")`), null);

      await evaluate(`${f}.state.handle.current.reveal("new", 20)`);
      await frames();
      const [line20, line21] = await tops("new", 20, 21);
      assert.deepEqual(await value<Box>(`${f}.zone("folded-new")`), { top: line20! + 20, height: 60 });
      assert.equal(line21, line20! + 80);
      // Monaco ignores the zones that grow while it unfolds: DiffEditor gives the old side its room again.
      assert.deepEqual(await tops("old", 21, 50), await tops("new", 21, 50));
      const [old, current] = await heights();
      assert.equal(old, current);

      await evaluate(`${f}.state.handle.current.reveal("old", 150)`);
      await frames();
      const [line150, line151] = await tops("old", 150, 151);
      assert.deepEqual(await value<Box>(`${f}.zone("folded-old")`), { top: line150! + 20, height: 60 });
      assert.equal(line151, line150! + 80);
      assert.deepEqual(await tops("new", 145, 146), [line150, line151]);
      // In the middle of the view.
      assert.ok(Math.abs(line150! - (await value<number>(`document.getElementById("diff").getBoundingClientRect().top`)) - 290) <= 20, String(line150));

      // Asked before the first comparison of a document, the line is shown once it is done, in the place of the first change.
      await evaluate(`${f}.clearZones(); ${f}.state.set({ documentKey: "document ${++documents}" })`);
      await evaluate(`${f}.state.handle.current.reveal("new", 250)`);
      await until(`${f}.state.editors.diff.getLineChanges()?.length === 3 && ${f}.lineTop("new", 250) !== null`);
      await frames();
      assert.notEqual((await tops("new", 251))[0], null);
      assert.equal((await tops("new", 50))[0], null);
    });

    await t.test("with comments, both sides have a glyph margin whose clicks name the lines of their own side", async () => {
      await fresh();
      const glyph = () => value<boolean[]>(`[${f}.state.editors.original, ${f}.state.editors.modified].map(editor => editor.getOption(${f}.monaco.editor.EditorOption.glyphMargin))`);
      assert.deepEqual(await glyph(), [true, true]);
      for (const [side, line, where] of [["old", 51, "glyph"], ["new", 51, "glyph"], ["new", 51, "text"], ["old", 101, "glyph"], ["new", 297, "glyph"]] as const) {
        if (line > 100) await evaluate(`${f}.state.handle.current.reveal("${side}", ${line})`), await frames();
        await click(await value(`${f}.point("${side}", ${line}, "${where}")`));
      }
      const glyphMargin = 2, text = 6;
      assert.deepEqual(await value<Mouse[]>(`${f}.state.mouse`), [
        { side: "old", type: glyphMargin, line: 51, zone: null, removed: null },
        { side: "new", type: glyphMargin, line: 51, zone: null, removed: null },
        { side: "new", type: text, line: 51, zone: null, removed: null },
        { side: "old", type: glyphMargin, line: 101, zone: null, removed: null },
        { side: "new", type: glyphMargin, line: 297, zone: null, removed: null }]);
      // Without comments, the diff is as it was: Monaco gives the old side a glyph margin of its own side by side
      // (and none in one column), whatever the option says; the new side has none.
      await evaluate(`${f}.state.set({ comments: false })`); await frames();
      assert.deepEqual(await glyph(), [true, false]);
      await evaluate(`${f}.state.set({ comments: true })`); await frames();
    });

    await t.test("in one column, removed lines are a zone of the new side whose rows map back to old lines, and old-side zones break the alignment", async () => {
      await fresh();
      assert.deepEqual(await value(`${f}.state.inline`), [false]);
      // Narrower than the breakpoint, and then side by side turned off: each change is reported once.
      await evaluate(`${f}.state.set({ width: 700 })`); await frames(); await frames();
      await evaluate(`${f}.state.set({ width: 1000 })`); await frames(); await frames();
      await evaluate(`${f}.state.set({ sideBySide: false })`); await frames();
      assert.deepEqual(await value(`${f}.state.inline`), [false, true, false, true]);
      // The old side is a narrow strip of old line numbers, beside the rows of removed lines; it has no glyph margin.
      assert.deepEqual(await value(`(l => [l.glyphMarginWidth, l.width < 40])(${f}.state.editors.original.getLayoutInfo())`), [0, true]);
      assert.deepEqual(await value(`[...document.querySelectorAll("#diff .view-lines.line-delete")].map(node => node.querySelectorAll(":scope > .view-line").length)`), [3, 5]);
      assert.deepEqual(await tops("old", 100, 104, 105), [(await tops("new", 99))[0]! + 20, (await tops("new", 99))[0]! + 100, (await tops("new", 100))[0]]);

      for (const [block, row, where] of [[0, 0, "margin"], [0, 2, "margin"], [1, 0, "margin"], [1, 4, "margin"], [1, 3, "text"]] as const)
        await click(await value(`${f}.removedPoint(${block}, ${row}, "${where}")`));
      await click(await value(`${f}.point("new", 100, "glyph")`));
      const gutterZone = 5, contentZone = 8, glyphMargin = 2;
      assert.deepEqual(await value<Mouse[]>(`${f}.state.mouse.map(({ side, type, zone, removed }) => ({ side, type, zone, removed }))`), [
        { side: "new", type: gutterZone, zone: 49, removed: 50 },
        { side: "new", type: gutterZone, zone: 49, removed: 52 },
        { side: "new", type: gutterZone, zone: 99, removed: 100 },
        { side: "new", type: gutterZone, zone: 99, removed: 104 },
        { side: "new", type: contentZone, zone: 99, removed: 103 },
        { side: "new", type: glyphMargin, zone: null, removed: null }]);

      // A new-side zone after the line before removed lines goes above them, or under them with an ordinal above Monaco's 10000.
      const [line99] = await tops("new", 99);
      await evaluate(`${f}.addZone("above", "new", 99, 40, 0); ${f}.addZone("below", "new", 99, 30, 10001)`); await frames();
      assert.deepEqual(await value(`[${f}.zone("above"), ${f}.zone("below")]`), [{ top: line99! + 20, height: 40 }, { top: line99! + 20 + 40 + 100, height: 30 }]);
      assert.deepEqual(await tops("new", 100), [line99! + 20 + 40 + 100 + 30]);
      await evaluate(`${f}.removeZone("above"); ${f}.removeZone("below")`);
      // An old-side zone is drawn in the strip and pushes the old numbers down, away from the rows they name.
      const [old] = await heights();
      await evaluate(`${f}.addZone("old-strip", "old", 102, 40)`); await frames();
      assert.equal((await heights())[0], old + 40);
      assert.equal((await tops("old", 103))[0], (await tops("old", 102))[0]! + 60);
      assert.notEqual((await tops("old", 105))[0], (await tops("new", 100))[0]);
      await evaluate(`${f}.removeZone("old-strip")`);

      // An old line is revealed through the strip, which scrolls with the new side: a folded one, then a removed one.
      await evaluate(`${f}.state.handle.current.reveal("old", 160)`); await frames();
      assert.deepEqual(await tops("old", 160), await tops("new", 155));
      await evaluate(`${f}.state.handle.current.reveal("old", 102)`); await frames();
      const [line102] = await tops("old", 102);
      assert.equal(await value(`Math.round(document.querySelectorAll("#diff .view-lines.line-delete")[1].getBoundingClientRect().top) + 40`), line102);
      assert.equal(await value(`${f}.state.editors.original.getScrollTop() === ${f}.state.editors.modified.getScrollTop()`), true);

      // An old line asked for while a text read again is being compared (a long removed line, wrapped) waits for the
      // comparison, whose folds would hide it again, and then for the heights of the wrapped, unfolded lines.
      const long = `${f}.original.replace("line 101\\n", "line 101 " + "long ".repeat(200) + "\\n")`;
      const compared = await value<number>(`${f}.state.compared`);
      await evaluate(`new Promise(resolve => { ${f}.state.set({ wrap: true, original: ${long} });
        (function wait() { if (${f}.state.editors.original.getModel().getValue().includes("long long")) resolve(); else setTimeout(wait, 1); })(); })`);
      await evaluate(`${f}.state.handle.current.reveal("old", 160)`);
      assert.equal(await value(`${f}.state.compared`), compared);
      await until(`${f}.state.compared > ${compared} && ${f}.lineTop("old", 160) !== null`); await frames();
      const [line160] = await tops("old", 160);
      assert.deepEqual(await tops("new", 155), [line160]);
      // The wrapped removed line takes several rows: they no longer name lines, and none is given.
      await evaluate(`${f}.state.handle.current.reveal("new", 99)`);
      await until(`document.querySelectorAll("#diff .view-lines.line-delete")[1]?.querySelectorAll(":scope > .view-line").length > 5
        && document.querySelectorAll("#diff .inline-deleted-margin-view-zone")[1]?.getBoundingClientRect().height > 100`);
      await frames();
      await evaluate(`void ${f}.state.mouse.splice(0)`);
      await click(await value(`${f}.removedPoint(1, 0, "margin")`));
      assert.deepEqual(await value(`${f}.state.mouse.map(({ type, zone, removed }) => ({ type, zone, removed }))`), [{ type: gutterZone, zone: 99, removed: null }]);
      await evaluate(`${f}.state.set({ wrap: false, original: ${f}.original, sideBySide: true })`); await frames();
      assert.deepEqual(await value(`${f}.state.inline`), [false, true, false, true, false]);
    });

    await t.test("a text read again drops the decorations of the side it replaces and leaves zones where they were until laid out again; a new document drops them", async () => {
      await fresh();
      await evaluate(`${f}.addZone("kept", "new", 51, 40); void (window.marks = [${f}.decorate("old", 51), ${f}.decorate("new", 51)])`);
      await frames();
      // Ten lines added at the top of the new text: DiffEditor sets the new text in place.
      await evaluate(`${f}.state.set({ modified: Array.from({ length: 10 }, (_, line) => "top " + line).join("\\n") + "\\n" + ${f}.modified })`);
      await until(`${f}.state.editors.diff.getLineChanges()?.length === 4`);
      await frames();
      assert.deepEqual(await value(`marks.map(mark => mark.getRanges().map(range => range.startLineNumber))`), [[51], []]);
      // The zone stays where it was drawn, at its line of the view before (a line of the folded text), not under
      // line 51, an unchanged line in a folded region now: revealed, the line has no zone under it.
      await evaluate(`${f}.state.handle.current.reveal("new", 51)`); await frames();
      assert.equal(await value(`${f}.state.editors.modified.getModel().getLineContent(51)`), "line 41");
      assert.equal((await tops("new", 52))[0], (await tops("new", 51))[0]! + 20);
      // Laid out again, it is under line 51.
      await evaluate(`${f}.layoutZones()`); await frames();
      assert.deepEqual(await value(`${f}.zone("kept")`), { top: (await tops("new", 51))[0]! + 20, height: 40 });
      assert.equal((await tops("new", 52))[0], (await tops("new", 51))[0]! + 60);
      // Another document is another view: its zones are gone with it.
      await evaluate(`${f}.state.set({ documentKey: "document ${++documents}" })`);
      await until(`${f}.state.editors.diff.getLineChanges()?.length === 4`);
      assert.equal(await value(`document.querySelector(".fixture-zone-kept")?.isConnected ?? false`), false);
    });

    await t.test("the code editor gives its editor, with a glyph margin with comments and none without", async () => {
      const glyph = () => value<boolean>(`${f}.state.single.getOption(${f}.monaco.editor.EditorOption.glyphMargin)`);
      assert.equal(await value(`${f}.state.single?.getModel()?.getLineCount()`), 401);
      assert.equal(await glyph(), true);
      await evaluate(`${f}.state.set({ comments: false })`); await frames();
      assert.equal(await glyph(), false);
      await evaluate(`${f}.state.set({ comments: true })`); await frames();
      assert.equal(await glyph(), true);
    });
  } finally {
    if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ id: 99_999, method: "Browser.close" }));
    socket?.close();
    await endBrowser(browser);
    await rm(root, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 }).catch(() => { /* The profile of a browser that is still closing. */ });
  }
});
