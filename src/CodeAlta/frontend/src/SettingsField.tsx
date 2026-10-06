import type { ReactNode } from "react";

/** One preference per row: its name on the left, its control on the right, and what belongs to it below (a notice, an editor). */
export function SettingsField({ label, htmlFor, notice, children }: { label: string; htmlFor?: string; notice?: ReactNode; children: ReactNode }) {
  return <div className="settings-field">
    {htmlFor ? <label htmlFor={htmlFor}>{label}</label> : <span className="settings-field-label">{label}</span>}
    <div className="settings-field-control">{children}</div>
    {notice}
  </div>;
}
