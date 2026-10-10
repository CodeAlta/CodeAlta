import { Component, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { Button, Switch } from "@blueprintjs/core";
import { AppIcon, type IconName } from "../AppIcon";
import { PluginHtml } from "../PluginHtml";
import { PluginIcon } from "../pluginButtons/PluginIcon";
import { PluginUiContext, pluginsChangedEvent, type PluginUiValue } from "../pluginUi";
import { sessionTime } from "../sessionTime";
import { plainTitle } from "../sessionTitle";
import { useShellLanguage } from "../shellLanguage";
import { cardCommand, documentationCommand, onboardingSteps, readLandingCards, recentProjects, recentSessions, sameLandingCards,
  type LandingCardAction, type LandingCardView } from "./landingModel";
import { landingPreferences, useLandingPreferences, type LandingPreferenceStore } from "./landingPreferences";
import { landingCardsChangedEvent, type LandingProviders, type LandingShell } from "./landingShell";
import { LandingAccent } from "./LandingAccent";

/** How long the page waits for more changes before it reads the cards again: a burst of invalidations is one read. */
const settleMilliseconds = 60;
/** How long the page waits before it asks again what the providers are doing, while the window still looks for them. */
const providerRetryMilliseconds = 4000;
const noCards: readonly LandingCardView[] = Object.freeze([]);

/**
 * The cards that plugins pin, read while the page is shown: when it is first shown and each time it comes back in front, when a plugin
 * says its cards changed, when a command of a plugin ended, and when plugins started or stopped. A hidden page reads nothing. A read
 * that fails keeps the cards that are drawn.
 */
function useLandingCards(shell: LandingShell, visible: boolean): readonly LandingCardView[] {
  const [cards, setCards] = useState<readonly LandingCardView[]>(noCards);
  const [revision, setRevision] = useState(0);
  useEffect(() => {
    if (!visible) return;
    let timer: number | undefined;
    const changed = () => { window.clearTimeout(timer); timer = window.setTimeout(() => setRevision(value => value + 1), settleMilliseconds); };
    window.addEventListener(landingCardsChangedEvent, changed);
    window.addEventListener(pluginsChangedEvent, changed);
    return () => { window.clearTimeout(timer); window.removeEventListener(landingCardsChangedEvent, changed); window.removeEventListener(pluginsChangedEvent, changed); };
  }, [visible]);
  const { epoch, readCards } = shell, spaceId = shell.space.id;
  useEffect(() => {
    if (!epoch) { setCards(current => current.length ? noCards : current); return; }
    if (!visible) return;
    const abort = new AbortController();
    void readCards(abort.signal).then(reply => {
      if (abort.signal.aborted) return;
      const read = readLandingCards(reply);
      if (read) setCards(current => sameLandingCards(current, read) ? current : read);
    }, () => { /* The cards that are drawn stay. */ });
    return () => abort.abort();
  }, [epoch, readCards, spaceId, visible, revision]);
  return cards;
}

/** What the providers of models are doing, read while the page is shown and again while the window still looks for them. */
function useLandingProviders(shell: LandingShell, visible: boolean): LandingProviders | null {
  const [providers, setProviders] = useState<LandingProviders | null>(null);
  const { epoch, readProviders } = shell;
  useEffect(() => {
    if (!epoch || !visible) return;
    const abort = new AbortController();
    let timer: number | undefined;
    const read = () => void readProviders(abort.signal).then(value => {
      if (abort.signal.aborted) return;
      if (value) setProviders(current => current && current.ready === value.ready && current.detecting === value.detecting ? current : value);
      if (!value || value.detecting) timer = window.setTimeout(read, providerRetryMilliseconds);
    }, () => { if (!abort.signal.aborted) timer = window.setTimeout(read, providerRetryMilliseconds); });
    read();
    return () => { abort.abort(); window.clearTimeout(timer); };
  }, [epoch, readProviders, visible]);
  return providers;
}

/** Keeps a card that throws while it is drawn from taking the page with it. */
class CardBoundary extends Component<{ children: ReactNode; fallback: ReactNode }, { failed: boolean }> {
  state = { failed: false };

  static getDerivedStateFromError() { return { failed: true }; }

  render() { return this.state.failed ? this.props.fallback : this.props.children; }
}

/** One card of a plugin: its title, what the plugin wrote, and its actions. */
function PluginCard({ card, onAction, onCommand }: {
  card: LandingCardView; onAction: (card: LandingCardView, action: LandingCardAction) => void;
  /** Runs a command of the plugin of the card, for the project of the card. */
  onCommand: (card: LandingCardView, commandId: string, name: string) => void;
}) {
  const { t } = useShellLanguage();
  const pane = useMemo(() => ({ projectId: card.projectId, sessionId: null }), [card.projectId]);
  // An element of the fragment names a command (`data-alta-command`): it is one of the plugin of the card, run for the project of the card,
  // whatever project the window has selected. A name the plugin does not have runs nothing, and never a command of another plugin.
  const ui = useContext(PluginUiContext);
  const latest = useRef({ card, onCommand });
  latest.current = { card, onCommand };
  const cardUi = useMemo<PluginUiValue>(() => ({ ...ui, runNamed: name => {
    const command = cardCommand(latest.current.card, name);
    if (command) latest.current.onCommand(latest.current.card, command.id, command.name);
  } }), [ui]);
  const failed = <p className="landing-card-failed" role="status">{t("This card could not be loaded.")}</p>;
  return <section className="landing-card landing-plugin-card" data-plugin={card.pluginKey} data-card={card.cardId} data-state={card.state} aria-label={card.title} title={card.plugin || undefined}>
    <header className="landing-card-header">
      <PluginIcon icon={card.icon} data={card.iconData} pluginKey={card.pluginKey} size={16} className="landing-card-icon" />
      <h2>{card.title}</h2>
      {card.projectName && <span className="landing-card-project">{card.projectName}</span>}
      {card.status && <span className="landing-card-status" data-tone={card.tone}>{card.status}</span>}
    </header>
    {card.state === "failed" ? failed
      // A new fragment is a new try: a card that failed once is drawn again with what its plugin writes next.
      : <CardBoundary key={card.html} fallback={failed}><PluginUiContext.Provider value={cardUi}>
        <PluginHtml className="landing-card-html" html={card.html} pluginKey={card.pluginKey} pane={pane} /></PluginUiContext.Provider></CardBoundary>}
    {card.actions.length > 0 && <footer className="landing-card-actions">
      {card.actions.map((action, index) => <Button key={index} size="small" intent={action.primary ? "primary" : "none"} variant={action.primary ? "solid" : "outlined"} disabled={action.disabled}
        icon={action.icon ? <PluginIcon icon={action.icon} data={action.iconData} pluginKey={card.pluginKey} size={14} /> : undefined} onClick={() => onAction(card, action)}>{action.label}</Button>)}
    </footer>}
  </section>;
}

/** A line of a list of the page: a project or a session, opened by a click or by Enter. */
function Row({ icon, title, detail, time, onOpen }: { icon: IconName; title: string; detail: string | null; time: Readonly<{ label: string; title: string; dateTime?: string }> | null; onOpen: () => void }) {
  return <li>
    <button type="button" className="landing-row" onClick={onOpen}>
      <AppIcon name={icon} size={15} className="landing-row-icon" aria-hidden="true" />
      <span className="landing-row-text"><span className="landing-row-title">{title}</span>{detail && <span className="landing-row-detail">{detail}</span>}</span>
      {time && time.label && <time className="landing-row-time" dateTime={time.dateTime} title={time.title}>{time.label}</time>}
    </button>
  </li>;
}

/**
 * The landing page: a welcome with the first things to do, the projects and the sessions used last in the space the window shows, the
 * way to the documentation and to the pages of the application, and the cards that plugins pin (the Statistics overview among them).
 *
 * The page keeps nothing. The projects and the sessions are the ones the window already has; the cards are asked of the plugins while
 * the page is shown, and a card that fails stays a card. A hidden page reads nothing and its accent does not move.
 */
export function LandingCanvas({ shell, visible, dark, openCanvas, preferences = landingPreferences }: {
  /** What the shell of the window lends the page. */
  shell: LandingShell;
  /** The tab of the page is in front. */
  visible: boolean;
  /** The theme of the window is dark. */
  dark: boolean;
  /** Opens a canvas of a plugin, for the action of a card. */
  openCanvas: (request: Readonly<{ pluginKey: string; canvasId: string; projectId: string | null; key: string | null }>) => void;
  /** The preferences of the page: the window's, unless a test brings its own. */
  preferences?: LandingPreferenceStore;
}) {
  const { t, locale } = useShellLanguage();
  const chosen = useLandingPreferences(preferences);
  const cards = useLandingCards(shell, visible);
  const providers = useLandingProviders(shell, visible);
  const { projects, sessions } = shell;
  const sessionsShown = useMemo(() => sessions ? recentSessions(sessions) : [], [sessions]);
  const projectsShown = useMemo(() => projects ? recentProjects(projects, sessions ?? []) : [], [projects, sessions]);
  const projectNames = useMemo(() => new Map((projects ?? []).map(project => [project.id, project.name])), [projects]);
  const steps = onboardingSteps(projects, providers);
  // The times of the lists are told from the moment the page was last shown: a page left in front does not count seconds.
  const now = useMemo(() => Date.now(), [visible, sessions]);

  // Every button of the page is a command of the window, run the way the palette runs it: one that cannot run now says so.
  const run = (command: string, label: string) => { if (!shell.run(command)) shell.notifyUnavailable(label); };
  // The documentation is the one shipped with the application, in its own viewer: the page never sends to the web.
  const openDocumentation = () => run(documentationCommand, t("Documentation"));
  const activate = (card: LandingCardView, action: LandingCardAction) => {
    if (action.disabled) return;
    if (action.commandId) shell.runCardCommand(action.commandId, card.projectId, action.label);
    // A canvas of the application is the same tab wherever it is opened from: only a canvas of a project takes the project of the card.
    else if (action.canvas) openCanvas({ pluginKey: card.pluginKey, canvasId: action.canvas, projectId: action.canvasScope === "Project" ? card.projectId : null, key: action.key });
  };
  const runCommand = (card: LandingCardView, commandId: string, name: string) => shell.runCardCommand(commandId, card.projectId, name);
  const explore: readonly Readonly<{ icon: IconName; label: string; command: string }>[] = [
    { icon: "canvases", label: t("Canvases"), command: "canvases" },
    { icon: "plugin", label: t("Plugins"), command: "plugins" },
    { icon: "provider", label: t("Model Providers"), command: "model_providers" },
    { icon: "settings", label: t("Settings"), command: "settings" },
    { icon: "question", label: t("Keyboard shortcuts"), command: "help" },
  ];

  return <div className="landing" data-animate={chosen.animate} data-theme={dark ? "dark" : "light"}>
    <div className="landing-page">
      <header className="landing-hero">
        <LandingAccent animate={chosen.animate} visible={visible} dark={dark} />
        <div className="landing-hero-text">
          <div className="landing-wordmark" role="img" aria-label="CodeAlta"><span aria-hidden="true">Code</span><span className="logo-alta" aria-hidden="true">Alta</span></div>
          <h1>{t("Welcome")}</h1>
          <p className="landing-tagline">{t("Pick up where you left off, or start something new.")}</p>
          <div className="landing-hero-actions">
            <Button intent="primary" icon={<AppIcon name="newSession" size={15} />} onClick={() => run("new_session", t("New session"))}>{t("New session")}</Button>
            <Button icon={<AppIcon name="open" size={15} />} onClick={() => run("open", t("Open a project"))}>{t("Open a project")}</Button>
            <Button variant="minimal" icon={<AppIcon name="fileText" size={15} />} onClick={openDocumentation}>{t("Documentation")}</Button>
          </div>
        </div>
      </header>

      {steps.length > 0 && <section className="landing-onboarding" aria-label={t("Get started")}>
        <h2>{t("Get started")}</h2>
        <div className="landing-steps">
          {steps.includes("provider") && <div className="landing-step" data-step="provider">
            <AppIcon name="provider" size={18} aria-hidden="true" />
            <div><strong>{t("Set up a model provider")}</strong><p>{t("Sign in to a provider or add an API key, so that sessions can run.")}</p></div>
            <Button intent="primary" size="small" onClick={() => run("model_providers", t("Set up providers"))}>{t("Set up providers")}</Button>
          </div>}
          {steps.includes("project") && <div className="landing-step" data-step="project">
            <AppIcon name="folder" size={18} aria-hidden="true" />
            <div><strong>{t("Add your first project")}</strong><p>{t("Open the folder of a repository to work on it with an agent.")}</p></div>
            <Button intent="primary" size="small" onClick={() => run("open", t("Open a project"))}>{t("Open a project")}</Button>
          </div>}
        </div>
      </section>}

      <div className="landing-grid">
        <section className="landing-card landing-list" data-list="sessions" aria-label={t("Recent sessions")}>
          <header className="landing-card-header"><AppIcon name="chat" size={16} className="landing-card-icon" aria-hidden="true" /><h2>{t("Recent sessions")}</h2></header>
          {sessions === null ? <p className="landing-empty" aria-busy="true">{t("Loading…")}</p>
            : sessionsShown.length === 0 ? <p className="landing-empty">{t("No session yet. Send a first prompt to start one.")}</p>
            : <ul>{sessionsShown.map(session => <Row key={session.id} icon="chat" title={plainTitle(session.title) || t("New session")}
              detail={session.projectId ? projectNames.get(session.projectId) ?? null : t("Chat")} time={sessionTime(session.updatedAt, locale, now)} onOpen={() => shell.openSession(session.id)} />)}</ul>}
        </section>

        <section className="landing-card landing-list" data-list="projects" aria-label={t("Recent projects")}>
          <header className="landing-card-header"><AppIcon name="folder" size={16} className="landing-card-icon" aria-hidden="true" /><h2>{t("Recent projects")}</h2>
            {!shell.space.isDefault && <span className="landing-card-project">{shell.space.name}</span>}</header>
          {projects === null ? <p className="landing-empty" aria-busy="true">{t("Loading…")}</p>
            : projectsShown.length === 0 ? <p className="landing-empty">{t("No project yet. Open a folder to add one.")}</p>
            : <ul>{projectsShown.map(({ project, updatedAt }) => <Row key={project.id} icon="folder" title={project.name} detail={project.path}
              time={updatedAt ? sessionTime(updatedAt, locale, now) : null} onOpen={() => shell.openProject(project.id)} />)}</ul>}
        </section>

        {cards.map(card => <PluginCard key={card.id} card={card} onAction={activate} onCommand={runCommand} />)}

        <section className="landing-card landing-explore" aria-label={t("Explore")}>
          <header className="landing-card-header"><AppIcon name="search" size={16} className="landing-card-icon" aria-hidden="true" /><h2>{t("Explore")}</h2></header>
          <div className="landing-links">
            <Button variant="minimal" alignText="start" icon={<AppIcon name="fileText" size={15} />} onClick={openDocumentation}>{t("Documentation")}</Button>
            {explore.map(item => <Button key={item.label} variant="minimal" alignText="start" icon={<AppIcon name={item.icon} size={15} />} onClick={() => run(item.command, item.label)}>{item.label}</Button>)}
          </div>
        </section>
      </div>

      <footer className="landing-footer">
        <Switch className="landing-switch" checked={chosen.openAtStartup} label={t("Show at startup")} onChange={event => preferences.set("openAtStartup", event.currentTarget.checked)} />
        <Switch className="landing-switch" checked={chosen.animate} label={t("Animation")} onChange={event => preferences.set("animate", event.currentTarget.checked)} />
      </footer>
    </div>
  </div>;
}
