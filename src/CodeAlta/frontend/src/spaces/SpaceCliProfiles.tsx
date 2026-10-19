import { useRef, useState } from "react";
import { Button, HTMLSelect, InputGroup } from "@blueprintjs/core";
import type { IdentityCheckLine, SpaceCliProfileItem } from "#neoastra";
import { ActivitySpinner } from "../ActivitySpinner";
import { AppIcon } from "../AppIcon";
import type { MessageKey } from "../localization";
import { useShellLanguage } from "../shellLanguage";
import type { SpaceSectionProps } from "./spaceSection";
import type { SpaceIdentityApi } from "./SpaceIdentity";
import {
  cliLoginCommand, cliPresets, newProfile, profileItem, profileItems, profileProblem, profilesValid, readProfiles, sameProfiles,
  type CliProblem, type CliProfileCard, type CliVariable,
} from "./cliProfiles";

const problems: Record<CliProblem["kind"], MessageKey> = {
  name: "The name of a profile: 1 to 64 letters, digits, -, _ and ., such as azure.",
  duplicateName: "Another profile has this name.",
  noVariable: "A profile sets at least one variable.",
  variableName: "{variable} is not the name of a variable: letters, digits and _.",
  reserved: "{variable} is given by the Git and GitHub cards.",
  duplicateVariable: "{variable} is set twice.",
  value: "{variable} needs a value on one line: a path, a name or ${env:NAME}.",
  reference: "In {variable}, ${…} is only ${env:NAME}, a variable of your environment.",
  secret: "{variable} holds a secret: write ${env:NAME}, the variable of your environment that has it, never the secret itself.",
  conflict: "{variable} is given another value by the profile {other}.",
  check: "The check command is one line of at most 2048 characters.",
  login: "The login command is one line of at most 2048 characters.",
};

type Check = Readonly<{ running: boolean; lines: readonly IdentityCheckLine[] }>;

/**
 * The CLI profiles of the identity of a space: for each command line tool, the variables that choose its
 * configuration, login or context, a command that checks it and one that signs it in. They are saved together, when
 * a field is left and every profile can be saved; a tool without a profile keeps its usual configuration.
 */
export function SpaceCliProfiles({ id, spaceId, epoch, api, run, disabled, initial, home, platform, onSaved }: Readonly<{
  id: string; spaceId: string; epoch: string; api: SpaceIdentityApi; run: SpaceSectionProps["run"]; disabled: boolean;
  initial: readonly SpaceCliProfileItem[] | null; home: string | null; platform: string | null;
  /** Called when the profiles were saved: the file of the space has nothing left out any more. */
  onSaved?: () => void;
}>) {
  const { t } = useShellLanguage();
  const [saved, setSaved] = useState<readonly CliProfileCard[]>(() => readProfiles(initial));
  const [draft, setDraft] = useState<readonly CliProfileCard[]>(saved);
  const [checks, setChecks] = useState<Readonly<Record<string, Check | undefined>>>({});
  const [copied, setCopied] = useState<string | null>(null);
  const draftRef = useRef(draft);
  draftRef.current = draft;
  const savedRef = useRef(saved);
  savedRef.current = saved;
  // The last check asked for each card: the answer of an earlier one, for what the card said then, is dropped.
  const checking = useRef(new Map<string, number>());

  function edit(key: string, change: (card: CliProfileCard) => CliProfileCard) {
    setDraft(cards => cards.map(card => card.key === key ? change(card) : card));
    checking.current.set(key, (checking.current.get(key) ?? 0) + 1);
    setChecks(all => ({ ...all, [key]: undefined }));
  }

  // Saves the profiles when they changed and each can be saved; a profile being written keeps its card.
  async function save(cards: readonly CliProfileCard[] = draftRef.current) {
    if (sameProfiles(cards, savedRef.current) || !profilesValid(cards)) return;
    await run(async () => {
      const reply = await api.update({ expectedEpoch: epoch, spaceId, part: "cli", git: null, gitHub: null, jira: null, cli: profileItems(cards) },
        { timeoutMilliseconds: 30_000 });
      if (reply.status === "ok") {
        // The cards keep their keys: what the host stored is in the same order.
        const stored = readProfiles(reply.identity?.cli).map((card, index) => ({ ...card, key: cards[index]?.key ?? card.key }));
        setSaved(stored);
        setDraft(all => sameProfiles(all, cards) ? stored : all);
        onSaved?.();
      }
      return { ok: reply.status === "ok", status: reply.status, message: reply.message, space: null };
    });
  }

  // A change that is a choice (a profile or a variable added or removed) is saved at once when it can be.
  function choose(cards: readonly CliProfileCard[]) {
    setDraft(cards);
    void save(cards);
  }

  async function check(card: CliProfileCard) {
    const asked = (checking.current.get(card.key) ?? 0) + 1;
    checking.current.set(card.key, asked);
    setChecks(all => ({ ...all, [card.key]: { running: true, lines: all[card.key]?.lines ?? [] } }));
    let lines: readonly IdentityCheckLine[];
    try {
      const reply = await api.check({ expectedEpoch: epoch, part: "cli", git: null, gitHub: null, jira: null, profile: profileItem(card) }, { timeoutMilliseconds: 60_000 });
      lines = reply.lines;
    } catch { lines = [{ kind: "error", text: t("The check did not complete.") }]; }
    if (checking.current.get(card.key) !== asked) return;
    setChecks(all => ({ ...all, [card.key]: { running: false, lines } }));
  }

  async function copy(key: string, text: string) {
    try { await navigator.clipboard.writeText(text); setCopied(key); setTimeout(() => setCopied(null), 1500); } catch { /* Nothing was copied: the command stays to select. */ }
  }

  const input = (card: CliProfileCard, name: string, value: string, change: (value: string) => CliProfileCard, label: string, placeholder?: string) =>
    <InputGroup id={`${id}-cli-${card.key}-${name}`} size="small" fill value={value} disabled={disabled} spellCheck={false} autoComplete="off"
      aria-label={label} placeholder={placeholder} onChange={event => edit(card.key, () => change(event.target.value))} onBlur={() => void save()}
      onKeyDown={event => {
        if (event.key === "Enter") event.currentTarget.blur();
        else if (event.key === "Escape") { event.stopPropagation(); setDraft(savedRef.current); }
      }} />;

  const field = (card: CliProfileCard, name: string, label: MessageKey, value: string, change: (value: string) => CliProfileCard, placeholder?: string) =>
    <div className="space-identity-field">
      <label htmlFor={`${id}-cli-${card.key}-${name}`}>{t(label)}</label>
      <span className="space-identity-control">{input(card, name, value, change, t(label), placeholder)}</span>
    </div>;

  const present = new Set(draft.map(card => card.name.trim().toLowerCase()));
  // A change waits while a profile cannot be saved: the page says so, since the profiles are saved together.
  const waiting = !sameProfiles(draft, saved) && !profilesValid(draft);
  return <section className="space-identity-cli" aria-labelledby={`${id}-cli`}>
    <header>
      <h3 id={`${id}-cli`}>{t("Command line tools")}</h3>
      <p>{t("Each profile gives one tool the variables that choose its configuration, for the sessions, jobs, terminals and MCP servers of the space. A tool without a profile keeps its usual configuration.")}</p>
    </header>
    {draft.length > 0 && <div className="space-identity-cards">
      {draft.map((card, index) => {
        const problem = profileProblem(draft, index);
        const state = checks[card.key];
        const login = home && platform ? cliLoginCommand(card, home, platform) : null;
        const title = card.name.trim() || t("New profile");
        const setVariable = (at: number, change: Partial<CliVariable>) => ({ ...card, env: card.env.map((variable, place) => place === at ? { ...variable, ...change } : variable) });
        return <section key={card.key} className="space-identity-card" data-part="cli" data-profile={card.name.trim()} aria-labelledby={`${id}-cli-${card.key}`}>
          <header>
            <AppIcon name="terminal" size={15} />
            <h3 id={`${id}-cli-${card.key}`}>{title}</h3>
            <Button className="space-identity-check" size="small" variant="minimal" disabled={state?.running || !!problem}
              icon={state?.running ? <ActivitySpinner size={12} /> : <AppIcon name="play" size={13} />} text={t("Check")} onClick={() => void check(card)} />
            {!disabled && <Button className="space-identity-remove" size="small" variant="minimal" icon={<AppIcon name="trash" size={13} />}
              aria-label={t("Remove the profile")} title={t("Remove the profile")} onClick={() => choose(draftRef.current.filter(other => other.key !== card.key))} />}
          </header>
          {field(card, "name", "Name", card.name, name => ({ ...card, name }), "azure")}
          <div className="space-identity-field" role="group" aria-labelledby={`${id}-cli-${card.key}-variables`}>
            <span id={`${id}-cli-${card.key}-variables`} className="space-identity-label">{t("Variables")}</span>
            {card.env.map((variable, at) => <span key={at} className="space-identity-control space-identity-variable">
              {input(card, `variable-${at}`, variable.name, name => setVariable(at, { name }), t("Name of the variable"), "NAME")}
              {input(card, `value-${at}`, variable.value, value => setVariable(at, { value }), t("Value of {name}", { name: variable.name.trim() || "?" }),
                t("A path, a name or ${env:NAME}"))}
              {!disabled && <Button size="small" variant="minimal" icon={<AppIcon name="minus" size={13} />} aria-label={t("Remove the variable")} title={t("Remove the variable")}
                onClick={() => choose(draftRef.current.map(other => other.key === card.key ? { ...other, env: other.env.filter((_, place) => place !== at) } : other))} />}
            </span>)}
            {!disabled && <Button className="space-identity-add-variable" size="small" variant="minimal" icon={<AppIcon name="plus" size={13} />} text={t("Add a variable")}
              onClick={() => setDraft(cards => cards.map(other => other.key === card.key ? { ...other, env: [...other.env, { name: "", value: "" }] } : other))} />}
          </div>
          {field(card, "check", "Check command", card.check, check => ({ ...card, check }), t("Optional"))}
          {field(card, "login", "Login command", card.login, login => ({ ...card, login }), t("Optional"))}
          {login && <div className="space-identity-login">
            <span>{t("Sign in with this profile")}</span>
            <code title={login}>{login}</code>
            <Button size="small" variant="minimal" icon={<AppIcon name={copied === card.key ? "check" : "copy"} size={14} />}
              aria-label={t("Copy command")} title={t(copied === card.key ? "Copied" : "Copy command")} onClick={() => void copy(card.key, login)} />
          </div>}
          {problem && <p className="space-identity-problem" role="alert">{t(problems[problem.kind], { variable: problem.variable ?? "", other: problem.other ?? "" })}</p>}
          {state && state.lines.length > 0 && <ul className="space-identity-result" aria-label={t("Check of {name}", { name: title })}>
            {state.lines.map((line, place) => <li key={place} data-kind={line.kind}>
              <AppIcon name={line.kind === "ok" ? "check" : line.kind === "error" ? "error" : "info"} size={13} />
              <span>{line.text}</span>
            </li>)}
          </ul>}
        </section>;
      })}
    </div>}
    {waiting && <p className="space-identity-waiting">{t("The profiles are saved once each of them can be.")}</p>}
    {!disabled && <HTMLSelect className="space-identity-add" value="" aria-label={t("Add a CLI profile…")}
      onChange={event => {
        const chosen = event.currentTarget.value;
        if (!chosen) return;
        const preset = cliPresets.find(item => item.id === chosen) ?? null;
        const card = newProfile(preset, spaceId);
        // A preset is saved at once; another tool waits for its name and its variables.
        if (preset) choose([...draftRef.current, card]);
        else setDraft(cards => [...cards, card]);
      }}
      options={[{ value: "", label: t("Add a CLI profile…") },
        ...cliPresets.map(preset => ({ value: preset.id, label: preset.label, disabled: present.has(preset.id) })),
        { value: "other", label: t("Other tool") }]} />}
  </section>;
}
