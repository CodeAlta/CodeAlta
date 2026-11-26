import assert from "node:assert/strict";
import test from "node:test";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import type { Locale } from "./localization";
import { ShellLanguageContext } from "./shellLanguage";
import { UnreadMark } from "./UnreadMark";

const never = () => assert.fail("rendering changes no language");
const render = (unread: boolean, locale: Locale = "en") => renderToStaticMarkup(createElement(ShellLanguageContext.Provider,
  { value: { locale, choice: locale, setLanguage: never } }, createElement(UnreadMark, { unread })));

test("a session whose turn ended unseen has a dot that says so to the eye and to a screen reader", () => {
  assert.equal(render(true), '<span class="session-unread" role="img" aria-label="New since you last looked" title="New since you last looked"></span>');
  assert.match(render(true, "fr"), /aria-label="Du nouveau depuis votre dernier passage"/);
  // A session that has nothing new: nothing.
  assert.equal(render(false), "");
});
