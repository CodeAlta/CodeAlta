import { useState } from "react";
import type { WorkspaceProject } from "#neoastra";
import { ConfirmPopover } from "../ConfirmPopover";
import type { ProjectDeleteOutcome, ProjectDeleteTarget } from "../projectDeletion";
import type { useShellLanguage } from "../shellLanguage";

/** Presents a project deletion and its outcome while the workspace holds the captured target and mutation lock. */
export function useProjectDeleteQuestion({ target, project, busy, onConfirm, onCancel, t }: {
  target: ProjectDeleteTarget | null; project: WorkspaceProject | undefined; busy: boolean;
  onConfirm: () => void; onCancel: () => void; t: ReturnType<typeof useShellLanguage>["t"];
}) {
  const [error, setError] = useState("");
  const clearError = () => setError("");

  function reportFailure(outcome: Exclude<ProjectDeleteOutcome, "deleted"> | null) {
    setError(t(outcome === "inUse" ? "A session of this project is running or waits for an answer. Stop it or let it end first."
      : outcome === "children" ? "A session of another project was started from this project. Delete that session first."
        : outcome === "failed" ? "The project could not be deleted completely. Some of its sessions may be gone. Try again."
          : "The project changed. Nothing was deleted."));
  }

  const question = target && project && target.id === project.id ? { id: target.id,
    form: <ConfirmPopover always title={t("Delete this project?")} subject={project.name}
      detail={t("It is removed from CodeAlta and its sessions are deleted: they cannot be restored. Its folder and its files are kept.")}
      confirmLabel={t("Delete")} intent="danger" busy={busy} error={error || null}
      onConfirm={onConfirm}
      onCancel={() => { onCancel(); clearError(); }} /> } : undefined;

  return { question, clearError, reportFailure };
}
