import { HTMLSelect } from "@blueprintjs/core";
import { contextSizes, defaultContextSummary, type ContextModel } from "./contextSizes";
import { compactTokens } from "./contextUsage";
import { useShellLanguage } from "./shellLanguage";

/** Default is always available; discovery never turns an unsupported retained choice into an offered endpoint. */
export function ContextSizeSelect({ id, value, model, loading = false, disabled = false, onChange }: {
  id: string; value: number | null; model?: ContextModel | null; loading?: boolean; disabled?: boolean;
  onChange: (tokens: number | null) => void;
}) {
  const { t } = useShellLanguage();
  const sizes = loading ? [] : contextSizes(model);
  const unverified = value !== null && !sizes.includes(value);
  const effectiveDefault = loading ? null : defaultContextSummary(model);
  return <div className="context-size-control">
    <HTMLSelect id={id} fill aria-label={t("Context size")} aria-describedby={`${id}-notice`} value={value ?? ""} disabled={disabled}
      onChange={event => {
        const tokens = event.target.value === "" ? null : Number(event.target.value);
        if (tokens === null || sizes.includes(tokens)) onChange(tokens);
      }}>
      <option value="">{effectiveDefault ? t("Default ({value})", { value: effectiveDefault }) : t("Default")}</option>
      {sizes.map(tokens => <option key={tokens} value={tokens}>{compactTokens(String(tokens))}</option>)}
      {unverified && <option value={value} disabled>{compactTokens(String(value))} · {t("Unverified")}</option>}
    </HTMLSelect>
    <small id={`${id}-notice`} role="status">{t(loading ? "Loading compatible context sizes…" : !model || unverified
      ? "Compatible context sizes unavailable. Saved choices remain unverified."
      : "Context budget; output reservations depend on the provider.")}</small>
  </div>;
}
