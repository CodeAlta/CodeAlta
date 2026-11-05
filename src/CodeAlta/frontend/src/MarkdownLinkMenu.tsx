import { createContext, useContext, useEffect, useState } from "react";
import { ExternalEditorIcon, useExternalOpener } from "./externalEditors/externalEditors";
import { markdownHrefKind, type MarkdownLinkScope } from "./markdownLinks";
import { SessionTabMenu, type SessionMenuEntry } from "./SessionTabMenu";
import { revealLabel } from "./SettingsFileLocation";
import { useShellLanguage } from "./shellLanguage";

/**
 * What the menu of a link does with the file it names. The host finds the file from the link, as it does to open
 * it: the page never sends a path. Null where nothing can (a window without a host): the menu only copies the link.
 */
export type MarkdownLinkFiles = Readonly<{
  /** The full path the link names; null when it names none. */
  path(address: string, scope: MarkdownLinkScope | null): Promise<string | null>;
  reveal(address: string, scope: MarkdownLinkScope | null): void;
  openExternal(address: string, scope: MarkdownLinkScope | null): void;
  /** The system, which names its file manager; null where a file cannot be shown. */
  platform: string | null;
}>;

export const MarkdownLinkFilesContext = createContext<MarkdownLinkFiles | null>(null);

/** The link a menu is open on, and where: at the point of a right-click, or under the link. */
export type LinkMenu = Readonly<{ address: string; anchor: HTMLElement; at?: Readonly<{ x: number; y: number }> }>;

const copy = (text: string) => void navigator.clipboard?.writeText(text).catch(() => { /* The clipboard is unavailable: nothing was copied. */ });

/** The context menu of a link of the Markdown: it opens the link, copies it, and shows its file outside CodeAlta. */
export function MarkdownLinkMenu({ menu, scope, onOpen, onClose }: {
  menu: LinkMenu; scope: MarkdownLinkScope | null; onOpen: (address: string) => void; onClose: () => void;
}) {
  const { t } = useShellLanguage();
  const files = useContext(MarkdownLinkFilesContext);
  const external = useExternalOpener();
  const { address } = menu;
  const web = markdownHrefKind(address) === "web";
  // Asked when the menu opens: a system may refuse the clipboard once the press that chose the line is over.
  const [fullPath, setFullPath] = useState<string | null | undefined>(undefined);
  useEffect(() => {
    if (web || !files) return;
    let current = true;
    void files.path(address, scope).then(path => { if (current) setFullPath(path); }, () => { if (current) setFullPath(null); });
    return () => { current = false; };
  }, [address, web, files]);
  const open: SessionMenuEntry = { key: "open", label: t("Open"), icon: web ? "openExternal" : "code", onSelect: () => onOpen(address) };
  const reveal = files && revealLabel(files.platform);
  const items: SessionMenuEntry[] = web
    ? [open, { key: "copy", label: t("Copy link"), icon: "copy", onSelect: () => copy(address) }]
    : [open,
      // A link the host cannot read is copied as it is written.
      { key: "copy", label: t("Copy path"), icon: "copy", disabled: !!files && fullPath === undefined, onSelect: () => copy(fullPath ?? address) },
      ...(files && reveal ? [{ key: "reveal", label: t(reveal), icon: "openExternal" as const, onSelect: () => files.reveal(address, scope) }] : []),
      ...(files && external.editor ? [{ key: "editor", label: t("Open in {editor}", { editor: external.editor.name }),
        iconNode: <ExternalEditorIcon id={external.editor.id} size={15} />, onSelect: () => files.openExternal(address, scope) }] : [])];
  // Placed in the dialog of a link inside one: outside it, the menu would be under the modal layer.
  return <SessionTabMenu anchor={menu.anchor} at={menu.at} container={menu.anchor.closest("dialog") ?? document.body} title={t("Link options")}
    current={() => true} onClose={onClose} items={items} />;
}
