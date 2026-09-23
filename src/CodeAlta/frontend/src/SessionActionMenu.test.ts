import assert from "node:assert/strict";
import test from "node:test";
import { createElement, type ReactElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { SessionActionMenu } from "./SessionActionMenu";

test("menu exposes keyboard actions, and disables mutations without authority or typed confirmation", () => {
  const render = (enabled: boolean) => renderToStaticMarkup(createElement(SessionActionMenu, {
    id: "actions", label: "A session", rename: enabled, deleteAllowed: enabled, menuRef: null,
    onAction: () => assert.fail("Rendering must not invoke an action"), onDismiss: () => {},
  }));
  const readOnly = render(false);
  assert.match(readOnly, /role="menu"/);
  assert.match(readOnly, /Open session/);
  assert.match(readOnly, /Rename…<\/button>/);
  assert.match(readOnly, /Delete… \(confirmation required\)/);
  assert.equal((readOnly.match(/disabled=""/g) ?? []).length, 2);
  assert.equal((render(true).match(/disabled=""/g) ?? []).length, 0);
  assert.doesNotMatch(readOnly, /prompt|draft|Delete this session/);
});

test("menu click only requests an action; Delete cannot invoke a mutation or bypass the existing confirmation", () => {
  const actions: string[] = [];
  const element = SessionActionMenu({ id: "menu", label: "Session", rename: true, deleteAllowed: true,
    menuRef: null, onAction: action => actions.push(action), onDismiss: () => {} });
  const buttons = (element as ReactElement<{ children: ReactElement<{ onClick: () => void }>[] }>).props.children;
  assert.deepEqual(actions, []);
  buttons[0].props.onClick();
  buttons[1].props.onClick();
  buttons[2].props.onClick();
  assert.deepEqual(actions, ["open", "rename", "delete"]);
});
