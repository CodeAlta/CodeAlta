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
