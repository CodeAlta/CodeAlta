import assert from "node:assert/strict";
import test from "node:test";
import { environmentRows, environmentRowsText, secretOfValue } from "./ProviderEnvironment";
import { environmentEntries, environmentProblem, environmentText } from "./pluginProviders/pluginProviderForm";

test("the rows of an environment are its lines, and write the text they were read from", () => {
  const text = environmentText([{ name: "CLAUDE_CODE_OAUTH_TOKEN", value: "${secret:CLAUDE_TOKEN}" }, { name: "HTTPS_PROXY", value: "http://proxy:8080/?a=b" }, { name: "EMPTY", value: "" }]);
  const rows = environmentRows(text);
  assert.deepEqual(rows, [{ name: "CLAUDE_CODE_OAUTH_TOKEN", value: "${secret:CLAUDE_TOKEN}" }, { name: "HTTPS_PROXY", value: "http://proxy:8080/?a=b" }, { name: "EMPTY", value: "" }]);
  assert.equal(environmentRowsText(rows), text, "rows left as they were read do not change the form");
  assert.deepEqual(environmentRows("NAME\r\n\r\nOTHER=1"), [{ name: "NAME", value: "" }, { name: "OTHER", value: "1" }], "a line without = is a name, a blank line is nothing");
});

test("a row left empty is not written, and a value without a name is a problem of the form", () => {
  assert.equal(environmentRowsText([{ name: "A", value: "1" }, { name: "", value: "" }, { name: " B ", value: " 2 " }]), "A=1\nB= 2 ", "the name is trimmed, the value is kept");
  const nameless = environmentRowsText([{ name: "", value: "token" }]);
  assert.equal(nameless, "=token");
  assert.equal(environmentProblem(nameless), "A line of the environment has no name before =.");
  assert.deepEqual(environmentEntries(environmentRowsText([{ name: "A", value: "1" }])), [{ name: "A", value: "1" }]);
});

test("a value that is one reference to a secret names it", () => {
  assert.equal(secretOfValue("${secret:CLAUDE_TOKEN}"), "CLAUDE_TOKEN");
  assert.equal(secretOfValue("  ${secret:CLAUDE_TOKEN} "), "CLAUDE_TOKEN");
  assert.equal(secretOfValue("Bearer ${secret:CLAUDE_TOKEN}"), null, "a reference inside a longer value stays a text");
  assert.equal(secretOfValue("${var:REGION}"), null);
  assert.equal(secretOfValue("sk-ant-123"), null);
});
