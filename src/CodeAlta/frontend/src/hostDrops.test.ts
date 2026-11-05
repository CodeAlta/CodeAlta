import assert from "node:assert/strict";
import test from "node:test";
import { deliverHostDrop, dropSurface, hostDropEvent, readHostDrop, type HostDropDetail } from "./hostDrops";
import { attachmentLimits } from "./promptImages";

const id = (n: number) => n.toString(16).padStart(32, "0");

test("a files-dropped notice is read only when it is well formed", () => {
  const drop = { x: 12, y: 30, folders: ["/home/me/docs"], files: [{ id: id(1), name: "notes.txt", size: 5 }] };
  assert.deepEqual(readHostDrop(drop), drop);
  for (const value of [null, undefined, 7, {}, { ...drop, x: "12" }, { ...drop, y: Number.NaN }, { ...drop, folders: [], files: [] },
    { ...drop, folders: [""] }, { ...drop, folders: ["/a\0b"] }, { ...drop, folders: "/home" }, { ...drop, files: [{ id: "x", name: "a", size: 1 }] },
    { ...drop, files: [{ id: id(1), name: " ", size: 1 }] }, { ...drop, files: [{ id: id(1), name: "a", size: -1 }] },
    { ...drop, files: [{ id: id(1), name: "a", size: 1.5 }] }, { ...drop, files: [null] },
    { ...drop, folders: Array.from({ length: 257 }, (_, n) => `/f${n}`) }])
    assert.equal(readHostDrop(value), null, JSON.stringify(value));
});

// A page of plain objects: a body with two panes, each with a transcript and a composer surface, and a dialog.
function page({ hidden = false, onePane = false } = {}) {
  type Node = { name: string; classes: string[]; role?: string; children: Node[]; parentElement: Node | null; shown: boolean; isConnected: boolean;
    events: CustomEvent[]; closest: (selector: string) => Node | null; matches: (selector: string) => boolean;
    querySelectorAll: (selector: string) => Node[]; getClientRects: () => { length: number }; dispatchEvent: (event: Event) => boolean };
  const make = (name: string, classes: string[], children: Node[] = [], role?: string): Node => {
    const node: Node = { name, classes, role, children, parentElement: null, shown: true, isConnected: true, events: [],
      matches: selector => selector === ".prompt-surface" ? classes.includes("prompt-surface") : selector.includes("dialog") ? role === "dialog" : false,
      closest: selector => { for (let current: Node | null = node; current; current = current.parentElement) if (current.matches(selector)) return current; return null; },
      querySelectorAll: selector => node.children.flatMap(child => [...(child.matches(selector) ? [child] : []), ...child.querySelectorAll(selector)]),
      getClientRects: () => ({ length: node.shown ? 1 : 0 }),
      dispatchEvent: event => { node.events.push(event as CustomEvent); return true; } };
    for (const child of children) child.parentElement = node;
    return node;
  };
  const left = { transcript: make("left-transcript", []), surface: make("left-surface", ["prompt-surface"]) };
  const right = { transcript: make("right-transcript", []), surface: make("right-surface", ["prompt-surface"]) };
  const dialogButton = make("dialog-button", []);
  const sidebar = make("sidebar", []);
  const panes = onePane ? [make("left-pane", [], [left.transcript, left.surface])]
    : [make("left-pane", [], [left.transcript, left.surface]), make("right-pane", [], [right.transcript, right.surface])];
  make("body", [], [sidebar, ...panes, make("dialog", [], [dialogButton], "dialog")]);
  if (hidden) right.surface.shown = false;
  const at: Record<string, Node> = { "1,1": left.transcript, "1,2": left.surface, "2,1": right.transcript, "2,2": right.surface, "0,0": sidebar, "9,9": dialogButton };
  const root = { elementFromPoint: (x: number, y: number) => at[`${x},${y}`] ?? null } as unknown as Document;
  return { root, left, right };
}

test("a host drop goes to the composer under its point, or to the one of the pane under it", () => {
  const { root, left, right } = page();
  assert.equal(dropSurface(root, 1, 2), left.surface);
  assert.equal(dropSurface(root, 2, 2), right.surface);
  assert.equal(dropSurface(root, 1, 1), left.surface, "The transcript of a pane leads to its composer.");
  assert.equal(dropSurface(root, 2, 1), right.surface);
  assert.equal(dropSurface(root, 0, 0), null, "Between two panes there is no composer to choose.");
  assert.equal(dropSurface(root, 9, 9), null, "A dialog takes nothing.");
  assert.equal(dropSurface(root, 5, 5), null);
  const one = page({ onePane: true });
  assert.equal(dropSurface(one.root, 0, 0), one.left.surface, "With one pane, any point outside a dialog leads to its composer.");
  const hidden = page({ hidden: true });
  assert.equal(dropSurface(hidden.root, 0, 0), hidden.left.surface, "A composer that is not shown (another tab) does not count.");
  assert.equal(dropSurface(hidden.root, 2, 2), null);
});

test("a host drop reads each file once and gives the files and the folders to the composer", async () => {
  const { root, left } = page();
  const reads: string[] = [];
  const replies: Record<string, unknown> = {
    [id(1)]: { status: "ok", name: "notes.txt", mediaType: "text/plain", base64: btoa("hello") },
    [id(2)]: { status: "failed", name: "gone.txt" },
    [id(3)]: { status: "too_large", name: "grown.bin" },
    [id(5)]: { status: "ok", name: "bad.txt", mediaType: "text/plain", base64: "%%%" },
  };
  const result = await deliverHostDrop({ x: 1, y: 1, folders: ["/home/me/docs"], files: [
    { id: id(1), name: "notes.txt", size: 5 }, { id: id(2), name: "gone.txt", size: 3 }, { id: id(3), name: "grown.bin", size: 9 },
    { id: id(4), name: "huge.bin", size: attachmentLimits.fileBytes + 1 }, { id: id(5), name: "bad.txt", size: 3 }, { id: id(6), name: "thrown.txt", size: 3 }] },
  async ({ id: read }) => { reads.push(read); if (read === id(6)) throw new Error("closed"); return replies[read]; }, root);
  assert.equal(result.delivered, true);
  assert.deepEqual(result.problems, [{ name: "gone.txt", reason: "unreadable" }, { name: "grown.bin", reason: "too_large" },
    { name: "huge.bin", reason: "too_large" }, { name: "bad.txt", reason: "unreadable" }, { name: "thrown.txt", reason: "unreadable" }]);
  assert.deepEqual(reads, [id(1), id(2), id(3), id(5), id(6)], "A file larger than an attachment is not read.");
  const [event] = left.surface.events;
  assert.equal(event.type, hostDropEvent);
  const detail = event.detail as HostDropDetail;
  assert.deepEqual(detail.folders, ["/home/me/docs"]);
  assert.deepEqual(detail.files.map(file => [file.name, file.type, file.size]), [["notes.txt", "text/plain", 5]]);
  assert.equal(await detail.files[0].text(), "hello");
});

test("a host drop where no composer is reads nothing", async () => {
  const { root } = page();
  const reads: string[] = [];
  assert.deepEqual(await deliverHostDrop({ x: 0, y: 0, folders: [], files: [{ id: id(1), name: "a.txt", size: 1 }] },
    async ({ id: read }) => { reads.push(read); return {}; }, root), { delivered: false, problems: [] });
  assert.deepEqual(reads, []);
});
