import type { Ref } from "react";

export function ProjectRailToggle({ expanded, onToggle, buttonRef }: {
  expanded: boolean; onToggle: () => void; buttonRef: Ref<HTMLButtonElement>;
}) {
  return <button ref={buttonRef} type="button" className="project-rail-toggle" aria-label={expanded ? "Hide projects" : "Show projects"}
    aria-controls="project-rail" aria-expanded={expanded} onClick={onToggle}>Projects</button>;
}
