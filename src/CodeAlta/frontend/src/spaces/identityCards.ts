// The identity of a space as its page edits it: one card for Git, GitHub and Jira, each saved on its own. A field
// left empty is the usual setting of the user. Only references are written here (paths, emails, names of
// variables), never a secret.
import type { SpaceGitIdentityItem, SpaceGitHubIdentityItem, SpaceIdentityItem, SpaceJiraIdentityItem } from "#neoastra";

/** How a space signs its commits; empty for the usual way. */
export type GitSigning = "" | "off" | "ssh" | "gpg";

export type GitCard = Readonly<{ name: string; email: string; sshKey: string; sign: GitSigning; signingKey: string; signOff: boolean }>;
export type GitHubCard = Readonly<{ configDir: string }>;
export type JiraCard = Readonly<{ site: string; email: string; tokenEnv: string }>;
export type IdentityCards = Readonly<{ git: GitCard; github: GitHubCard; jira: JiraCard }>;
export type IdentityPart = keyof IdentityCards;

/** What can be wrong with a card before it is saved. */
export type CardProblem = "email" | "variable" | "signingKey" | "site";

export const identityParts: readonly IdentityPart[] = Object.freeze(["git", "github", "jira"]);

const signings: readonly GitSigning[] = ["off", "ssh", "gpg"];
const text = (value: string | null | undefined) => value ?? "";

/** The cards of an identity as the host gives it; a card that sets nothing is empty. */
export function readCards(identity: SpaceIdentityItem | null | undefined): IdentityCards {
  const git = identity?.git, github = identity?.gitHub, jira = identity?.jira;
  const sign = signings.find(value => value === git?.sign) ?? "";
  return {
    git: { name: text(git?.name), email: text(git?.email), sshKey: text(git?.sshKey), sign, signingKey: text(git?.signingKey), signOff: git?.signOff === true },
    github: { configDir: text(github?.configDir) },
    jira: { site: text(jira?.site), email: text(jira?.email), tokenEnv: text(jira?.tokenEnv) },
  };
}

const value = (field: string) => field.trim() || null;

/** The Git card as the host takes it: an empty field is null, the usual setting. */
export const gitItem = (card: GitCard): SpaceGitIdentityItem => ({ name: value(card.name), email: value(card.email), sshKey: value(card.sshKey),
  sign: card.sign || null, signingKey: value(card.signingKey), signOff: card.signOff ? true : null });
export const gitHubItem = (card: GitHubCard): SpaceGitHubIdentityItem => ({ configDir: value(card.configDir) });
export const jiraItem = (card: JiraCard): SpaceJiraIdentityItem => ({ site: value(card.site), email: value(card.email), tokenEnv: value(card.tokenEnv) });

/** Whether two cards of the same part say the same, once their fields are trimmed. */
export function sameCard<P extends IdentityPart>(part: P, left: IdentityCards[P], right: IdentityCards[P]): boolean {
  const item = (card: IdentityCards[P]) => JSON.stringify(part === "git" ? gitItem(card as GitCard) : part === "github" ? gitHubItem(card as GitHubCard) : jiraItem(card as JiraCard));
  return item(left) === item(right);
}

/** Whether a card sets nothing: the space keeps the usual settings for that part. */
export function emptyCard(part: IdentityPart, card: IdentityCards[IdentityPart]): boolean {
  return sameCard(part, card, readCards(null)[part]);
}

const emailShape = /^[^\s@]+@[^\s@]+$/u;
const variableShape = /^[A-Za-z_][A-Za-z0-9_]{0,127}$/u;
const siteShape = /^(https:\/\/)?[A-Za-z0-9.-]+\.[A-Za-z0-9-]+\/?$/u;

/** What is wrong with a card, checked as the host checks it; null when it can be saved. */
export function cardProblem(part: IdentityPart, cards: IdentityCards): CardProblem | null {
  if (part === "git") {
    const git = cards.git;
    if (git.email.trim() && !emailShape.test(git.email.trim())) return "email";
    return (git.sign === "ssh" || git.sign === "gpg") && !git.signingKey.trim() ? "signingKey" : null;
  }
  if (part === "jira") {
    const jira = cards.jira;
    if (jira.site.trim() && !siteShape.test(jira.site.trim())) return "site";
    if (jira.email.trim() && !emailShape.test(jira.email.trim())) return "email";
    return jira.tokenEnv.trim() && !variableShape.test(jira.tokenEnv.trim()) ? "variable" : null;
  }
  return null;
}

/** A path the user wrote, with `~` read as the folder of the user. */
export function expandHome(path: string, home: string): string {
  const trimmed = path.trim();
  if (trimmed === "~") return home;
  if (!/^~[\\/]/u.test(trimmed)) return trimmed;
  const separator = home.includes("\\") ? "\\" : "/";
  return home.replace(/[\\/]+$/u, "") + separator + trimmed.slice(2).replace(/[\\/]/gu, separator);
}

/**
 * The command that signs the GitHub CLI in with the profile of a folder, for the shell of the platform:
 * PowerShell on Windows, a POSIX shell elsewhere. The folder is quoted as one word.
 */
export function gitHubLoginCommand(folder: string, home: string, platform: string): string | null {
  if (!folder.trim()) return null;
  const path = expandHome(folder, home);
  return platform === "windows"
    ? `$env:GH_CONFIG_DIR='${path.replaceAll("'", "''")}'; gh auth login`
    : `GH_CONFIG_DIR='${path.replaceAll("'", "'\\''")}' gh auth login`;
}
