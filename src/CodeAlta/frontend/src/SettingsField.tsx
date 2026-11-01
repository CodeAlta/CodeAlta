import type { ReactNode } from "react";

/**
 * One preference per row: its name on the left, its control on the right, and what belongs to it below (a notice, an editor).
 * `setting` is its id in the catalog of the settings: the search, `Ctrl+P` and `alta settings open` find the row by it, and make it glow.
 */
export function SettingsField({ label, htmlFor, notice, setting, children }: { label: string; htmlFor?: string; notice?: ReactNode; setting?: string; children: ReactNode }) {
  return <div className="settings-field" data-setting={setting}>
    {htmlFor ? <label htmlFor={htmlFor}>{label}</label> : <span className="settings-field-label">{label}</span>}
    <div className="settings-field-control">{children}</div>
    {notice}
  </div>;
}
