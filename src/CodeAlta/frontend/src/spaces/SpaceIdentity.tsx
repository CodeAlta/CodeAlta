import { useEffect, useRef, useState, type ReactNode } from "react";
import { Button, Card, HTMLSelect, InputGroup, Switch } from "@blueprintjs/core";
import { desktopShell, spaceIdentity, type IdentityCheckLine, type SpaceIdentityResponse, type SpaceIdentityUsual } from "#neoastra";
import { ActivitySpinner } from "../ActivitySpinner";
import { AppIcon } from "../AppIcon";
import { BrandIcon, type BrandIconName } from "../BrandIcon";
import { pickFolder } from "../folderPicker";
import type { MessageKey } from "../localization";
import { useShellLanguage } from "../shellLanguage";
import type { SpaceSectionProps } from "./spaceSection";
import {
  cardProblem, emptyCard, gitHubItem, gitHubLoginCommand, gitItem, jiraItem, readCards, sameCard,
  type CardProblem, type GitSigning, type IdentityCards, type IdentityPart,
} from "./identityCards";

export type SpaceIdentityApi = Pick<typeof spaceIdentity, "get" | "update" | "check">;

const problems: Record<CardProblem, MessageKey> = {
  email: "This is not an email.",
  variable: "The name of a variable: letters, digits and _, such as CONTOSO_JIRA_TOKEN.",
  signingKey: "Signing needs the key to sign with.",
  site: "The address of a Jira site, such as contoso.atlassian.net.",
};

type Check = Readonly<{ running: boolean; lines: readonly IdentityCheckLine[] }>;

/**
 * The section "Identity" of the page of a space: the Git, GitHub and Jira identity its sessions work under, each on
 * a card of its own. A field left empty keeps the usual setting of the user, shown in it. Each card is saved when a
 * field is left, and checked with the tool it is for.
 */
export function SpaceIdentity({ space, hub, canEdit, run, api = spaceIdentity }: SpaceSectionProps & Readonly<{ api?: SpaceIdentityApi }>) {
  const { t } = useShellLanguage();
  const [saved, setSaved] = useState<IdentityCards>(() => readCards(null));
  const [draft, setDraft] = useState<IdentityCards>(() => readCards(null));
  const [usual, setUsual] = useState<SpaceIdentityUsual | null>(null);
  const [available, setAvailable] = useState(false);
  const [checks, setChecks] = useState<Partial<Record<IdentityPart, Check>>>({});
  const [copied, setCopied] = useState(false);
  const draftRef = useRef(draft);
  draftRef.current = draft;
  const savedRef = useRef(saved);
  savedRef.current = saved;

  // The identity as the file has it, read again for another space and when the host connects.
  // A page drawn without a host (its tests, a hub of a window without spaces) has nothing to read.
  const epoch = hub.hostEpoch?.() ?? null;
  useEffect(() => {
    if (!epoch) { setAvailable(false); return; }
    let current = true;
    void api.get({ expectedEpoch: epoch, spaceId: space.id }, { timeoutMilliseconds: 30_000 }).then((reply: SpaceIdentityResponse) => {
      if (!current) return;
      setAvailable(reply.status === "ok");
      if (reply.status !== "ok") return;
      const cards = readCards(reply.identity);
      setSaved(cards); setDraft(cards); setUsual(reply.usual); setChecks({});
    }, () => { if (current) setAvailable(false); });
    return () => { current = false; };
  }, [api, epoch, space.id]);

  if (!available) return null;
  // The fields stay usable while a card is saved: the next field is typed in while the last one is written, and a
  // save always carries its whole card.
  const disabled = !canEdit;
  const id = `space-${space.id}-identity`;

  function edit<P extends IdentityPart>(part: P, change: Partial<IdentityCards[P]>) {
    setDraft(cards => ({ ...cards, [part]: { ...cards[part], ...change } }));
    setChecks(all => ({ ...all, [part]: undefined }));
  }

  // Saves a card that changed and can be saved; another card being written stays as it is on the page.
  async function save(part: IdentityPart, cards: IdentityCards = draftRef.current) {
    if (!epoch || sameCard(part, cards[part], savedRef.current[part]) || cardProblem(part, cards)) return;
    await run(async () => {
      const reply = await api.update({ expectedEpoch: epoch, spaceId: space.id, part, git: part === "git" ? gitItem(cards.git) : null,
        gitHub: part === "github" ? gitHubItem(cards.github) : null, jira: part === "jira" ? jiraItem(cards.jira) : null }, { timeoutMilliseconds: 30_000 });
      if (reply.status === "ok") {
        const stored = readCards(reply.identity)[part];
        setSaved(all => ({ ...all, [part]: stored }));
        setDraft(all => sameCard(part, all[part], cards[part]) ? { ...all, [part]: stored } : all);
      }
      return { ok: reply.status === "ok", status: reply.status, message: reply.message, space: null };
    });
  }

  // A choice is saved as soon as it is made.
  function choose<P extends IdentityPart>(part: P, change: Partial<IdentityCards[P]>) {
    const cards = { ...draftRef.current, [part]: { ...draftRef.current[part], ...change } };
    setDraft(cards); setChecks(all => ({ ...all, [part]: undefined }));
    void save(part, cards);
  }

  async function check(part: IdentityPart) {
    if (!epoch) return;
    const cards = draftRef.current;
    setChecks(all => ({ ...all, [part]: { running: true, lines: all[part]?.lines ?? [] } }));
    let lines: readonly IdentityCheckLine[];
    try {
      const reply = await api.check({ expectedEpoch: epoch, part, git: part === "git" ? gitItem(cards.git) : null,
        gitHub: part === "github" ? gitHubItem(cards.github) : null, jira: part === "jira" ? jiraItem(cards.jira) : null }, { timeoutMilliseconds: 60_000 });
      lines = reply.lines;
    } catch { lines = [{ kind: "error", text: t("The check did not complete.") }]; }
    setChecks(all => ({ ...all, [part]: { running: false, lines } }));
  }

  const field = (part: IdentityPart, name: string, label: MessageKey, value: string, placeholder: string | null, change: (value: string) => void, extra?: ReactNode) =>
    <label className="space-identity-field" htmlFor={`${id}-${part}-${name}`}>
      <span>{t(label)}</span>
      <span className="space-identity-control">
        <InputGroup id={`${id}-${part}-${name}`} size="small" fill value={value} disabled={disabled} spellCheck={false} autoComplete="off"
          placeholder={placeholder ?? t("Usual settings")} onChange={event => change(event.target.value)} onBlur={() => void save(part)}
          onKeyDown={event => {
            if (event.key === "Enter") event.currentTarget.blur();
            else if (event.key === "Escape") { event.stopPropagation(); setDraft(all => ({ ...all, [part]: savedRef.current[part] })); }
          }} />
        {extra}
      </span>
    </label>;

  const git = draft.git, github = draft.github, jira = draft.jira;
  const login = usual ? gitHubLoginCommand(github.configDir, usual.home, usual.platform) : null;
  const usualSigning = usual?.signingFormat ? `${t("Usual")} (${usual.signingFormat})` : t("Usual");
  async function browse() {
    const picked = await pickFolder(desktopShell.pickFolder, t("GitHub profile folder"), github.configDir.trim() || usual?.gitHubFolder || null);
    if (picked.status === "ok") choose("github", { configDir: picked.path });
  }
  async function copy(text: string) {
    try { await navigator.clipboard.writeText(text); setCopied(true); setTimeout(() => setCopied(false), 1500); } catch { /* Nothing was copied: the command stays to select. */ }
  }

  return <Card className="appearance-settings space-section space-identity" aria-labelledby={id}>
    <h2 id={id}>{t("Identity")}</h2>
    <div className="space-identity-cards">
      <IdentityCard id={id} part="git" brand="git" title="Git" empty={emptyCard("git", git)} check={checks.git} problem={cardProblem("git", draft)} onCheck={check}>
        {field("git", "name", "Name", git.name, usual?.gitName ?? null, name => edit("git", { name }))}
        {field("git", "email", "Email", git.email, usual?.gitEmail ?? null, email => edit("git", { email }))}
        {field("git", "ssh", "SSH key for fetch and push", git.sshKey, null, sshKey => edit("git", { sshKey }))}
        <label className="space-identity-field" htmlFor={`${id}-git-sign`}>
          <span>{t("Signing")}</span>
          <HTMLSelect id={`${id}-git-sign`} className="space-identity-control" fill value={git.sign} disabled={disabled}
            onChange={event => choose("git", { sign: event.currentTarget.value as GitSigning })}
            options={[{ value: "", label: usualSigning }, { value: "off", label: t("Off") }, { value: "ssh", label: "SSH" }, { value: "gpg", label: "GPG" }]} />
        </label>
        {(git.sign === "ssh" || git.sign === "gpg" || git.signingKey) && field("git", "signing", git.sign === "gpg" ? "GPG key id" : "Signing key", git.signingKey,
          usual?.signingKey ?? null, signingKey => edit("git", { signingKey }))}
        <Switch className="space-identity-switch" checked={git.signOff} disabled={disabled} label={t("Signed-off-by")}
          onChange={event => choose("git", { signOff: event.currentTarget.checked })} />
      </IdentityCard>
      <IdentityCard id={id} part="github" brand="github" title="GitHub" empty={emptyCard("github", github)} check={checks.github} problem={null} onCheck={check}>
        {field("github", "folder", "Profile folder", github.configDir, usual?.gitHubFolder ?? null, configDir => edit("github", { configDir }),
          canEdit && <Button size="small" variant="minimal" icon={<AppIcon name="folder" size={14} />} disabled={disabled}
            aria-label={t("Browse…")} title={t("Browse…")} onClick={() => void browse()} />)}
        {login && <div className="space-identity-login">
          <span>{t("Sign in with this profile")}</span>
          <code title={login}>{login}</code>
          <Button size="small" variant="minimal" icon={<AppIcon name={copied ? "check" : "copy"} size={14} />}
            aria-label={t("Copy command")} title={t(copied ? "Copied" : "Copy command")} onClick={() => void copy(login)} />
        </div>}
      </IdentityCard>
      <IdentityCard id={id} part="jira" brand="jira" title="Jira" empty={emptyCard("jira", jira)} check={checks.jira} problem={cardProblem("jira", draft)} onCheck={check}>
        {field("jira", "site", "Site", jira.site, null, site => edit("jira", { site }))}
        {field("jira", "email", "Email", jira.email, usual?.jiraEmail ?? null, email => edit("jira", { email }))}
        {field("jira", "token", "Token variable", jira.tokenEnv, usual?.jiraTokenVariable ?? null, tokenEnv => edit("jira", { tokenEnv }))}
      </IdentityCard>
    </div>
  </Card>;
}

// One card of the identity: its header with the check, its fields, and what the check found.
function IdentityCard({ id, part, brand, title, empty, check: state, problem, onCheck, children }: {
  id: string; part: IdentityPart; brand: BrandIconName; title: string; empty: boolean; check: Check | undefined; problem: CardProblem | null;
  onCheck: (part: IdentityPart) => void; children: ReactNode;
}) {
  const { t } = useShellLanguage();
  return <section className="space-identity-card" aria-labelledby={`${id}-${part}`} data-part={part}>
    <header>
      <BrandIcon name={brand} size={16} />
      <h3 id={`${id}-${part}`}>{title}</h3>
      {empty && <span className="space-identity-usual">{t("Usual settings")}</span>}
      <Button className="space-identity-check" size="small" variant="minimal" disabled={state?.running || !!problem}
        icon={state?.running ? <ActivitySpinner size={12} /> : <AppIcon name="play" size={13} />} text={t("Check")} onClick={() => onCheck(part)} />
    </header>
    {children}
    {problem && <p className="space-identity-problem" role="alert">{t(problems[problem])}</p>}
    {state && state.lines.length > 0 && <ul className="space-identity-result" aria-label={t("Check of {name}", { name: title })}>
      {state.lines.map((line, index) => <li key={index} data-kind={line.kind}>
        <AppIcon name={line.kind === "ok" ? "check" : line.kind === "error" ? "error" : "info"} size={13} />
        <span>{line.text}</span>
      </li>)}
    </ul>}
  </section>;
}
