import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { ImageView } from "./ImageView";

// A picture of the project editor, an icon as the disk gives it, over a clipboard that keeps what is written to it.
function picture(width: number, height: number) {
  const canvas = document.createElement("canvas");
  canvas.width = width; canvas.height = height;
  const context = canvas.getContext("2d")!;
  context.fillStyle = "#4f8cff"; context.fillRect(0, 0, width, height);
  return canvas.toDataURL("image/png");
}

const state = { written: [] as { types: string[]; type: string; width: number; height: number }[] };
Object.defineProperty(navigator, "clipboard", { configurable: true, value: {
  async write(items: ClipboardItem[]) {
    for (const item of items) {
      const blob = await item.getType("image/png");
      const bitmap = await createImageBitmap(blob);
      state.written.push({ types: [...item.types], type: blob.type, width: bitmap.width, height: bitmap.height });
      bitmap.close();
    }
  },
} });
Object.assign(window, { imageViewFixture: { state } });

// An icon (a PNG inside, as icons often hold) with the type the editor gives it: one the copy draws again as PNG.
const url = picture(64, 48).replace("data:image/png", "data:image/x-icon");
const drawing = `data:image/svg+xml;charset=utf-8,${encodeURIComponent('<svg xmlns="http://www.w3.org/2000/svg" width="10" height="10"/>')}`;

createRoot(document.getElementById("root")!).render(<StrictMode><div className="ide-shell" style={{ height: "100%", display: "grid", gridTemplateRows: "1fr 1fr" }}>
  <div className="fixture-picture" style={{ display: "flex", minHeight: 0 }}><ImageView url={url} label="icon.ico" /></div>
  <div className="fixture-drawing" style={{ display: "flex", minHeight: 0 }}><ImageView vector url={drawing} label="logo.svg" /></div>
</div></StrictMode>);
