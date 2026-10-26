import { useEffect, useId, useRef } from "react";
import { FormGroup, PopoverNext } from "@blueprintjs/core";
import { ActivitySpinner } from "../ActivitySpinner";
import { useShellLanguage } from "../shellLanguage";
import { SandboxScopeSelect, useSandboxScopes, type SandboxScopeApi } from "./SandboxScopeControls";

/**
 * The sandbox of a project, in a popover beside its row of the Explorer, as its rename form is: a choice is saved at
 * once and closes it; Escape or a click elsewhere leaves it as it was. Rendered inside the row while it is open.
 */
export function ProjectSandboxPopover({ epoch, projectId, projectName, api, onClose }: {
  epoch: string; projectId: string; projectName: string; api: SandboxScopeApi; onClose: () => void;
}) {
  const { t } = useShellLanguage();
  const id = useId();
  const select = useRef<HTMLDivElement>(null);
  const scopes = useSandboxScopes(epoch, api);
  const ready = !!scopes.listing;
  // The menu it comes from gives the focus back to its button as it closes: the select takes it once it is there.
  useEffect(() => {
    if (!ready) return;
    const timer = setTimeout(() => select.current?.querySelector("select")?.focus());
    return () => clearTimeout(timer);
  }, [ready]);
  const form = <div ref={select} className="sandbox-project-form" onKeyDown={event => { if (event.key === "Escape") { event.preventDefault(); event.stopPropagation(); onClose(); } }}>
    <FormGroup label={t("Sandbox of {name}", { name: projectName })} labelFor={id}
      helperText={t("The sessions of this project run in it, unless their model or their provider chooses another.")}>
      {ready ? <SandboxScopeSelect id={id} value={scopes.value("project", projectId)} profiles={scopes.profiles} disabled={scopes.busy}
        label={t("Sandbox of {name}", { name: projectName })}
        onChange={value => void scopes.choose("project", projectId, null, value).then(saved => { if (saved) onClose(); })} />
        : <ActivitySpinner size={16} />}
    </FormGroup>
  </div>;
  return <PopoverNext isOpen content={form} placement="right-start" popoverClassName="rename-popover sandbox-project-popover" className="rename-anchor" targetTagName="span"
    autoFocus={false} enforceFocus={false} shouldReturnFocusOnClose={false} canEscapeKeyClose={!scopes.busy}
    onInteraction={next => { if (!next && !scopes.busy) onClose(); }}>
    <span aria-hidden="true" />
  </PopoverNext>;
}
