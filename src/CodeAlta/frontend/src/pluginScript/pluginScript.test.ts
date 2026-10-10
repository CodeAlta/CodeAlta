import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { existsSync, readFileSync } from "node:fs";
import { createRequire } from "node:module";
import test from "node:test";
import { importMapText, lentLibraries } from "../lent/libraries";
import { AltaError, createAlta, type AltaHostBridge, type AltaOptions, type AltaTheme } from "./alta";
import { createHtml, HtmlTemplateError } from "./html";
import { isScriptPath, loadScriptModule, readScriptModule, ScriptError } from "./scriptModule";
import { altaInterfaceVersion, lentVersions } from "./versions";

const read = (path: string) => readFileSync(new URL(path, import.meta.url), "utf8");

// ---- the import map and the policy of the page ----

test("the import map of the entry document is the one the build and the policy know: its hash is in the content security policy", () => {
  const document = read("../../index.html");
  const found = [...document.matchAll(/<script type="importmap">([^<]*)<\/script>/gu)];
  assert.equal(found.length, 1, "one inline import map");
  assert.equal(found[0][1], importMapText(), "index.html carries the text the libraries file produces, as one line");
  assert.ok(!found[0][1].includes("\n") && !found[0][1].includes("\r"), "a line break would make the hash depend on the checkout");
  const hash = `'sha256-${createHash("sha256").update(found[0][1], "utf8").digest("base64")}'`;
  // What the build made of the document, when it was built: the map is what it was, character for character, or the policy would refuse it.
  const built = new URL("../../dist/index.html", import.meta.url);
  if (existsSync(built)) {
    const output = [...readFileSync(built, "utf8").matchAll(/<script type="importmap">([^<]*)<\/script>/gu)];
    assert.deepEqual(output.map(item => item[1]), [found[0][1]], "the built document carries the same map");
  }

  const policy = (JSON.parse(read("../../../neoastra.json")) as { assets: { csp: string } }).assets.csp;
  const script = policy.split(";").map(directive => directive.trim()).find(directive => directive.startsWith("script-src "));
  assert.equal(script, `script-src 'self' ${hash}`, "the script policy is the origin and the hash of the import map, nothing else");
  for (const unsafe of ["unsafe-inline", "unsafe-eval", "data:", "http:", "https:", "*"]) assert.ok(!script!.includes(unsafe), `no ${unsafe} in script-src`);
});

test("every lent library has an entry file, a distinct file in the build output, and a name in the import map", () => {
  const map = (JSON.parse(importMapText()) as { imports: Record<string, string> }).imports;
  assert.deepEqual(Object.keys(map), lentLibraries.map(library => library.name));
  assert.equal(new Set(lentLibraries.map(library => library.file)).size, lentLibraries.length);
  for (const library of lentLibraries) {
    assert.match(library.file, /^lib\/[a-z0-9-]+\.js$/u);
    assert.equal(map[library.name], `./${library.file}`);
    assert.ok(read(`../../${library.entry}`).length > 0, library.entry);
  }

  for (const name of ["react", "react/jsx-runtime", "react-dom", "react-dom/client", "@blueprintjs/core", "@blueprintjs/table", "flexlayout-react", "lucide-react", "codealta"]) {
    assert.ok(name in map, `${name} is lent`);
  }
});

test("the versions alta.versions tells are the ones the application is built with, and the lent React modules list what React exports", () => {
  const dependencies = (JSON.parse(read("../../package.json")) as { dependencies: Record<string, string> }).dependencies;
  for (const [name, version] of Object.entries(lentVersions)) assert.equal(dependencies[name], version, name);
  const require = createRequire(import.meta.url);
  const exported = (file: string, from: string) => {
    const source = read(`../lent/${file}`);
    const block = new RegExp(`export \\{([^}]*)\\} from "${from}"`, "u").exec(source);
    assert.ok(block, file);
    return block[1].split(",").map(name => name.trim()).filter(Boolean).sort();
  };
  const public_ = (module: object) => Object.keys(module).filter(name => !name.startsWith("__")).sort();
  assert.deepEqual(exported("react.ts", "react"), public_(require("react")).filter(name => name !== "unstable_useCacheRefresh"));
  assert.deepEqual(exported("react-dom.ts", "react-dom"), public_(require("react-dom")));
  assert.deepEqual(exported("react-jsx-runtime.ts", "react/jsx-runtime"), public_(require("react/jsx-runtime")));
});

// ---- the module of a script ----

test("a script path is one the host serves, and nothing else", () => {
  assert.ok(isScriptPath("/plugin/YnVpbHRpbjpzdGF0cw/abc_DEF-123/main.js"));
  assert.ok(isScriptPath("/plugin/a/b/ui/board.js"));
  for (const bad of ["", "plugin/a/b/c.js", "/plugin/a/b", "/plugin/a/b/../c.js", "/plugin/a/b/./c.js", "/plugin/a//c.js", "https://example.com/x.js", "//host/plugin/a/b/c.js",
    "/plugin/a/b/c.js?x=1", "/plugin/a/b/c.js#x", "/plugin/a/b/c\\d.js", "/assets/index.js", "/plugin/a b/c/d.js", "/plugin/a/b/c.js\u0000"]) {
    assert.equal(isScriptPath(bad), false, JSON.stringify(bad));
  }
});

test("a module is a component when it exports one by default, a mount function otherwise, and nothing else is a script", () => {
  const component = () => null;
  const mount = () => undefined;
  assert.equal(readScriptModule({ default: component, mount }).kind, "component", "a module with both is drawn as a component");
  assert.deepEqual(readScriptModule({ default: component }), { kind: "component", component });
  assert.deepEqual(readScriptModule({ mount }), { kind: "mount", mount });
  assert.equal(readScriptModule({ default: { $$typeof: Symbol.for("react.memo") } }).kind, "component", "memo and forwardRef components are components");
  for (const bad of [{}, { default: 3 }, { default: { not: "a component" } }, { mount: "no" }, null, undefined]) {
    assert.throws(() => readScriptModule(bad), (error: unknown) => error instanceof ScriptError && error.stage === "shape");
  }
});

test("a script that does not load or is not served fails with the stage and the message", async () => {
  await assert.rejects(loadScriptModule("/plugin/a/b/c.js", async () => { throw new Error("boom"); }), (error: unknown) => error instanceof ScriptError && error.stage === "load" && error.message === "boom");
  await assert.rejects(loadScriptModule("../x.js", async () => ({})), (error: unknown) => error instanceof ScriptError && error.stage === "load");
  assert.equal((await loadScriptModule("/plugin/a/b/c.js", async () => ({ mount: () => undefined }))).kind, "mount");
});

// ---- the alta object ----

const theme = (dark: boolean): AltaTheme => ({ dark, fontFamily: "sans", text: "#fff", muted: "#aaa", axis: "#999", grid: "#333", surface: "#111", tooltipBackground: "#111", tooltipBorder: "#222",
  series: ["#1", "#2"], ramp: ["#a", "#b"], diverging: ["#l", "#m", "#h"] });

function make(host: AltaHostBridge = {}, overrides: Partial<AltaOptions> = {}) {
  let current = theme(true);
  const listeners = new Set<() => void>();
  const handle = createAlta({
    context: { pluginKey: "plugin:board", canvasId: "board", instanceId: "i1", spaceId: "work", projectId: "p1", sessionId: "s1", key: "k", input: { a: 1 } },
    visible: true, host, sanitize: text => `<clean>${text.replace(/<script.*?<\/script>/gu, "")}</clean>`,
    readTheme: () => current, subscribeTheme: listener => { listeners.add(listener); return () => { listeners.delete(listener); }; }, ...overrides,
  });
  return { ...handle, changeTheme(dark: boolean) { current = theme(dark); for (const listener of [...listeners]) listener(); }, listeners };
}

test("alta tells the context, the versions and the interface, and is frozen", () => {
  const { alta } = make();
  assert.deepEqual(alta.context, { pluginKey: "plugin:board", canvasId: "board", instanceId: "i1", spaceId: "work", projectId: "p1", sessionId: "s1", key: "k", input: { a: 1 } });
  assert.equal(alta.versions.interface, altaInterfaceVersion);
  assert.equal(alta.versions["@blueprintjs/core"], lentVersions["@blueprintjs/core"]);
  assert.ok(Object.isFrozen(alta) && Object.isFrozen(alta.context) && Object.isFrozen(alta.host) && Object.isFrozen(alta.versions));
  assert.throws(() => { (alta as { closed: unknown }).closed = null; }, TypeError);
});

test("alta.visible follows the tab, with an event, and alta.closed aborts when the content goes away", () => {
  const { alta, setVisible, dispose } = make();
  const seen: boolean[] = [];
  const stop = alta.visible.subscribe(value => seen.push(value));
  assert.equal(alta.visible.value, true);
  setVisible(false); setVisible(false); setVisible(true);
  assert.deepEqual(seen, [false, true], "only a change is told");
  stop(); setVisible(false);
  assert.deepEqual(seen, [false, true], "a listener that left hears nothing");
  assert.equal(alta.visible.value, false);
  const aborted: string[] = [];
  alta.closed.addEventListener("abort", () => aborted.push("closed"));
  assert.equal(alta.closed.aborted, false);
  dispose(); dispose();
  assert.deepEqual(aborted, ["closed"], "disposed once");
  assert.equal(alta.closed.aborted, true);
  setVisible(true);
  assert.equal(alta.visible.value, false, "an ended object does not move");
});

test("alta.theme gives the colors as values and tells when they change, watching the window only while a script listens", () => {
  const { alta, changeTheme, listeners, dispose } = make();
  assert.equal(alta.theme.value.dark, true);
  assert.equal(listeners.size, 0, "nothing watches before a listener");
  const seen: boolean[] = [];
  const stop = alta.theme.subscribe(value => seen.push(value.dark));
  assert.equal(listeners.size, 1);
  changeTheme(false);
  assert.deepEqual(seen, [false]);
  assert.equal(alta.theme.value.dark, false);
  stop();
  assert.equal(listeners.size, 0, "the last listener leaving stops the watching");
  alta.theme.subscribe(() => { });
  dispose();
  assert.equal(listeners.size, 0, "ending the object stops it too");
});

test("alta.host checks what a script passes and does what the window serves, and nothing when it does not", () => {
  const calls: unknown[][] = [];
  const bridge: AltaHostBridge = {
    openFile: (path, line, scope) => calls.push(["file", path, line, scope]), openDiff: id => calls.push(["diff", id]), openSession: id => calls.push(["session", id]),
    openCanvas: request => calls.push(["canvas", request]), openLink: url => calls.push(["link", url]), notify: (message, tone) => calls.push(["notify", message, tone]),
    runCommand: name => calls.push(["command", name]), setTitle: title => calls.push(["title", title]), setStatus: status => calls.push(["status", status]), setBadge: badge => calls.push(["badge", badge]),
  };
  const { alta } = make(bridge);
  alta.host.openFile("src/a.cs", { line: 12 }); alta.host.openFile("src/b.cs"); alta.host.openFile(""); alta.host.openFile("x", { line: -3 });
  alta.host.openDiff(); alta.host.openDiff({ projectId: "p2" });
  alta.host.openSession("s9"); alta.host.openSession("");
  alta.host.openCanvas("board"); alta.host.openCanvas("other", { pluginKey: "plugin:x", projectId: "p3", key: "k2" }); alta.host.openCanvas("../bad");
  alta.host.openLink("https://example.com/x"); alta.host.openLink("javascript:alert(1)"); alta.host.openLink("file:///etc/passwd");
  alta.host.notify("hello"); alta.host.notify("careful", { tone: "warning" }); alta.host.notify("x", { tone: "bad" as never }); alta.host.notify("");
  alta.host.runCommand("refresh"); alta.host.runCommand("");
  alta.host.setTitle("Board"); alta.host.setTitle(null); alta.host.setStatus("3 left"); alta.host.setBadge(7); alta.host.setBadge("new"); alta.host.setBadge(null); alta.host.setBadge(Number.NaN);
  assert.deepEqual(calls, [
    ["file", "src/a.cs", 12, { projectId: "p1", sessionId: "s1" }], ["file", "src/b.cs", null, { projectId: "p1", sessionId: "s1" }], ["file", "x", null, { projectId: "p1", sessionId: "s1" }],
    ["diff", "p1"], ["diff", "p2"], ["session", "s9"],
    ["canvas", { pluginKey: "plugin:board", canvasId: "board", projectId: "p1", sessionId: "s1", key: null }],
    ["canvas", { pluginKey: "plugin:x", canvasId: "other", projectId: "p3", sessionId: "s1", key: "k2" }],
    ["link", "https://example.com/x"], ["notify", "hello", "info"], ["notify", "careful", "warning"], ["notify", "x", "info"], ["command", "refresh"],
    ["title", "Board"], ["title", null], ["status", "3 left"], ["badge", "7"], ["badge", "new"], ["badge", null],
  ]);
  // A window that serves nothing: every request does nothing and nothing throws.
  const { alta: bare } = make({});
  assert.doesNotThrow(() => { bare.host.openFile("a"); bare.host.openSession("s"); bare.host.notify("m"); bare.host.setBadge(1); bare.host.openCanvas("board"); });
});

test("alta.html cleans a string through the sanitizer of the window, and alta.rpc says it is not available yet", async () => {
  const { alta } = make();
  assert.equal(alta.html("<b>x</b><script>alert(1)</script>"), "<clean><b>x</b></clean>");
  assert.equal(alta.html(undefined as never), "<clean></clean>");
  await assert.rejects(alta.rpc.invoke("board.get"), (error: unknown) => error instanceof AltaError && error.code === "rpc_unavailable");
  await assert.rejects(alta.rpc.stream("board.watch"), (error: unknown) => error instanceof AltaError && error.code === "rpc_unavailable");
  const given = { invoke: async () => 42, stream: async () => (async function* () { yield 1; })() };
  const { alta: carried } = make({}, { rpc: given });
  assert.equal(await carried.rpc.invoke("x"), 42, "a window that carries the RPC gives it");
});

// ---- the html template ----

type Made = { type: unknown; props: Record<string, unknown> | null; children: unknown[] };
const make_ = (type: unknown, props: Record<string, unknown> | null, ...children: unknown[]): Made => ({ type, props, children });
const html = createHtml(make_ as never, "fragment") as (strings: TemplateStringsArray, ...values: unknown[]) => Made | unknown[];

test("html reads elements as JSX does: tags, components, attributes, text, children, fragments and several roots", () => {
  const Button = () => null;
  const onClick = () => undefined;
  const one = html`<${Button} intent="primary" minimal disabled=${false} onClick=${onClick} title="a ${"b"} c">Save ${"now"}<//>` as Made;
  assert.equal(one.type, Button);
  assert.deepEqual(one.props, { intent: "primary", minimal: true, disabled: false, onClick, title: "a b c" });
  assert.deepEqual(one.children, ["Save ", "now"]);
  const nested = html`
    <div class="x">
      <span>a</span>
      <${Button} />
      ${["p", "q"]}
    </div>` as Made;
  assert.equal(nested.type, "div");
  assert.deepEqual(nested.props, { className: "x" });
  assert.equal(nested.children.length, 3, "the layout of the template is not text");
  assert.deepEqual((nested.children[0] as Made).children, ["a"]);
  assert.equal((nested.children[1] as Made).type, Button);
  assert.deepEqual(nested.children[2], ["p", "q"]);
  const named = html`<label class="a" for="x" title="t">l</label>` as Made;
  assert.deepEqual(named.props, { className: "a", htmlFor: "x", title: "t" }, "a page element has the names of React");
  assert.deepEqual((html`<${Button} class="a" />` as Made).props, { class: "a" }, "a component is given what is written");
  const spread = html`<input ...${{ id: "i", type: "text" }} type="number" />` as Made;
  assert.deepEqual(spread.props, { id: "i", type: "number" });
  const fragment = html`<><b>1</b><i>2</i></>` as Made;
  assert.equal(fragment.type, "fragment");
  assert.equal(fragment.children.length, 2);
  const many = html`<a>1</a><b>2</b>` as Made[];
  assert.deepEqual(many.map(item => item.type), ["a", "b"]);
  assert.equal((html`<${Button}>x</${Button}>` as Made).type, Button, "a closing tag may name its component");
  assert.deepEqual((html`<p>${"a"}<!-- ${"skipped"} -->${"b"}</p>` as Made).children, ["a", "b"], "holes in a comment keep their place");
  assert.equal(html`just text`, "just text");
});

test("html parses a template once and reads its values each time, and refuses what is not well formed", () => {
  const template = (value: string) => html`<p title=${value}>${value}</p>` as Made;
  assert.deepEqual(template("a").props, { title: "a" });
  assert.deepEqual(template("b").children, ["b"]);
  for (const bad of [() => html`<p>`, () => html`</p>`, () => html`<p title="x>text</p>`, () => html`<!-- open`]) assert.throws(bad, HtmlTemplateError);
});
