// The tools a session does without, as a few tags and a small popover that adds one: the built-in tools of
// CodeAlta, and any other name (a tool of a plugin or an MCP server) typed in its field. Used by the Permissions page
// (every session), the page of a space and the editor of an agent prompt.
import { useState } from "react";
import { Button, InputGroup, Menu, MenuDivider, MenuItem, PopoverNext, Tag } from "@blueprintjs/core";
import { AppIcon } from "./AppIcon";
import { useShellLanguage } from "./shellLanguage";

/** The built-in tools of CodeAlta, offered first; the host gives its own list where it can. */
export const builtInToolNames: readonly string[] = ["read_file", "view_image", "list_dir", "grep", "webget", "shell_command", "write_file",
  "replace_in_file", "delete_file_or_dir", "rename_file_or_dir", "apply_patch", "request_user_input"];

/** Whether a text can be the name of a tool: 1 to 128 letters, digits, `_`, `-`, `.` and `:`, as the host checks it. */
export const toolNameValid = (name: string) => /^[A-Za-z0-9_.:-]{1,128}$/.test(name);

/** The list once a tool is added: each name once, in the order they were added. */
export function withTool(tools: readonly string[], name: string): readonly string[] {
  const trimmed = name.trim();
  return !toolNameValid(trimmed) || tools.includes(trimmed) ? tools : [...tools, trimmed];
}

/** The tools turned off, each with its remove button, then the button that adds one. */
export function DisabledToolsField({ tools, known = builtInToolNames, inherited = [], disabled, label, onChange }: {
  tools: readonly string[];
  /** The names offered in the popover. */
  known?: readonly string[];
  /** What another setting already turns off (the one of every session): shown, not removable here. */
  inherited?: readonly string[];
  disabled?: boolean;
  /** What the field is about, for a screen reader. */
  label: string;
  onChange: (next: readonly string[]) => void;
}) {
  const { t } = useShellLanguage();
  const [typed, setTyped] = useState("");
  const offered = known.filter(name => !tools.includes(name) && !inherited.includes(name));
  const add = (name: string) => { const next = withTool(tools, name); if (next !== tools) onChange(next); setTyped(""); };
  return <span className="disabled-tools" role="group" aria-label={label}>
    {inherited.filter(name => !tools.includes(name)).map(name => <Tag key={`inherited:${name}`} minimal round className="disabled-tool" data-inherited="true"
      title={t("Turned off for every session")}><code>{name}</code></Tag>)}
    {tools.map(name => <Tag key={name} minimal round className="disabled-tool" title={t("Turn {name} on again", { name })}
      onRemove={disabled ? undefined : () => onChange(tools.filter(tool => tool !== name))}><code>{name}</code></Tag>)}
    {tools.length === 0 && inherited.length === 0 && <span className="disabled-tools-none">{t("None")}</span>}
    <PopoverNext placement="bottom-start" disabled={disabled} content={<Menu className="disabled-tools-menu" aria-label={t("Turn off a tool")}>
      {offered.map(name => <MenuItem key={name} text={<code>{name}</code>} onClick={() => add(name)} />)}
      {offered.length > 0 && <MenuDivider />}
      <li className="disabled-tools-other">
        <InputGroup size="small" value={typed} placeholder={t("Another tool")} aria-label={t("Another tool")} spellCheck={false} autoComplete="off"
          intent={typed && !toolNameValid(typed.trim()) ? "danger" : undefined} onChange={event => setTyped(event.target.value)}
          onKeyDown={event => { if (event.key === "Enter" && toolNameValid(typed.trim())) { event.preventDefault(); add(typed); } }}
          rightElement={<Button variant="minimal" size="small" icon={<AppIcon name="plus" size={13} />} aria-label={t("Turn off a tool")}
            disabled={!toolNameValid(typed.trim())} onClick={() => add(typed)} />} />
      </li>
    </Menu>}>
      <Button variant="minimal" size="small" className="disabled-tools-add" disabled={disabled} icon={<AppIcon name="plus" size={13} />} text={t("Turn off a tool")} />
    </PopoverNext>
  </span>;
}
