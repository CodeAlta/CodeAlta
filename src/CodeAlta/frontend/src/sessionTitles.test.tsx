import assert from "node:assert/strict";
import test from "node:test";
import { createElement, type ReactElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { locales, messages, translate } from "./localization";
import { AttributionText, SessionSettings } from "./SessionSettings";
import { attributionSaved } from "./attributionText";
import { sessionTitleChoice, sessionTitleModes } from "./sessionTitles";
import { ShellLanguageContext } from "./shellLanguage";

const never = () => assert.fail("rendering must not act");
// The host is asked nothing while the page is drawn.
const silent = () => new Promise<never>(() => { });
const render = (element: ReactElement) =>
  renderToStaticMarkup(createElement(ShellLanguageContext.Provider, { value: { locale: "en", choice: "en", setLanguage: never } }, element));

test("the three title modes are the ones of the configuration file, each with its name and its line in every language", () => {
  assert.deepEqual(sessionTitleModes.map(mode => mode.value), ["first_prompt", "last_message", "model"]);
  for (const mode of sessionTitleModes) {
    assert.equal(sessionTitleChoice(mode.value), mode);
    for (const key of [mode.label, mode.help]) {
      assert.ok(key in messages, key);
      for (const locale of locales) assert.ok(translate(locale, key).trim(), `${locale}: ${key}`);
    }
  }
  // What the page does not know is not shown as another mode.
  for (const unknown of ["summary", "", null, undefined]) assert.equal(sessionTitleChoice(unknown), null);
});

test("the settings of the sessions are read before they are shown", () => {
  const html = render(createElement(SessionSettings, { epoch: "e", api: { sessionTitles: silent, saveSessionTitles: never, reviewSettings: silent, saveReviewSettings: never,
    attributionSettings: silent, saveAttributionSettings: never, promptSuggestionSettings: silent, savePromptSuggestionSettings: never } }));
  assert.match(html, /<span class="eyebrow">Agent &amp; models<\/span><h1>Sessions<\/h1>/);
  assert.match(html, /How CodeAlta titles your sessions, reviews their changes and shows them in tabs\./);
  assert.match(html, /Loading…/);
  assert.equal(html.includes("settings-session-title-mode"), false);
  assert.equal(html.includes("settings-prompt-suggestions"), false);
});

test("the card of the prompt suggestions is named and explained in every language", () => {
  for (const key of ["Prompt suggestions", "Suggest the next prompt", "After a turn, the model suggests what you may write next, in the empty prompt: Tab or → takes it."] as const) {
    assert.ok(key in messages, key);
    for (const locale of locales) assert.ok(translate(locale, key).trim(), `${locale}: ${key}`);
  }
});

test("the cards of the attribution are named and explained in every language, and their texts start as the ones of CodeAlta", () => {
  for (const key of ["Commits", "Add CodeAlta as co-author", "Co-author line", "The trailer agents add, such as Co-authored-by: Name <email>. Empty for the one of CodeAlta.",
    "Comments and messages", "Say that CodeAlta wrote it", "When an agent writes a pull request comment, an issue, a chat message or an email for you, it ends the text with a line saying that it comes from CodeAlta.",
    "Instruction to agents", "What agents are told about that line, in Markdown. Its line breaks become spaces.", "Use CodeAlta's text", "At most {count} characters."] as const) {
    assert.ok(key in messages, key);
    for (const locale of locales) assert.ok(translate(locale, key).trim(), `${locale}: ${key}`);
  }
  // The field starts with the text agents get, the one of CodeAlta until the user writes another, to be tweaked; only a
  // text of the user has the button that goes back to the one of CodeAlta.
  const codeAlta = "Co-authored-by: CodeAlta <noreply@codealta.io>";
  const field = (value: string) => render(createElement(AttributionText, { id: "f", value, defaultValue: codeAlta, maxLength: 200, disabled: false, onSave: never }));
  const own = field(codeAlta);
  assert.match(own, /<input[^>]*value="Co-authored-by: CodeAlta &lt;noreply@codealta.io&gt;"/);
  assert.equal(own.includes("Use CodeAlta&#x27;s text"), false);
  const tweaked = field("Assisted-by: Contoso");
  assert.match(tweaked, /<input[^>]*value="Assisted-by: Contoso"/);
  assert.match(tweaked, /aria-label="Use CodeAlta&#x27;s text"/);
  // What is saved is on one line, and the text of CodeAlta, or no text, saves none of the user.
  assert.equal(attributionSaved("  End with\n_Posted by the Contoso agent._  ", codeAlta), "End with _Posted by the Contoso agent._");
  assert.equal(attributionSaved(` ${codeAlta} `, codeAlta), "");
  assert.equal(attributionSaved("   ", codeAlta), "");
});
