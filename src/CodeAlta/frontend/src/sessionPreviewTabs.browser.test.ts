import assert from "node:assert/strict";
import test from "node:test";
import { browserExecutable } from "./browserTarget";
import { withSessionWindow } from "./sessionWindowHarness";

// The window itself (main.tsx) over an isolated host, driven with the pointer: a session that is looked at from the
// Explorer takes the one transient tab, a double click keeps it, and the row of a project selects it without opening
// or closing it and shows the prompt of a new session of it.
test("sessions are looked at in one transient tab, kept by a double click, and a project row shows its new-session prompt without folding", { skip: !browserExecutable, timeout: 120_000 }, async () => {
  await withSessionWindow({ settingsFixtureOwned: "true", settingsFixtureSecondProject: "true" }, async ({ command, evaluate, until, tabs, shown, press, session, scope, title, exceptions }) => {
    const state = (id: string) => evaluate(`({ open: ${scope(id)}.getAttribute('aria-expanded'), selected: ${scope(id)}.getAttribute('aria-pressed') })`);

    await until(`!!${session("two")} && ${session("one")}.getAttribute('aria-pressed')==='true'`, "the selected project with session one open");
    // The session the window starts on has a tab of its own.
    await shown(["one - Project*"], "the tab of the first session");

    // A session is looked at: it is in the transient tab, whose title is in italics. The next one takes its place.
    await press(session("two"));
    await shown(["one - Project", "~two - Project*"], "the session looked at, in the transient tab");
    assert.equal(await evaluate(`getComputedStyle(${title("two - Project")}).fontStyle`), "italic");
    assert.equal(await evaluate(`getComputedStyle(${title("one - Project")}).fontStyle`), "normal");
    await press(`document.querySelector('.project-action-row > button[data-scope=""] .tree-twist')`);
    await until(`!!${session("chat-1")}`, "the chats, opened by their chevron");
    assert.deepEqual(await state(""), { open: "true", selected: "false" }, "the chevron opens a scope without selecting it");
    await shown(["one - Project", "~two - Project*"], "nothing changed in the tabs");
    await press(session("chat-1"));
    await shown(["one - Project", "~chat-1 - Chat*"], "another session looked at, in the same tab");
    // Showing a tab that is kept, then the transient one again, changes neither.
    await press(title("one - Project"));
    await shown(["one - Project*", "~chat-1 - Chat"], "the kept tab in front");
    await press(title("chat-1 - Chat"));
    await shown(["one - Project", "~chat-1 - Chat*"], "the transient tab in front again");

    // A double click on a row keeps its session: the tab is its own, and the next session looked at gets another.
    await press(session("chat-2"), 2);
    await shown(["one - Project", "chat-2 - Chat*"], "the session of the row that was double-clicked, kept");
    await press(session("chat-1"));
    await shown(["one - Project", "chat-2 - Chat", "~chat-1 - Chat*"], "a transient tab beside the kept ones");
    // A double click on the title of the transient tab keeps it too.
    await press(title("chat-1 - Chat"), 2);
    await shown(["one - Project", "chat-2 - Chat", "chat-1 - Chat*"], "the transient tab, kept by its title");
    assert.equal(await evaluate(`getComputedStyle(${title("chat-1 - Chat")}).fontStyle`), "normal");
    await press(session("two"));
    await shown(["one - Project", "chat-2 - Chat", "chat-1 - Chat", "~two - Project*"], "the session looked at after that");

    // The row of a project selects it and shows the prompt of a new session of it in the transient tab, in the place of
    // the session that was looked at. It is not opened: its chevron does that.
    assert.deepEqual(await state("other"), { open: "false", selected: "false" });
    await press(scope("other"));
    await shown(["one - Project", "chat-2 - Chat", "chat-1 - Chat", "~New session — Other project*"], "the prompt of the project, in the transient tab");
    assert.deepEqual(await state("other"), { open: "false", selected: "true" }, "a closed project stays closed");
    assert.equal(await evaluate(`document.querySelector('.session-row[data-session-id="other-session"]')?.getBoundingClientRect().height ?? 0`), 0, "its sessions are not shown");
    // An open project stays open, pressed once or again.
    assert.deepEqual(await state("project"), { open: "true", selected: "false" });
    await press(scope("project"));
    await shown(["one - Project", "chat-2 - Chat", "chat-1 - Chat", "~New session — Project*"], "the prompt of the other project, in the same tab");
    assert.deepEqual(await state("project"), { open: "true", selected: "true" });
    await press(scope("project"));
    assert.deepEqual(await state("project"), { open: "true", selected: "true" }, "the row of the selected project does not close it");
    await press(`${scope("project")}.querySelector('.tree-twist')`);
    await until(`${scope("project")}.getAttribute('aria-expanded')==='false'`, "the project closed by its chevron");
    await press(`${scope("project")}.querySelector('.tree-twist')`);
    await until(`${scope("project")}.getAttribute('aria-expanded')==='true'`, "and opened again");

    // A double click on the row of a project gives its prompt a tab of its own; so does one on the title of the
    // transient prompt. Two projects and the chats each keep theirs.
    await press(scope("other"), 2);
    await shown(["one - Project", "chat-2 - Chat", "chat-1 - Chat", "New session — Other project*"], "the prompt of a project, kept");
    assert.deepEqual(await state("other"), { open: "false", selected: "true" });
    await press(scope("project"));
    await shown(["one - Project", "chat-2 - Chat", "chat-1 - Chat", "New session — Other project", "~New session — Project*"], "the prompt of another project, beside it");
    await press(title("New session — Project"), 2);
    await shown(["one - Project", "chat-2 - Chat", "chat-1 - Chat", "New session — Other project", "New session — Project*"], "kept by its title");
    // The prompt that is written in is the one of the tab in front; showing the other tab selects its project.
    assert.equal(await evaluate(`[...document.querySelectorAll('#catalog-prompt')].filter(prompt => prompt.getBoundingClientRect().width > 0).length`), 1);
    await press(title("New session — Other project"));
    await shown(["one - Project", "chat-2 - Chat", "chat-1 - Chat", "New session — Other project*", "New session — Project"], "the tab of the first prompt in front");
    await until(`${scope("other")}.getAttribute('aria-pressed')==='true'`, "its project selected");
    assert.equal(await evaluate(`[...document.querySelectorAll('#catalog-prompt')].filter(prompt => prompt.getBoundingClientRect().width > 0).length`), 1);
    // A session looked at leaves the kept prompts where they are.
    await press(session("two"));
    await shown(["one - Project", "chat-2 - Chat", "chat-1 - Chat", "New session — Other project", "New session — Project", "~two - Project*"], "a session looked at, beside the prompts");

    // The tabs come back as they were at the next start: the kept ones, the prompts, and the transient one.
    await command("Page.reload");
    await until(`!!${session("two")} && ${tabs}.length === 6`, "the window loaded again");
    // The dock is laid out anew at a start: the tabs are the same ones, in an order of its own.
    assert.deepEqual((await evaluate(tabs) as string[]).sort(),
      ["one - Project", "chat-2 - Chat", "chat-1 - Chat", "~two - Project*", "New session — Other project", "New session — Project"].sort());
    assert.deepEqual(exceptions, []);
  });
});

// A prompt that waits above the composer of a session keeps the pane of that session when its tab closes: the pane is
// what sends it once the session is idle. It is out of sight, and leaves with the prompt or when the session has a tab
// again.
test("a session whose tab leaves while a prompt waits above its composer keeps its pane out of sight", { skip: !browserExecutable, timeout: 120_000 }, async () => {
  await withSessionWindow({ settingsFixtureOwned: "true", settingsFixtureSecondProject: "true" }, async ({ command, evaluate, until, shown, press, session, title, exceptions }) => {
    const pane = `[...document.querySelectorAll('.session-workspace')].find(node => node.getBoundingClientRect().width > 0)`;
    const waiting = `[...document.querySelectorAll('.session-background-panes .session-workspace')].map(node => node.querySelectorAll('.composer-queue-row').length)`;
    const background = `!!document.querySelector('.session-background-panes')`;
    const enqueue = async (text: string) => {
      await press(`${pane}.querySelector('.composer-send-options')`);
      await until(`[...document.querySelectorAll('.bp6-menu-item')].some(item => item.textContent.includes('Enqueue until idle'))`, "the options of Send");
      await press(`[...document.querySelectorAll('.bp6-menu-item')].find(item => item.textContent.includes('Enqueue until idle'))`);
      await until(`!!${pane}.querySelector('.composer-send-group > button[aria-label="Enqueue"]')`, "Send enqueues");
      await evaluate(`${pane}.querySelector('#session-prompt').focus()`);
      await command("Input.insertText", { text });
      await press(`${pane}.querySelector('.composer-send-group > button[aria-label="Enqueue"]')`);
      await until(`${pane}.querySelectorAll('.composer-queue-row').length === 1`, "a prompt waiting above the composer");
    };

    await until(`!!${session("two")} && ${session("one")}.getAttribute('aria-pressed')==='true'`, "the selected project with session one open");
    await shown(["one - Project*"], "the tab of the first session");

    // A prompt enqueued in the transient tab keeps that tab; closed, the tab leaves, and the pane of its session stays.
    await press(session("two"));
    await shown(["one - Project", "~two - Project*"], "a session looked at, in the transient tab");
    await enqueue("Later, in two");
    await shown(["one - Project", "two - Project*"], "the tab kept by what was enqueued in it");
    assert.equal(await evaluate(background), false, "a session with a tab has its pane in its tab");
    await press(`${title("two - Project")}.closest('.flexlayout__tab_button').querySelector('.flexlayout__tab_button_trailing')`);
    await shown(["one - Project*"], "the tab of the session closed");
    await until(`JSON.stringify(${waiting}) === '[1]'`, "the pane of the session whose prompt waits, kept");
    assert.equal(await evaluate(`document.querySelector('.session-background-panes').getBoundingClientRect().width`), 0, "out of sight");

    // The last tab of a session, closed: its session keeps its pane as well.
    await enqueue("Later, in one");
    await press(`${title("one - Project")}.closest('.flexlayout__tab_button').querySelector('.flexlayout__tab_button_trailing')`);
    await until(`!${title("one - Project")}`, "the tab of the first session closed");
    await until(`JSON.stringify(${waiting}) === '[1,1]'`, "both panes kept, out of sight");

    // A session that has a tab again has its pane there; it leaves with its last prompt.
    await press(session("two"));
    await until(`${pane}?.querySelectorAll('.composer-queue-row').length === 1 && JSON.stringify(${waiting}) === '[1]'`, "the waiting prompt in the pane of the tab, one pane left out of sight");
    await press(`${pane}.querySelector('.composer-queue-delete')`);
    await until(`${pane}.querySelectorAll('.composer-queue-row').length === 0`, "the prompt taken off the queue");
    await press(`document.querySelector('.project-action-row > button[data-scope=""] .tree-twist')`);
    await until(`!!${session("chat-1")}`, "the chats, opened by their chevron");
    await press(session("chat-1"));
    await until(`!${title("two - Project")} && JSON.stringify(${waiting}) === '[1]'`, "no pane for the session with nothing waiting");
    assert.deepEqual(exceptions, []);
  });
});

// What is typed in the prompt of a new session keeps its tab: the prompt of another scope, or a session, does not
// take its place. An empty prompt leaves its place as before.
test("a prompt of a new session that has a text keeps its tab when another prompt or a session takes its place", { skip: !browserExecutable, timeout: 120_000 }, async () => {
  await withSessionWindow({ settingsFixtureOwned: "true", settingsFixtureSecondProject: "true" }, async ({ command, evaluate, until, tabs, shown, press, session, scope, title, exceptions }) => {
    const prompt = `[...document.querySelectorAll('#catalog-prompt')].find(node => node.getBoundingClientRect().width > 0)`;
    const type = async (text: string) => {
      await until(`!!${prompt}`, "the prompt of a new session");
      await evaluate(`${prompt}.focus()`);
      await command("Input.insertText", { text });
    };
    // An expression of the page: what the window keeps of the prompt of a scope.
    const stored = (id: string | null) => `(localStorage.getItem(${JSON.stringify(`codealta.desktop.localPrompt.${JSON.stringify(id)}`)}) ?? '')`;

    await until(`!!${session("two")} && ${session("one")}.getAttribute('aria-pressed')==='true'`, "the selected project with session one open");
    await press(scope("other"));
    await shown(["one - Project", "~New session — Other project*"], "the prompt of a project, in the transient tab");
    // Nothing is typed: the prompt of another project takes its place.
    await press(scope("project"));
    await shown(["one - Project", "~New session — Project*"], "an empty prompt leaves its place");

    // Something is typed: the tab is still the transient one while it is in front, and stays when another prompt is shown.
    await press(scope("other"));
    await shown(["one - Project", "~New session — Other project*"], "the prompt of the other project again");
    await type("A draft for the other project");
    await until(`${stored("other")}.includes('A draft for the other project')`, "the text is kept with its project");
    await shown(["one - Project", "~New session — Other project*"], "typing changes no tab");
    await press(scope("project"));
    await shown(["one - Project", "New session — Other project", "~New session — Project*"], "the prompt that has a text keeps a tab of its own");
    assert.equal(await evaluate(`getComputedStyle(${title("New session — Other project")}).fontStyle`), "normal");

    // A session that is looked at leaves it too, and takes the place of the empty prompt only.
    await press(session("two"));
    await shown(["one - Project", "New session — Other project", "~two - Project*"], "a session looked at, beside the kept prompt");
    // The chats have a prompt of their own: with a text, a session of a project keeps it open as well.
    await press(scope(""));
    await shown(["one - Project", "New session — Other project", "~New chat*"], "the prompt of the chats");
    await type("A question for a chat");
    await until(`${stored(null)}.includes('A question for a chat')`, "the text of the chats is kept");
    await press(session("two"));
    await shown(["one - Project", "New session — Other project", "New chat", "~two - Project*"], "both prompts are kept beside the session");

    // The kept tab shows what was typed, in the prompt of its project.
    await press(title("New session — Other project"));
    await shown(["one - Project", "New session — Other project*", "New chat", "~two - Project"], "the kept prompt in front");
    await until(`${scope("other")}.getAttribute('aria-pressed')==='true'`, "its project selected");
    await until(`(${prompt}?.querySelector('.view-lines')?.textContent ?? '').replaceAll('\\u00a0', ' ').includes('A draft for the other project')`, "the text that was typed");

    // It is closed like any tab; what was typed stays with its project.
    await press(`${title("New session — Other project")}.closest('.flexlayout__tab_button').querySelector('.flexlayout__tab_button_trailing')`);
    await until(`!${tabs}.some(name => name.includes('New session — Other project'))`, "the tab of the prompt closed");
    assert.equal(await evaluate(`${stored("other")}.includes('A draft for the other project')`), true, "the text is not lost with the tab");
    await shown(["one - Project*", "New chat", "~two - Project"], "a prompt that is closed stays closed, and a session is in front");

    // The transient tab of a prompt that has a text is closed the same way: it does not come back as a tab of its own.
    await press(scope("other"));
    await shown(["one - Project", "New chat", "~New session — Other project*"], "the prompt of the project, with its text, in the transient tab");
    await press(`${title("New session — Other project")}.closest('.flexlayout__tab_button').querySelector('.flexlayout__tab_button_trailing')`);
    await shown(["one - Project*", "New chat"], "the transient prompt closed");
    assert.deepEqual(exceptions, []);
  });
});
