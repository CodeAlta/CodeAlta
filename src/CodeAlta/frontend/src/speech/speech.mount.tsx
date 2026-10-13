// Test-owned mount of the production microphone button, prompt editor, download notice and Speech settings
// page against a host and a microphone the test plays (speechFixture.ts): no native host, no model, no network.
import { StrictMode, useRef, useState } from "react";
import { createRoot } from "react-dom/client";
import { Button } from "@blueprintjs/core";
import { AppIcon } from "../AppIcon";
import { ComposerSurface } from "../ComposerSurface";
import type { Locale } from "../localization";
import type { PromptInput } from "../PromptEditor";
import { settingsNavigation } from "../settingsNavigation";
import { ShellLanguageContext } from "../shellLanguage";
import { MicButton } from "./MicButton";
import { SpeechContext, type Speech } from "./speech";
import { createFixtureHost, createFixtureMicrophone } from "./speechFixture";
import { SpeechSettings } from "./SpeechSettings";
import { useSpeechInstall } from "./useSpeechInstall";

// What a test asks of the page before it loads: a model that is already in the cache, and how long a dictation may
// hear nothing before it is said (a test does not sit through eight seconds; the others never see the notice).
const host = createFixtureHost({ installed: localStorage.getItem("speechFixture.installed")?.split(",") ?? [], send: localStorage.getItem("speechFixture.send") ?? undefined });
const microphone = createFixtureMicrophone();
const warnAfter = Number(localStorage.getItem("speechFixture.warnAfter")) || 600_000;
// And how long it may hear nothing before it stops by itself.
const idleAfter = Number(localStorage.getItem("speechFixture.idleAfter")) || 600_000;
type ComposerState = { text: string; changes: string[]; sent: string[]; setDisabled: (value: boolean) => void; setVisible: (value: boolean) => void };
const composers: Record<string, ComposerState> = {};
const fixture = { host, microphone, composers, opened: [] as string[], renders: 0, speech: null as Speech | null,
  /** How long the Test of the settings keeps the microphone open; the test of the limit shortens it before it opens the page. */
  testLimit: 30_000,
  setLocale: (_value: Locale) => { }, setComposers: (_value: boolean) => { }, setSettings: (_value: boolean) => { }, setSize: (_value: { width: number; height: number }) => { } };
Object.assign(window, { speechFixture: fixture });
settingsNavigation.subscribe(page => fixture.opened.push(page));

// A composer as the session one keeps it: the text is state of React, and every change of the editor goes through it.
function Composer({ name, active, speech }: { name: string; active: boolean; speech: Speech }) {
  const [text, setText] = useState("");
  const [disabled, setDisabled] = useState(false);
  const [visible, setVisible] = useState(true);
  const input = useRef<PromptInput>(null);
  const state = composers[name] ??= { text: "", changes: [], sent: [], setDisabled, setVisible };
  Object.assign(state, { text, setDisabled, setVisible });
  // The send of the composer, as the session one does it: it sends the text of its state, and a send asked while it
  // dictates waits for the last words, then sends what the prompt holds by then. One send waits at a time.
  const send = () => { state.sent.push(text); setText(""); };
  const latest = useRef(send); latest.current = send;
  const waits = useRef(false);
  const ask = () => {
    const written = speech.finishForSend(name);
    if (!written) { send(); return; }
    if (!waits.current) { waits.current = true; void written.then(() => { waits.current = false; latest.current(); }); }
  };
  return <div className="composer-region" data-composer={name} hidden={!visible}>
    <ComposerSurface status="Prompt ready" editor={{ id: active ? "session-prompt" : `session-prompt-${name}`, ref: input, label: "Message", value: text, disabled,
      placeholder: "Ask CodeAlta to work on this project…", onChange: value => { state.changes.push(value); setText(value); },
      onKeyDown: event => { if (event.key === "Enter" && !event.shiftKey && !event.nativeEvent.isComposing) { event.preventDefault(); event.stopPropagation(); ask(); } } }}>
      {/* A dictation that sends asks the composer, which sends the text it holds then. */}
      <MicButton owner={name} input={input} disabled={disabled} visible={visible} id={active ? "dictate-prompt" : `dictate-prompt-${name}`} onSend={send} />
      <Button intent="primary" icon={<AppIcon name="send" size={16} />} aria-label="Send" data-send={name} onClick={ask} />
    </ComposerSurface>
  </div>;
}

// The part of `App`: it owns speech to prompt, and nothing a dictation or a download tells renders it.
function Fixture() {
  const [locale, setLocale] = useState<Locale>("en");
  const [showComposers, setComposers] = useState(true);
  const [showSettings, setSettings] = useState(false);
  const [size, setSize] = useState({ width: 760, height: 620 });
  const speech = useSpeechInstall(host.api, host.epoch, locale, { microphones: microphone.microphones, silenceAfter: 300, warnAfter, sendWait: 1_500, idleAfter });
  fixture.renders++;
  Object.assign(fixture, { speech, setLocale, setComposers, setSettings, setSize });
  return <ShellLanguageContext.Provider value={{ locale, choice: locale, setLanguage: value => setLocale(value as Locale) }}>
    <SpeechContext.Provider value={speech}>
      <div id="workspace-shell" className="ide-shell"><div className="session-workspace">
        {showComposers && <><Composer name="a" active speech={speech} /><Composer name="b" active={false} speech={speech} /></>}
      </div></div>
      {showSettings && <div className="settings-dialog-content" style={{ ...size, overflow: "auto" }}>
        <SpeechSettings epoch={host.epoch} api={host.api} speech={speech} testLimit={fixture.testLimit} />
      </div>}
    </SpeechContext.Provider>
  </ShellLanguageContext.Provider>;
}
createRoot(document.getElementById("root")!).render(<StrictMode><Fixture /></StrictMode>);
