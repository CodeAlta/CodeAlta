import assert from "node:assert/strict";
import test from "node:test";
import { browserExecutable } from "./browserTarget";
import { withSessionWindow } from "./sessionWindowHarness";
import { testLimit } from "./testTime";

// The window itself (main.tsx) over an isolated host (#307): a click on a row of the Explorer hands the keyboard to the
// prompt, as a click on a tab does, and a character typed where typing goes nowhere lands in the prompt.
test("a click on a row of the Explorer and a character typed outside text give the keyboard to the prompt", { skip: !browserExecutable, timeout: testLimit(120_000) }, async () => {
  await withSessionWindow({ settingsFixtureOwned: "true", settingsFixtureSecondProject: "true" }, async ({ command, evaluate, until, press, session, scope, exceptions }) => {
    const inPrompt = (id = "session-prompt") => `!!document.activeElement?.closest('#${id}')`;
    const text = (id = "session-prompt") => `(document.querySelector('#${id} .view-lines')?.textContent ?? '').replaceAll('\\u00a0', ' ')`;
    // A key as a keyboard gives it: its key-down, the character it types, its release.
    const type = async (key: string, modifiers = 0, commands: string[] = []) => {
      const code = key.length === 1 && /[a-z]/iu.test(key) ? `Key${key.toUpperCase()}` : key;
      const virtual = key.length === 1 ? key.toUpperCase().charCodeAt(0) : { Enter: 13 }[key] ?? 0;
      const typed = modifiers === 0 ? { text: key === "Enter" ? "\r" : key } : {};
      await command("Input.dispatchKeyEvent", { type: modifiers === 0 ? "keyDown" : "rawKeyDown", key, code, windowsVirtualKeyCode: virtual, modifiers, commands, ...typed });
      await command("Input.dispatchKeyEvent", { type: "keyUp", key, code, windowsVirtualKeyCode: virtual, modifiers });
    };
    const frames = () => evaluate(`new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(() => requestAnimationFrame(() => resolve(true)))))`);
    const nowhere = async () => { await evaluate("document.activeElement?.blur(), true"); await until("document.activeElement === document.body", "the keyboard nowhere"); };

    await until(`!!${session("two")} && ${session("one")}.getAttribute('aria-pressed')==='true' && !!document.querySelector('#session-prompt')`, "session one with its prompt");

    // A click on the row of another session shows it, and its prompt takes the keyboard (it stayed on the row).
    await press(session("two"));
    await until(`${session("two")}.getAttribute('aria-pressed')==='true'`, "session two shown");
    await until(inPrompt(), "a click on the row of a session gives the keyboard to its prompt");
    // A click on the row of a scope shows the prompt of a new session of it, with the keyboard.
    await press(scope(""));
    await until(`${scope("")}.getAttribute('aria-pressed')==='true' && !!document.querySelector('#catalog-prompt')`, "the prompt of a new chat");
    await until(inPrompt("catalog-prompt"), "a click on the row of the chats gives the keyboard to the prompt of a new chat");
    await press(scope(""));
    await press(session("one"));
    await until(`${session("one")}.getAttribute('aria-pressed')==='true' && ${inPrompt()}`, "session one again, its prompt with the keyboard");

    // A row activated from the keyboard keeps it: the arrow keys go on through the tree.
    await evaluate(`${session("two")}.focus(), true`);
    await type("Enter");
    await until(`${session("two")}.getAttribute('aria-pressed')==='true'`, "Enter on the row of session two shows it");
    await frames();
    assert.equal(await evaluate(`document.activeElement === ${session("two")}`), true, "the row activated from the keyboard keeps it");

    // A letter typed on a row that does not answer it goes to the prompt, with the keyboard.
    await type("h");
    await until(`${inPrompt()} && ${text()} === 'h'`, "the letter typed on a row lands in the prompt");
    // The rest of what is typed follows, in the prompt now.
    await type("i");
    await until(`${text()} === 'hi'`, "the next letter follows in the prompt");

    // From nowhere (Escape, a click on the timeline), after a selection of the page outside the prompt: the characters land
    // in the prompt, at the end of its text, and the clipboard keys still act in the prompt (#306).
    await evaluate(`getSelection().selectAllChildren(document.querySelector('.session-tabs') ?? document.body), true`);
    await nowhere();
    await type("!");
    await until(`${inPrompt()} && ${text()} === 'hi!'`, "a character typed from nowhere lands at the end of the prompt");
    await type("a", 2, ["selectAll"]);
    await type("x", 2, ["cut"]);
    await until(`${text()} === ''`, "Ctrl+A and Ctrl+X act in the prompt that took the keyboard");

    // `/` from nowhere does what it does in an empty prompt: the search of the commands.
    await nowhere();
    await type("/");
    await until(`document.querySelector('dialog[open] input[aria-label="Search"]')?.value === '/'`, "/ opens the search of the commands");
    assert.equal(await evaluate(text()), "", "and is not typed in the prompt");
    await command("Input.dispatchKeyEvent", { type: "rawKeyDown", key: "Escape", code: "Escape", windowsVirtualKeyCode: 27 });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: "Escape", code: "Escape", windowsVirtualKeyCode: 27 });
    await until(`!document.querySelector('dialog[open]')`, "the search closed");

    // Fields, other editors and widgets keep their own letters. A prevented key is already claimed by its control.
    for (const markup of ['<input />', '<textarea></textarea>', '<div contenteditable="true" tabindex="0"></div>',
      '<div class="monaco-editor" tabindex="0"></div>', '<div class="terminal-host" tabindex="0"></div>',
      '<div data-editor-keys tabindex="0"></div>', '<div role="tree" tabindex="0"></div>',
      '<div role="listbox" tabindex="0"></div>', '<div role="combobox" tabindex="0"></div>',
      '<button onkeydown="event.preventDefault()">Keeps letters</button>']) {
      await evaluate(`window.typingControl = document.createElement('div'); typingControl.innerHTML = ${JSON.stringify(markup)};
        document.body.append(typingControl); typingControl.firstElementChild.focus(); true`);
      await type("q");
      await frames();
      assert.equal(await evaluate(text()), "", `typing stays in ${markup}`);
      await evaluate("typingControl.remove(), true");
    }
    // A prompt hidden by layout or a banner, disabled or inert must never receive a character.
    for (const mark of ["hidden", "inert", "covered", "disabled", "visibility"]) {
      await evaluate(`window.typingPromptNode = document.querySelector('#session-prompt');
        ${mark === "covered" ? "typingPromptNode.classList.add('covered')"
          : mark === "disabled" ? "typingPromptNode.setAttribute('aria-disabled', 'true')"
          : mark === "visibility" ? "typingPromptNode.style.visibility = 'hidden'"
          : `typingPromptNode.setAttribute('${mark}', '')`}; true`);
      await nowhere();
      await type("q");
      await frames();
      assert.equal(await evaluate(text()), "", `the ${mark} prompt takes no text`);
      await evaluate(`typingPromptNode.removeAttribute('hidden'); typingPromptNode.removeAttribute('inert');
        typingPromptNode.removeAttribute('aria-disabled'); typingPromptNode.classList.remove('covered');
        typingPromptNode.style.visibility = ''; true`);
    }
    // Menus and nonmodal popovers also keep typing out, even if their trigger still owns the keyboard.
    for (const markup of ['<div role="menu"><button>Entry</button></div>', '<div class="bp6-popover"><button>Entry</button></div>']) {
      await evaluate(`window.typingControl = document.createElement('div'); typingControl.innerHTML = ${JSON.stringify(markup)};
        document.body.append(typingControl); true`);
      await nowhere();
      await type("q");
      await frames();
      assert.equal(await evaluate(text()), "", `typing while ${markup} is open stays out of the prompt`);
      await evaluate("typingControl.remove(), true");
    }
    // The More menu of the composer, open with the keyboard on its button, keeps typing out too.
    const shownMenu = `[...document.querySelectorAll('details.composer-actions-menu')].find(menu => menu.getClientRects().length > 0)`;
    assert.equal(await evaluate(`!!${shownMenu}`), true, "the composer shown has its More menu");
    await evaluate(`window.typingMenu = ${shownMenu}; typingMenu.open = true; typingMenu.querySelector('summary').focus(); true`);
    assert.equal(await evaluate(`typingMenu.open && document.activeElement === typingMenu.querySelector('summary')`), true, "the More menu is open with the keyboard");
    await type("q");
    await frames();
    assert.equal(await evaluate(text()), "", "typing while the More menu is open stays out of the prompt");
    await evaluate("typingMenu.open = false, true");

    // A shortcut is no character, and a window over the page keeps what is typed.
    await nowhere();
    await type("k", 2);
    await frames();
    assert.equal(await evaluate(`${text()} === '' && document.activeElement === document.body`), true, "Ctrl+K types nothing in the prompt");
    await command("Input.dispatchKeyEvent", { type: "rawKeyDown", key: "F1", code: "F1", windowsVirtualKeyCode: 112 });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: "F1", code: "F1", windowsVirtualKeyCode: 112 });
    await until(`!!document.querySelector('dialog[open]')`, "Help open");
    await evaluate("document.activeElement?.blur(), true");
    await type("q");
    await frames();
    assert.equal(await evaluate(text()), "", "a key typed while a window is open stays out of the prompt");
    assert.deepEqual(exceptions, []);
  });
});
