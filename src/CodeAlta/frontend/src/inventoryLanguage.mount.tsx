import { useLayoutEffect, useState, type ReactNode } from "react";
import { ShellLanguageContext } from "./shellLanguage";
import type { Locale } from "./localization";

// Change context without recreating fixture transport callbacks or operation props.
export function InventoryLanguageFixture({ children }: { children: ReactNode }) {
  const [locale, setLocale] = useState<Locale>("en");
  useLayoutEffect(() => { Object.assign(window, { inventoryLanguage: setLocale, workflowLanguage: setLocale }); }, []);
  return <ShellLanguageContext value={{ locale, choice: locale, setLanguage: () => {} }}>{children}</ShellLanguageContext>;
}
