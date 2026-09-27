import type { Ref } from "react";
import { useShellLanguage } from "./shellLanguage";

export function ProjectRailToggle({ expanded, onToggle, buttonRef }: {
  expanded: boolean; onToggle: () => void; buttonRef: Ref<HTMLButtonElement>;
}) {
  const { t } = useShellLanguage();
  return <button ref={buttonRef} type="button" className="project-rail-toggle" aria-label={t(expanded ? "Hide projects" : "Show projects")}
    aria-controls="project-rail" aria-expanded={expanded} onClick={onToggle}>{t("Projects")}</button>;
}
