// The Appearance card in a window as Settings opens it: the dialog of an AppWindow is shown after its content is mounted.
import { StrictMode, useState } from "react";
import { createRoot } from "react-dom/client";
import { createAppearancePreview } from "./appearancePreview";
import { AppWindow } from "./AppWindow";
import type { ColorSchemeLibrary } from "./colorSchemeLibrary";
import { colorSchemeOf, defaultColorScheme } from "./colorSchemes";
import { GeneralSettings } from "./GeneralSettings";
import type { Locale } from "./localization";
import { ShellLanguageContext } from "./shellLanguage";

const library: ColorSchemeLibrary = { status: "ok", directory: null, problems: [], reload: async () => { }, reveal: async () => ({ status: "unavailable", message: null }),
  save: async () => ({ status: "unavailable", id: null, message: null }), remove: async () => ({ status: "unavailable", message: null }) };
// Every width the setting was given, in order.
const changes: number[] = [];

function Fixture() {
  const [locale, setLocale] = useState<Locale>("en");
  const [width, setWidth] = useState(100);
  const [preview] = useState(createAppearancePreview);
  Object.assign(window, { sessionWidthSetting: { changes, setLocale } });
  return <ShellLanguageContext.Provider value={{ locale, choice: locale, setLanguage: value => setLocale(value as Locale) }}>
    <AppWindow storageKey="codealta.test.window.settings" className="settings-dialog" titleId="settings-title" title="Settings"
      preferredSize={viewport => ({ width: viewport.width * 0.8, height: viewport.height * 0.8 })} minimumSize={{ width: 300, height: 300 }} onClose={() => { }} closeLabel="Close">
      <div className="settings-dialog-body"><div className="settings-dialog-content"><div className="configuration-page settings-card-page"><div className="settings-grid">
        <GeneralSettings theme="dark" setTheme={() => { }} darker={false} setDarker={() => { }} sort="name" setSort={() => { }}
          desktopCollapsed={false} setDesktopCollapsed={() => { }} recentSessionCount={6} setRecentSessionCount={() => { }} subAgentCount={4} setSubAgentCount={() => { }} notices={{}}
          sessionWidth={width} setSessionWidth={value => { changes.push(value); setWidth(value); }}
          schemes={{ colorScheme: defaultColorScheme, setColorScheme: () => { }, shownScheme: colorSchemeOf(defaultColorScheme), variant: "dark", customSchemes: [],
            library, preview, platform: "windows" }} />
      </div></div></div></div>
    </AppWindow>
  </ShellLanguageContext.Provider>;
}
document.documentElement.classList.add("bp6-dark");
createRoot(document.getElementById("root")!).render(<StrictMode><Fixture /></StrictMode>);
