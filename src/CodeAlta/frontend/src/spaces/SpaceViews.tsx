import { useEffect, useLayoutEffect, useRef, useState, type KeyboardEvent } from "react";
import { Button, Menu, MenuDivider, MenuItem, PopoverNext } from "@blueprintjs/core";
import { ActivitySpinner } from "../ActivitySpinner";
import { AppIcon } from "../AppIcon";
import { optionAfterKey } from "../ColorSchemeSelect";
import { Logo } from "../ProviderIcon";
import { useShellLanguage } from "../shellLanguage";
import { spaceBrand, spaceQuiet, type Space, type SpaceActivity, type SpaceCall } from "./spaces";

type Translate = ReturnType<typeof useShellLanguage>["t"];

/** The icon of a space, in its color. */
export function SpaceIcon({ space, size = 16 }: { space: Readonly<{ icon: string | null; color: string | null }>; size?: number }) {
  return <span className="space-icon"><Logo brand={spaceBrand(space)} size={size} fallback="space" /></span>;
}

/** What the sessions of a space are doing, in words: for a tooltip and for what a screen reader says. */
export function activityText(t: Translate, activity: SpaceActivity | undefined): string {
  if (!activity || spaceQuiet(activity)) return "";
  const parts: string[] = [];
  if (activity.waiting) parts.push(t(activity.waiting === 1 ? "{count} session waits for you" : "{count} sessions wait for you", { count: activity.waiting }));
  if (activity.failed) parts.push(t(activity.failed === 1 ? "{count} session failed" : "{count} sessions failed", { count: activity.failed }));
  if (activity.running) parts.push(t(activity.running === 1 ? "{count} session running" : "{count} sessions running", { count: activity.running }));
  if (activity.background) parts.push(t(activity.background === 1 ? "{count} session working in the background" : "{count} sessions working in the background", { count: activity.background }));
  return parts.join(", ");
}

/** What a space is doing, as small marks: sessions that wait for the user, that failed, that run, that work in the background. */
export function SpaceActivityMarks({ activity }: { activity: SpaceActivity | undefined }) {
  const { t } = useShellLanguage();
  if (!activity || spaceQuiet(activity)) return null;
  return <span className="space-marks" role="img" aria-label={activityText(t, activity)}>
    {activity.waiting > 0 && <span className="space-mark" data-kind="waiting"><AppIcon name="ask" size={12} />{activity.waiting}</span>}
    {activity.failed > 0 && <span className="space-mark" data-kind="failed"><AppIcon name="error" size={12} />{activity.failed}</span>}
    {activity.running > 0 && <span className="space-mark" data-kind="running"><ActivitySpinner size={11} />{activity.running}</span>}
    {activity.running === 0 && activity.background > 0 && <span className="space-mark" data-kind="background"><i aria-hidden="true" />{activity.background}</span>}
  </span>;
}

// Up, Down, Home and End move through the entries as they do in a native list.
function moveFocus(event: KeyboardEvent<HTMLUListElement>) {
  const items = Array.from(event.currentTarget.querySelectorAll<HTMLElement>(".bp6-menu-item:not(.bp6-disabled)"));
  const next = optionAfterKey(event.key, items.indexOf(document.activeElement as HTMLElement), items.length);
  if (next === null) return;
  event.preventDefault();
  items[next].focus();
}

/**
 * The space of the window in the title bar, before the zoom and the theme: the space that is shown, which
 * opens the list of the spaces, a way to make a new one and the page that organizes them. With the default
 * space alone it is its icon; a space that needs the user while another one is shown puts a dot on it.
 */
export function SpaceSwitch({ spaces, shownId, activity, canEdit, request, onShow, onCreate, onOrganize }: {
  spaces: readonly Space[]; shownId: string; activity: ReadonlyMap<string, SpaceActivity>;
  /** Whether spaces can be made and organized: a window with a host of its own. */
  canEdit: boolean;
  /** Changes each time the list is asked for from elsewhere (a command, a key): it then opens. */
  request: number;
  onShow: (id: string) => void; onCreate: () => void; onOrganize: () => void;
}) {
  const { t } = useShellLanguage();
  const [open, setOpen] = useState(false);
  const asked = useRef(request);
  useEffect(() => {
    if (asked.current === request) return;
    asked.current = request;
    setOpen(true);
  }, [request]);
  // The tab strip under the title bar leaves the switch the room it takes, whatever the name of the space.
  const button = useRef<HTMLButtonElement>(null);
  useLayoutEffect(() => {
    const element = button.current;
    const shell = element?.closest<HTMLElement>(".ide-shell");
    if (!element || !shell) return;
    const measure = () => shell.style.setProperty("--space-switch-width", `${Math.ceil(element.getBoundingClientRect().width) + 2}px`);
    const observer = new ResizeObserver(measure);
    observer.observe(element);
    measure();
    return () => { observer.disconnect(); shell.style.removeProperty("--space-switch-width"); };
  }, []);
  const shown = spaces.find(space => space.id === shownId) ?? spaces[0];
  const alone = spaces.length < 2;
  // What happens where the user is not looking: the spaces other than the one shown.
  const elsewhere = spaces.filter(space => space.id !== shown.id && !space.isDefault).map(space => activity.get(space.id));
  const attention = elsewhere.some(value => value && value.waiting + value.failed > 0);
  const menu = <Menu className="space-menu" aria-label={t("Spaces")} onKeyDown={moveFocus}>
    <MenuDivider title={t("Spaces")} />
    {spaces.map((space, index) => <MenuItem key={space.id} roleStructure="listoption" selected={space.id === shown.id}
      icon={<SpaceIcon space={space} />} text={space.name} title={space.description ?? undefined}
      labelElement={<span className="space-menu-label"><SpaceActivityMarks activity={activity.get(space.id)} />{index < 9 && <kbd>{`Ctrl+G ${index + 1}`}</kbd>}</span>}
      onClick={() => onShow(space.id)} />)}
    {canEdit && <><MenuDivider />
      <MenuItem icon={<AppIcon name="plus" size={15} />} text={`${t("New space")}…`} onClick={onCreate} />
      <MenuItem icon={<AppIcon name="settings" size={15} />} text={`${t("Organize spaces")}…`} onClick={onOrganize} /></>}
  </Menu>;
  const label = t("Space: {name}", { name: shown.name });
  return <PopoverNext isOpen={open} onInteraction={next => setOpen(next)} placement="bottom-end" content={menu}
    onOpened={popover => popover.querySelector<HTMLElement>("[aria-selected=true] > .bp6-menu-item, .bp6-menu-item.bp6-selected")?.focus()}>
    <Button ref={button} variant="minimal" size="small" className="space-switch" data-alone={alone ? "true" : undefined} data-attention={attention ? "true" : undefined}
      aria-haspopup="listbox" aria-label={label} title={`${label} (Ctrl+G, Ctrl+V)`}
      icon={<SpaceIcon space={shown} size={15} />} endIcon={alone ? undefined : <AppIcon name="chevronDown" size={13} />}>
      {alone ? undefined : <span className="space-switch-name">{shown.name}</span>}
    </Button>
  </PopoverNext>;
}

/**
 * The spaces at the foot of the Explorer, while there is more than the default one: each as its icon, the one
 * shown marked, with what its sessions are doing. A space where a session waits for the user or failed is also
 * named in a line, which shows that space and opens that session. A click on an icon shows its space.
 */
export function SpaceActivityBar({ spaces, shownId, activity, calls, onShow, onOpen }: {
  spaces: readonly Space[]; shownId: string; activity: ReadonlyMap<string, SpaceActivity>;
  /** The sessions that need the user in the spaces that are not shown. */
  calls: readonly SpaceCall[];
  onShow: (id: string) => void;
  /** Shows a space and opens one of its sessions. */
  onOpen: (spaceId: string, session: Readonly<{ sessionId: string; projectId: string | null }>) => void;
}) {
  const { t } = useShellLanguage();
  if (spaces.length < 2) return null;
  return <footer className="space-bar" aria-label={t("Spaces")}>
    {calls.slice(0, 3).map(call => <button type="button" key={call.space.id} className="space-call" data-kind={call.waiting ? "waiting" : "failed"}
      title={t(call.waiting ? "{title} waits for you in {space}" : "{title} failed in {space}", { title: call.title || t("A session"), space: call.space.name })}
      onClick={() => onOpen(call.space.id, call)}>
      <AppIcon name={call.waiting ? "ask" : "error"} size={13} /><span className="space-call-space">{call.space.name}</span>
      <span className="space-call-title">{call.title || t("A session")}</span>
    </button>)}
    <div className="space-chips" role="toolbar" aria-label={t("Show a space")}>
      {spaces.map(space => {
        const doing = activity.get(space.id);
        const words = activityText(t, doing);
        const state = !doing || spaceQuiet(doing) ? undefined : doing.waiting ? "waiting" : doing.failed ? "failed" : doing.running ? "running" : "background";
        return <button type="button" key={space.id} className="space-chip" aria-pressed={space.id === shownId} data-activity={state}
          aria-label={words ? `${space.name}: ${words}` : space.name} title={words ? `${space.name}: ${words}` : space.name} onClick={() => onShow(space.id)}>
          <SpaceIcon space={space} size={15} />
          {state && <span className="space-chip-mark" aria-hidden="true">{state === "running" ? <ActivitySpinner size={9} /> : null}</span>}
        </button>;
      })}
    </div>
  </footer>;
}
