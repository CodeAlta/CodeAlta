import assert from "node:assert/strict";
import test from "node:test";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import type { AutomationAutonomyItem } from "#neoastra";
import { locales, translate } from "../localization";
import { ShellLanguageContext } from "../shellLanguage";
import { AutonomyFields } from "./AutonomyFields";
import { newAutonomy } from "./automations";

const never = () => assert.fail("rendering must not act");
const fields = (autonomy: AutomationAutonomyItem | null, permissionMode: string | null = null, locale: typeof locales[number] = "en") =>
  renderToStaticMarkup(createElement(ShellLanguageContext.Provider, { value: { locale, choice: locale, setLanguage: never } },
    createElement(AutonomyFields, { autonomy, permissionMode, disabled: false, onChange: never })));

test("off, the autonomy of an automation is one switch; on, it says what its runs do and asks the rest", () => {
  const off = fields(null);
  assert.match(off, /Decide what its runs do without a person/);
  assert.doesNotMatch(off, /automation-allowed-tools|Approvals go to/);

  const on = fields({ ...newAutonomy(), allowedTools: ["jira_comment"], maxCallsPerHour: 5 });
  assert.match(on, /Its runs read without asking\./);
  assert.match(on, /id="automation-allowed-tools"[^>]*value="jira_comment"/);
  assert.match(on, /id="automation-max-calls"[^>]*value="5"/);
  assert.match(on, /<option value="stop" selected="">then stop the run<\/option><option value="wait">then keep waiting<\/option>/);
  // The approvals follow the owner of the automation, a team on a server.
  assert.match(on, /<option value="owner" selected="">Its owner<\/option><option value="oncall">The person on call of its team<\/option>/);
  assert.match(on, /Its owner: you, or on your server the team that owns it/);
  assert.match(fields({ ...newAutonomy(), approvers: "oncall" }), /takes turns on call; without a rotation, its whole team answers/);
  assert.match(fields({ ...newAutonomy(), approvers: "oncall", onCall: { source: "pagerduty", schedule: "PSCHED", token: "${secret:PD}", region: null } }),
    /Who is on call comes from the schedule PSCHED of PagerDuty/);
  assert.match(on, /It calls jira_comment by itself; it asks you for anything else\./);
  assert.match(on, /Pins, bounds and preconditions are written in its configuration file/);
  const pinned = fields({ ...newAutonomy(), allowedTools: ["k8s_restart_pod"], tools: [{ name: "k8s_restart_pod", version: "1.4.2", hash: null, bounds: [], preconditions: [] }] });
  assert.match(pinned, /<code>k8s_restart_pod<\/code> pinned to 1\.4\.2/);
  assert.doesNotMatch(on, /role="alert"/);
});

test("a permission mode that allows everything is pointed out, in every language", () => {
  assert.match(fields(newAutonomy(), "bypassPermissions"), /role="alert"[\s\S]*Its runs need a permission mode that asks/);
  assert.doesNotMatch(fields(null, "bypassPermissions"), /role="alert"/);
  for (const locale of locales) {
    const html = fields(newAutonomy(), "bypassPermissions", locale);
    assert.ok(html.includes(translate(locale, "Read only: it asks you before anything else than reading.")) && html.includes(translate(locale, "Tools it calls by itself")), locale);
  }
});
