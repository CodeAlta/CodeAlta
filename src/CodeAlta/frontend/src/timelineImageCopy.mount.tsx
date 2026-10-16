import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { TimelineImages } from "./TimelineImageStrip";
import { inlineImageSource } from "./timelineImages";

// The images of a message, a PNG and a JPEG, over a clipboard that keeps what is written to it.
function picture(type: string, color: string, width: number, height: number) {
  const canvas = document.createElement("canvas");
  canvas.width = width; canvas.height = height;
  const context = canvas.getContext("2d")!;
  context.fillStyle = color; context.fillRect(0, 0, width, height);
  return canvas.toDataURL(type);
}

const state = { written: [] as { types: string[]; type: string; width: number; height: number }[], refuse: false };
Object.defineProperty(navigator, "clipboard", { configurable: true, value: {
  async write(items: ClipboardItem[]) {
    if (state.refuse) throw new DOMException("Refused.", "NotAllowedError");
    for (const item of items) {
      const blob = await item.getType("image/png");
      const bitmap = await createImageBitmap(blob);
      state.written.push({ types: [...item.types], type: blob.type, width: bitmap.width, height: bitmap.height });
      bitmap.close();
    }
  },
} });

const images = [{ index: 0, title: "First.png", mediaType: "image/png" }, { index: 1, title: "Second.jpg", mediaType: "image/jpeg" }];
const source = inlineImageSource("message", [{ url: picture("image/png", "#4f8cff", 320, 200) }, { url: picture("image/jpeg", "#c050e0", 120, 90) }]);
Object.assign(window, { imageCopyFixture: { state } });

createRoot(document.getElementById("root")!).render(<StrictMode><div className="ide-shell" style={{ height: "100%", padding: 16 }}>
  <TimelineImages images={images} source={source} />
</div></StrictMode>);
