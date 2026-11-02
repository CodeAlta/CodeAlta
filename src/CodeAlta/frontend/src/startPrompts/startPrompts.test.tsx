import assert from "node:assert/strict";
import test from "node:test";
import { createElement, type ReactElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import type { StartPromptItem } from "#neoastra";
import { ShellLanguageContext } from "../shellLanguage";
import { isStartPromptKey, reconcileStartPrompts, startPromptForm, startPromptKey, StartPromptDetail, StartPromptRows, unsavedStartPromptKey } from "./StartPrompts";

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

test("a list read again shows the text as it is now, and leaves the page alone when the user moved on", () => {
  const edited = { ...mine, content: "Changed on disk." };
  const team: StartPromptItem = { id: "pull-request-review", source: "project", overridden: false, content: "The team's.", file: "/app/.alta/prompts/start/pull-request-review.md" };
  const builtInUnderMine = { ...shipped, overridden: true };
  const alone = { ...shipped, overridden: false };
  const state = (selected: string | null, wanted: string | null = null, projectChanged = false) => ({ selected, wanted, id: "pull-request-review", projectChanged });

  // A reload reads the shown text again: what changed on disk replaces the form and what Revert goes back to.
  assert.deepEqual(reconcileStartPrompts([builtInUnderMine, edited], state(startPromptKey(mine))),
    { key: startPromptKey(mine), form: startPromptForm(edited) });
  // A save shows what it wrote; a removal shows the text now in use.
  assert.deepEqual(reconcileStartPrompts([builtInUnderMine, mine], state(unsavedStartPromptKey, startPromptKey(mine))),
    { key: startPromptKey(mine), form: startPromptForm(mine) });
  assert.deepEqual(reconcileStartPrompts([alone], state(startPromptKey(mine), "")), { key: startPromptKey(alone), form: startPromptForm(alone) });
  // The text of another project is never kept: a project's text gone with its project gives the one in use.
  assert.deepEqual(reconcileStartPrompts([builtInUnderMine, mine], state(startPromptKey(team), null, true)), { key: startPromptKey(mine), form: startPromptForm(mine) });
  // A copy not saved yet stays while its project does, and gives way to the text in use in another project.
  assert.equal(reconcileStartPrompts([alone], state(unsavedStartPromptKey)), undefined);
  assert.deepEqual(reconcileStartPrompts([alone], state(unsavedStartPromptKey, null, true)), { key: startPromptKey(alone), form: startPromptForm(alone) });
  // The user went to an agent prompt or a new prompt while the list was read: nothing is taken from them.
  assert.equal(reconcileStartPrompts([builtInUnderMine, mine], state("Agent:Global\u0000default", startPromptKey(mine))), undefined);
  assert.equal(reconcileStartPrompts([builtInUnderMine, mine], state(null, startPromptKey(mine))), undefined);
});