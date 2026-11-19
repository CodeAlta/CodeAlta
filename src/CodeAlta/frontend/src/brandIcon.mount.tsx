import { createElement } from "react";
import { flushSync } from "react-dom";
import { createRoot } from "react-dom/client";
import { BrandIcon, type BrandIconName } from "./BrandIcon";

// Logos of brands that their page renders again, to see what a render does to what they drew.
const root = createRoot(document.getElementById("root")!);
function show(names: readonly BrandIconName[], size = 16) {
  flushSync(() => root.render(createElement("div", null, names.map(name => createElement(BrandIcon, { key: name, name, size })))));
}
Object.assign(window, { brandIcons: { show } });
