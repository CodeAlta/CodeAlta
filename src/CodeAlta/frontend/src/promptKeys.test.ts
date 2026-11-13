import assert from "node:assert/strict";
import test from "node:test";
import { locales, messages, translate } from "./localization";
import { promptKeysHint } from "./promptKeys";

test("the hint of an empty prompt names its five keys in every language", () => {
  assert.ok(promptKeysHint in messages);
  for (const locale of locales) {
    const hint = translate(locale, promptKeysHint);
    for (const key of ["?", "/", "!", "@", "#"]) assert.ok(hint.includes(key), `${locale}: ${key} in ${hint}`);
  }
});
