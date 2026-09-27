import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import * as ts from "typescript";
import { captureReferenceInput, closeReferencePopup, createReferencePopupLifetime, createReferenceSearchFence, referencePopupReadiness, referencePopupKey, validReferenceSearch, type ReferenceInputState } from "./referencePopup";
import type { SessionReferenceSearchResponse } from "#neoastra";

function fixture() {
  let state = { generation: 4, revision: 8, key: '["epoch","project","session","/path"]', available: true };
  let input: ReferenceInputState = { node: {}, connected: true, disabled: false, text: "@src/old.cs:2-4", start: 4, end: 4, revision: 3 };
  const lifetime = createReferencePopupLifetime(() => state);
  const capture = captureReferenceInput(input, () => input, lifetime)!;
  return { lifetime, capture, get input() { return input; }, set input(value) { input = value; },
    get state() { return state; }, set state(value) { state = value; }, tick: () => { state = { ...state, generation: state.generation + 1 }; } };
}

test("IME query renders preserve durable review while fencing reads, edits and focus", () => {
  const f = fixture();
  assert.equal(f.lifetime.open(f.tick), true);
  let composing = false, failed = false, query = "old", reads = 0, inserts = 0, focuses = 0;
  const gate = referencePopupReadiness(() => f.capture.current(), () => composing);
  const fence = createReferenceSearchFence();
  const render = () => { if (!failed && !gate.current()) { f.lifetime.retire(); failed = true; } };
  const read = () => { if (failed || !gate.ready()) return null; reads++; return { query, valid: fence.capture(gate.ready) }; };
  const old = read()!;
  composing = true; fence.cancel();
  query = "settled"; render();
  assert.equal(read(), null);
  if (gate.ready()) { inserts++; focuses++; }
  assert.deepEqual([reads, inserts, focuses], [1, 0, 0]);
  const closing = fixture();
  assert.equal(closing.lifetime.open(closing.tick), true);
  const closeGate = referencePopupReadiness(closing.capture.current, () => composing);
  if (closeReferencePopup(closing.lifetime, closeGate.ready, closing.tick)) { inserts++; focuses++; }
  assert.deepEqual([inserts, focuses], [0, 0], "close cannot authorize an edit or explicit focus during IME");
  assert.equal(old.valid(), false);
  assert.equal(failed, false, "composition/query render must not permanently retire review");
  composing = false; render();
  const settled = read()!;
  assert.equal(settled.query, "settled"); assert.equal(settled.valid(), true);
  assert.equal(old.valid(), false);
  f.input = { ...f.input, revision: f.input.revision + 1 }; render();
  assert.equal(failed, true); assert.equal(settled.valid(), false); assert.equal(read(), null);
});

test("permanent failure is wired to durable readiness, not IME action admission", () => {
  const source = readFileSync(new URL("./ProjectReferencePicker.tsx", import.meta.url), "utf8");
  assert.match(source, /function current\(value: Review\) \{ return readiness\(value\)\.current\(\); \}/);
  assert.match(source, /return referencePopupReadiness\(/);
  assert.match(source, /if \(review && !failed && !current\(review\)\) \{ abortRead\(\); review\.lifetime\.retire\(\); setFailed\(true\)/);
  assert.match(source, /const restore = ready\(value\)/);
  assert.match(source, /closeReferencePopup\(value\.lifetime, \(\) => ready\(value\)/);
  assert.match(source, /if \(!value \|\| !ready\(value\)/);
  assert.match(source, /if \(!review \|\| failed \|\| !ready\(review\)\) return/);
  assert.match(source, /searchFence\.capture\(\(\) => !abort\.signal\.aborted && ready\(review\)\)/);
  assert.match(source, /if \(!valid\(\)\) return;\s+const \{ observe/);
  assert.match(source, /if \(ready\(review\)\) search\.current\?\.focus\(\)/);
  assert.match(source, /!active\.current && !composing\.current && value\.lifetime\.current\(\)/);
});

test("capture precedes one own-modal handoff; query edits are separate and insertion preserves exact range once", () => {
  const f = fixture();
  assert.deepEqual(f.capture.span, { start: 0, end: 11, query: "src" });
  assert.equal(f.lifetime.open(f.tick), true);
  let query = f.capture.span.query; query = "other";
  assert.equal(query, "other"); assert.equal(f.input.text, "@src/old.cs:2-4");
  assert.deepEqual(f.capture.choose("src/a b.cs", false), { text: '@"src/a b.cs":2-4', caret: 13 });
  assert.equal(f.capture.choose("elsewhere", false), null);
  assert.equal(f.lifetime.close(f.tick), true);
  assert.equal(f.lifetime.current(), true);
  assert.equal(f.lifetime.open(f.tick), false);
  f.tick(); assert.equal(f.lifetime.current(), false);
});

test("missing own generation, nested modal transitions, scope/host/revision ABA and retirement fail closed", () => {
  for (const transition of [() => {}, (f: ReturnType<typeof fixture>) => { f.tick(); f.tick(); }]) {
    const f = fixture();
    assert.equal(f.lifetime.open(() => transition(f)), false);
    assert.equal(f.capture.choose("a", false), null);
  }
  for (const key of ["generation", "revision", "key", "available"] as const) {
    const f = fixture(); f.lifetime.open(f.tick);
    f.state = key === "key" ? { ...f.state, key: "other scope" } : key === "available" ? { ...f.state, available: false }
      : { ...f.state, [key]: f.state[key] + 1 };
    assert.equal(f.capture.current(), false);
    assert.equal(f.capture.choose("a", false), null);
  }
  const f = fixture(); f.lifetime.open(f.tick); f.tick(); f.tick();
  assert.equal(f.lifetime.current(), false);
  f.lifetime.retire(); assert.equal(f.lifetime.close(f.tick), false);
});

test("original node, caret, span, text, revision and connection fence selection including input ABA", () => {
  const variants: Partial<ReferenceInputState>[] = [{ node: {} }, { connected: false }, { disabled: true },
    { text: "changed" }, { start: 2 }, { end: 5 }, { revision: 5 }];
  for (const change of variants) {
    const f = fixture(); f.lifetime.open(f.tick); f.input = { ...f.input, ...change };
    assert.equal(f.capture.choose("a", false), null, JSON.stringify(change));
  }
  const f = fixture(); const original = f.input;
  f.input = { ...original, text: "changed", revision: 4 };
  f.input = { ...original, revision: 5 };
  assert.equal(f.capture.current(), false);
  assert.equal(captureReferenceInput({ ...original, end: 5 }, () => original, f.lifetime), null);
  f.lifetime.retire(); assert.equal(f.capture.choose("a", false), null);
});

test("close-time invalidation prevents insertion and focus admission; valid own close inserts exactly once", () => {
  for (const change of ["text", "selection", "revision", "node", "ownership", "modal", "ABA", "parent", "IME", "latestText", "identity"]) {
    const f = fixture(); f.lifetime.open(f.tick);
    let extraCurrent = true;
    let edits = 0; let focus = 0;
    const next = f.capture.choose("src/a b.cs", false)!;
    const allowed = closeReferencePopup(f.lifetime, () => f.capture.current() && extraCurrent, () => {
      f.tick();
      if (change === "text") f.input = { ...f.input, text: "changed" };
      else if (change === "selection") f.input = { ...f.input, start: 2, end: 3 };
      else if (change === "revision") f.input = { ...f.input, revision: f.input.revision + 1 };
      else if (change === "node") f.input = { ...f.input, node: {} };
      else if (change === "ownership") f.state = { ...f.state, available: false };
      else if (change === "modal") f.tick();
      else if (change === "ABA") {
        const before = f.input;
        f.input = { ...before, text: "changed", revision: before.revision + 1 };
        f.input = { ...before, revision: before.revision + 2 };
      } else extraCurrent = false;
    });
    if (allowed) { edits++; focus++; f.input = { ...f.input, text: next.text }; }
    assert.deepEqual({ allowed, edits, focus }, { allowed: false, edits: 0, focus: 0 }, change);
  }
  const f = fixture(); f.lifetime.open(f.tick);
  let edits = 0; let focus = 0;
  const next = f.capture.choose("src/a b.cs", false)!;
  if (closeReferencePopup(f.lifetime, f.capture.current, f.tick)) { edits++; focus++; f.input = { ...f.input, text: next.text }; }
  assert.deepEqual({ edits, focus, text: f.input.text }, { edits: 1, focus: 1, text: '@"src/a b.cs":2-4' });
  assert.equal(closeReferencePopup(f.lifetime, f.capture.current, f.tick), false);
  assert.equal(f.capture.choose("again", false), null);
});

test("bounded search observations reject wrong epoch, unsafe paths, duplicates and excess rows", () => {
  const page: SessionReferenceSearchResponse = { status: "ok", epoch: "e", omitted: false,
    items: [{ path: "src/a.ts", directory: false, recent: false }] };
  assert.equal(validReferenceSearch(page, "e"), true);
  assert.equal(validReferenceSearch({ ...page, status: "incomplete", omitted: true }, "e"), true);
  assert.equal(validReferenceSearch({ ...page, status: "read_error", omitted: true }, "e"), true);
  assert.equal(validReferenceSearch(page, "other"), false);
  assert.equal(validReferenceSearch({ ...page, items: [...page.items, ...page.items] }, "e"), false);
  assert.equal(validReferenceSearch({ ...page, items: Array.from({ length: 65 }, (_, i) => ({ path: `${i}`, directory: true, recent: true })) }, "e"), false);
  for (const path of ["../a", "/absolute", "a\\b", 'a"b', "a:b", "a\nfile"])
    assert.equal(validReferenceSearch({ ...page, items: [{ path, directory: false, recent: false }] }, "e"), false);
});

test("search query generations discard late replies after query ABA, scope retirement and cancel", () => {
  const f = fixture(); const search = createReferenceSearchFence();
  const first = search.capture(f.capture.current);
  const second = search.capture(f.capture.current);
  const third = search.capture(f.capture.current);
  assert.equal(first(), false); assert.equal(second(), false); assert.equal(third(), true);
  search.cancel(); assert.equal(third(), false);
  const pending = search.capture(f.capture.current); f.lifetime.retire(); assert.equal(pending(), false);
  assert.equal(f.input.text, "@src/old.cs:2-4");
});

test("popup keys consume only deliberate navigation/selection/cancel; IME and modified/repeated Enter never insert", () => {
  for (const [key, action] of [["ArrowDown", "next"], ["ArrowUp", "previous"], ["Enter", "choose"], ["Escape", "cancel"]]) {
    assert.equal(referencePopupKey({ key }), action);
    for (const flags of [{ isComposing: true }, { keyCode: 229 }, { repeat: true }, { ctrlKey: true },
      { altKey: true }, { metaKey: true }, { shiftKey: true }, { defaultPrevented: true }])
      assert.equal(referencePopupKey({ key, ...flags }), "none");
  }
  assert.equal(referencePopupKey({ key: "Tab" }), "none");
});

// Syntactic regression guard only: no alias resolution, computed-name evaluation or
// interprocedural analysis. Strings/comments must not be mistaken for API calls.
function forbiddenReferenceCalls(source: string) {
  const file = ts.createSourceFile("reference.tsx", source, ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX);
  const calls: string[] = [];
  function name(expression: ts.Expression): string | undefined {
    if (ts.isParenthesizedExpression(expression) || ts.isNonNullExpression(expression)
      || ts.isAsExpression(expression) || ts.isTypeAssertionExpression(expression)) return name(expression.expression);
    if (ts.isIdentifier(expression)) return expression.text;
    if (ts.isPropertyAccessExpression(expression)) return expression.name.text;
    if (ts.isElementAccessExpression(expression) && expression.argumentExpression
      && (ts.isStringLiteral(expression.argumentExpression) || ts.isNoSubstitutionTemplateLiteral(expression.argumentExpression)))
      return expression.argumentExpression.text;
    return undefined;
  }
  function visit(node: ts.Node) {
    if (ts.isCallExpression(node)) {
      const called = name(node.expression);
      if (called && /^(?:send|steer|readFile|readFileSync|upload|uploadFile|uploadFiles)$/u.test(called)) calls.push(node.getText(file));
    }
    ts.forEachChild(node, visit);
  }
  visit(file); return calls;
}

test("forbidden-call AST guard rejects direct/member/optional/literal-computed calls but permits help text", () => {
  for (const source of ["send()", "api.steer(request)", "fs.readFile(path)", "readFileSync(path)",
    "client?.upload?.(file)", "client['uploadFile'](file)", "client[`uploadFiles`](files)",
    "(client.send)(request)", "client.upload!(file)", "const x = <button onClick={() => api.send()} />"])
    assert.equal(forbiddenReferenceCalls(source).length, 1, source);
  for (const source of ['const help = "file contents are not uploaded; api.send() is forbidden";',
    "// upload(file)\nconst name = 'readFile';", "const x = <p>Do not send or upload files.</p>;",
    "sessionOperations.searchReferences(request); edit(next.text); insertProjectReference(text, 0, 1, path, false);",
    "const upload = () => {}; const choices = { send: false };", "api.uploadStatus();"])
    assert.deepEqual(forbiddenReferenceCalls(source), [], source);
});

test("native popup replaces permanent results; query reads exclude locale dependencies and keep budgets", () => {
  const source = readFileSync(new URL("./ProjectReferencePicker.tsx", import.meta.url), "utf8");
  assert.match(source, /<dialog ref=\{dialog\}/);
  assert.doesNotMatch(source, /<aside/);
  assert.match(source, /maxLength=\{256\}/);
  assert.match(source, /timeoutMilliseconds: 3000/);
  assert.match(source, /\}, 150\)/);
  assert.match(source, /\[review, query, failed, interaction\]/);
  assert.match(source, /captureReferenceInput\(original, readInput, lifetime\)/);
  assert.match(source, /review\.lifetime\.open\(\(\) => element\.showModal\(\)\)/);
  assert.match(source, /active\.current === review/);
  assert.deepEqual(forbiddenReferenceCalls(source), [], "reference picker must not invoke forbidden operations");
});
