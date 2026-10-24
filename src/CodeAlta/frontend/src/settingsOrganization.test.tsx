import assert from "node:assert/strict";
import test from "node:test";
import { renderToStaticMarkup } from "react-dom/server";
import { AppearanceSettings } from "./AppearanceSettings";
import { createAppearancePreview } from "./appearancePreview";
import type { ColorSchemeLibrary } from "./colorSchemeLibrary";
import { colorSchemeOf, defaultColorScheme } from "./colorSchemes";
import { GeneralSettings } from "./GeneralSettings";
import { PermissionSettings } from "./PermissionSettings";

const library: ColorSchemeLibrary = { status: "ok", directory: null, problems: [], reload: async () => {},
  reveal: async () => ({ status: "unavailable", message: null }), save: async () => ({ status: "unavailable", id: null, message: null }),
  remove: async () => ({ status: "unavailable", message: null }) };
const noop = () => {};

test("Appearance, General and Permissions preferences each have exactly one home", () => {
  const appearance = renderToStaticMarkup(<AppearanceSettings theme="dark" setTheme={noop} darker setDarker={noop}
    sort="recent" setSort={noop} desktopCollapsed setDesktopCollapsed={noop} recentSessionCount={17} setRecentSessionCount={noop}
    subAgentCount={9} setSubAgentCount={noop} sessionWidth={75} setSessionWidth={noop} notices={{}}
    schemes={{ colorScheme: defaultColorScheme, setColorScheme: noop, shownScheme: colorSchemeOf(defaultColorScheme), variant: "darker",
      customSchemes: [], library, preview: createAppearancePreview(), platform: null }} />);
  const general = renderToStaticMarkup(<GeneralSettings remoteControl={{ reconnect: true, set: noop }}
    closing={{ behavior: "keep", platform: "windows", set: noop }} keepAwake={{ on: true, set: noop }} />);
  const permissions = renderToStaticMarkup(<PermissionSettings permissions={null} confirms={{ sessionDelete: false, projectArchive: false, pluginStop: false, set: noop }} />);
  const homes = [
    [appearance, ["Theme", "Darker dark theme", "Color scheme", "Sort projects", "Recent session display count (1–50)",
      "Sub-agent display count (1–50)", "Collapse desktop project rail", "Width of the conversation", "Entry appearance", "Changes are shown", "Code font", "Prompt font"]],
    [general, ["Language", "Show the welcome page at startup", "Animate the welcome page", "Turn Remote Control back on when CodeAlta starts", "When the window is closed",
      "Keep computer awake when CodeAlta is running"]],
    [permissions, ["Ask before deleting a session", "Ask before archiving a project"]],
  ] as const;
  for (const [home, labels] of homes) for (const label of labels) {
    const field = new RegExp(`(?:<label|<span class="settings-field-label")[^>]*>${label.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")}<`, "g");
    assert.equal([...home.matchAll(field)].length, 1, label);
    for (const [other] of homes) if (home !== other) assert.equal([...other.matchAll(field)].length, 0, label);
  }
  for (const title of ["Theme and colors", "Fonts", "Explorer", "Conversation and changes"]) assert.ok(appearance.includes(`>${title}</h2>`));
  for (const title of ["Language", "Welcome page", "Startup and closing"]) assert.ok(general.includes(`>${title}</h2>`));
  assert.match(permissions, /Agent permissions<\/h2>/);
  assert.match(permissions, /Confirmations<\/h2>/);
  assert.match(permissions.match(/<select[^>]*id="settings-permission-mode"[^>]*>/)![0], /disabled/);
  for (const id of ["settings-confirm-session-delete", "settings-confirm-project-archive"]) {
    assert.match(permissions, new RegExp(`id="${id}"`));
    assert.doesNotMatch(permissions.match(new RegExp(`<input[^>]*id="${id}"[^>]*>`))![0], /disabled/);
  }
});

test("General leaves unavailable shell controls out without hiding local welcome preferences", () => {
  const general = renderToStaticMarkup(<GeneralSettings remoteControl={null} closing={null} keepAwake={null} />);
  assert.doesNotMatch(general, /settings-reconnect-remote-control|settings-on-close|settings-keep-awake/);
  assert.match(general, /settings-language/);
  assert.match(general, /settings-landing-startup/);
  assert.match(general, /settings-landing-animation/);
});
