import { AppIcon } from "./AppIcon";

export function SessionDraftBadge({ active }: { active: boolean }) {
  return active ? <span className="session-draft-badge"><AppIcon name="prompt" size={12} />Edited draft</span> : null;
}
