import assert from "node:assert/strict";
import test from "node:test";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { ShellLanguageContext } from "../shellLanguage";
import { createResumeHub, type ResumeApi, type ResumeHub } from "./resumeHub";
import { SessionResumeContext, TimelineResumeCard } from "./TimelineResumeCard";

const id = "0123456789abcdef0123456789abcdef";
const never = () => assert.fail("rendering must not act");

async function hubWith(resumeId: string | null, at: string): Promise<ResumeHub> {
  const api: ResumeApi = {
    pendingResume: async request => ({ status: "ok", epoch: "epoch", sessionId: request.sessionId,
      resume: resumeId ? { resumeId, resumeAt: at, providerKey: "claude-code", providerName: "Claude Code", attempt: 1 } : null }),
    cancelResume: never, resumeNow: never,
  };
  const hub = createResumeHub(api, "epoch", "session-1", { setTimeout: () => 0 as unknown as ReturnType<typeof setTimeout>, clearTimeout: () => {} });
  await hub.refresh();
  return hub;
}

const render = (hub: ResumeHub | null, locale: "en" | "fr" = "en") => renderToStaticMarkup(createElement(ShellLanguageContext.Provider, { value: { locale, choice: locale, setLanguage: never } },
  createElement(SessionResumeContext.Provider, { value: hub }, createElement(TimelineResumeCard, { offer: { id, at: "2026-10-27T13:05:30Z", providerName: "Claude Code" } }))));

test("the card of a resume that waits says when and offers Resume now and Cancel", async () => {
  const at = new Date(Date.now() + 3_600_000).toISOString();
  const html = render(await hubWith(id, at));
  assert.match(html, /role="group" aria-label="Automatic resume"/);
  assert.match(html, /Resumes (at|on) .* with Claude Code/);
  assert.match(html, /<button[^>]*>.*Resume now/);
  assert.match(html, /<button[^>]*>.*Cancel/);
  assert.match(render(await hubWith(id, at), "fr"), /Reprendre maintenant/);
});

test("the card shows nothing once its resume no longer waits, nor where nothing can resume", async () => {
  const at = new Date(Date.now() + 3_600_000).toISOString();
  assert.equal(render(await hubWith("fedcba9876543210fedcba9876543210", at)), "", "Another resume took its place.");
  assert.equal(render(await hubWith(null, at)), "");
  assert.equal(render(null), "");
});
