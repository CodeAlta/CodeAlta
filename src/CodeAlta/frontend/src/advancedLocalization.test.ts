import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import ts from "typescript";
import { locales, messages, translate, type MessageKey } from "./localization";

test("advanced/recovery static source keys have frozen five-language rows and English fallback", () => {
  const keys = new Set<string>();
  for (const file of ["OwnedSessionPanel", "RetainedRequestStrip", "ArchivedActionRecovery", "ArchivedInteractionRecovery", "ReadOnlyComposer"]) {
    const source = ts.createSourceFile(file, readFileSync(new URL(`./${file}.tsx`, import.meta.url), "utf8"), ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX);
    function literals(node: ts.Node): void {
      if (ts.isStringLiteral(node)) keys.add(node.text);
      else if (ts.isConditionalExpression(node)) { literals(node.whenTrue); literals(node.whenFalse); }
      else if (ts.isParenthesizedExpression(node)) literals(node.expression);
    }
    function visit(node: ts.Node): void {
      if (ts.isCallExpression(node) && node.expression.getText(source) === "t" && node.arguments[0]) literals(node.arguments[0]);
      ts.forEachChild(node, visit);
    }
    visit(source);
  }
  // A floor that proves the walk found the composer/recovery keys, not an exact inventory.
  assert.ok(keys.size > 100);
  for (const key of keys) {
    assert.ok(Object.hasOwn(messages, key), `missing translation row: ${key}`);
    const row = messages[key as MessageKey];
    assert.equal(row.length, 5, key);
    assert.ok(Object.isFrozen(row), key);
    for (const value of row) assert.ok(value.trim(), key);
  }
  for (const locale of locales) {
    for (const key of ["Advanced session controls and diagnostics", "Captured model", "Retry exact original Send Abort",
      "Archived interaction recovery", "Copy retained Queue text", "Original waiter pending"] as const) {
      if (locale === "en") assert.equal(translate(locale, key), key);
      else assert.notEqual(translate(locale, key), key);
    }
    for (const literal of ["Settings", "Unknown Ready Copy", "stale_epoch", "C:\\Settings\\Read-only", '{"selection":"Send"}']) {
      assert.ok(translate(locale, "Runtime observation unavailable ({code}).", { code: literal }).includes(literal));
      assert.ok(translate(locale, "Provider {provider}; model, prompt and reasoning not available without an owned runtime.", { provider: literal }).includes(literal));
    }
  }
});
