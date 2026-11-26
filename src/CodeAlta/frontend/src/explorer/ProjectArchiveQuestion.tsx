import { useEffect, useState } from "react";
import type { WorkspaceProject } from "#neoastra";
import { ConfirmPopover } from "../ConfirmPopover";
import type { ArchiveScope } from "../projectArchive";
import type { useShellLanguage } from "../shellLanguage";

/** Keeps the Explorer question for archiving or unarchiving the selected project. */
export function useProjectArchiveQuestion({ project, railVisible, busy, onConfirm, t }: {
  project: WorkspaceProject | undefined; railVisible: boolean; busy: boolean;
  onConfirm: (remember: boolean) => void; t: ReturnType<typeof useShellLanguage>["t"];
}) {
  const [target, setTarget] = useState<Pick<ArchiveScope, "id" | "archived"> | null>(null);
  const [error, setError] = useState("");
  const ask = ({ id, archived }: Pick<ArchiveScope, "id" | "archived">) => setTarget({ id, archived });
  const close = () => setTarget(null);
  const reset = () => { close(); setError(""); };

  // The question goes with its row when the Explorer is hidden, unless an answer is already in flight.
  useEffect(() => { if (!railVisible && !busy) setTarget(null); }, [railVisible]);

  const question = target && project && target.id === project.id && target.archived === project.archived ? { id: target.id,
    form: <ConfirmPopover title={t(target.archived ? "Unarchive this project?" : "Archive this project?")} subject={project.name}
      detail={t(target.archived ? "It can be worked in again. Nothing is started."
        : "It becomes read-only in CodeAlta until it is unarchived. Its files and its sessions are kept.")}
      confirmLabel={t(target.archived ? "Unarchive" : "Archive")} busy={busy} error={error || null}
      onConfirm={onConfirm} onCancel={reset} /> } : undefined;

  return { ask, close, reset, setError, question };
}
