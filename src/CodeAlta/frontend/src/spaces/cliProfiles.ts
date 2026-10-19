// The CLI profiles of a space as its page edits them: for each command line tool, the environment variables that
// select its configuration, login or context, and the commands that check it and sign it in. Only references are
// written here (paths, names of profiles or contexts, ${env:NAME}), never a secret.
import type { SpaceCliProfileItem } from "#neoastra";
import { expandHome } from "./identityCards";

export type CliVariable = Readonly<{ name: string; value: string }>;
/** A profile on the page; `key` only tells the cards apart while they are edited. */
export type CliProfileCard = Readonly<{ key: string; name: string; env: readonly CliVariable[]; check: string; login: string }>;

/** What can be wrong with a profile before it is saved, with what the message names. */
export type CliProblem = Readonly<
  | { kind: "name" | "duplicateName" | "noVariable" | "check" | "login"; variable?: undefined; other?: undefined }
  | { kind: "variableName" | "reserved" | "duplicateVariable" | "value" | "reference" | "secret"; variable: string; other?: undefined }
  | { kind: "conflict"; variable: string; other: string }>;

const text = (value: string | null | undefined) => value ?? "";
let next = 0;
/** A key for a card the page adds. */
export const cardKey = () => `cli-${++next}`;

/** The profiles of an identity as the host gives them, in their order. */
export function readProfiles(items: readonly SpaceCliProfileItem[] | null | undefined): readonly CliProfileCard[] {
  return (items ?? []).map(item => ({ key: cardKey(), name: text(item.name), check: text(item.check), login: text(item.login),
    env: (item.env ?? []).map(variable => ({ name: text(variable.name), value: text(variable.value) })) }));
}

const blank = (variable: CliVariable) => !variable.name.trim() && !variable.value.trim();

/** The profiles as the host takes them: trimmed, without the lines of variables left empty. */
export function profileItems(cards: readonly CliProfileCard[]): SpaceCliProfileItem[] {
  return cards.map(profileItem);
}

/** One profile as the host takes it. */
export function profileItem(card: CliProfileCard): SpaceCliProfileItem {
  return { name: card.name.trim(), check: card.check.trim() || null, login: card.login.trim() || null,
    env: card.env.filter(variable => !blank(variable)).map(variable => ({ name: variable.name.trim(), value: variable.value.trim() })) };
}

/** Whether two lists of profiles say the same, once their fields are trimmed. */
export const sameProfiles = (left: readonly CliProfileCard[], right: readonly CliProfileCard[]) =>
  JSON.stringify(profileItems(left)) === JSON.stringify(profileItems(right));

const nameShape = /^[A-Za-z0-9._-]{1,64}$/u;
const variableShape = /^[A-Za-z_][A-Za-z0-9_]{0,127}$/u;
const reserved = new Set(["GIT_AUTHOR_NAME", "GIT_AUTHOR_EMAIL", "GIT_COMMITTER_NAME", "GIT_COMMITTER_EMAIL", "GIT_SSH_COMMAND", "GIT_CONFIG_COUNT", "GH_CONFIG_DIR"]);
const secretWords = new Set(["TOKEN", "SECRET", "PASSWORD", "PASSWD", "PASSPHRASE", "APIKEY", "PAT"]);
const secretPairs = new Set(["API_KEY", "ACCESS_KEY", "PRIVATE_KEY", "CLIENT_KEY", "ACCOUNT_KEY"]);
const placeWords = new Set(["FILE", "PATH", "DIR", "HOME"]);
// One line of at most the length given, without control characters.
const isLine = (value: string, maximum: number) => value.length > 0 && value.length <= maximum && !/\p{Cc}/u.test(value);
const loneReference = /^\$\{env:[A-Za-z_][A-Za-z0-9_]{0,127}\}$/u;

/**
 * Whether the name of a variable says it holds a secret, as the host reads it: a token, a password, a secret key; not
 * a name that ends in a word of a place (`AZURE_FEDERATED_TOKEN_FILE`), which holds where a secret is.
 */
export function isSecretName(name: string): boolean {
  const words = name.toUpperCase().split("_").filter(Boolean);
  if (placeWords.has(words.at(-1) ?? "")) return false;
  return words.some((word, index) => secretWords.has(word) || index > 0 && secretPairs.has(`${words[index - 1]}_${word}`));
}

const isReserved = (name: string) => reserved.has(name.toUpperCase()) || /^GIT_CONFIG_(KEY|VALUE)_/iu.test(name);

// Every ${ starts a reference ${env:NAME}.
function badReference(value: string): boolean {
  for (let start = value.indexOf("${"); start >= 0; start = value.indexOf("${", start + 2)) {
    const end = value.indexOf("}", start);
    if (!value.startsWith("${env:", start) || end < 0 || !variableShape.test(value.slice(start + 6, end))) return true;
  }
  return false;
}

/** What is wrong with one profile among the others, checked as the host checks it; null when it can be saved. */
export function profileProblem(cards: readonly CliProfileCard[], index: number): CliProblem | null {
  const card = cards[index];
  const name = card.name.trim();
  if (!nameShape.test(name)) return { kind: "name" };
  if (cards.some((other, at) => at < index && other.name.trim().toLowerCase() === name.toLowerCase())) return { kind: "duplicateName" };
  const env = profileItem(card).env ?? [];
  if (env.length === 0) return { kind: "noVariable" };
  const seen = new Set<string>();
  for (const { name: variable, value } of env as { name: string; value: string }[]) {
    if (!variableShape.test(variable)) return { kind: "variableName", variable };
    if (isReserved(variable)) return { kind: "reserved", variable };
    if (seen.has(variable.toUpperCase())) return { kind: "duplicateVariable", variable };
    seen.add(variable.toUpperCase());
    if (!isLine(value, 2048)) return { kind: "value", variable };
    if (badReference(value)) return { kind: "reference", variable };
    if (isSecretName(variable) && !loneReference.test(value)) return { kind: "secret", variable };
    // Another profile before gives it another value: the variable would have two.
    for (const other of cards.slice(0, index)) {
      const given = (profileItem(other).env ?? []).find(item => item.name?.toUpperCase() === variable.toUpperCase());
      if (given && given.value !== value) return { kind: "conflict", variable, other: other.name.trim() };
    }
  }
  if (card.check.trim() && !isLine(card.check.trim(), 2048)) return { kind: "check" };
  if (card.login.trim() && !isLine(card.login.trim(), 2048)) return { kind: "login" };
  return null;
}

/** Whether every profile can be saved. */
export const profilesValid = (cards: readonly CliProfileCard[]) => cards.every((_, index) => profileProblem(cards, index) === null);

/** A preset: what the page fills a new profile with for a well-known tool; `{space}` is the identifier of the space. */
export type CliPreset = Readonly<{ id: string; label: string; env: readonly CliVariable[]; check: string; login: string }>;

export const cliPresets: readonly CliPreset[] = Object.freeze([
  { id: "azure", label: "Azure CLI", env: [{ name: "AZURE_CONFIG_DIR", value: "~/.azure-{space}" }],
    check: "az account show --query \"{user:user.name, subscription:name}\" -o tsv", login: "az login" },
  { id: "aws", label: "AWS CLI", env: [{ name: "AWS_PROFILE", value: "{space}" }], check: "aws sts get-caller-identity", login: "aws sso login" },
  { id: "gcloud", label: "Google Cloud CLI", env: [{ name: "CLOUDSDK_CONFIG", value: "~/.config/gcloud-{space}" }], check: "gcloud auth list", login: "gcloud auth login" },
  { id: "kubectl", label: "kubectl", env: [{ name: "KUBECONFIG", value: "~/.kube/{space}" }], check: "kubectl config current-context", login: "" },
  { id: "helm", label: "Helm", env: [{ name: "HELM_CONFIG_HOME", value: "~/.config/helm-{space}" }, { name: "HELM_CACHE_HOME", value: "~/.cache/helm-{space}" },
    { name: "HELM_DATA_HOME", value: "~/.local/share/helm-{space}" }], check: "helm env", login: "" },
  { id: "terraform", label: "Terraform", env: [{ name: "TF_CLI_CONFIG_FILE", value: "~/.terraformrc-{space}" }], check: "", login: "" },
  { id: "docker", label: "Docker", env: [{ name: "DOCKER_CONFIG", value: "~/.docker-{space}" }], check: "", login: "docker login" },
  { id: "gitlab", label: "GitLab CLI", env: [{ name: "GLAB_CONFIG_DIR", value: "~/.config/glab-{space}" }], check: "glab auth status", login: "glab auth login" },
]);

/** A new profile: from a preset, with the space in its paths, or an empty one for any other tool. */
export function newProfile(preset: CliPreset | null, spaceId: string): CliProfileCard {
  if (!preset) return { key: cardKey(), name: "", env: [{ name: "", value: "" }], check: "", login: "" };
  return { key: cardKey(), name: preset.id, check: preset.check, login: preset.login,
    env: preset.env.map(variable => ({ name: variable.name, value: variable.value.replaceAll("{space}", spaceId) })) };
}

type Piece = Readonly<{ literal: string; variable?: undefined } | { literal?: undefined; variable: string }>;

// A value in pieces: what is written as it is, and the variables ${env:NAME} it reads. ~ is the folder of the user at
// the start of the value, and of each path of a list, as the host reads it.
function pieces(value: string, home: string, platform: string): Piece[] {
  const separator = platform === "windows" ? ";" : ":";
  const parts: Piece[] = [];
  let last = 0;
  for (const match of value.matchAll(/\$\{env:([A-Za-z_][A-Za-z0-9_]*)\}/gu)) {
    if (match.index > last) parts.push({ literal: value.slice(last, match.index) });
    parts.push({ variable: match[1] });
    last = match.index + match[0].length;
  }
  if (last < value.length) parts.push({ literal: value.slice(last) });
  return parts.map((part, index) => part.literal === undefined ? part : {
    // The first path of a piece after a reference goes on from it: it does not start a path.
    literal: part.literal.split(separator).map((path, at) => (index === 0 || at > 0) && /^~([\\/]|$)/u.test(path) ? expandHome(path, home) : path).join(separator),
  });
}

/**
 * The command that signs a tool in with a profile, for the shell of the platform: its variables set first, then its
 * login command; PowerShell on Windows, a POSIX shell elsewhere. A reference `${env:NAME}` reads the variable there.
 */
export function cliLoginCommand(card: CliProfileCard, home: string, platform: string): string | null {
  const item = profileItem(card);
  if (!item.login) return null;
  const env = (item.env ?? []) as { name: string; value: string }[];
  if (platform === "windows") {
    const assignments = env.map(({ name, value }) => {
      const parts = pieces(value, home, platform).map(part => part.variable !== undefined ? `$env:${part.variable}` : `'${part.literal.replaceAll("'", "''")}'`);
      return `$env:${name}=${parts.length === 1 ? parts[0] : `(${parts.join(" + ")})`}`;
    });
    return [...assignments, item.login].join("; ");
  }
  const assignments = env.map(({ name, value }) => `${name}=${pieces(value, home, platform)
    .map(part => part.variable !== undefined ? `"\${${part.variable}}"` : `'${part.literal.replaceAll("'", "'\\''")}'`).join("")}`);
  return [...assignments, item.login].join(" ");
}
