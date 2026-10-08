// The Appearance card alone, in a frame of a chosen width: what the Settings window gives it at its sizes.
import { useState } from "react";
import { createRoot } from "react-dom/client";
import { createAppearancePreview } from "./appearancePreview";
import type { ColorSchemeLibrary } from "./colorSchemeLibrary";
import { colorSchemeOf, defaultColorScheme } from "./colorSchemes";
import { GeneralSettings } from "./GeneralSettings";
import type { Locale } from "./localization";
import { ShellLanguageContext } from "./shellLanguage";

const library: ColorSchemeLibrary = { status: "ok", directory: null, problems: [], reload: async () => { }, reveal: async () => ({ status: "unavailable", message: null }),
  save: async () => ({ status: "unavailable", id: null, message: null }), remove: async () => ({ status: "unavailable", message: null }) };

function Fixture() {
  const [locale, setLocale] = useState<Locale>("en");
  const [width, setWidth] = useState(900);
  const [preview] = useState(createAppearancePreview);
  Object.assign(window, { appearanceLayout: { setLocale, setWidth } });
  return <ShellLanguageContext.Provider value={{ locale, choice: locale, setLanguage: value => setLocale(value as Locale) }}>
    <div className="settings-card-page" style={{ width }}>
      <GeneralSettings theme="dark" setTheme={() => { }} darker={false} setDarker={() => { }} sort="name" setSort={() => { }}
        desktopCollapsed={false} setDesktopCollapsed={() => { }} recentSessionCount={6} setRecentSessionCount={() => { }} notices={{}}
        closing={{ behavior: "ask", platform: "windows", set: () => { } }}
        schemes={{ colorScheme: defaultColorScheme, setColorScheme: () => { }, shownScheme: colorSchemeOf(defaultColorScheme), variant: "dark", customSchemes: [],
          library, preview, platform: "windows" }} />
    </div>
  </ShellLanguageContext.Provider>;
}
document.documentElement.classList.add("bp6-dark");
createRoot(document.getElementById("root")!).render(<Fixture />);
