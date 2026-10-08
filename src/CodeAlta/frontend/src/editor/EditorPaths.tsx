import { useEffect, useRef, useState } from "react";
import { AppIcon, type IconName } from "../AppIcon";
import { useShellLanguage } from "../shellLanguage";

/** The folder an editor shows, under its name: one line, cut at its start so that the end of the path stays in view. */
export function EditorRootPath({ path }: { path: string }) {
  return <span className="editor-side-root" title={path}><bdi>{path}</bdi></span>;
}

/**
 * The file shown, in the status bar: its path in the folder as text and its full path as tooltip. A click copies
 * the full path, and the icon says so for a moment.
 */
export function EditorStatusPath({ icon, tone, text, fullPath, copy = value => navigator.clipboard.writeText(value) }: {
  icon: IconName; tone?: string; text: string; fullPath: string;
  copy?: (text: string) => Promise<void>;
}) {
  const { t } = useShellLanguage();
  // The path that was copied: another file shown in its place is not a copied one.
  const [copied, setCopied] = useState<string | null>(null);
  const reset = useRef(0);
  useEffect(() => () => window.clearTimeout(reset.current), []);
  const done = copied === fullPath;
  return <button type="button" className="editor-status-path" title={done ? t("Copied") : fullPath} aria-label={`${t("Copy path")}: ${fullPath}`}
    onClick={() => void copy(fullPath).then(() => {
      setCopied(fullPath);
      window.clearTimeout(reset.current);
      reset.current = window.setTimeout(() => setCopied(null), 1400);
    }, () => { /* The clipboard is not available: nothing is copied. */ })}>
    <span className="file-tab-icon" data-file-tone={done ? undefined : tone}><AppIcon name={done ? "check" : icon} size={13} /></span><span>{text}</span>
  </button>;
}
