import assert from "node:assert/strict";
import test from "node:test";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { locales, translate } from "./localization";
import { PromptTags } from "./PromptTags";
import { ShellLanguageContext } from "./shellLanguage";

const never = () => assert.fail("rendering must not act");
const render = (scope: string, shadowed = false, locale: typeof locales[number] = "en") => renderToStaticMarkup(
  createElement(ShellLanguageContext.Provider, { value: { locale, choice: locale, setLanguage: never } }, createElement(PromptTags, { prompt: { scope, shadowed } })));

test("a prompt says where it is kept, and a custom agent of GitHub Copilot carries the mark of Copilot", () => {
  for (const locale of locales) {
    for (const [scope, label] of [["BuiltIn", "Built-in"], ["Global", "Global"], ["Project", "Project"]] as const) {
      const html = render(scope, false, locale);
      assert.ok(html.includes(`>${translate(locale, label)}<`) && !html.includes("copilot-tag"), html);
    }
    for (const [scope, label] of [["CopilotGlobal", "Global"], ["CopilotProject", "Project"]] as const) {
      const html = render(scope, false, locale);
      assert.ok(html.includes(`>${translate(locale, label)}<`) && html.includes("copilot-tag") && html.includes(">Copilot<"), html);
      assert.ok(html.includes(`title="${translate(locale, "From the GitHub Copilot layout")}"`), html);
    }
  }
});

test("a prompt that another one replaces says so, a custom agent of GitHub Copilot too", () => {
  assert.ok(!render("CopilotProject").includes(translate("en", "Overridden")));
  for (const scope of ["BuiltIn", "CopilotGlobal", "CopilotProject"]) assert.ok(render(scope, true).includes(`>${translate("en", "Overridden")}<`));
});
