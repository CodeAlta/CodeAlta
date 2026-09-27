import { createContext, useContext, useEffect, useState } from "react";
import { languageStorageKey, readLanguage, resolveLocale, saveLanguage, translate, type LanguageChoice, type Locale, type PreferenceIssue, type MessageKey } from "./localization";

type LanguageState = Readonly<{ locale: Locale; choice: LanguageChoice; issue?: PreferenceIssue; setLanguage: (choice: string) => void }>;
export const ShellLanguageContext = createContext<LanguageState>({ locale: "en", choice: "en", setLanguage: () => {} });
export function useShellLanguage() {
  const value = useContext(ShellLanguageContext);
  return { ...value, t: (key: MessageKey, parameters?: Readonly<Record<string, string | number>>) => translate(value.locale, key, parameters) };
}
// Called once by App. Locale updates do not replace/key any owner or workspace subtree.
export function useLanguagePreference(): LanguageState {
  const [saved, setSaved] = useState(() => readLanguage(() => localStorage.getItem(languageStorageKey)));
  const [browserLanguages] = useState(() => typeof navigator === "undefined" ? [] : [...navigator.languages].slice(0, 8));
  const locale = resolveLocale(saved.choice, browserLanguages);
  useEffect(() => { document.documentElement.lang = locale; }, [locale]);
  return { ...saved, locale, setLanguage: value => {
    const next = saveLanguage(value, choice => localStorage.setItem(languageStorageKey, choice));
    if (next) setSaved(next);
  } };
}
