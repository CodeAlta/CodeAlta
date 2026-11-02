import assert from "node:assert/strict";
import test from "node:test";
import { createElement, type ReactElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import type { StartPromptItem } from "#neoastra";
import { ShellLanguageContext } from "../shellLanguage";
import { isStartPromptKey, startPromptForm, startPromptKey, StartPromptDetail, StartPromptRows } from "./StartPrompts";

const never = () => assert.fail("rendering must not act");
const render = (element: ReactElement) => renderToStaticMarkup(createElement(ShellLanguageContext.Provider, { value: { locale: "en", choice: "en", setLanguage: never } }, element));
const shipped: StartPromptItem = { id: "pull-request-review", source: "builtin", overridden: true, content: "Review the pull request {reference} of {location} on {service}.", file: null };
const mine: StartPromptItem = { id: "pull-request-review", source: "global", overridden: false, content: "My review of {reference}.", file: "/home/me/.alta/prompts/start/pull-request-review.md" };
const files = { open: undefined, reveal: never, platform: null };
const editor = ({ value, readOnly }: { value: string; readOnly: boolean }) => createElement("textarea", { readOnly, value, onChange: never });
const detail = (form: ReturnType<typeof startPromptForm>, builtIn: StartPromptItem | null) => render(createElement(StartPromptDetail, {
  form, builtIn, project: { id: "p", name: "Alpha" }, files, editor, disabled: false, onCopy: never, onChange: never, onSave: never, onCancel: never, onRemove: never }));

test("a start prompt has a key of its own beside the agent prompts, and a form that keeps what ships apart", () => {
  assert.ok(isStartPromptKey(startPromptKey(mine)));
  assert.notEqual(startPromptKey(shipped), startPromptKey(mine));
  assert.ok(!isStartPromptKey("Agent:Global\u0000default") && !isStartPromptKey(null));
  assert.deepEqual(startPromptForm(mine), { original: mine, id: "pull-request-review", scope: "Global", content: "My review of {reference}." });
  // What ships is not a file to edit: its form is the one of a copy, kept by the user unless the project is chosen.
  assert.deepEqual(startPromptForm(shipped), { original: null, id: "pull-request-review", scope: "Global", content: shipped.content });
  assert.deepEqual(startPromptForm(shipped, "Project"), { original: null, id: "pull-request-review", scope: "Project", content: shipped.content });
});

test("the rows name the prompt, say where each text is kept, and which one a nearer file replaces", () => {
  assert.equal(render(createElement(StartPromptRows, { items: [], selected: null, unsaved: null, onChoose: never })), "");
  const html = render(createElement(StartPromptRows, { items: [shipped, mine], selected: startPromptKey(mine), unsaved: null, onChoose: never }));
  assert.match(html, /<h3 class="settings-editor-group">Start prompts<\/h3>/);
  assert.equal(html.split("<strong>Pull request review</strong>").length - 1, 2);
  assert.ok(html.includes("Overridden") && html.includes("Built-in") && html.includes("Global") && html.includes("pull-request-review.md"));
  assert.equal(html.split('aria-current="true"').length - 1, 1, "the selected text is the user's");
  assert.ok(render(createElement(StartPromptRows, { items: [shipped], selected: null, unsaved: startPromptForm(shipped, "Global"), onChoose: never })).includes("Unsaved changes"));
});

test("what ships is only read and copied; a text of the user is saved, reverted or removed", () => {
  const builtIn = detail(startPromptForm(shipped), shipped);
  assert.ok(builtIn.includes("What a session is told first when you start the review of a pull request from the Issues tab."));
  assert.ok(builtIn.includes("{reference}, {location} and {service} are replaced by the number of the pull request"), "the placeholders are named as they are written");
  assert.ok(builtIn.includes("Customize for me") && builtIn.includes("Customize for Alpha") && !builtIn.includes(">Save<"));

  const own = detail(startPromptForm(mine), null);
  assert.ok(own.includes("/home/me/.alta/prompts/start/pull-request-review.md") && own.includes(">Save<") && own.includes(">Revert<") && own.includes(">Remove<"));
  assert.ok(!own.includes("Customize for me") && !own.includes("Stored in"));

  const copy = detail(startPromptForm(shipped, "Global"), null);
  assert.ok(copy.includes("Not saved yet") && copy.includes("Stored in") && copy.includes(">Cancel<") && !copy.includes(">Remove<"));
});
