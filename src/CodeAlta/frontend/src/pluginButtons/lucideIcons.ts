// The one lazy chunk that holds every icon of the library. The application ships the icons it draws itself in its main
// chunk (symbolIcons.ts, AppIcon.tsx); a plugin that names another one loads this chunk, once, when it first needs it.
export { icons } from "lucide-react";
