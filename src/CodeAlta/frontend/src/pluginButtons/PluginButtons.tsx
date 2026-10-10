import { useContext, useEffect, useLayoutEffect, useRef, useState, useSyncExternalStore } from "react";
import { Button, Menu, MenuItem, PopoverNext } from "@blueprintjs/core";
import { AppIcon } from "../AppIcon";
import { SessionTabMenu } from "../SessionTabMenu";
import { useShellLanguage } from "../shellLanguage";
import { PluginIcon } from "./PluginIcon";
import {
  PluginButtonsActiveContext, PluginButtonsContext, badgeText, buttonName, buttonTooltip, foldButtons, narrowWindowWidth, pluginButtonsChangedEvent, railInlineLimit, readPluginButtons, samePluginButtons,
  shownButtons, type PluginButtonContext, type PluginButtonPlace, type PluginButtonView,
} from "./pluginButtonModel";
import { pluginsChangedEvent } from "../pluginUi";

/** How long the window waits for more changes before it reads the buttons again: a burst of invalidations is one read. */
const settleMilliseconds = 40;

/**
 * The buttons that plugins put at a place, for a context, as the host lists them (hidden ones included). They are read when
 * the place is shown or its context changes, when a command of a plugin ended, when plugins changed, and when a plugin says
 * its buttons changed; a read that changed nothing keeps the list as it is. A null context reads nothing.
 */
export function usePluginButtons(place: PluginButtonPlace | null, context: PluginButtonContext | null): readonly PluginButtonView[] {
  const host = useContext(PluginButtonsContext);
  const [buttons, setButtons] = useState<readonly PluginButtonView[]>(none);
  const [revision, setRevision] = useState(0);
  const projectId = context?.projectId ?? null, sessionId = context?.sessionId ?? null, reading = context !== null;
  useEffect(() => {
    // A place that reads nothing (the menu of a row that is closed) listens to nothing.
    if (!reading) return;
    let timer: number | undefined;
    const changed = () => { window.clearTimeout(timer); timer = window.setTimeout(() => setRevision(value => value + 1), settleMilliseconds); };
    window.addEventListener(pluginButtonsChangedEvent, changed);
    window.addEventListener(pluginsChangedEvent, changed);
    return () => { window.clearTimeout(timer); window.removeEventListener(pluginButtonsChangedEvent, changed); window.removeEventListener(pluginsChangedEvent, changed); };
  }, [reading]);
  useEffect(() => {
    if (!host.epoch || !reading) { setButtons(current => current.length ? none : current); return; }
    const abort = new AbortController();
    void host.api.buttons({ expectedEpoch: host.epoch, place, spaceId: host.spaceId, projectId, sessionId }, { signal: abort.signal, timeoutMilliseconds: 8000 })
      .then(reply => {
        if (abort.signal.aborted) return;
        const read = readPluginButtons(reply) ?? none;
        setButtons(current => samePluginButtons(current, read) ? current : read);
      }, () => { if (!abort.signal.aborted) setButtons(current => current.length ? none : current); });
    return () => abort.abort();
  }, [host.api, host.epoch, host.spaceId, place, projectId, sessionId, reading, revision]);
  return buttons;
}
const none: readonly PluginButtonView[] = Object.freeze([]);

/** The buttons to draw at a place: the ones neither the plugin nor the user hides. */
export function useShownPluginButtons(place: PluginButtonPlace, context: PluginButtonContext | null): readonly PluginButtonView[] {
  const host = useContext(PluginButtonsContext);
  const buttons = usePluginButtons(place, context);
  const hidden = useSyncExternalStore(host.hidden.subscribe, host.hidden.getSnapshot, host.hidden.getSnapshot);
  return shownButtons(buttons, hidden);
}

/** Whether the window is narrow enough for the buttons of plugins to fold into a menu. */
export function useNarrowWindow(): boolean {
  const query = `(max-width: ${narrowWindowWidth}px)`;
  const [narrow, setNarrow] = useState(() => typeof matchMedia === "function" && matchMedia(query).matches);
  useEffect(() => {
    if (typeof matchMedia !== "function") return;
    const list = matchMedia(query);
    const update = () => setNarrow(list.matches);
    update();
    list.addEventListener("change", update);
    return () => list.removeEventListener("change", update);
  }, [query]);
  return narrow;
}

/** The number of a badge and the ring of a busy button, over the corner of the icon. */
function Glyph({ button, size }: { button: PluginButtonView; size: number }) {
  const count = badgeText(button);
  return <span className="plugin-button-glyph">
    <PluginIcon icon={button.icon} data={button.iconData} pluginKey={button.pluginKey} size={size} />
    {count && <span className="plugin-button-badge" data-kind="count" aria-hidden="true">{count}</span>}
    {button.badge === "dot" && <span className="plugin-button-badge" data-kind="dot" aria-hidden="true" />}
    {button.badge === "busy" && <span className="plugin-button-badge" data-kind="busy" aria-hidden="true" />}
  </span>;
}

/**
 * The buttons of plugins at the title bar or the rail: icons with a badge, in the order the host lists them. A right
 * click on one offers to hide it. In a narrow window they fold into one menu, before anything of the application
 * moves; the rail also folds the buttons past two.
 */
export function PluginButtons({ place, context }: { place: "TitleBar" | "Rail"; context: PluginButtonContext }) {
  const host = useContext(PluginButtonsContext);
  useContext(PluginButtonsActiveContext); // The marks follow the tab in front.
  const { t } = useShellLanguage();
  const buttons = useShownPluginButtons(place, context);
  const narrow = useNarrowWindow();
  const { inline, folded } = foldButtons(buttons, place === "Rail" ? railInlineLimit : Number.POSITIVE_INFINITY, narrow);
  const group = useRef<HTMLDivElement>(null);
  const [menu, setMenu] = useState<{ button: PluginButtonView; anchor: HTMLElement; at: { x: number; y: number } } | null>(null);
  // The tab strip under the title bar leaves the buttons the room they take, and the mark at the left of the title bar makes room for those of the rail.
  useLayoutEffect(() => {
    const element = group.current;
    const shell = element?.closest<HTMLElement>(".ide-shell");
    if (!element || !shell) return;
    const property = place === "TitleBar" ? "--plugin-buttons-width" : "--plugin-rail-width";
    const measure = () => shell.style.setProperty(property, `${Math.ceil(element.getBoundingClientRect().width) + 2}px`);
    const observer = new ResizeObserver(measure);
    observer.observe(element);
    measure();
    return () => { observer.disconnect(); shell.style.removeProperty(property); };
  }, [place, buttons.length === 0]);
  const withCount = (label: string, count: string) => t("{label}: {count}", { label, count });
  if (buttons.length === 0) return null;
  const size = place === "TitleBar" ? 16 : 16;
  return <div ref={group} className="plugin-buttons" data-place={place} role="group" aria-label={t("Plugin buttons")}>
    {inline.map(button => <Button key={button.id} variant="minimal" size="small" className="plugin-button" data-tone={button.tone} data-badge={button.badge}
      data-plugin={button.pluginId} data-button={button.buttonId} active={host.isActive(button, context)} disabled={button.disabled} aria-busy={button.badge === "busy" ? true : undefined}
      aria-label={buttonName(button, withCount)} title={buttonTooltip(button, withCount)} icon={<Glyph button={button} size={size} />}
      onClick={() => host.activate(button, context)}
      onContextMenu={event => { event.preventDefault(); setMenu({ button, anchor: event.currentTarget, at: { x: event.clientX, y: event.clientY } }); }} />)}
    {folded.length > 0 && <PopoverNext placement="bottom-end" content={<Menu aria-label={t("Plugin buttons")}>
      {folded.map(button => <MenuItem key={button.id} icon={<PluginIcon icon={button.icon} data={button.iconData} pluginKey={button.pluginKey} size={15} />} text={button.label} disabled={button.disabled} active={host.isActive(button, context)}
        title={button.tooltip ?? undefined} labelElement={badgeText(button) ?? (button.badge === "dot" ? "•" : undefined)}
        onClick={() => host.activate(button, context)} />)}
    </Menu>}>
      <Button variant="minimal" size="small" className="plugin-button plugin-buttons-more" icon={<AppIcon name="plugin" size={16} />}
        aria-haspopup="menu" aria-label={t("More plugin buttons")} title={t("More plugin buttons")} />
    </PopoverNext>}
    {menu && <SessionTabMenu anchor={menu.anchor} at={menu.at} container={document.body} title={menu.button.label} current={() => true} onClose={() => setMenu(null)}
      items={[{ key: "hide", label: t("Hide this button"), icon: "hide", onSelect: () => host.hidden.set(menu.button, true) }]} />}
  </div>;
}
