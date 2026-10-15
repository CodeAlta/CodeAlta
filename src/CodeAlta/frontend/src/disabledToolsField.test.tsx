import assert from "node:assert/strict";
import test from "node:test";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { builtInToolNames, DisabledToolsChoice, DisabledToolsField, toolNameValid, withTool } from "./DisabledToolsField";
import { ShellLanguageContext } from "./shellLanguage";

const never = () => assert.fail("rendering must not act");
const render = (props: Parameters<typeof DisabledToolsField>[0]) => renderToStaticMarkup(createElement(ShellLanguageContext.Provider,
  { value: { locale: "en", choice: "en", setLanguage: never } }, createElement(DisabledToolsField, props)));

test("a tool is turned off once, by a name the host takes", () => {
  assert.deepEqual(withTool([], " shell_command "), ["shell_command"]);
  assert.deepEqual(withTool(["shell_command"], "shell_command"), ["shell_command"]);
  assert.deepEqual(withTool(["shell_command"], "mcp__docs__search"), ["shell_command", "mcp__docs__search"]);
  const tools = ["webget"];
  assert.equal(withTool(tools, "rm -rf"), tools, "what is no tool name changes nothing");
  assert.equal(toolNameValid("a".repeat(128)), true);
  assert.equal(toolNameValid("a".repeat(129)), false);
});

test("the field shows the tools turned off, those every session does without, and the button that adds one", () => {
  const html = render({ tools: ["shell_command"], inherited: ["webget", "shell_command"], label: "Tools turned off", onChange: never });
  assert.match(html, /<code>shell_command<\/code>/);
  assert.match(html, /data-inherited="true"[^>]*title="Turned off for every session"[\s\S]*<code>webget<\/code>/);
  assert.equal(html.match(/<code>shell_command<\/code>/g)!.length, 1, "a tool turned off in both places is shown once");
  assert.match(html, /Turn off a tool/);
  assert.match(render({ tools: [], label: "Tools turned off", onChange: never }), /None/);
});

test("the popover puts the field for any name above the list of known tools, which alone scrolls", () => {
  const choice = (offered: readonly string[]) => renderToStaticMarkup(createElement(ShellLanguageContext.Provider,
    { value: { locale: "en", choice: "en", setLanguage: never } }, createElement(DisabledToolsChoice, { offered, onAdd: never })));
  const html = choice(builtInToolNames);
  const field = html.indexOf('placeholder="Another tool"'), list = html.indexOf('class="disabled-tools-menu');
  assert.ok(field >= 0 && list > field, html);
  assert.equal(html.match(/<code>/g)!.length, builtInToolNames.length);
  assert.ok(!choice([]).includes("disabled-tools-menu"), "with every tool turned off there is the field alone");
});
