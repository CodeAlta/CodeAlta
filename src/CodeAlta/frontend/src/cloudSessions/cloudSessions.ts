import type { CloudContinueResponse, CloudReadinessResponse, CloudSessionDetailsResponse, CloudStartRequest, CloudStartResponse, WorkspaceSession, cloudSessions as cloudSessionsApi } from "#neoastra";
import type { MessageKey } from "../localization";
import { quotePath, type CliShell } from "../claudeCli/claudeCli";

/** The calls of the host for cloud sessions; tests give their own. */
export type CloudSessionsApi = Pick<typeof cloudSessionsApi, "readiness" | "start" | "startState" | "cancelStart" | "bringHere" | "details" | "send" | "continueHere">;

/** Whether a provider of that type runs sessions in the cloud: Claude Code. */
export function providerHasCloudSessions(type: string | null | undefined): boolean {
  return type === "claude-code";
}

/** The cloud session a session runs as, or ran as, as the Explorer is given it. */
export type CloudView = Readonly<{ id: string; url: string | null; title: string | null; continued: boolean; continuing: boolean }>;

const cloudSessionId = /^(?:session|cse)_[A-Za-z0-9]{8,64}$/u;

/** An address of claude.ai, which the window opens in the browser; anything else is none. */
export function claudeAiLink(value: unknown): string | null {
  return typeof value === "string" && value.length <= 512 && value.startsWith("https://claude.ai/") && !/[\s"<>\\]/u.test(value) ? value : null;
}

/** The cloud session of a session, when it has a well-formed one. */
export function cloudOf(session: Readonly<Pick<WorkspaceSession, "cloud">> | null | undefined): CloudView | null {
  const cloud = session?.cloud;
  if (!cloud || typeof cloud !== "object" || typeof cloud.id !== "string" || !cloudSessionId.test(cloud.id)) return null;
  return {
    id: cloud.id, url: claudeAiLink(cloud.url), title: typeof cloud.title === "string" && cloud.title.trim() ? cloud.title.trim().slice(0, 256) : null,
    continued: cloud.continued === true, continuing: cloud.continuing === true,
  };
}

/** Whether a session runs in the cloud: it has a cloud session that was not brought here yet. */
export function inCloud(session: Readonly<Pick<WorkspaceSession, "cloud">> | null | undefined): boolean {
  const cloud = cloudOf(session);
  return !!cloud && !cloud.continued;
}

/**
 * The cloud session a pasted text names: a link of claude.ai/code, or the identifier itself. Null for anything else,
 * a link of another site included.
 */
export function pastedCloudSession(text: string): string | null {
  const value = text.trim();
  if (!value || value.length > 2048) return null;
  if (cloudSessionId.test(value)) return value;
  let url: URL;
  try { url = new URL(value); } catch { return null; }
  if (url.protocol !== "https:" || url.hostname !== "claude.ai") return null;
  const id = url.pathname.split("/").filter(Boolean).find(part => cloudSessionId.test(part));
  return id ?? null;
}

/** A line of what a project and an account allow before a cloud session starts. */
export type ReadinessLine = Readonly<{ tone: "ok" | "warning" | "info"; key: MessageKey; parameters?: Readonly<Record<string, string | number>> }>;

/**
 * What the readiness of a project says before a cloud session starts: the account, the branch it starts from, what
 * stays on this computer, and that it counts against the plan. Claude Code decides itself when the account is not known.
 */
export function readinessLines(readiness: CloudReadinessResponse, provider: string): readonly ReadinessLine[] {
  const lines: ReadinessLine[] = [];
  if (readiness.account === "subscription") lines.push({ tone: "ok", key: "{provider} is signed in with a Claude plan", parameters: { provider } });
  else if (readiness.account === "not_subscription")
    lines.push({ tone: "warning", key: "{provider} is not signed in with a Claude plan: cloud sessions need Pro, Max, Team or Enterprise.", parameters: { provider } });
  else lines.push({ tone: "info", key: "Claude Code checks the sign-in of {provider} when the session starts.", parameters: { provider } });
  if (!readiness.repository) {
    lines.push({ tone: "warning", key: "The folder is not in a git repository: Claude Code may refuse to start a cloud session from it." });
    return [...lines, usage];
  }
  if (readiness.branch) lines.push({ tone: "ok", key: "Starts from the branch {branch}", parameters: { branch: readiness.branch } });
  if (!readiness.gitHub) lines.push({ tone: "info", key: "The repository is not on GitHub: Claude Code uploads it, and the changes made in the cloud come back as the conversation only, not as a branch." });
  else if (readiness.unpushed === null && readiness.branch)
    lines.push({ tone: "warning", key: "The branch {branch} is not on GitHub: the cloud session may start from another branch.", parameters: { branch: readiness.branch } });
  else if (readiness.unpushed === 1) lines.push({ tone: "warning", key: "1 commit is not on GitHub: the cloud session starts without it." });
  else if (readiness.unpushed !== null && readiness.unpushed > 1)
    lines.push({ tone: "warning", key: "{count} commits are not on GitHub: the cloud session starts without them.", parameters: { count: readiness.unpushed } });
  if (readiness.changed === 1) lines.push({ tone: "warning", key: "1 file has changes that are not committed: they stay here, unless Claude Code uploads the folder with them." });
  else if (readiness.changed > 1)
    lines.push({ tone: "warning", key: "{count} files have changes that are not committed: they stay here, unless Claude Code uploads the folder with them.", parameters: { count: readiness.changed } });
  return [...lines, usage];
}

const usage: ReadinessLine = { tone: "info", key: "A cloud session counts against your Claude plan. CodeAlta's statistics do not see it." };

/** What a start that is running says, by its step. */
export function startStepKey(step: string | null | undefined): MessageKey {
  return step === "asking" ? "Claude Code asks a question: answer it in the terminal tab." : "Starting Claude Code…";
}

/** What a start that ended without a session says: a sentence of ours, then what Claude Code said. */
export function startFailure(status: string, message: string | null | undefined): Readonly<{ key: MessageKey; said: string | null }> {
  const said = typeof message === "string" && message.trim() ? message.trim().slice(0, 2000) : null;
  const key: MessageKey = status === "needs_claude_ai" ? "Cloud sessions need Claude Code signed in with a claude.ai account (Pro, Max, Team or Enterprise)."
    : status === "terminal_unavailable" ? "Claude Code needs a terminal to start a cloud session, and none could be opened."
    : status === "canceled" ? "The start was stopped. Nothing was sent to the cloud."
    : status === "lost" ? "CodeAlta lost track of the start. Look for the session in the list of the project, or on claude.ai, before you send again."
    : status === "unrecorded" ? "The cloud session runs, but CodeAlta could not record it. Open it on claude.ai, or bring it here."
    : status === "unavailable" || status === "provider_unavailable" ? "This provider cannot run sessions in the cloud."
    : status === "project_missing" ? "The project is no longer there."
    : status === "stale_epoch" ? "The host changed. Reload the window."
    : status === "closed" || status === "unconfigured" ? "Cloud sessions cannot be started from this window."
    : status === "provider_outside_space" ? "This provider is not in the space of the project."
    : "Claude Code could not start the cloud session.";
  return { key, said };
}

/** What a message that was not sent to the cloud session says. */
export function sendFailure(status: string, message: string | null | undefined): Readonly<{ key: MessageKey; said: string | null }> {
  const said = typeof message === "string" && message.trim() ? message.trim().slice(0, 2000) : null;
  const key: MessageKey = status === "archived" ? "The cloud session is archived on claude.ai: it takes no more messages. You can still continue it here."
    : status === "not_found" ? "Claude Code does not find this cloud session any more. It may have been deleted on claude.ai."
    : status === "needs_claude_ai" ? "Cloud sessions need Claude Code signed in with a claude.ai account (Pro, Max, Team or Enterprise)."
    : status === "policy" ? "The policy of your organization does not allow this message to be sent."
    : status === "busy" ? "The session is busy: it is being brought here. Try again in a moment."
    : status === "not_cloud" ? "This session no longer runs in the cloud."
    : status === "stale_epoch" ? "The host changed. Reload the window."
    : "Claude Code could not send the message.";
  return { key, said };
}

/** What a return to this computer that did not happen says. */
export function continueFailure(reply: Readonly<Pick<CloudContinueResponse, "status" | "message" | "changedFiles">>): Readonly<{ key: MessageKey; said: string | null;
  parameters?: Readonly<Record<string, number>> }> {
  const said = typeof reply.message === "string" && reply.message.trim() ? reply.message.trim().slice(0, 2000) : null;
  if (reply.status === "uncommitted_changes") return reply.changedFiles === 1
    ? { key: "1 file of the folder has changes that are not committed: Claude Code would switch its branch. Commit them, or continue in a new worktree.", said: null }
    : { key: "{count} files of the folder have changes that are not committed: Claude Code would switch its branch. Commit them, or continue in a new worktree.", said: null,
      parameters: { count: reply.changedFiles } };
  const key: MessageKey = reply.status === "busy" ? "The session is busy. Try again in a moment."
    : reply.status === "not_cloud" ? "This session no longer runs in the cloud."
    : reply.status === "worktree_failed" ? "The worktree could not be created."
    : reply.status === "stale_epoch" ? "The host changed. Reload the window."
    : reply.status === "unavailable" ? "The provider of this session cannot reach its cloud session."
    : "Claude Code could not bring the cloud session here.";
  return { key, said };
}

/** A start that is followed: its step while it runs, then its outcome. */
export type CloudStartState = Readonly<{ requestId: string; projectId: string; step: string | null; outcome: CloudStartResponse | null }>;

/**
 * Starts a cloud session and follows it: the host answers once the session exists, or why there is none, and says
 * meanwhile where it is. Its outcome is taken from whichever comes first, the answer of the start or the state the
 * host keeps by request, so that an answer that is late or lost never holds the window. A host that no longer knows
 * the request after the answer was lost (it restarted) ends it as `lost`.
 */
export async function runCloudStart(api: Pick<CloudSessionsApi, "start" | "startState">, request: CloudStartRequest, onStep: (step: string) => void,
  wait: (milliseconds: number) => Promise<void> = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds))): Promise<CloudStartResponse> {
  // Undefined while the answer is awaited, null once it was lost.
  let answer: CloudStartResponse | null | undefined;
  void api.start(request, { timeoutMilliseconds: 30 * 60_000 }).then(value => { answer = value; }, () => { answer = null; });
  for (let unknown = 0; ;) {
    if (answer) return answer;
    await wait(400);
    if (answer) return answer;
    const state = await api.startState({ expectedEpoch: request.expectedEpoch, requestId: request.requestId }, { timeoutMilliseconds: 5000 }).catch(() => null);
    if (answer) return answer;
    if (state?.status === "running") { if (state.step) onStep(state.step); continue; }
    if (state && state.status !== "unknown") return state;
    if (answer === null && ++unknown >= 5) return { status: "lost", epoch: null, requestId: request.requestId, sessionId: null, cloudSessionId: null, url: null,
      message: null, terminalId: null, step: null };
  }
}

/**
 * The command a terminal is given to bring a cloud session back: the one of the host, which starts with `claude`, run
 * with the executable of the provider when the host names it, as the sign-in command of a provider is.
 */
export function bringBackCommand(command: string, executable: string | null | undefined, shell: CliShell): string {
  if (!executable?.trim() || !command.startsWith("claude ")) return command;
  const program = quotePath(executable, shell);
  return `${shell.windows ? `& ${program}` : program} ${command.slice("claude ".length)}`;
}

/** A start of a cloud session the window waits for, kept so that a reload finds it again. */
export type PendingCloudStart = Readonly<{ requestId: string; projectId: string; epoch: string }>;
export const pendingCloudStartKey = "codealta.desktop.cloudStart.v1";

/** The start a reload finds, as it was stored; anything else is none. */
export function readPendingCloudStart(read: () => string | null): PendingCloudStart | null {
  try {
    const raw = read();
    if (!raw || raw.length > 1024) return null;
    const value = JSON.parse(raw) as Record<string, unknown>;
    const text = (item: unknown) => typeof item === "string" && item.length > 0 && item.length <= 256;
    return value && text(value.requestId) && text(value.projectId) && text(value.epoch)
      ? { requestId: value.requestId as string, projectId: value.projectId as string, epoch: value.epoch as string } : null;
  } catch { return null; }
}

/** A message the user sent to the cloud session, or the task it started with. */
export type CloudMessage = Readonly<{ text: string; at: string | null; task: boolean }>;

/** The messages of the card of a session in the cloud: the task first, then what was sent, oldest first. */
export function cloudMessages(details: CloudSessionDetailsResponse | null): readonly CloudMessage[] {
  if (!details || details.status !== "ok") return [];
  const messages: CloudMessage[] = [];
  if (details.task?.trim()) messages.push({ text: details.task.trim(), at: details.startedAt, task: true });
  for (const sent of details.sent.slice(-50)) if (typeof sent.text === "string" && sent.text.trim()) messages.push({ text: sent.text.trim(), at: sent.at, task: false });
  return messages;
}
