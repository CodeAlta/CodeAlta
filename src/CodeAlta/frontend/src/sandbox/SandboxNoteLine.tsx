import { AppIcon } from "../AppIcon";
import { useShellLanguage } from "../shellLanguage";
import type { SandboxNote } from "../toolCall";

/**
 * What the sandbox of the session said after a command, under its output: that the command ran outside it, or what
 * it blocked, in the words of the host.
 */
export function SandboxNoteLine({ note }: { note: SandboxNote }) {
  const { t } = useShellLanguage();
  return <p className="tool-sandbox-note" role="note" data-outside={note.outside || undefined} title={note.text}>
    <AppIcon name={note.outside ? "warning" : "sandbox"} size={13} />
    <strong>{t(note.outside ? "Ran outside the sandbox {profile}" : "Sandbox {profile}", { profile: note.profile })}</strong>
    {!note.outside && <span>{note.text}</span>}
  </p>;
}
