import assert from "node:assert/strict";
import test from "node:test";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { translate } from "../localization";
import { ShellLanguageContext } from "../shellLanguage";
import { NoServerSecrets } from "./FleetSecretsSettings";

const never = () => assert.fail("rendering must not act");
const english = (key: Parameters<typeof translate>[1]) => translate("en", key).replaceAll(">", "&gt;");

test("without a server, the page sends to Settings > Secrets, where the values of this computer are", () => {
  const html = renderToStaticMarkup(createElement(ShellLanguageContext.Provider, { value: { locale: "en", choice: "en", setLanguage: never } },
    createElement(NoServerSecrets, { openSecrets: never })));
  assert.ok(html.includes(english("Not linked to a server.")), html);
  assert.ok(html.includes(english("The secrets and variables of this computer are in Settings > Secrets.")), html);
  assert.match(html, /<button[^>]*>.*Secrets.*<\/button>/s, "a button opens Settings > Secrets");
  assert.notEqual(translate("fr", "The secrets and variables of this computer are in Settings > Secrets."),
    "The secrets and variables of this computer are in Settings > Secrets.", "it is translated");
});
