import { useEffect, useState, useSyncExternalStore } from "react";
import type { SkillsCapture, SkillsInspection } from "./skillsInspection";
import { useShellLanguage } from "./shellLanguage";

export function SkillsInspectionPanel({ owner, capture }: { owner: SkillsInspection; capture: () => SkillsCapture | null }) {
  const { t } = useShellLanguage();
  const state = useSyncExternalStore(owner.subscribe, owner.getSnapshot);
  const [root, setRoot] = useState("");
  const [search, setSearch] = useState("");
  const [selected, setSelected] = useState<string | null>(null);
  useEffect(() => () => owner.invalidate(), [owner]);
  const available = capture();
  const rows = (available ? state.rows : []).filter(row => `${row.relativePath ?? ""} ${row.name ?? ""} ${row.description ?? ""} ${row.status}`.toLowerCase().includes(search.toLowerCase()));
  const detail = rows.find(row => row.id === selected);
  return <section className="skills-inspection" aria-label={t("Raw skill candidates")}>
    <h2>{t("Skills — raw file candidates")}</h2>
    <p>{t("This explicit read-only inspection is not loaded, active, enabled or effective skills. Ignored paths may appear. No ignore, trust, shadowing or activation decision is made.")}</p>
    <p>{t("One root per action: up to 257 encountered entries, 64 directories, 16 candidates and 4 bounded metadata reads. Five-second cooperative deadline; OS calls may block longer. No automatic refresh.")}</p>
    {!available && <p role="status">{t("Unavailable: select a verified, nonarchived saved session in an owned host. Catalog-only and unknown scopes cannot scan.")}</p>}
    {available && <p>{t("Verification scope: {scope}; saved session {session}. Root ownership is rechecked by the host on Scan.", { scope: available.target.scope, session: available.target.sessionId })}</p>}
    <label>{t("Source root")} <select aria-label={t("Skill source root")} value={root} onChange={event => { owner.invalidate(); setRoot(event.target.value); setSelected(null); }}>
      <option value="">{t("Choose a supported root")}</option>
      <option value="project_alta" disabled={available?.target.scope !== "project"}>{t("Project CodeAlta — .alta/skills")}</option>
      <option value="user_alta">{t("User CodeAlta — host catalog root/skills")}</option>
    </select></label>
    <button type="button" disabled={!available || !root || state.busy || root === "project_alta" && available.target.scope !== "project"}
      onClick={() => { const original = capture(); if (original) { setSelected(null); void owner.scan({ ...original.target, rootKind: root }, original.current, original.capability); } }}>{t("Scan raw candidates")}</button>
    <p role="status">{state.message}</p>
    {state.source && available && <p>{t("Source scope: {source}. Point-in-time raw subset only.", { source: t(state.source === "project_alta" ? "Project CodeAlta (.alta/skills)" : "User CodeAlta (host catalog root/skills)") })}</p>}
    <label>{t("Search observed candidates")} <input aria-label={t("Search raw skill candidates")} maxLength={128} value={search}
      onChange={event => { if (state.busy) owner.invalidate(); setSearch(event.target.value.slice(0, 128)); setSelected(null); }} /></label>
    <ul>{rows.map(row => <li key={row.id}><button type="button" aria-pressed={selected === row.id} onClick={() => setSelected(row.id)}>
      {row.name ?? row.relativePath ?? t("Omitted path")} — {row.status}</button></li>)}</ul>
    {state.rows.length > 0 && !rows.length && <p>{t("No local search matches. No filesystem read performed.")}</p>}
    {detail && <section aria-label={t("Selected raw candidate metadata")}><h3>{detail.name ?? t("Unparsed candidate")}</h3>
      <dl><dt>{t("Relative path")}</dt><dd>{detail.relativePath ?? t("Omitted")}</dd><dt>{t("Metadata status")}</dt><dd>{detail.status}</dd>
        <dt>{t("Diagnostic")}</dt><dd>{detail.diagnostic}</dd><dt>{t("Description")}</dt><dd>{detail.description ?? t("Unknown — no parsed metadata released")}</dd></dl>
      <p>{t("Not a usable/activatable skill determination. Source body and related files are not returned.")}</p></section>}
  </section>;
}
